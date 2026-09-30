using System;
using System.Collections.Generic;
using System.Net;

namespace Sentinel.Agent.Infrastructure;

/// <summary>
/// Полный текст сетевой ошибки. HttpRequestException на .NET Framework пишет только «ошибка при отправке запроса»,
/// а причина (TLS, сертификат, отказ соединения, прокси) — во вложенных WebException/SocketException.
/// </summary>
public static class ErrorText
{
    public static string Full(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 6; e = e.InnerException)
        {
            var text = e.Message.Trim().TrimEnd('.');
            if (e is WebException we && we.Status != WebExceptionStatus.UnknownError) text += $" [{we.Status}]";
            if (e is System.Net.Sockets.SocketException se) text += $" [сокет {se.SocketErrorCode}]";
            if (text.Length > 0 && !parts.Contains(text)) parts.Add(text);
        }
        return string.Join(" → ", parts);
    }
}
