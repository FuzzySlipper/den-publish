using DenPublish.Api;
using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PromotionDryRunEndpointTests
{
    [Fact]
    public void ValidateAndDryRun_ValidatesRequestThenReturnsPlannedPublishResponse()
    {
        var expectedHead = Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var validation = new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok", ["all validation checks passed"]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: expectedHead);
        var workflow = new RecordingWorkflow(validation);
        var publisher = new RecordingPublisher(PromotionPublishResult.DryRun(
            "dry-run planned",
            ["git push canonical refs/den-publish/submissions/sub_1424_001:refs/heads/task/1416-den-channels"]));

        var response = PromotionValidationEndpoints.ValidateAndDryRun(Request(), workflow, publisher);

        Assert.True(response.Succeeded);
        Assert.Equal("dry_run", response.PublishStatus);
        Assert.Equal("dry-run planned", response.PublishSummary);
        Assert.True(response.Validation.IsPublishable);
        Assert.Equal("validated", response.Validation.Status);
        Assert.Equal(["git push canonical refs/den-publish/submissions/sub_1424_001:refs/heads/task/1416-den-channels"], response.PlannedCommands);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.NotNull(publisher.CapturedRequest);
        Assert.Equal("pub_1424_001", publisher.CapturedRequest.Decision.DecisionId);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", publisher.CapturedRequest.Validation.LocalRef);
    }

    [Fact]
    public void ValidateAndDryRun_DoesNotCallWorkflowOrPublisherWhenRequestIsMalformed()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok"),
            LocalRef: null,
            FetchedHeadCommit: null));
        var publisher = new RecordingPublisher(PromotionPublishResult.DryRun("dry-run planned", []));
        var request = Request() with
        {
            Decision = Request().Decision with { ExpectedHeadCommit = "not-a-sha" }
        };

        var response = PromotionValidationEndpoints.ValidateAndDryRun(request, workflow, publisher);

        Assert.False(response.Succeeded);
        Assert.Equal("rejected", response.PublishStatus);
        Assert.Equal("rejected", response.Validation.Status);
        Assert.Equal("invalid_request", Assert.Single(response.Validation.Failures).Code);
        Assert.Empty(response.PlannedCommands);
        Assert.Null(workflow.CapturedRequest);
        Assert.Null(publisher.CapturedRequest);
    }

    [Fact]
    public void ValidateAndDryRun_DoesNotCallPublisherWhenValidationRejects()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Rejected(
                "scope rejected",
                new ValidationFailure(PublishFailureCode.ScopeViolation, "outside scope")),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var publisher = new RecordingPublisher(PromotionPublishResult.DryRun("dry-run planned", []));

        var response = PromotionValidationEndpoints.ValidateAndDryRun(Request(), workflow, publisher);

        Assert.False(response.Succeeded);
        Assert.Equal("rejected", response.PublishStatus);
        Assert.Equal("scope rejected", response.Validation.Summary);
        Assert.Equal("scope_violation", Assert.Single(response.Validation.Failures).Code);
        Assert.Empty(response.PlannedCommands);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.Null(publisher.CapturedRequest);
    }

    private sealed class RecordingWorkflow(PromotionValidationWorkflowResult result) : IPromotionValidationWorkflow
    {
        public PromotionValidationRequest? CapturedRequest { get; private set; }

        public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
        {
            CapturedRequest = request;
            return result;
        }
    }

    private sealed class RecordingPublisher(PromotionPublishResult result) : IPromotionPublisher
    {
        public PromotionPublishRequest? CapturedRequest { get; private set; }

        public PromotionPublishResult Publish(PromotionPublishRequest request)
        {
            CapturedRequest = request;
            return result;
        }
    }

    private static PromotionValidationApiRequest Request()
        => new(
            WorkspacePath: "/var/lib/den-publish/workspaces/den-channels",
            AllowedPathPrefixes: ["src/DenChannels/"],
            Decision: new PublishDecisionApiModel(
                DecisionId: "pub_1424_001",
                ProjectId: "den-channels",
                TaskId: 1416,
                SubmissionId: "sub_1424_001",
                RequestedBy: "den-channels-orchestrator",
                Operation: "push_branch",
                TargetRemote: "canonical",
                TargetBranch: "task/1416-den-channels",
                ExpectedHeadCommit: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ExpectedBaseBranch: "main",
                ReviewRoundId: 680,
                ScopeOverrideIds: [],
                ValidateOnly: true,
                CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z")),
            Submission: new CodeSubmissionApiModel(
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
                BaseCommit: "cccccccccccccccccccccccccccccccccccccccc",
                HeadCommit: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
                TargetBranch: "task/1416-den-channels",
                ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
                TestsRun: ["dotnet test --no-restore: passed"],
                Status: "approved",
                CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z")));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
