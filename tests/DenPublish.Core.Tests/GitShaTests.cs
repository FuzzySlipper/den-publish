using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class GitShaTests
{
    [Fact]
    public void TryCreate_AcceptsFullFortyCharacterSha()
    {
        var ok = GitSha.TryCreate("2e9ef65b3ec07b0f63b16760f6b36ce352112c57", out var sha);

        Assert.True(ok);
        Assert.Equal("2e9ef65b3ec07b0f63b16760f6b36ce352112c57", sha.Value);
    }

    [Theory]
    [InlineData("2e9ef65")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("")]
    public void TryCreate_RejectsInvalidSha(string value)
    {
        var ok = GitSha.TryCreate(value, out var sha);

        Assert.False(ok);
        Assert.Equal(string.Empty, sha.Value);
    }
}
