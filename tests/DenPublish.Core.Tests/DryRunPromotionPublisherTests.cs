using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class DryRunPromotionPublisherTests
{
    [Fact]
    public void Publish_ReturnsDryRunCommandForValidatedPushBranchDecision()
    {
        var decision = Decision(validateOnly: true, PublishOperation.PushBranch, targetBranch: "task/1416-den-channels");
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_1424_001");
        var publisher = new DryRunPromotionPublisher();

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation));

        Assert.Equal(PromotionPublishStatus.DryRun, result.Status);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(["git push canonical refs/den-publish/submissions/sub_1424_001:refs/heads/task/1416-den-channels"], result.PlannedCommands);
        Assert.Contains("validate-only", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publish_ReturnsDryRunCommandForValidatedFastForwardDecision()
    {
        var decision = Decision(validateOnly: true, PublishOperation.FastForwardMain, targetBranch: "main");
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_1424_001");
        var publisher = new DryRunPromotionPublisher();

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation));

        Assert.Equal(PromotionPublishStatus.DryRun, result.Status);
        Assert.Equal(["git push canonical refs/den-publish/submissions/sub_1424_001:refs/heads/main"], result.PlannedCommands);
    }

    [Fact]
    public void Publish_FailsClosedForLivePublishWhenCredentialsAreNotAvailable()
    {
        var decision = Decision(validateOnly: false, PublishOperation.PushBranch, targetBranch: "task/1416-den-channels");
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_1424_001");
        var publisher = new DryRunPromotionPublisher();

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation));

        Assert.Equal(PromotionPublishStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.CredentialUnavailable, failure.Code);
        Assert.Empty(result.PlannedCommands);
    }

    [Fact]
    public void Publish_RejectsWhenValidationResultIsNotPublishable()
    {
        var decision = Decision(validateOnly: true, PublishOperation.PushBranch, targetBranch: "task/1416-den-channels");
        var validation = new PromotionValidationWorkflowResult(
            PublishValidationResult.Rejected(
                "scope rejected",
                new ValidationFailure(PublishFailureCode.ScopeViolation, "outside scope")),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: decision.ExpectedHeadCommit);
        var publisher = new DryRunPromotionPublisher();

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation));

        Assert.Equal(PromotionPublishStatus.Rejected, result.Status);
        Assert.Equal(PublishFailureCode.ScopeViolation, Assert.Single(result.Failures).Code);
        Assert.Empty(result.PlannedCommands);
    }

    [Fact]
    public void Publish_FailsClosedWhenValidatedHeadDoesNotMatchDecisionHead()
    {
        var decision = Decision(validateOnly: true, PublishOperation.PushBranch, targetBranch: "task/1416-den-channels");
        var validation = ValidatedWorkflowResult(Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "refs/den-publish/submissions/sub_1424_001");
        var publisher = new DryRunPromotionPublisher();

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation));

        Assert.Equal(PromotionPublishStatus.Rejected, result.Status);
        Assert.Equal(PublishFailureCode.CodeGateHeadMismatch, Assert.Single(result.Failures).Code);
        Assert.Empty(result.PlannedCommands);
    }

    private static PromotionValidationWorkflowResult ValidatedWorkflowResult(GitSha head, string localRef)
        => new(
            PublishValidationResult.Approved("workflow ok", ["all checks passed"]),
            LocalRef: localRef,
            FetchedHeadCommit: head);

    private static PublishDecision Decision(bool validateOnly, PublishOperation operation, string targetBranch)
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1424_001",
            RequestedBy: "den-channels-orchestrator",
            Operation: operation,
            TargetRemote: "canonical",
            TargetBranch: targetBranch,
            ExpectedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ExpectedBaseBranch: "main",
            ReviewRoundId: 680,
            ScopeOverrideIds: [],
            ValidateOnly: validateOnly,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
