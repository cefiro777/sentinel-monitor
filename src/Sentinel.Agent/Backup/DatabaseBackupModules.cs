using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Modules;

namespace Sentinel.Agent.Backup;

/// <summary>BACKUP DATABASE (полный раз в N дней, между ними DIFFERENTIAL) + RESTORE VERIFYONLY. Один артефакт на базу.</summary>
public sealed class MsSqlBackupModule : BackupModuleBase<MsSqlBackupSettings>
{
    public MsSqlBackupModule(ILogger<MsSqlBackupModule> log) : base(log) { }

    public override string Id => ModuleIds.BackupMsSql;
    protected override string[] ArtifactExtensions => new[] { "bak" };

    protected override async Task<List<Artifact>> CreateArtifactsAsync(ModuleContext ctx, MsSqlBackupSettings s, string slug, string destination, BackupLog log, CancellationToken ct)
    {
        if (s.Databases.Count == 0) throw new InvalidOperationException("Не заданы базы данных.");
        var sqlDir = string.IsNullOrWhiteSpace(s.SqlBackupDir) ? destination : s.SqlBackupDir!.Trim();

        var csb = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(s.Server) ? "." : s.Server.Trim(),
            IntegratedSecurity = s.IntegratedSecurity,
            ConnectTimeout = 30,
            ApplicationName = "SentinelAgent",
        };
        if (!s.IntegratedSecurity) { csb.UserID = s.Username; csb.Password = s.Password; }

        var artifacts = new List<Artifact>();
        using (var conn = new SqlConnection(csb.ConnectionString))
        {
            await conn.OpenAsync(ct);
            log.Info($"SQL Server {csb.DataSource}: {conn.ServerVersion}");

            foreach (var db in s.Databases.Select(d => d.Trim()).Where(d => d.Length > 0))
            {
                ct.ThrowIfCancellationRequested();
                var a = new Artifact { Target = db };
                try
                {
                    var now = DateTimeOffset.Now;
                    var lastFull = LastFull(destination, Prefix(slug, db));
                    var kind = lastFull is null || s.FullEveryDays <= 1 || now - lastFull.Value.at >= TimeSpan.FromDays(Math.Max(1, s.FullEveryDays)) ? "full" : "diff";
                    var name = ArtifactName(slug, db, kind, "bak", now);
                    var sqlPath = Path.Combine(sqlDir, name);
                    a.Kind = kind;
                    log.Info($"[{db}] {(kind == "full" ? "полный" : "разностный")} бэкап → {sqlPath}");

                    var options = new List<string> { "INIT", "CHECKSUM", "STATS = 25" };
                    if (kind == "diff") options.Insert(0, "DIFFERENTIAL");
                    if (s.Compression) options.Add("COMPRESSION");
                    var sql = $"BACKUP DATABASE [{db.Replace("]", "]]")}] TO DISK = @path WITH {string.Join(", ", options)}";

                    try { await ExecAsync(conn, sql, sqlPath, ct); }
                    catch (SqlException ex) when (s.Compression && (ex.Number == 1844 || ex.Message.Contains("COMPRESSION")))
                    {
                        // Express и старые Standard не умеют сжатие — повторяем без него.
                        log.Warn($"[{db}] сжатие не поддерживается этой редакцией, повтор без COMPRESSION");
                        options.Remove("COMPRESSION");
                        await ExecAsync(conn, $"BACKUP DATABASE [{db.Replace("]", "]]")}] TO DISK = @path WITH {string.Join(", ", options)}", sqlPath, ct);
                    }

                    if (s.Verify)
                    {
                        await ExecAsync(conn, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", sqlPath, ct);
                        a.Verified = true;
                        log.Info($"[{db}] RESTORE VERIFYONLY: ок");
                    }

                    // Если SQL Server писал в свой каталог, переносим в назначение.
                    var finalPath = Path.Combine(destination, name);
                    if (!string.Equals(Path.GetFullPath(sqlDir), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(sqlPath)) throw new FileNotFoundException($"Файл бэкапа не виден агенту: {sqlPath}. Укажите каталог, доступный и SQL Server, и агенту.");
                        File.Copy(sqlPath, finalPath, true);
                        File.Delete(sqlPath);
                        log.Info($"[{db}] перенесён в {destination}");
                    }
                    a.Path = finalPath;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    a.Error = $"[{db}] {ex.Message}";
                }
                artifacts.Add(a);
            }
        }
        return artifacts;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, string path, CancellationToken ct)
    {
        using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 })
        {
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

/// <summary>pg_dump в custom-формате (-Fc) + проверка pg_restore --list. Один артефакт на базу.</summary>
public sealed class PostgresBackupModule : BackupModuleBase<PostgresBackupSettings>
{
    public PostgresBackupModule(ILogger<PostgresBackupModule> log) : base(log) { }

    public override string Id => ModuleIds.BackupPostgres;
    protected override string[] ArtifactExtensions => new[] { "dump" };

    protected override async Task<List<Artifact>> CreateArtifactsAsync(ModuleContext ctx, PostgresBackupSettings s, string slug, string destination, BackupLog log, CancellationToken ct)
    {
        if (s.Databases.Count == 0) throw new InvalidOperationException("Не заданы базы данных.");
        var bin = FindBinDir(s.BinDir) ?? throw new FileNotFoundException("pg_dump.exe не найден. Укажите папку bin PostgreSQL в настройках.");
        log.Info($"pg_dump: {bin}");

        var artifacts = new List<Artifact>();
        foreach (var db in s.Databases.Select(d => d.Trim()).Where(d => d.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            var a = new Artifact { Target = db, Kind = "dump" };
            var outPath = Path.Combine(destination, ArtifactName(slug, db, "dump", "dump", DateTimeOffset.Now));
            try
            {
                var args = $"-h \"{s.Host}\" -p {s.Port} -U \"{s.Username}\" -Fc -Z 6 --no-password -f \"{outPath}\" \"{db}\"";
                var (code, output) = await RunAsync(Path.Combine(bin, "pg_dump.exe"), args, s.Password, ct);
                if (code != 0) throw new InvalidOperationException($"pg_dump завершился с кодом {code}: {output}");
                log.Info($"[{db}] pg_dump: ок");

                var (vcode, voutput) = await RunAsync(Path.Combine(bin, "pg_restore.exe"), $"--list \"{outPath}\"", null, ct);
                a.Verified = vcode == 0;
                if (!a.Verified) log.Warn($"[{db}] pg_restore --list: код {vcode}: {voutput}");
                a.Path = outPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { File.Delete(outPath); } catch { }
                a.Error = $"[{db}] {ex.Message}";
            }
            artifacts.Add(a);
        }
        return artifacts;
    }

    private static string? FindBinDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "pg_dump.exe"))) return configured;
        foreach (var root in new[] { @"C:\Program Files\PostgreSQL", @"C:\Program Files (x86)\PostgreSQL", @"C:\Program Files\PostgreSQL 1C" })
        {
            if (!Directory.Exists(root)) continue;
            var candidate = Directory.GetDirectories(root).OrderByDescending(d => d).Select(d => Path.Combine(d, "bin")).FirstOrDefault(b => File.Exists(Path.Combine(b, "pg_dump.exe")));
            if (candidate is not null) return candidate;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "pg_dump.exe"))) return dir;
        return null;
    }

    private static async Task<(int code, string output)> RunAsync(string exe, string args, string? password, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (password is not null) psi.EnvironmentVariables["PGPASSWORD"] = password;
        psi.EnvironmentVariables["PGCLIENTENCODING"] = "UTF8";
        using (var p = Process.Start(psi)!)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await Task.Run(() => p.WaitForExit(), ct);
            var output = ((await stderr) + "\n" + (await stdout)).Trim();
            return (p.ExitCode, output.Length > 2000 ? output.Substring(0, 2000) : output);
        }
    }
}
