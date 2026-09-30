using System.Text;
using System.Text.Json;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;
using Sentinel.Contracts.Security;

namespace Sentinel.Contracts.Tests;

public class AgentAuthTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    [Fact]
    public void Sign_and_verify_roundtrip()
    {
        var canonical = AgentAuth.CanonicalString("post", "/api/agent/results", 1_700_000_000, "nonce", AgentAuth.Sha256Hex([1, 2, 3]));
        var sig = AgentAuth.Sign(Secret, canonical);

        Assert.True(AgentAuth.Verify(Secret, canonical, sig));
        Assert.False(AgentAuth.Verify(Secret, canonical + "x", sig));
        Assert.False(AgentAuth.Verify(Encoding.UTF8.GetBytes("другой секрет...................."), canonical, sig));
        Assert.False(AgentAuth.Verify(Secret, canonical, "not-base64!"));
    }

    [Fact]
    public void Canonical_string_uppercases_method_and_is_stable()
    {
        var a = AgentAuth.CanonicalString("get", "/x", 1, "n", "h");
        var b = AgentAuth.CanonicalString("GET", "/x", 1, "n", "h");
        Assert.Equal(a, b);
        Assert.Equal("GET\n/x\n1\nn\nh", a);
    }

    [Fact]
    public void Sha256Hex_of_empty_matches_known_value()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AgentAuth.Sha256Hex([]));
    }

    [Fact]
    public void Command_canonical_string_ignores_payload_whitespace()
    {
        var id = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var pretty = new CommandEnvelope { Id = id, AgentId = agent, Type = "service.restart", IssuedAt = at, ExpiresAt = at.AddMinutes(5),
            Payload = JsonDocument.Parse("{ \"name\" : \"MSSQLSERVER\" ,\n \"timeoutSeconds\": 60 }").RootElement };
        var compact = new CommandEnvelope { Id = id, AgentId = agent, Type = "service.restart", IssuedAt = at, ExpiresAt = at.AddMinutes(5),
            Payload = JsonDocument.Parse("{\"name\":\"MSSQLSERVER\",\"timeoutSeconds\":60}").RootElement };

        Assert.Equal(pretty.CanonicalString(), compact.CanonicalString());
    }

    [Fact]
    public void Command_survives_json_roundtrip_with_same_canonical_string()
    {
        var cmd = new CommandEnvelope
        {
            Id = Guid.NewGuid(), AgentId = Guid.NewGuid(), Type = "ping",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Payload = JsonDocument.Parse("{\"reason\":\"проверка\"}").RootElement,
        };
        var roundtrip = SentinelJson.Deserialize<CommandEnvelope>(SentinelJson.Serialize(cmd))!;
        Assert.Equal(cmd.CanonicalString(), roundtrip.CanonicalString());
    }

    [Fact]
    public void Enum_serializes_as_camel_case_string()
    {
        var json = SentinelJson.Serialize(new CheckResult { Status = CheckStatus.Critical });
        Assert.Contains("\"status\":\"critical\"", json);
        Assert.Equal(CheckStatus.Critical, SentinelJson.Deserialize<CheckResult>(json)!.Status);
    }
}
