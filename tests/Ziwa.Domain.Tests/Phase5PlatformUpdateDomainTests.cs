using Xunit;
using Ziwa.Domain.Platform;

namespace Ziwa.Domain.Tests;

public sealed class Phase5PlatformUpdateDomainTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("v1.2.0-beta.1", "1.1.9", true)]
    public void IsVersionGreater_ComparesSemVerCorrectly(string latest, string current, bool expected)
    {
        Assert.Equal(expected, AppUpdatePolicy.IsVersionGreater(latest, current));
    }

    [Fact]
    public void TryNormalizeSha256Hex_ValidatesLengthAndHexCharacters()
    {
        string validUpper = new('A', 64);
        Assert.True(AppUpdatePolicy.TryNormalizeSha256Hex($"  {validUpper}  ", out string normalized));
        Assert.Equal(new string('a', 64), normalized);

        Assert.False(AppUpdatePolicy.TryNormalizeSha256Hex("abc", out _));
        Assert.False(AppUpdatePolicy.TryNormalizeSha256Hex(new string('z', 64), out _));
        Assert.False(AppUpdatePolicy.TryNormalizeSha256Hex(null, out _));
    }

    [Fact]
    public void ParseChecksumForInstaller_FindsMatchingZiwaArtifact()
    {
        string expected = new('f', 64);
        string other = new('0', 64);
        string text = $"""
            {other}  notes.txt
            {expected}  ZiwaStudio-Setup-1.0.1.exe
            """;
        Assert.Equal(expected, AppUpdatePolicy.ParseChecksumForInstaller(text));
    }

    [Fact]
    public void SensitiveLogRedactor_MasksTokensKeysAndSessions()
    {
        string raw = "Authorization: Bearer eyJhbGci.secret.jwt | GET /cam?token=tok123&apikey=sb_key | https://grido.cloud-ip.cc/cam#session=uid/tok | \"refresh_token\":\"ref999\" | X-Bridge-Token: br777";
        string redacted = SensitiveLogRedactor.Redact(raw);

        Assert.DoesNotContain("eyJhbGci.secret.jwt", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("tok123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sb_key", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("uid/tok", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ref999", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("br777", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupArgumentParser_ExtractsZiwaProjectPathOnly()
    {
        Assert.Equal(@"C:\Work\card.ziwa", StartupArgumentParser.TryExtractZiwaProjectPath(["--minimized", "\"C:\\Work\\card.ziwa\""]));
        Assert.Null(StartupArgumentParser.TryExtractZiwaProjectPath(["--safe-mode", @"C:\Work\photo.png"]));
    }
}
