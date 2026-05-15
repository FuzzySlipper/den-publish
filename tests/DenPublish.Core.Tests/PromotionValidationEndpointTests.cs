using DenPublish.Api;
using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PromotionValidationEndpointTests
{
    [Fact]
    public void Validate_MapsHttpRequestIntoWorkflowAndReturnsPublishableResponse()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok", ["all checks passed"]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));

        var response = PromotionValidationEndpoints.Validate(Request(), workflow);

        Assert.True(response.IsPublishable);
        Assert.Equal("validated", response.Status);
        Assert.Equal("workflow ok", response.Summary);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", response.LocalRef);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", response.FetchedHeadCommit);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.Equal("/var/lib/den-publish/workspaces/den-channels", workflow.CapturedRequest.WorkspacePath);
        Assert.Equal("pub_1424_001", workflow.CapturedRequest.Decision.DecisionId);
        Assert.Equal("sub_1424_001", workflow.CapturedRequest.Submission?.SubmissionId);
        Assert.Equal(["src/DenChannels/"], workflow.CapturedRequest.ScopePolicy.AllowedPathPrefixes);
    }

    [Fact]
    public void Validate_ReturnsInvalidRequestWithoutCallingWorkflowWhenShaIsMalformed()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok"),
            LocalRef: null,
            FetchedHeadCommit: null));
        var request = Request() with
        {
            Decision = Request().Decision with { ExpectedHeadCommit = "not-a-sha" }
        };

        var response = PromotionValidationEndpoints.Validate(request, workflow);

        Assert.False(response.IsPublishable);
        Assert.Equal("rejected", response.Status);
        Assert.Equal("invalid_request", Assert.Single(response.Failures).Code);
        Assert.Null(workflow.CapturedRequest);
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
