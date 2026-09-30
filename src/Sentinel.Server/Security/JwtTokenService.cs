using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Options;

namespace Sentinel.Server.Security;

public sealed class JwtTokenService(KeyStore keys, IOptions<SentinelOptions> options)
{
    public const string ClaimRole = "role";
    public const string ClaimTenant = "tenant";
    /// <summary>"true", если сессия подтверждена вторым фактором.</summary>
    public const string ClaimMfa = "mfa";
    /// <summary>Промежуточный токен между паролем и кодом TOTP.</summary>
    public const string ClaimPurpose = "purpose";
    public const string PurposeTotpChallenge = "totp";

    public SymmetricSecurityKey SecurityKey { get; } = new(keys.JwtKey);

    public (string token, DateTimeOffset expires) Issue(User user, bool mfa)
    {
        var jwt = options.Value.Jwt;
        var expires = DateTimeOffset.UtcNow.AddMinutes(jwt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(ClaimRole, user.Role.ToString()),
            new(ClaimMfa, mfa ? "true" : "false"),
        };
        if (user.TenantId is { } tenantId) claims.Add(new Claim(ClaimTenant, tenantId.ToString()));
        return Write(claims, expires, jwt);
    }

    /// <summary>Короткоживущий токен «пароль принят, ждём код TOTP». Ничего, кроме /auth/totp/verify, с ним сделать нельзя.</summary>
    public string IssueTotpChallenge(User user)
    {
        var jwt = options.Value.Jwt;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimPurpose, PurposeTotpChallenge),
        };
        return Write(claims, DateTimeOffset.UtcNow.AddMinutes(5), jwt).token;
    }

    public Guid? ValidateTotpChallenge(string token)
    {
        try
        {
            var jwt = options.Value.Jwt;
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), IssuerSigningKey = SecurityKey,
            }, out _);
            if (principal.FindFirstValue(ClaimPurpose) != PurposeTotpChallenge) return null;
            return Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;
        }
        catch { return null; }
    }

    private (string token, DateTimeOffset expires) Write(List<Claim> claims, DateTimeOffset expires, JwtOptions jwt)
    {

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SecurityKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class UserPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new UnauthorizedAccessException());

    public static UserRole GetRole(this ClaimsPrincipal user)
        => Enum.TryParse<UserRole>(user.FindFirstValue(JwtTokenService.ClaimRole), out var r) ? r : UserRole.ReadOnly;

    public static Guid? GetTenantScope(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(JwtTokenService.ClaimTenant), out var t) ? t : null;

    public static bool CanOperate(this ClaimsPrincipal user) => user.GetRole() is UserRole.Admin or UserRole.Engineer;
    public static bool HasMfa(this ClaimsPrincipal user) => user.FindFirstValue(JwtTokenService.ClaimMfa) == "true";
    public static bool IsAdmin(this ClaimsPrincipal user) => user.GetRole() is UserRole.Admin;
}
