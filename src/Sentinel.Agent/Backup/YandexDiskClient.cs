using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sentinel.Agent.Backup;

/// <summary>REST API Яндекс.Диска (cloud-api.yandex.net/v1/disk) по OAuth-токену приложения.</summary>
public sealed class YandexDiskClient : IDisposable
{
    private const string Api = "https://cloud-api.yandex.net/v1/disk";
    private readonly HttpClient _http;

    public YandexDiskClient(string token)
    {
        Infrastructure.ServerClient.ConfigureTls();
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip }) { Timeout = TimeSpan.FromHours(6) };
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("OAuth", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SentinelAgent/" + Infrastructure.AgentPaths.Version);
    }

    public sealed class Quota { public long Total; public long Used; public long Trash; public long Free => Total - Used; }
    public sealed class Item { public string Name = ""; public string Path = ""; public long Size; public string? Md5; public string? Sha256; public DateTimeOffset Modified; public bool IsDir; }

    public async Task<Quota> GetQuotaAsync(CancellationToken ct)
    {
        var j = await GetJsonAsync($"{Api}?fields=total_space,used_space,trash_size", ct);
        return new Quota { Total = j.GetProperty("total_space").GetInt64(), Used = j.GetProperty("used_space").GetInt64(), Trash = j.TryGetProperty("trash_size", out var t) ? t.GetInt64() : 0 };
    }

    /// <summary>Создаёт цепочку папок; существующие пропускает.</summary>
    public async Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        var parts = path.Trim('/').Split('/');
        var current = "";
        foreach (var p in parts)
        {
            current += "/" + p;
            var resp = await _http.PutAsync($"{Api}/resources?path={Uri.EscapeDataString(current)}", null, ct);
            if (resp.StatusCode == HttpStatusCode.Conflict || resp.StatusCode == HttpStatusCode.Created) continue;
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, "mkdir " + current);
        }
    }

    public async Task<List<Item>> ListAsync(string folder, CancellationToken ct)
    {
        var items = new List<Item>();
        var offset = 0;
        while (true)
        {
            var resp = await _http.GetAsync($"{Api}/resources?path={Uri.EscapeDataString(folder)}&limit=200&offset={offset}&fields=_embedded.items.name,_embedded.items.path,_embedded.items.size,_embedded.items.md5,_embedded.items.sha256,_embedded.items.modified,_embedded.items.type,_embedded.total", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return items;
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, "list " + folder);
            var j = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            if (!j.TryGetProperty("_embedded", out var emb)) return items;
            var total = emb.GetProperty("total").GetInt32();
            foreach (var it in emb.GetProperty("items").EnumerateArray())
            {
                items.Add(new Item
                {
                    Name = it.GetProperty("name").GetString() ?? "",
                    Path = it.GetProperty("path").GetString() ?? "",
                    Size = it.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                    Md5 = it.TryGetProperty("md5", out var md5) ? md5.GetString() : null,
                    Sha256 = it.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                    Modified = it.TryGetProperty("modified", out var m) && m.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(m.GetString()!) : default,
                    IsDir = it.GetProperty("type").GetString() == "dir",
                });
            }
            offset += 200;
            if (offset >= total) return items;
        }
    }

    public async Task<Item?> GetItemAsync(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"{Api}/resources?path={Uri.EscapeDataString(path)}&fields=name,path,size,md5,sha256,modified,type", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, "stat " + path);
        var it = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return new Item
        {
            Name = it.GetProperty("name").GetString() ?? "", Path = path,
            Size = it.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            Md5 = it.TryGetProperty("md5", out var md5) ? md5.GetString() : null,
            Sha256 = it.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
            IsDir = it.GetProperty("type").GetString() == "dir",
        };
    }

    /// <summary>Загрузка файла: получаем upload-ссылку и делаем PUT потоком (без буферизации в памяти).</summary>
    public async Task UploadAsync(string localFile, string remotePath, CancellationToken ct)
    {
        var j = await GetJsonAsync($"{Api}/resources/upload?path={Uri.EscapeDataString(remotePath)}&overwrite=true", ct);
        var href = j.GetProperty("href").GetString()!;
        using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
        using (var content = new StreamContent(fs, 1 << 20))
        {
            content.Headers.ContentLength = fs.Length;
            using (var resp = await _http.PutAsync(href, content, ct))
            {
                if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, "upload " + remotePath);
            }
        }
    }

    /// <summary>Удаление в корзину (permanently=false) — есть шанс восстановить.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var resp = await _http.DeleteAsync($"{Api}/resources?path={Uri.EscapeDataString(path)}&permanently=false", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, "delete " + path);
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, url.Substring(Api.Length));
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<Exception> ErrorAsync(HttpResponseMessage resp, string op)
    {
        var body = await resp.Content.ReadAsStringAsync();
        string msg = body;
        try { var j = JsonDocument.Parse(body).RootElement; msg = j.TryGetProperty("message", out var m) ? m.GetString() ?? body : body; } catch { }
        if (resp.StatusCode == HttpStatusCode.Unauthorized) msg = "токен недействителен или отозван";
        if ((int)resp.StatusCode == 507) msg = "на Диске нет места";
        return new IOException($"Яндекс.Диск ({op}): {(int)resp.StatusCode} {msg.Substring(0, Math.Min(msg.Length, 300))}");
    }

    public void Dispose() => _http.Dispose();
}
