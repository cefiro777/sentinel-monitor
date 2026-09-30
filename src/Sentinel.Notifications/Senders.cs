using System.Net.Http.Json;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Sentinel.Server.Core.Entities;

namespace Sentinel.Notifications;

/// <summary>Готовое к отправке сообщение; каналы сами решают, что из него использовать.</summary>
/// <param name="Html">Полноценный HTML для почты.</param>
/// <param name="TelegramHtml">Ограниченная разметка Telegram: только b, i, a, code, pre.</param>
public sealed record NotificationMessage(string Subject, string PlainText, string Html, string TelegramHtml);

public interface INotificationSender
{
    ChannelType Type { get; }
    /// <summary>Отправляет сообщение; исключение — неудача, текст исключения попадёт в лог канала.</summary>
    Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct);
}

public sealed class EmailChannelSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>none | starttls | ssl</summary>
    public string Security { get; set; } = "starttls";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}

public sealed class TelegramChannelSettings
{
    public string BotToken { get; set; } = "";
    /// <summary>ID чатов/групп. Узнать: написать боту и открыть https://api.telegram.org/bot&lt;token&gt;/getUpdates</summary>
    public List<string> ChatIds { get; set; } = new();
}

public sealed class EmailSender : INotificationSender
{
    public ChannelType Type => ChannelType.Email;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var s = JsonSerializer.Deserialize<EmailChannelSettings>(settingsJson, JsonOpts) ?? throw new InvalidOperationException("Нет настроек SMTP.");
        if (s.To.Count == 0) throw new InvalidOperationException("Не указаны получатели.");

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(s.From));
        foreach (var to in s.To) mail.To.Add(MailboxAddress.Parse(to.Trim()));
        mail.Subject = message.Subject;
        mail.Body = new BodyBuilder { TextBody = message.PlainText, HtmlBody = message.Html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var secure = s.Security.ToLowerInvariant() switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(s.Host, s.Port, secure, ct);
        if (!string.IsNullOrEmpty(s.Username)) await client.AuthenticateAsync(s.Username, s.Password ?? "", ct);
        await client.SendAsync(mail, ct);
        await client.DisconnectAsync(true, ct);
    }

    internal static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}

public sealed class TelegramSender(IHttpClientFactory httpFactory) : INotificationSender
{
    public ChannelType Type => ChannelType.Telegram;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var s = JsonSerializer.Deserialize<TelegramChannelSettings>(settingsJson, EmailSender.JsonOpts) ?? throw new InvalidOperationException("Нет настроек Telegram.");
        if (string.IsNullOrWhiteSpace(s.BotToken) || s.ChatIds.Count == 0) throw new InvalidOperationException("Нужны botToken и chatIds.");

        var http = httpFactory.CreateClient("telegram");
        var errors = new List<string>();
        foreach (var chat in s.ChatIds)
        {
            var resp = await http.PostAsJsonAsync($"https://api.telegram.org/bot{s.BotToken}/sendMessage",
                new { chat_id = chat.Trim(), text = message.TelegramHtml, parse_mode = "HTML", disable_web_page_preview = true }, ct);
            if (!resp.IsSuccessStatusCode)
                errors.Add($"{chat}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }
}

public sealed class MaxChannelSettings
{
    /// <summary>Токен бота из @MasterBot в Max.</summary>
    public string Token { get; set; } = "";
    /// <summary>ID чатов/диалогов. Узнать: GET https://botapi.max.ru/chats с заголовком Authorization: токен.</summary>
    public List<string> ChatIds { get; set; } = new();
    public string ApiBase { get; set; } = "https://botapi.max.ru";
}

/// <summary>Мессенджер Max (VK): Bot API, совместимый с TamTam.</summary>
public sealed class MaxSender(IHttpClientFactory httpFactory) : INotificationSender
{
    public ChannelType Type => ChannelType.Max;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var s = JsonSerializer.Deserialize<MaxChannelSettings>(settingsJson, EmailSender.JsonOpts) ?? throw new InvalidOperationException("Нет настроек Max.");
        if (string.IsNullOrWhiteSpace(s.Token) || s.ChatIds.Count == 0) throw new InvalidOperationException("Нужны token и chatIds.");

        var http = httpFactory.CreateClient("max");
        var errors = new List<string>();
        foreach (var chat in s.ChatIds)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{s.ApiBase.TrimEnd('/')}/messages?chat_id={Uri.EscapeDataString(chat.Trim())}");
            req.Headers.TryAddWithoutValidation("Authorization", s.Token.Trim());
            req.Content = JsonContent.Create(new { text = message.TelegramHtml, format = "html" });
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                errors.Add($"{chat}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }
}
