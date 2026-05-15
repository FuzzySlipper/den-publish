using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class CodeGateRefVerifierTests
{
    [Fact]
    public void Validate_ResolvesImmutableIngressRef()
    {
        var submission = ApprovedSubmission();
        var resolver = new RecordingResolver(CodeGateRefResolution.Found(submission.HeadCommit));
        IPublishEngine engine = new CodeGatePublishValidationEngine(new PublishValidationEngine(), resolver);

        var result = engine.Validate(Decision(), submission);

        Assert.True(result.IsPublishable);
        Assert.Same(submission, resolver.CapturedSubmission);
        Assert.Contains("code-gate immutable ref resolved to reviewed head", result.Decisions);
    }

    [Fact]
    public void Validate_FailsWhenCodeGateRefCannotBeResolved()
    {
        var resolver = new RecordingResolver(CodeGateRefResolution.Failed("ssh denied"));
        IPublishEngine engine = new CodeGatePublishValidationEngine(new PublishValidationEngine(), resolver);

        var result = engine.Validate(Decision(), ApprovedSubmission());

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Failed, result.Status);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CodeGateFetchFailed, failure.Code);
        Assert.Contains("ssh denied", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsWhenResolvedHeadDiffers()
    {
        var resolver = new RecordingResolver(CodeGateRefResolution.Found(Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")));
        IPublishEngine engine = new CodeGatePublishValidationEngine(new PublishValidationEngine(), resolver);

        var result = engine.Validate(Decision(), ApprovedSubmission());

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CodeGateHeadMismatch, failure.Code);
    }

    [Fact]
    public void Validate_SkipsResolveWhenContractRejected()
    {
        var resolver = new RecordingResolver(CodeGateRefResolution.Found(Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        IPublishEngine engine = new CodeGatePublishValidationEngine(new PublishValidationEngine(), resolver);
        var decision = Decision(submissionId: "different-submission");

        var result = engine.Validate(decision, ApprovedSubmission());

        Assert.False(result.IsPublishable);
        Assert.Null(resolver.CapturedSubmission);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.InvalidRequest, failure.Code);
    }

    private sealed class RecordingResolver(CodeGateRefResolution resolution) : ICodeGateRefResolver
    {
        public CodeSubmission? CapturedSubmission { get; private set; }

        public CodeGateRefResolution ResolveHead(CodeSubmission submission)
        {
            CapturedSubmission = submission;
            return resolution;
        }
    }

    private static PublishDecision Decision(string submissionId = "sub_1424_001")
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: submissionId,
            RequestedBy: "den-channels-orchestrator",
            Operation: PublishOperation.PushBranch,
            TargetRemote: "canonical",
            TargetBranch: "task/1416-den-channels",
            ExpectedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ExpectedBaseBranch: "main",
            ReviewRoundId: 680,
            ScopeOverrideIds: [],
            ValidateOnly: true,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"));

    private static CodeSubmission ApprovedSubmission()
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
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Approved,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}

public sealed class GitLsRemoteCodeGateRefResolverTests
{
    [Fact]
    public void ResolveHead_RunsGitLsRemoteAgainstIngressRef()
    {
        var submission = ApprovedSubmission();
        var runner = new RecordingGitCommandRunner(new GitCommandResult(0, $"{submission.HeadCommit.Value}\t{submission.IngressRef}\n", string.Empty));
        var resolver = new GitLsRemoteCodeGateRefResolver(runner);

        var result = resolver.ResolveHead(submission);

        Assert.True(result.Succeeded);
        Assert.Equal(["ls-remote", "--exit-code", submission.CodeGateRemoteUrl, submission.IngressRef], runner.Arguments);
    }

    [Fact]
    public void ResolveHead_ParsesExactRefHead()
    {
        var submission = ApprovedSubmission();
        var runner = new RecordingGitCommandRunner(new GitCommandResult(0, $"{submission.HeadCommit.Value}\t{submission.IngressRef}\n", string.Empty));
        var resolver = new GitLsRemoteCodeGateRefResolver(runner);

        var result = resolver.ResolveHead(submission);

        Assert.True(result.Succeeded);
        Assert.Equal(submission.HeadCommit, result.HeadCommit);
    }

    [Fact]
    public void ResolveHead_FailsWhenGitCommandFails()
    {
        var runner = new RecordingGitCommandRunner(new GitCommandResult(128, string.Empty, "repository not found"));
        var resolver = new GitLsRemoteCodeGateRefResolver(runner);

        var result = resolver.ResolveHead(ApprovedSubmission());

        Assert.False(result.Succeeded);
        Assert.Contains("repository not found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveHead_FailsWhenOutputDoesNotContainExactRef()
    {
        var submission = ApprovedSubmission();
        var runner = new RecordingGitCommandRunner(new GitCommandResult(0, $"{submission.HeadCommit.Value}\t{submission.ConvenienceRef}\n", string.Empty));
        var resolver = new GitLsRemoteCodeGateRefResolver(runner);

        var result = resolver.ResolveHead(submission);

        Assert.False(result.Succeeded);
        Assert.Contains(submission.IngressRef, result.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class RecordingGitCommandRunner(GitCommandResult result) : IGitCommandRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public GitCommandResult Run(IReadOnlyList<string> arguments)
        {
            Arguments = arguments;
            return result;
        }
    }

    private static CodeSubmission ApprovedSubmission()
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
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Approved,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
