namespace DenPublish.Core.Tests;

public sealed class PublishValidationEngineTests
{
    [Fact]
    public void Validate_RejectsMissingSubmission()
    {
        var decision = Decision();
        IPublishEngine engine = new PublishValidationEngine();

        var result = engine.Validate(decision, submission: null);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.MissingSubmission, failure.Code);
    }

    [Fact]
    public void Validate_RejectsDecisionForDifferentSubmission()
    {
        var submission = ApprovedSubmission();
        var decision = Decision(submissionId: "sub_other");
        IPublishEngine engine = new PublishValidationEngine();

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.InvalidRequest, failure.Code);
    }

    [Fact]
    public void Validate_RejectsHeadMismatch()
    {
        var submission = ApprovedSubmission(headCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var decision = Decision(expectedHeadCommit: Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        IPublishEngine engine = new PublishValidationEngine();

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CodeGateHeadMismatch, failure.Code);
    }

    [Theory]
    [InlineData(CodeSubmissionStatus.Submitted)]
    [InlineData(CodeSubmissionStatus.ReviewRequested)]
    [InlineData(CodeSubmissionStatus.ChangesRequested)]
    [InlineData(CodeSubmissionStatus.Superseded)]
    [InlineData(CodeSubmissionStatus.Rejected)]
    [InlineData(CodeSubmissionStatus.Failed)]
    public void Validate_RejectsSubmissionThatIsNotApprovedForPublish(CodeSubmissionStatus status)
    {
        var submission = ApprovedSubmission(status: status);
        var decision = Decision();
        IPublishEngine engine = new PublishValidationEngine();

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(status == CodeSubmissionStatus.Superseded ? PublishFailureCode.StaleSubmission : PublishFailureCode.ReviewNotApproved, failure.Code);
    }

    [Fact]
    public void Validate_AcceptsMatchingApprovedSubmissionAndValidateOnlyDecision()
    {
        var submission = ApprovedSubmission();
        var decision = Decision(validateOnly: true);
        IPublishEngine engine = new PublishValidationEngine();

        var result = engine.Validate(decision, submission);

        Assert.True(result.IsPublishable);
        Assert.Empty(result.Failures);
        Assert.Contains("submission head matches publish decision", result.Decisions);
        Assert.Contains("validate-only decision accepted without pushing", result.Decisions);
    }

    private static PublishDecision Decision(
        GitSha? expectedHeadCommit = null,
        bool validateOnly = false,
        string submissionId = "sub_1424_001")
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: submissionId,
            RequestedBy: "den-channels-orchestrator",
            Operation: PublishOperation.PushBranch,
            TargetRemote: "canonical",
            TargetBranch: "task/1416-den-channels",
            ExpectedHeadCommit: expectedHeadCommit ?? Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ExpectedBaseBranch: "main",
            ReviewRoundId: 680,
            ScopeOverrideIds: [],
            ValidateOnly: validateOnly,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"));

    private static CodeSubmission ApprovedSubmission(
        GitSha? headCommit = null,
        CodeSubmissionStatus status = CodeSubmissionStatus.Approved)
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
            HeadCommit: headCommit ?? Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            TargetBranch: "task/1416-den-channels",
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: status,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
