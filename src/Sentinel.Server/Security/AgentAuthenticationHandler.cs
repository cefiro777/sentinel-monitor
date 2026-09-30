using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sentinel.Contracts.Protocol;
using Sentinel.Contracts.Security;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Data;

namespace Sentinel.Server.Security;

/// <summary>
/// Схема "Agent": HMAC-подпись запроса секретом агента.
/// REST: заголовки X-Agent-Id / X-Timestamp / X-Nonce / X-Signature, подпись включает SHA-256 тела.
/// SignalR: query access_token = "{agentId}.{timestamp}.{nonce}.{signature}", подпись над CONNECT + путь хаба.
/// </summary>
public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMemoryCache cache,
    ISecretProtector protector,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Agent";
    public const string ClaimAgentId = "agent_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var path = Request.Path.Value ?? "/";
        var isHub = path.StartsWith(AgentHubContract.Path, StringComparison.OrdinalIgnoreCase);

        if (isHub)
        {
            // WebSocket-транспорт передаёт токен в query, SSE/long-polling и negotiate — в заголовке Authorization.
            if (Request.Query.TryGetValue("access_token", out var accessToken))
                return await AuthenticateHubTokenAsync(accessToken.ToString());
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return await AuthenticateHubTokenAsync(auth.Substring(7).Trim());
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue(AgentAuth.HeaderAgentId, out var agentIdRaw))
            return AuthenticateResult.NoResult();

        if (!Guid.TryParse(agentIdRaw, out var agentId)) return AuthenticateResult.Fail("Некорректный X-Agent-Id.");
        if (!long.TryParse(Request.Headers[AgentAuth.HeaderTimestamp], out var ts)) return AuthenticateResult.Fail("Нет X-Timestamp.");
        var nonce = Request.Headers[AgentAuth.HeaderNonce].ToString();
        var signature = Request.Headers[AgentAuth.HeaderSignature].ToString();
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signature)) return AuthenticateResult.Fail("Нет подписи.");

        if (!IsFresh(ts)) return AuthenticateResult.Fail("Время запроса вне допустимого окна.");
        if (!cache.TryGetValue(NonceKey(agentId, nonce), out _))
            cache.Set(NonceKey(agentId, nonce), true, AgentAuth.MaxClockSkew * 2);
        else
            return AuthenticateResult.Fail("Повтор nonce.");

        var secret = await GetSecretAsync(agentId);
        if (secret is null) return AuthenticateResult.Fail("Агент не найден.");

        Request.EnableBuffering();
        string bodyHash;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms);
            bodyHash = AgentAuth.Sha256Hex(ms.ToArray());
        }
        Request.Body.Position = 0;

        var canonical = AgentAuth.CanonicalString(Request.Method, path, ts, nonce, bodyHash);
        if (!AgentAuth.Verify(secret, canonical, signature)) return AuthenticateResult.Fail("Неверная подпись.");

        return AuthenticateResult.Success(Ticket(agentId));
    }

    private async Task<AuthenticateResult> AuthenticateHubTokenAsync(string token)
    {
        var parts = token.Split('.', 4);
        if (parts.Length != 4 || !Guid.TryParse(parts[0], out var agentId) || !long.TryParse(parts[1], out var ts))
            return AuthenticateResult.Fail("Некорректный токен хаба.");
        if (!IsFresh(ts)) return AuthenticateResult.Fail("Токен хаба устарел.");

        var secret = await GetSecretAsync(agentId);
        if (secret is null) return AuthenticateResult.Fail("Агент не найден.");

        // Long-polling переиспользует токен на каждом запросе, поэтому nonce здесь не учитывается — только окно времени.
        var canonical = AgentAuth.CanonicalString("CONNECT", AgentHubContract.Path, ts, parts[2], AgentAuth.Sha256Hex([]));
        if (!AgentAuth.Verify(secret, canonical, parts[3])) return AuthenticateResult.Fail("Неверная подпись токена хаба.");

        return AuthenticateResult.Success(Ticket(agentId));
    }

    private async Task<byte[]?> GetSecretAsync(Guid agentId)
    {
        return await cache.GetOrCreateAsync($"agent-secret:{agentId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            var enc = await db.Agents.Where(a => a.Id == agentId).Select(a => a.SecretEncrypted).FirstOrDefaultAsync();
            return enc is null ? null : protector.Unprotect(enc);
        });
    }

    private static bool IsFresh(long unixTime)
    {
        var delta = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTime);
        return delta.Duration() <= AgentAuth.MaxClockSkew;
    }

    private static string NonceKey(Guid agentId, string nonce) => $"agent-nonce:{agentId}:{nonce}";

    private AuthenticationTicket Ticket(Guid agentId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimAgentId, agentId.ToString())], Scheme);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme);
    }
}

public static class AgentPrincipalExtensions
{
    public static Guid GetAgentId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(AgentAuthenticationHandler.ClaimAgentId) ?? throw new UnauthorizedAccessException());
}
