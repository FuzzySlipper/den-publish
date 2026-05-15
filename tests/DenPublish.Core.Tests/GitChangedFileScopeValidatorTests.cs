using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class GitChangedFileScopeValidatorTests
{
    [Fact]
    public void ValidateScope_RunsGitDiffFromSubmissionBaseToFetchedLocalRef()
    {
        var submission = ApprovedSubmission(changedFiles: ["src/DenChannels/Bridge.cs"]);
        var runner = new RecordingGitCommandRunner(new GitCommandResult(0, "src/DenChannels/Bridge.cs\n", string.Empty));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = validator.ValidateScope(
            submission,
            workspacePath: "/var/lib/den-publish/workspaces/den-channels",
            localRef: "refs/den-publish/submissions/sub_1424_001",
            policy);

        Assert.True(result.IsPublishable);
        Assert.Equal([
            "-C",
            "/var/lib/den-publish/workspaces/den-channels",
            "diff",
            "--name-only",
            "--diff-filter=ACDMRTUXB",
            submission.BaseCommit.Value,
            "refs/den-publish/submissions/sub_1424_001",
            "--"
        ], runner.Commands.Single());
        Assert.Contains("changed files are within configured scope", result.Decisions);
    }

    [Fact]
    public void ValidateScope_AcceptsMultipleChangedFilesUnderAllowedPrefixes()
    {
        var submission = ApprovedSubmission(changedFiles: ["src/DenChannels/Bridge.cs", "tests/DenChannels.Tests/BridgeTests.cs"]);
        var runner = new RecordingGitCommandRunner(new GitCommandResult(
            0,
            "src/DenChannels/Bridge.cs\ntests/DenChannels.Tests/BridgeTests.cs\n",
            string.Empty));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/", "tests/DenChannels.Tests/"]);

        var result = validator.ValidateScope(submission, "/workspace", "refs/den-publish/submissions/sub_1424_001", policy);

        Assert.True(result.IsPublishable);
        Assert.Contains("observed changed files match submission claim", result.Decisions);
    }

    [Fact]
    public void ValidateScope_RejectsChangedFilesOutsideAllowedPrefixes()
    {
        var submission = ApprovedSubmission(changedFiles: ["src/DenChannels/Bridge.cs", "infra/secrets.env"]);
        var runner = new RecordingGitCommandRunner(new GitCommandResult(
            0,
            "src/DenChannels/Bridge.cs\ninfra/secrets.env\n",
            string.Empty));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = validator.ValidateScope(submission, "/workspace", "refs/den-publish/submissions/sub_1424_001", policy);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.ScopeViolation, failure.Code);
        Assert.Contains("infra/secrets.env", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateScope_RejectsWhenObservedDiffDoesNotMatchSubmissionClaim()
    {
        var submission = ApprovedSubmission(changedFiles: ["src/DenChannels/Bridge.cs"]);
        var runner = new RecordingGitCommandRunner(new GitCommandResult(
            0,
            "src/DenChannels/Bridge.cs\nsrc/DenChannels/Hidden.cs\n",
            string.Empty));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = validator.ValidateScope(submission, "/workspace", "refs/den-publish/submissions/sub_1424_001", policy);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.ScopeViolation, failure.Code);
        Assert.Contains("Hidden.cs", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateScope_FailsWhenGitDiffFails()
    {
        var runner = new RecordingGitCommandRunner(new GitCommandResult(128, string.Empty, "bad object cccccccc"));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = validator.ValidateScope(ApprovedSubmission(), "/workspace", "refs/den-publish/submissions/sub_1424_001", policy);

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Failed, result.Status);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.MissingRequiredValidation, failure.Code);
        Assert.Contains("bad object", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateScope_RejectsUnsafeLocalRefBeforeRunningGit()
    {
        var runner = new RecordingGitCommandRunner(new GitCommandResult(0, string.Empty, string.Empty));
        var validator = new GitChangedFileScopeValidator(runner);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = validator.ValidateScope(ApprovedSubmission(), "/workspace", "refs/heads/main;touch /tmp/nope", policy);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.InvalidRequest, failure.Code);
        Assert.Empty(runner.Commands);
    }

    private sealed class RecordingGitCommandRunner(params GitCommandResult[] results) : IGitCommandRunner
    {
        private int _index;

        public List<IReadOnlyList<string>> Commands { get; } = [];

        public GitCommandResult Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments.ToArray());
            return results[_index++];
        }
    }

    private static CodeSubmission ApprovedSubmission(IReadOnlyList<string>? changedFiles = null)
        => new(
            SubmissionId: "sub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            WorkerRunId: "run-20260514-def456",
            SubmittedBy: "den-channels-runner",
            Role: "coder",
            AttemptOrdinal: 1,
            ParentSubmissionId: null,
            CodeGateInstance: "den-code-gate",
            CodeGateRepo: "den-channels.git",
            CodeGateRemoteUrl: "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
            IngressRef: "refs/heads/submissions/den-channels/tasks/1416/runs/run-20260514-def456/attempt-001",
            ConvenienceRef: "refs/heads/submissions/den-channels/tasks/1416/current",
            BaseBranch: "main",
            BaseCommit: Sha("cccccccccccccccccccccccccccccccccccccccc"),
            HeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            TargetBranch: "task/1416-den-channels",
            ChangedFilesClaim: changedFiles ?? ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Approved,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
