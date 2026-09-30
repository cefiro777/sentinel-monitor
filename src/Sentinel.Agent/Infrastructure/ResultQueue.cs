using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Infrastructure;

/// <summary>Офлайн-буфер результатов на SQLite. Результаты не теряются при отсутствии связи и перезапуске службы.</summary>
public sealed class ResultQueue : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new object();

    /// <summary>Ограничение размера буфера: при переполнении удаляются самые старые записи.</summary>
    public int MaxItems { get; set; } = 200_000;

    public ResultQueue(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS results (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 created_at TEXT NOT NULL,
                 json TEXT NOT NULL);");
    }

    public void Enqueue(IEnumerable<CheckResult> results)
    {
        lock (_lock)
        {
            using (var tx = _db.BeginTransaction())
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO results(created_at, json) VALUES ($at, $json)";
                var pAt = cmd.Parameters.Add("$at", SqliteType.Text);
                var pJson = cmd.Parameters.Add("$json", SqliteType.Text);
                foreach (var r in results)
                {
                    pAt.Value = DateTimeOffset.UtcNow.ToString("O");
                    pJson.Value = SentinelJson.Serialize(r);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            Trim();
        }
    }

    /// <summary>Возвращает до <paramref name="max"/> самых старых записей вместе с их id для последующего подтверждения.</summary>
    public List<(long id, CheckResult result)> Peek(int max)
    {
        var list = new List<(long, CheckResult)>();
        lock (_lock)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, json FROM results ORDER BY id LIMIT $max";
                cmd.Parameters.AddWithValue("$max", max);
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        var r = SentinelJson.Deserialize<CheckResult>(rd.GetString(1));
                        if (r is not null) list.Add((rd.GetInt64(0), r));
                    }
                }
            }
        }
        return list;
    }

    public void Ack(long maxIdInclusive)
    {
        lock (_lock)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM results WHERE id <= $id";
                cmd.Parameters.AddWithValue("$id", maxIdInclusive);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM results";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    private void Trim()
    {
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM results WHERE id <= (SELECT id FROM results ORDER BY id DESC LIMIT 1 OFFSET $keep)";
            cmd.Parameters.AddWithValue("$keep", MaxItems);
            cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _db.Dispose();
}
