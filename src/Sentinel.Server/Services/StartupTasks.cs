using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>Миграции БД и первый администратор.</summary>
public static class StartupTasks
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        await db.Database.MigrateAsync(ct);

        if (!await db.Users.AnyAsync(ct))
        {
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<SentinelOptions>>().Value.Bootstrap;
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var password = opts.AdminPassword;
            var generated = false;
            if (string.IsNullOrWhiteSpace(password))
            {
                password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).TrimEnd('=');
                generated = true;
            }

            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Email = opts.AdminEmail.ToLowerInvariant(), DisplayName = "Администратор",
                PasswordHash = hasher.Hash(password), Role = UserRole.Admin, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);

            if (generated)
            {
                log.LogWarning("Создан администратор {Email} с паролем: {Password}  — смените его после первого входа.", opts.AdminEmail, password);
                // Как initialAdminPassword у Jenkins: установщики показывают пароль из файла, не разбирая лог.
                var dataDir = scope.ServiceProvider.GetRequiredService<IOptions<SentinelOptions>>().Value.DataDir;
                try
                {
                    Directory.CreateDirectory(dataDir);
                    // С BOM: файл читают Блокнот и установщик Inno Setup (без BOM он считает текст ANSI).
                    File.WriteAllText(Path.Combine(dataDir, "initial-admin-password.txt"),
                        $"Логин: {opts.AdminEmail}\r\nПароль: {password}\r\n\r\nСмените пароль после первого входа и удалите этот файл.\r\n",
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                }
                catch (Exception ex) { log.LogWarning("Не удалось записать initial-admin-password.txt: {Error}", ex.Message); }
            }
            else
                log.LogInformation("Создан администратор {Email}.", opts.AdminEmail);
        }

        await SyncBuiltInTemplatesAsync(db, scope.ServiceProvider.GetRequiredService<ServicePresets>(), log, ct);
    }

    /// <summary>
    /// Встроенные шаблоны: создаём отсутствующие и досыпаем в существующие проверки, появившиеся с новой версией
    /// (например, контроль ВМ в шаблоне Hyper-V). Уже имеющиеся проверки не трогаем — правки пользователя сохраняются.
    /// </summary>
    private static async Task SyncBuiltInTemplatesAsync(SentinelDbContext db, ServicePresets presets, ILogger log, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expected = BuiltInTemplates.Create(presets, now).ToList();
        var existing = await db.CheckTemplates.Where(t => t.TenantId == null).ToListAsync(ct);
        int created = 0, extended = 0;

        foreach (var want in expected)
        {
            var have = existing.FirstOrDefault(t => t.Name == want.Name);
            if (have is null) { db.CheckTemplates.Add(want); created++; continue; }

            var haveItems = JsonNode.Parse(have.ItemsJson) as JsonArray ?? new JsonArray();
            var wantItems = JsonNode.Parse(want.ItemsJson) as JsonArray ?? new JsonArray();
            var known = haveItems.OfType<JsonObject>()
                .Select(i => (Module: i["moduleId"]?.GetValue<string>() ?? "", Name: i["name"]?.GetValue<string>() ?? ""))
                .ToHashSet();
            var added = 0;
            foreach (var item in wantItems.OfType<JsonObject>())
            {
                var key = (Module: item["moduleId"]?.GetValue<string>() ?? "", Name: item["name"]?.GetValue<string>() ?? "");
                if (known.Contains(key)) continue;
                haveItems.Add(JsonNode.Parse(item.ToJsonString()));
                added++;
            }
            if (added == 0) continue;
            have.ItemsJson = haveItems.ToJsonString();
            have.UpdatedAt = now;
            extended++;
        }

        if (created + extended == 0) return;
        await db.SaveChangesAsync(ct);
        if (created > 0) log.LogInformation("Созданы встроенные шаблоны проверок: {Count}", created);
        if (extended > 0) log.LogInformation("Во встроенные шаблоны добавлены новые проверки: шаблонов {Count}", extended);
    }
}
