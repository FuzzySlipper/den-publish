using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PublishPolicyValidationEngineTests
{
    [Fact]
    public void Validate_RejectsUnknownTargetRemote()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var decision = Decision(targetRemote: "backup");

        var result = engine.Validate(decision, ApprovedSubmission());

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CanonicalRemoteMismatch, failure.Code);
    }

    [Fact]
    public void Validate_RejectsCanonicalRemoteMismatch()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var submission = ApprovedSubmission() with { CanonicalRemoteUrl = "git@github.com:FuzzySlipper/other.git" };

        var result = engine.Validate(Decision(), submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CanonicalRemoteMismatch, failure.Code);
    }

    [Fact]
    public void Validate_RejectsUnsafeTargetBranch()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var decision = Decision(targetBranch: "../main");
        var submission = ApprovedSubmission() with { TargetBranch = "../main" };

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.InvalidRequest, failure.Code);
    }

    [Fact]
    public void Validate_RejectsDisallowedPushBranch()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var decision = Decision(targetBranch: "main");
        var submission = ApprovedSubmission() with { TargetBranch = "main" };

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.ScopeViolation, failure.Code);
    }

    [Fact]
    public void Validate_AcceptsAllowedTaskBranch()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());

        var result = engine.Validate(Decision(), ApprovedSubmission());

        Assert.True(result.IsPublishable);
        Assert.Contains("target branch is allowed for push_branch", result.Decisions);
    }

    [Fact]
    public void Validate_AcceptsAllowedFastForwardMain()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var decision = Decision(operation: PublishOperation.FastForwardMain, targetBranch: "main");
        var submission = ApprovedSubmission() with { TargetBranch = "main" };

        var result = engine.Validate(decision, submission);

        Assert.True(result.IsPublishable);
        Assert.Contains("target branch is allowed for fast_forward_main", result.Decisions);
    }

    [Fact]
    public void Validate_RejectsUnlistedFastForwardBranch()
    {
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), Policy());
        var decision = Decision(operation: PublishOperation.FastForwardMain, targetBranch: "release");
        var submission = ApprovedSubmission() with { TargetBranch = "release" };

        var result = engine.Validate(decision, submission);

        Assert.False(result.IsPublishable);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.ScopeViolation, failure.Code);
    }


    [Fact]
    public void Validate_UsesProjectSpecificCanonicalRemotePolicy()
    {
        var globalPolicy = new PublishTargetPolicy(
            TargetRemoteName: "canonical",
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-publish.git",
            PushBranchPrefixes: ["task/"],
            FastForwardBranches: ["main"],
            ProjectPolicies: new Dictionary<string, ProjectPublishTargetPolicy>
            {
                ["den-channels"] = new(
                    ProjectId: "den-channels",
                    TargetRemoteName: null,
                    CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
                    PushBranchPrefixes: ["task/"],
                    FastForwardBranches: ["main"])
            });
        IPublishEngine engine = new PublishPolicyValidationEngine(new PublishValidationEngine(), globalPolicy);

        var result = engine.Validate(Decision(), ApprovedSubmission());

        Assert.True(result.IsPublishable);
        Assert.Contains("target remote matches configured canonical remote (project:den-channels)", result.Decisions);
    }

    private static PublishTargetPolicy Policy()
        => new(
            TargetRemoteName: "canonical",
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            PushBranchPrefixes: ["task/"],
            FastForwardBranches: ["main"]);

    private static PublishDecision Decision(
        PublishOperation operation = PublishOperation.PushBranch,
        string targetRemote = "canonical",
        string targetBranch = "task/1416-den-channels")
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1424_001",
            RequestedBy: "den-channels-orchestrator",
            Operation: operation,
            TargetRemote: targetRemote,
            TargetBranch: targetBranch,
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
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"),
            Review: new PublishReviewState(
                ReviewRoundId: 680,
                Verdict: PublishReviewVerdict.LooksGood,
                Findings: []));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
