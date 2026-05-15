using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class SubmissionRefBuilderTests
{
    [Fact]
    public void BuildImmutableRef_UsesExpectedSubmissionNamespace()
    {
        var result = SubmissionRefBuilder.BuildImmutableRef(
            projectId: "den-channels",
            taskId: 1416,
            runId: "run-20260514-abc123",
            attemptOrdinal: 1);

        Assert.Equal("refs/heads/submissions/den-channels/tasks/1416/runs/run-20260514-abc123/attempt-001", result);
    }

    [Theory]
    [InlineData("../den-core")]
    [InlineData("den core")]
    [InlineData("den$core")]
    [InlineData("/den-core")]
    [InlineData("den-core/")]
    public void BuildImmutableRef_RejectsUnsafeProjectTokens(string projectId)
    {
        var error = Assert.Throws<ArgumentException>(() => SubmissionRefBuilder.BuildImmutableRef(projectId, 1, "run-1", 1));
        Assert.Contains("projectId", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildImmutableRef_RejectsInvalidAttemptOrdinal(int attemptOrdinal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SubmissionRefBuilder.BuildImmutableRef("den-core", 1, "run-1", attemptOrdinal));
    }

    [Fact]
    public void BuildCurrentRef_UsesConvenienceNamespace()
    {
        var result = SubmissionRefBuilder.BuildCurrentRef("den-core", 1424);

        Assert.Equal("refs/heads/submissions/den-core/tasks/1424/current", result);
    }
}
