using DenPublish.Api;
using DenPublish.Core;
using Microsoft.AspNetCore.Http;

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
        Assert.Equal(680, workflow.CapturedRequest.Submission?.Review?.ReviewRoundId);
        Assert.Equal(PublishReviewVerdict.LooksGood, workflow.CapturedRequest.Submission?.Review?.Verdict);
        var finding = Assert.Single(workflow.CapturedRequest.Submission?.Review?.Findings ?? []);
        Assert.Equal("finding_1", finding.FindingId);
        Assert.True(finding.Blocking);
        Assert.False(finding.Resolved);
        Assert.Equal("override_scope_1", finding.OverrideId);
        var scopeOverride = Assert.Single(workflow.CapturedRequest.Decision.ScopeOverrides);
        Assert.Equal("override_scope_1", scopeOverride.OverrideId);
        Assert.Equal("Generated file outside normal prefix after tool regeneration", scopeOverride.Reason);
        Assert.Equal("planner", scopeOverride.ApprovedBy);
        Assert.Equal(["src/DenChannels/"], workflow.CapturedRequest.ScopePolicy.AllowedPathPrefixes);
    }

    [Fact]
    public void Validate_MapsPolicyContextAndOrchestratorOverride()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved(
                "workflow ok",
                ["audit_warn downgraded unclassified_soft_failure"],
                [new ValidationWarning(PublishFailureCode.UnclassifiedSoftFailure, "environmental issue", "trusted override")]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var request = Request() with
        {
            Decision = Request().Decision with
            {
                OrchestratorOverride = new PublishOrchestratorOverrideApiModel(
                    UnclassifiedFailurePolicy: "warn_and_audit",
                    Reason: "SSH config permission issue is environmental",
                    ExpectedRiskCategories: ["infra_papercut", "non_code"])
            }
        };
        var resolver = new FixedPromotionPolicyContextResolver(new PromotionPolicyContext(PromotionCallerTrust.TrustedOrchestrator, PromotionPolicyMode.AuditWarn));

        var response = PromotionValidationEndpoints.Validate(request, workflow, RequestWorkspacePathResolver.Instance, resolver);

        Assert.True(response.IsPublishable);
        var warning = Assert.Single(response.Warnings);
        Assert.Equal("unclassified_soft_failure", warning.Code);
        Assert.Equal("environmental issue", warning.Message);
        Assert.Equal("trusted override", warning.Reason);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal("reject", warning.StrictAction);
        Assert.Equal("allow_with_warning", warning.PermissiveAction);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.Equal(PromotionCallerTrust.TrustedOrchestrator, workflow.CapturedRequest.EffectivePolicyContext.CallerTrust);
        Assert.Equal(PromotionPolicyMode.AuditWarn, workflow.CapturedRequest.EffectivePolicyContext.Mode);
        Assert.NotNull(workflow.CapturedRequest.Decision.OrchestratorOverride);
        Assert.Equal("warn_and_audit", workflow.CapturedRequest.Decision.OrchestratorOverride.UnclassifiedFailurePolicy);
        Assert.Equal(["infra_papercut", "non_code"], workflow.CapturedRequest.Decision.OrchestratorOverride.ExpectedRiskCategories);
    }



    [Fact]
    public void Validate_UsesWorkspaceResolverInsteadOfCallerProvidedPath()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok"),
            LocalRef: null,
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var request = Request() with { WorkspacePath = "/tmp/worker-controlled-shim" };
        var resolver = new RecordingWorkspacePathResolver(WorkspacePathResolutionResult.Resolved(
            "/home/agents/runtime/den-publish/workspaces/den-channels/tasks/1416/submissions/sub_1424_001"));

        var response = PromotionValidationEndpoints.Validate(request, workflow, resolver);

        Assert.True(response.IsPublishable);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.Equal("/home/agents/runtime/den-publish/workspaces/den-channels/tasks/1416/submissions/sub_1424_001", workflow.CapturedRequest.WorkspacePath);
        Assert.Same(request, resolver.CapturedRequest);
    }

    [Fact]
    public void Validate_ReturnsInvalidRequestWhenWorkspaceResolverRejects()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok"),
            LocalRef: null,
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var resolver = new RecordingWorkspacePathResolver(WorkspacePathResolutionResult.Failed(
            new ValidationFailure(PublishFailureCode.InvalidRequest, "unsafe workspace identity")));

        var response = PromotionValidationEndpoints.Validate(Request(), workflow, resolver);

        Assert.False(response.IsPublishable);
        Assert.Equal("rejected", response.Status);
        Assert.Equal("invalid_request", Assert.Single(response.Failures).Code);
        Assert.Null(workflow.CapturedRequest);
    }

    [Theory]
    [InlineData("/home/agents/runtime/den-publish/workspaces", "den-channels", 1416, "sub_1424_001", "/home/agents/runtime/den-publish/workspaces/den-channels/tasks/1416/submissions/sub_1424_001")]
    [InlineData("/home/agents/runtime/den-publish/workspaces/", "den_channels", 7, "sub-abc", "/home/agents/runtime/den-publish/workspaces/den_channels/tasks/7/submissions/sub-abc")]
    public void ConfiguredWorkspacePathResolver_DerivesServiceOwnedPath(
        string root,
        string projectId,
        int taskId,
        string submissionId,
        string expectedPath)
    {
        var request = Request() with
        {
            WorkspacePath = "/tmp/caller-controlled",
            Decision = Request().Decision with
            {
                ProjectId = projectId,
                TaskId = taskId,
                SubmissionId = submissionId
            }
        };
        var resolver = new ConfiguredWorkspacePathResolver(root);

        var result = resolver.Resolve(request);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedPath, result.WorkspacePath);
    }

    [Theory]
    [InlineData("../den-channels", "sub_1424_001")]
    [InlineData("den/channels", "sub_1424_001")]
    [InlineData("den-channels", "../sub_1424_001")]
    [InlineData("den-channels", "sub/1424")]
    public void ConfiguredWorkspacePathResolver_RejectsUnsafePathComponents(string projectId, string submissionId)
    {
        var request = Request() with
        {
            Decision = Request().Decision with
            {
                ProjectId = projectId,
                SubmissionId = submissionId
            }
        };
        var resolver = new ConfiguredWorkspacePathResolver("/home/agents/runtime/den-publish/workspaces");

        var result = resolver.Resolve(request);

        Assert.False(result.Succeeded);
        Assert.Null(result.WorkspacePath);
        Assert.Equal(PublishFailureCode.InvalidRequest, result.Failure?.Code);
    }


    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_DoesNotTrustRequestBodyRequestedByByDefault()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn));

        var context = resolver.Resolve(Request());

        Assert.Equal(PromotionCallerTrust.Worker, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.Strict, context.Mode);
    }

    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_CanExplicitlyTrustRequestBodyRequestedBy()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn,
            TrustRequestBodyRequestedBy: true));

        var context = resolver.Resolve(Request());

        Assert.Equal(PromotionCallerTrust.TrustedOrchestrator, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.AuditWarn, context.Mode);
    }

    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_DoesNotTrustForwardedHeadersByDefault()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn));

        var context = resolver.Resolve(Request(), ForwardedTrustedHeaders());

        Assert.Equal(PromotionCallerTrust.Worker, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.Strict, context.Mode);
    }

    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_CanTrustForwardedCallerHeadersWhenEnabled()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn,
            TrustForwardedCallerHeaders: true));

        var context = resolver.Resolve(Request(), ForwardedTrustedHeaders());

        Assert.Equal(PromotionCallerTrust.TrustedOrchestrator, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.AuditWarn, context.Mode);
    }

    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_RejectsForwardedHeaderRequesterMismatch()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn,
            TrustForwardedCallerHeaders: true));
        var headers = ForwardedTrustedHeaders();
        headers["X-Den-Requested-By"] = "different-orchestrator";

        var context = resolver.Resolve(Request(), headers);

        Assert.Equal(PromotionCallerTrust.Worker, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.Strict, context.Mode);
    }

    [Fact]
    public void Validate_UsesTrustedForwardedCallerHeadersWhenConfigured()
    {
        var workflow = new RecordingWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok"),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-channels-orchestrator"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn,
            TrustForwardedCallerHeaders: true));

        var response = PromotionValidationEndpoints.Validate(
            Request(),
            workflow,
            RequestWorkspacePathResolver.Instance,
            resolver,
            ForwardedTrustedHeaders());

        Assert.True(response.IsPublishable);
        Assert.NotNull(workflow.CapturedRequest);
        Assert.Equal(PromotionCallerTrust.TrustedOrchestrator, workflow.CapturedRequest.EffectivePolicyContext.CallerTrust);
        Assert.Equal(PromotionPolicyMode.AuditWarn, workflow.CapturedRequest.EffectivePolicyContext.Mode);
    }


    [Fact]
    public void ConfiguredPromotionPolicyContextResolver_UsesProjectSpecificTrustedModeOverride()
    {
        var resolver = new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(["den-hermes-runner"], StringComparer.Ordinal),
            PromotionPolicyMode.AuditWarn,
            TrustRequestBodyRequestedBy: true,
            ProjectTrustedModes: new Dictionary<string, PromotionPolicyMode>(StringComparer.Ordinal)
            {
                ["den-publish"] = PromotionPolicyMode.Defensive
            }));

        var baseRequest = Request();
        var request = baseRequest with
        {
            Decision = baseRequest.Decision with
            {
                ProjectId = "den-publish",
                RequestedBy = "den-hermes-runner"
            },
            Submission = baseRequest.Submission! with { ProjectId = "den-publish" }
        };

        var context = resolver.Resolve(request);

        Assert.Equal(PromotionCallerTrust.TrustedOrchestrator, context.CallerTrust);
        Assert.Equal(PromotionPolicyMode.Defensive, context.Mode);
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



    private sealed class RecordingWorkspacePathResolver(WorkspacePathResolutionResult result) : IWorkspacePathResolver
    {
        public PromotionValidationApiRequest? CapturedRequest { get; private set; }

        public WorkspacePathResolutionResult Resolve(PromotionValidationApiRequest request)
        {
            CapturedRequest = request;
            return result;
        }
    }

    private sealed class FixedPromotionPolicyContextResolver(PromotionPolicyContext context) : IPromotionPolicyContextResolver
    {
        public PromotionPolicyContext Resolve(PromotionValidationApiRequest request) => context;
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


    private static HeaderDictionary ForwardedTrustedHeaders() => new()
    {
        ["X-Den-Requested-By"] = "den-channels-orchestrator",
        ["X-Den-Caller-Trust"] = "trusted_orchestrator",
        ["X-Den-Promotion-Policy-Mode"] = "audit_warn",
    };

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
                ScopeOverrideIds: ["override_scope_1"],
                ValidateOnly: true,
                CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"),
                ScopeOverrides: [new PublishScopeOverrideApiModel("override_scope_1", "Generated file outside normal prefix after tool regeneration", "planner")]),
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
                CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"),
                Review: new PublishReviewApiModel(
                    ReviewRoundId: 680,
                    Verdict: "looks_good",
                    Findings: [new PublishReviewFindingApiModel(
                        FindingId: "finding_1",
                        Blocking: true,
                        Resolved: false,
                        OverrideId: "override_scope_1")])));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
