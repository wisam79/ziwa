using Xunit;
using Ziwa.Domain.Bridge;

namespace Ziwa.Domain.Tests;

public sealed class PhoneBridgeDomainTests
{
    [Fact]
    public void FormatUrls_UseExpectedLocalAndGridoCloudFormats()
    {
        string localUrl = PhoneBridgeUrlPolicy.FormatLocalPairingUrl("192.168.1.42", 8741, "abcdef1234");
        Assert.Equal("http://192.168.1.42:8741/cam?token=abcdef1234", localUrl);

        string cloudUrl = PhoneBridgeUrlPolicy.FormatCloudPairingUrl(
            null,
            "11111111-2222-4333-8444-555555555555",
            "abcdef1234");
        Assert.Equal("https://grido.cloud-ip.cc/cam#session=11111111-2222-4333-8444-555555555555/abcdef1234", cloudUrl);

        Assert.Equal(string.Empty, PhoneBridgeUrlPolicy.FormatCloudPairingUrl(null, null, "abcdef"));
    }

    [Theory]
    [InlineData("uid-1/tok-1/photo.jpg", "uid-1/tok-1", true)]
    [InlineData("uid-1/tok-1/sub/shot.png", "uid-1/tok-1", true)]
    [InlineData("uid-1/tok-1/", "uid-1/tok-1", false)]
    [InlineData("uid-1/tok-2/photo.jpg", "uid-1/tok-1", false)]
    [InlineData("../other/photo.jpg", "uid-1/tok-1", false)]
    [InlineData("uid-1/tok-1/../secret.jpg", "uid-1/tok-1", false)]
    [InlineData("/uid-1/tok-1/photo.jpg", "uid-1/tok-1", false)]
    [InlineData("uid-1\\tok-1\\photo.jpg", "uid-1/tok-1", false)]
    public void IsSafeCloudObjectPath_RejectsTraversalAndForeignPrefixes(string path, string prefix, bool expected)
    {
        Assert.Equal(expected, PhoneBridgeUrlPolicy.IsSafeCloudObjectPath(path, prefix));
    }

    [Fact]
    public void FixedTimeEquals_ValidatesExactTokensOnly()
    {
        Assert.True(PhoneBridgeTokenValidator.FixedTimeEquals("token-abc-123", "token-abc-123"));
        Assert.False(PhoneBridgeTokenValidator.FixedTimeEquals("token-abc-124", "token-abc-123"));
        Assert.False(PhoneBridgeTokenValidator.FixedTimeEquals("token-abc", "token-abc-123"));
        Assert.False(PhoneBridgeTokenValidator.FixedTimeEquals(null, "token-abc-123"));
        Assert.False(PhoneBridgeTokenValidator.FixedTimeEquals("", ""));
    }
}
