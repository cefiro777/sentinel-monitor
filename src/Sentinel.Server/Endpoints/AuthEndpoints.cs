using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Core.Security;
using Sentinel.Server.Data;
using Sentinel.Server.Security;

namespace Sentinel.Server.Endpoints;

public static class AuthEndpoints
{
    public const string UserPolicy = "UserOnly";
    public const string OperatorPolicy = "Operator";
    public const string AdminPolicy = "Admin";
    /// <summary>Инженер/админ с подтверждённым вторым фактором — для удалённых действий.</summary>
    public const string MfaOperatorPolicy = "MfaOperator";

    public sealed record LoginRequest(string Email, string Password);
    /// <summary>Либо token (вход завершён), либо totpChallenge (нужен код).</summary>
    public sealed record LoginResponse(string? Token, DateTimeOffset? ExpiresAt, UserDto? User, string? TotpChallenge);
    public sealed record TotpVerifyRequest(string Challenge, string Code);
    public sealed record TotpCodeRequest(string Code);
    public sealed record TotpSetupResponse(string Secret, string OtpauthUrl);
    public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, Guid? TenantId, bool IsActive, bool TotpEnabled, bool Mfa);
    public sealed record CreateUserRequest(string Email, string DisplayName, string Password, UserRole Role, Guid? TenantId);
    public sealed record AuditDto(long Id, DateTimeOffset At, string? UserName, Guid? AgentId, string? TenantName, string Action, string? TargetType, Guid? TargetId, string? Details, string? RemoteIp);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");
        g.MapPost("/login", Login).AllowAnonymous();
        g.MapPost("/totp/verify", TotpVerify).AllowAnonymous();
        g.MapGet("/me", Me).RequireAuthorization(UserPolicy);
        g.MapPost("/totp/setup", TotpSetup).RequireAuthorization(UserPolicy);
        g.MapPost("/totp/enable", TotpEnable).RequireAuthorization(UserPolicy);
        g.MapPost("/totp/disable", TotpDisable).RequireAuthorization(UserPolicy);

        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(AdminPolicy);
        users.MapGet("/", (SentinelDbContext db) => db.Users.OrderBy(u => u.Email).Select(u => ToDto(u, false)).ToListAsync());
        users.MapPost("/", CreateUser);
        users.MapPost("/{id:guid}/deactivate", async (Guid id, SentinelDbContext db) =>
        {
            var n = await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
        users.MapPost("/{id:guid}/totp/reset", TotpReset);

        app.MapGet("/api/audit", ListAudit).WithTags("Audit").RequireAuthorization(AdminPolicy);
        return app;
    }

    // ---- вход ----

    private static async Task<IResult> Login(LoginRequest req, SentinelDbContext db, IPasswordHasher hasher, JwtTokenService jwt, HttpContext http, ILogger<LoginRequest> log)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLowerInvariant());
        if (user is null || !user.IsActive || !hasher.Verify(req.Password, user.PasswordHash))
        {
            log.LogWarning("Неудачный вход {Email} с {Ip}", req.Email, http.Connection.RemoteIpAddress);
            await Task.Delay(Random.Shared.Next(200, 600)); // сглаживаем тайминг для перебора
            return Results.Unauthorized();
        }

        if (user.TotpEnabled)
            return Results.Ok(new LoginResponse(null, null, null, jwt.IssueTotpChallenge(user)));

        return Results.Ok(await CompleteLoginAsync(user, mfa: false, db, jwt, http));
    }

    private static async Task<IResult> TotpVerify(TotpVerifyRequest req, SentinelDbContext db, ISecretProtector protector, JwtTokenService jwt, IMemoryCache cache, HttpContext http, ILogger<TotpVerifyRequest> log)
    {
        var userId = jwt.ValidateTotpChallenge(req.Challenge);
        var user = userId is null ? null : await db.Users.FindAsync(userId);
        if (user is null || !user.IsActive || !user.TotpEnabled || user.TotpSecretEncrypted is null) return Results.Unauthorized();

        if (!VerifyCode(user, req.Code, protector, cache))
        {
            log.LogWarning("Неверный код TOTP для {Email} с {Ip}", user.Email, http.Connection.RemoteIpAddress);
            await Task.Delay(Random.Shared.Next(300, 800));
            return Results.Unauthorized();
        }
        return Results.Ok(await CompleteLoginAsync(user, mfa: true, db, jwt, http));
    }

    private static async Task<LoginResponse> CompleteLoginAsync(User user, bool mfa, SentinelDbContext db, JwtTokenService jwt, HttpContext http)
    {
        user.LastLoginAt = DateTimeOffset.UtcNow;
        db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = user.Id, Action = mfa ? "user.login.mfa" : "user.login", RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
        var (token, expires) = jwt.Issue(user, mfa);
        return new LoginResponse(token, expires, ToDto(user, mfa), null);
    }

    private static async Task<IResult> Me(HttpContext http, SentinelDbContext db)
    {
        var u = await db.Users.FindAsync(http.User.GetUserId());
        return u is null ? Results.Unauthorized() : Results.Ok(ToDto(u, http.User.HasMfa()));
    }

    // ---- TOTP ----

    private static async Task<IResult> TotpSetup(HttpContext http, SentinelDbContext db, ISecretProtector protector)
    {
        var user = await db.Users.FindAsync(http.User.GetUserId());
        if (user is null) return Results.Unauthorized();
        if (user.TotpEnabled) return Results.BadRequest(new { error = "2FA уже включена. Сначала отключите её." });

        var secret = Totp.GenerateSecret();
        user.TotpSecretEncrypted = protector.Protect(secret);
        await db.SaveChangesAsync();
        return Results.Ok(new TotpSetupResponse(Totp.Base32Encode(secret), Totp.BuildUri(secret, user.Email)));
    }

    private static async Task<IResult> TotpEnable(TotpCodeRequest req, HttpContext http, SentinelDbContext db, ISecretProtector protector, JwtTokenService jwt, IMemoryCache cache)
    {
        var user = await db.Users.FindAsync(http.User.GetUserId());
        if (user is null) return Results.Unauthorized();
        if (user.TotpSecretEncrypted is null) return Results.BadRequest(new { error = "Сначала вызовите setup." });
        if (!VerifyCode(user, req.Code, protector, cache)) return Results.BadRequest(new { error = "Неверный код. Проверьте время на телефоне." });

        user.TotpEnabledAt = DateTimeOffset.UtcNow;
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = user.Id, Action = "user.totp.enable", RemoteIp = http.Connection.RemoteIpAddress?.ToString() });
        await db.SaveChangesAsync();
        // Текущая сессия сразу становится подтверждённой — перелогиниваться не нужно.
        var (token, expires) = jwt.Issue(user, mfa: true);
        return Results.Ok(new LoginResponse(token, expires, ToDto(user, true), null));
    }

    private static async Task<IResult> TotpDisable(TotpCodeRequest req, HttpContext http, SentinelDbContext db, ISecretProtector protector, JwtTokenService jwt, IMemoryCache cache)
    {
        var user = await db.Users.FindAsync(http.User.GetUserId());
        if (user is null) return Results.Unauthorized();
        if (!user.TotpEnabled) return Results.BadRequest(new { error = "2FA не включена." });
        if (!VerifyCode(user, req.Code, protector, cache)) return Results.BadRequest(new { error = "Неверный код." });

        user.TotpSecretEncrypted = null;
        user.TotpEnabledAt = null;
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = user.Id, Action = "user.totp.disable", RemoteIp = http.Connection.RemoteIpAddress?.ToString() });
        await db.SaveChangesAsync();
        var (token, expires) = jwt.Issue(user, mfa: false);
        return Results.Ok(new LoginResponse(token, expires, ToDto(user, false), null));
    }

    /// <summary>Админ сбрасывает 2FA пользователю, потерявшему телефон.</summary>
    private static async Task<IResult> TotpReset(Guid id, HttpContext http, SentinelDbContext db)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound();
        user.TotpSecretEncrypted = null;
        user.TotpEnabledAt = null;
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), Action = "user.totp.reset", TargetType = nameof(User), TargetId = id, RemoteIp = http.Connection.RemoteIpAddress?.ToString() });
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>Проверка кода с запретом повторного использования того же временного шага.</summary>
    private static bool VerifyCode(User user, string code, ISecretProtector protector, IMemoryCache cache)
    {
        var secret = protector.Unprotect(user.TotpSecretEncrypted!);
        var step = Totp.Verify(secret, code);
        if (step is null) return false;
        var key = $"totp-used:{user.Id}:{step}";
        if (cache.TryGetValue(key, out _)) return false;
        cache.Set(key, true, TimeSpan.FromMinutes(3));
        return true;
    }

    // ---- пользователи ----

    private static async Task<IResult> CreateUser(CreateUserRequest req, SentinelDbContext db, IPasswordHasher hasher, HttpContext http)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { error = "Пользователь уже существует." });
        if (req.Password.Length < 10) return Results.BadRequest(new { error = "Пароль не короче 10 символов." });
        if (req.Role == UserRole.ClientViewer && req.TenantId is null) return Results.BadRequest(new { error = "Для ClientViewer нужен tenantId." });

        var user = new User
        {
            Id = Guid.NewGuid(), Email = email, DisplayName = req.DisplayName, PasswordHash = hasher.Hash(req.Password),
            Role = req.Role, TenantId = req.Role == UserRole.ClientViewer ? req.TenantId : null, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), Action = "user.create", TargetType = nameof(User), TargetId = user.Id });
        await db.SaveChangesAsync();
        return Results.Created($"/api/users/{user.Id}", ToDto(user, false));
    }

    // ---- аудит ----

    private static async Task<IResult> ListAudit(SentinelDbContext db, int? take, Guid? tenantId, string? action)
    {
        var q = db.AuditLog.AsNoTracking();
        if (tenantId is not null) q = q.Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        var list = await q.OrderByDescending(a => a.At).Take(Math.Clamp(take ?? 200, 1, 2000))
            .Select(a => new AuditDto(a.Id, a.At,
                a.UserId == null ? null : db.Users.Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                a.AgentId,
                a.TenantId == null ? null : db.Tenants.Where(t => t.Id == a.TenantId).Select(t => t.Name).FirstOrDefault(),
                a.Action, a.TargetType, a.TargetId, a.DetailsJson, a.RemoteIp))
            .ToListAsync();
        return Results.Ok(list);
    }

    private static UserDto ToDto(User u, bool mfa) => new(u.Id, u.Email, u.DisplayName, u.Role, u.TenantId, u.IsActive, u.TotpEnabled, mfa);
}
