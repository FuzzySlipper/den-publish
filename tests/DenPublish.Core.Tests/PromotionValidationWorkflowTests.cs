using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PromotionValidationWorkflowTests
{
    [Fact]
    public void Validate_ComposesPreflightFetchScopeAndAncestry()
    {
        var submission = ApprovedSubmission();
        var decision = Decision();
        var preflight = new RecordingPublishEngine(PublishValidationResult.Approved("preflight ok", ["preflight passed"]));
        var fetcher = new RecordingSubmissionFetcher(SubmissionFetchResult.Fetched(
            "refs/den-publish/submissions/sub_1424_001",
            submission.HeadCommit));
        var scope = new RecordingScopeValidator(PublishValidationResult.Approved("scope ok", ["scope passed"]));
        var ancestry = new RecordingAncestryValidator(PublishValidationResult.Approved("ancestry ok", ["ancestry passed"]));
        var workflow = new PromotionValidationWorkflow(preflight, fetcher, scope, ancestry);
        var policy = new ChangedFileScopePolicy(AllowedPathPrefixes: ["src/DenChannels/"]);

        var result = workflow.Validate(new PromotionValidationRequest(decision, submission, "/workspace", policy));

        Assert.True(result.IsPublishable);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", result.LocalRef);
        Assert.Equal(submission.HeadCommit, result.FetchedHeadCommit);
        Assert.Same(decision, preflight.CapturedDecision);
        Assert.Same(submission, preflight.CapturedSubmission);
        Assert.Same(submission, fetcher.CapturedSubmission);
        Assert.Equal("/workspace", fetcher.CapturedWorkspacePath);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", scope.CapturedLocalRef);
        Assert.Same(policy, scope.CapturedPolicy);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", ancestry.CapturedLocalRef);
        Assert.Equal(["preflight passed", "submission fetched into managed local ref", "scope passed", "ancestry passed"], result.Validation.Decisions);
    }

    [Fact]
    public void Validate_StopsBeforeFetchWhenPreflightRejects()
    {
        var preflight = new RecordingPublishEngine(PublishValidationResult.Rejected(
            "preflight rejected",
            new ValidationFailure(PublishFailureCode.ReviewNotApproved, "review missing")));
        var fetcher = new RecordingSubmissionFetcher(SubmissionFetchResult.Fetched("refs/den-publish/submissions/sub_1424_001", Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var workflow = new PromotionValidationWorkflow(
            preflight,
            fetcher,
            new RecordingScopeValidator(PublishValidationResult.Approved("scope ok")),
            new RecordingAncestryValidator(PublishValidationResult.Approved("ancestry ok")));

        var result = workflow.Validate(new PromotionValidationRequest(Decision(), ApprovedSubmission(), "/workspace", Policy()));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishFailureCode.ReviewNotApproved, Assert.Single(result.Validation.Failures).Code);
        Assert.Null(result.LocalRef);
        Assert.Null(fetcher.CapturedSubmission);
    }

    [Fact]
    public void Validate_StopsBeforeScopeWhenFetchFails()
    {
        var scope = new RecordingScopeValidator(PublishValidationResult.Approved("scope ok"));
        var workflow = new PromotionValidationWorkflow(
            new RecordingPublishEngine(PublishValidationResult.Approved("preflight ok", ["preflight passed"])),
            new RecordingSubmissionFetcher(SubmissionFetchResult.Failed(
                "refs/den-publish/submissions/sub_1424_001",
                PublishFailureCode.CodeGateFetchFailed,
                "fetch denied")),
            scope,
            new RecordingAncestryValidator(PublishValidationResult.Approved("ancestry ok")));

        var result = workflow.Validate(new PromotionValidationRequest(Decision(), ApprovedSubmission(), "/workspace", Policy()));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Failed, result.Validation.Status);
        Assert.Equal(PublishFailureCode.CodeGateFetchFailed, Assert.Single(result.Validation.Failures).Code);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", result.LocalRef);
        Assert.Null(scope.CapturedSubmission);
    }

    [Fact]
    public void Validate_StopsBeforeAncestryWhenScopeRejects()
    {
        var ancestry = new RecordingAncestryValidator(PublishValidationResult.Approved("ancestry ok"));
        var workflow = new PromotionValidationWorkflow(
            new RecordingPublishEngine(PublishValidationResult.Approved("preflight ok", ["preflight passed"])),
            new RecordingSubmissionFetcher(SubmissionFetchResult.Fetched(
                "refs/den-publish/submissions/sub_1424_001",
                Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))),
            new RecordingScopeValidator(PublishValidationResult.Rejected(
                "scope rejected",
                new ValidationFailure(PublishFailureCode.ScopeViolation, "outside scope"))),
            ancestry);

        var result = workflow.Validate(new PromotionValidationRequest(Decision(), ApprovedSubmission(), "/workspace", Policy()));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishFailureCode.ScopeViolation, Assert.Single(result.Validation.Failures).Code);
        Assert.Null(ancestry.CapturedSubmission);
    }

    private sealed class RecordingPublishEngine(PublishValidationResult result) : IPublishEngine
    {
        public PublishDecision? CapturedDecision { get; private set; }
        public CodeSubmission? CapturedSubmission { get; private set; }

        public PublishValidationResult Validate(PublishDecision decision, CodeSubmission? submission)
        {
            CapturedDecision = decision;
            CapturedSubmission = submission;
            return result;
        }
    }

    private sealed class RecordingSubmissionFetcher(SubmissionFetchResult result) : ISubmissionFetcher
    {
        public CodeSubmission? CapturedSubmission { get; private set; }
        public string? CapturedWorkspacePath { get; private set; }

        public SubmissionFetchResult Fetch(CodeSubmission submission, string workspacePath)
        {
            CapturedSubmission = submission;
            CapturedWorkspacePath = workspacePath;
            return result;
        }
    }

    private sealed class RecordingScopeValidator(PublishValidationResult result) : IChangedFileScopeValidator
    {
        public CodeSubmission? CapturedSubmission { get; private set; }
        public string? CapturedWorkspacePath { get; private set; }
        public string? CapturedLocalRef { get; private set; }
        public ChangedFileScopePolicy? CapturedPolicy { get; private set; }

        public PublishValidationResult ValidateScope(CodeSubmission submission, string workspacePath, string localRef, ChangedFileScopePolicy policy)
        {
            CapturedSubmission = submission;
            CapturedWorkspacePath = workspacePath;
            CapturedLocalRef = localRef;
            CapturedPolicy = policy;
            return result;
        }
    }

    private sealed class RecordingAncestryValidator(PublishValidationResult result) : ISubmissionAncestryValidator
    {
        public CodeSubmission? CapturedSubmission { get; private set; }
        public string? CapturedWorkspacePath { get; private set; }
        public string? CapturedLocalRef { get; private set; }

        public PublishValidationResult ValidateAncestry(CodeSubmission submission, string workspacePath, string localRef)
        {
            CapturedSubmission = submission;
            CapturedWorkspacePath = workspacePath;
            CapturedLocalRef = localRef;
            return result;
        }
    }

    private static ChangedFileScopePolicy Policy()
        => new(AllowedPathPrefixes: ["src/DenChannels/"]);

    private static PublishDecision Decision()
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1424_001",
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
