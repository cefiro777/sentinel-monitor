using System.Text;
using Sentinel.Server.Core.Security;

namespace Sentinel.Server.Tests;

public class TotpTests
{
    // RFC 6238, приложение B: секрет "12345678901234567890", SHA1, 8 цифр — берём младшие 6.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_rfc6238_vectors(long unixTime, string expected6)
    {
        Assert.Equal(expected6, Totp.ComputeCode(RfcSecret, unixTime / Totp.StepSeconds));
    }

    [Fact]
    public void Verify_accepts_adjacent_steps_and_rejects_far_ones()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.CurrentStep(now);
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step), 1, now));
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 1), 1, now));
        Assert.Null(Totp.Verify(RfcSecret, Totp.ComputeCode(RfcSecret, step - 5), 1, now));
        Assert.Null(Totp.Verify(RfcSecret, "12345", 1, now));
        Assert.Null(Totp.Verify(RfcSecret, "abcdef", 1, now));
    }

    [Fact]
    public void Base32_and_uri_are_well_formed()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32Encode(RfcSecret));
        var uri = Totp.BuildUri(RfcSecret, "admin@local");
        Assert.StartsWith("otpauth://totp/Sentinel:admin%40local?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Sentinel", uri);
    }
}
