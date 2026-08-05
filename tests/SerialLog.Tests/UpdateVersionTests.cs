using SerialLog.Update;

namespace SerialLog.Tests;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.2.0", 0, 2, 0)]
    public void TryParse_AcceptsApplicationVersions(
        string value,
        int major,
        int minor,
        int patch)
    {
        Assert.True(ReleaseVersion.TryParse(value, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("v0.0.0")]
    public void TryParseTag_AcceptsStableReleaseTags(string value)
    {
        Assert.True(ReleaseVersion.TryParseTag(value, out _));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v01.2.3")]
    public void TryParseTag_RejectsInvalidReleaseTags(string value)
    {
        Assert.False(ReleaseVersion.TryParseTag(value, out _));
    }

    [Fact]
    public void CompareTo_OrdersSemanticVersions()
    {
        Assert.True(new ReleaseVersion(0, 3, 0).CompareTo(new ReleaseVersion(0, 2, 9)) > 0);
        Assert.True(new ReleaseVersion(1, 0, 0).CompareTo(new ReleaseVersion(0, 99, 99)) > 0);
        Assert.Equal(0, new ReleaseVersion(2, 1, 4).CompareTo(new ReleaseVersion(2, 1, 4)));
    }
}
