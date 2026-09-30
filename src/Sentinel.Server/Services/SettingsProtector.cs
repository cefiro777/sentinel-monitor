using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Server.Core.Abstractions;

namespace Sentinel.Server.Services;

/// <summary>
/// Секреты в настройках модулей (пароли SQL, токены облака, учётки шар) хранятся в БД зашифрованными:
/// значение поля заменяется на "enc:&lt;base64&gt;". В дашборд уходит маска, агенту — расшифрованное значение.
/// </summary>
public sealed class SettingsProtector(ISecretProtector protector)
{
    public const string Mask = "••••••";
    private const string Prefix = "enc:";
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase) { "password", "token", "botToken", "secret", "secretKey", "apiKey", "oauthToken" };
    /// <summary>Ключи-URL: шифруются целиком, если содержат логин:пароль; в маске виден адрес, скрыт только пароль.</summary>
    private static readonly HashSet<string> UrlKeys = new(StringComparer.OrdinalIgnoreCase) { "url", "streamUrl" };

    /// <summary>Шифрует секреты во входящих настройках. Маска «••••••» заменяется значением из <paramref name="storedJson"/> по тому же пути.</summary>
    public string ProtectForStorage(JsonElement incoming, string? storedJson)
    {
        var node = JsonNode.Parse(incoming.GetRawText());
        var stored = storedJson is null ? null : JsonNode.Parse(storedJson);
        Walk(node, stored, (value, old) =>
        {
            if (value == Mask || value.Contains(":" + Mask + "@")) return old is not null && old.StartsWith(Prefix) ? old : "";
            if (value.StartsWith(Prefix) || value.Length == 0) return value;
            return Prefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(value)));
        }, urlTransform: (value, old) =>
        {
            if (value.Contains(":" + Mask + "@")) return old ?? value;             // пароль не менялся — оставляем зашифрованный URL
            if (!HasCredentials(value) || value.StartsWith(Prefix)) return value;  // без учётки шифровать нечего
            return Prefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(value)));
        });
        return node?.ToJsonString() ?? "{}";
    }

    /// <summary>Для дашборда: секреты заменены маской.</summary>
    public JsonElement MaskSecrets(string storedJson)
    {
        var node = JsonNode.Parse(storedJson);
        Walk(node, null, (value, _) => value.StartsWith(Prefix) ? Mask : value,
            urlTransform: (value, _) => value.StartsWith(Prefix) ? MaskUrlPassword(Decrypt(value)) : value);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Для агента: секреты расшифрованы.</summary>
    public JsonElement Reveal(string storedJson)
    {
        var node = JsonNode.Parse(storedJson);
        Walk(node, null, (value, _) => value.StartsWith(Prefix) ? Decrypt(value) : value, urlTransform: (value, _) => value.StartsWith(Prefix) ? Decrypt(value) : value);
        return JsonSerializer.SerializeToElement(node);
    }

    private string Decrypt(string value)
    {
        try { return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(value.Substring(Prefix.Length)))); }
        catch { return ""; }
    }

    private static bool HasCredentials(string url)
    {
        try { return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.UserInfo.Contains(':'); } catch { return false; }
    }

    private static string MaskUrlPassword(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.UserInfo.Contains(':')) return url;
            var user = u.UserInfo.Split(':')[0];
            return $"{u.Scheme}://{user}:{Mask}@{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.PathAndQuery}";
        }
        catch { return url; }
    }

    private static void Walk(JsonNode? node, JsonNode? stored, Func<string, string?, string> transform, Func<string, string?, string>? urlTransform = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    var storedChild = (stored as JsonObject)?[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var str) && SecretKeys.Contains(key))
                        obj[key] = transform(str, storedChild is JsonValue sv && sv.TryGetValue<string>(out var old) ? old : null);
                    else if (child is JsonValue uv && uv.TryGetValue<string>(out var ustr) && UrlKeys.Contains(key) && urlTransform is not null)
                        obj[key] = urlTransform(ustr, storedChild is JsonValue usv && usv.TryGetValue<string>(out var uold) ? uold : null);
                    else
                        Walk(child, storedChild, transform, urlTransform);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) Walk(arr[i], (stored as JsonArray)?.ElementAtOrDefault(i), transform, urlTransform);
                break;
        }
    }
}
