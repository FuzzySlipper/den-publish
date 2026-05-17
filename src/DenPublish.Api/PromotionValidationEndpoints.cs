using DenPublish.Core;

namespace DenPublish.Api;

public static class PromotionValidationEndpoints
{
    public static IEndpointRouteBuilder MapPromotionValidationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/promotion/validate", static (PromotionValidationApiRequest request, IPromotionValidationWorkflow workflow, IWorkspacePathResolver workspacePathResolver, IPromotionPolicyContextResolver policyContextResolver, HttpContext httpContext) =>
        {
            var response = Validate(request, workflow, workspacePathResolver, policyContextResolver, httpContext.Request.Headers);
            return response.IsPublishable ? Results.Ok(response) : Results.BadRequest(response);
        });
        app.MapPost("/promotion/dry-run", static (PromotionValidationApiRequest request, IPromotionValidationWorkflow workflow, IPromotionPublisher publisher, IWorkspacePathResolver workspacePathResolver, IPromotionPolicyContextResolver policyContextResolver, HttpContext httpContext) =>
        {
            var response = ValidateAndDryRun(request, workflow, publisher, workspacePathResolver, policyContextResolver, httpContext.Request.Headers);
            return response.Succeeded ? Results.Ok(response) : Results.BadRequest(response);
        });
        app.MapPost("/promotion/publish", static (PromotionValidationApiRequest request, IPromotionValidationWorkflow workflow, ILivePromotionPublisher publisher, IWorkspacePathResolver workspacePathResolver, IPromotionPolicyContextResolver policyContextResolver, HttpContext httpContext) =>
        {
            var response = ValidateAndPublish(request, workflow, publisher, workspacePathResolver, policyContextResolver, httpContext.Request.Headers);
            return response.Succeeded ? Results.Ok(response) : Results.BadRequest(response);
        });
        return app;
    }

    public static PromotionValidationApiResponse Validate(PromotionValidationApiRequest request, IPromotionValidationWorkflow workflow)
        => Validate(request, workflow, RequestWorkspacePathResolver.Instance, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionValidationApiResponse Validate(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        IWorkspacePathResolver workspacePathResolver)
        => Validate(request, workflow, workspacePathResolver, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionValidationApiResponse Validate(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        IWorkspacePathResolver workspacePathResolver,
        IPromotionPolicyContextResolver policyContextResolver,
        IHeaderDictionary? headers = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(workspacePathResolver);
        ArgumentNullException.ThrowIfNull(policyContextResolver);

        var workspacePath = workspacePathResolver.Resolve(request);
        if (!workspacePath.Succeeded)
        {
            return PromotionValidationApiResponse.FromResult(MalformedRequestResult(workspacePath.Failure!));
        }

        if (!TryMapRequest(request, workspacePath.WorkspacePath!, ResolvePolicyContext(policyContextResolver, request, headers), out var domainRequest, out var failure))
        {
            return PromotionValidationApiResponse.FromResult(MalformedRequestResult(failure!));
        }

        var result = workflow.Validate(domainRequest!);
        return PromotionValidationApiResponse.FromResult(result);
    }

    public static PromotionDryRunApiResponse ValidateAndDryRun(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        IPromotionPublisher publisher)
        => ValidateAndDryRun(request, workflow, publisher, RequestWorkspacePathResolver.Instance, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionDryRunApiResponse ValidateAndDryRun(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        IPromotionPublisher publisher,
        IWorkspacePathResolver workspacePathResolver)
        => ValidateAndDryRun(request, workflow, publisher, workspacePathResolver, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionDryRunApiResponse ValidateAndDryRun(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        IPromotionPublisher publisher,
        IWorkspacePathResolver workspacePathResolver,
        IPromotionPolicyContextResolver policyContextResolver,
        IHeaderDictionary? headers = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(workspacePathResolver);
        ArgumentNullException.ThrowIfNull(policyContextResolver);

        if (!request.Decision.ValidateOnly)
        {
            var liveDecisionFailure = new ValidationFailure(
                PublishFailureCode.InvalidRequest,
                "/promotion/dry-run requires decision.validateOnly=true; use /promotion/publish for approved live promotions.");
            return PromotionDryRunApiResponse.FromValidationOnly(MalformedRequestResult(liveDecisionFailure));
        }

        var workspacePath = workspacePathResolver.Resolve(request);
        if (!workspacePath.Succeeded)
        {
            var malformed = MalformedRequestResult(workspacePath.Failure!);
            return PromotionDryRunApiResponse.FromValidationOnly(malformed);
        }

        if (!TryMapRequest(request, workspacePath.WorkspacePath!, ResolvePolicyContext(policyContextResolver, request, headers), out var domainRequest, out var failure))
        {
            var malformed = MalformedRequestResult(failure!);
            return PromotionDryRunApiResponse.FromValidationOnly(malformed);
        }

        var mappedRequest = domainRequest!;
        var validationResult = workflow.Validate(mappedRequest);
        if (!validationResult.IsPublishable)
        {
            return PromotionDryRunApiResponse.FromValidationOnly(validationResult);
        }

        var publishResult = publisher.Publish(new PromotionPublishRequest(mappedRequest.Decision, validationResult));
        return PromotionDryRunApiResponse.FromPublishResult(validationResult, publishResult);
    }



    public static PromotionDryRunApiResponse ValidateAndPublish(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        ILivePromotionPublisher publisher)
        => ValidateAndPublish(request, workflow, publisher, RequestWorkspacePathResolver.Instance, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionDryRunApiResponse ValidateAndPublish(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        ILivePromotionPublisher publisher,
        IWorkspacePathResolver workspacePathResolver)
        => ValidateAndPublish(request, workflow, publisher, workspacePathResolver, DefaultPromotionPolicyContextResolver.Instance);

    public static PromotionDryRunApiResponse ValidateAndPublish(
        PromotionValidationApiRequest request,
        IPromotionValidationWorkflow workflow,
        ILivePromotionPublisher publisher,
        IWorkspacePathResolver workspacePathResolver,
        IPromotionPolicyContextResolver policyContextResolver,
        IHeaderDictionary? headers = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(workspacePathResolver);
        ArgumentNullException.ThrowIfNull(policyContextResolver);

        if (request.Decision.ValidateOnly)
        {
            var validateOnlyFailure = new ValidationFailure(
                PublishFailureCode.InvalidRequest,
                "/promotion/publish requires decision.validateOnly=false; use /promotion/dry-run for validate-only planning.");
            return PromotionDryRunApiResponse.FromValidationOnly(MalformedRequestResult(validateOnlyFailure));
        }

        if (!publisher.IsEnabled)
        {
            var disabledFailure = new ValidationFailure(
                PublishFailureCode.CredentialUnavailable,
                "Live publishing is disabled by service configuration; no validation, fetch, audit, or push was attempted.");
            return PromotionDryRunApiResponse.FromValidationOnly(FailedRequestResult("live publishing is disabled by service configuration", disabledFailure));
        }

        var workspacePath = workspacePathResolver.Resolve(request);
        if (!workspacePath.Succeeded)
        {
            var malformed = MalformedRequestResult(workspacePath.Failure!);
            return PromotionDryRunApiResponse.FromValidationOnly(malformed);
        }

        if (!TryMapRequest(request, workspacePath.WorkspacePath!, ResolvePolicyContext(policyContextResolver, request, headers), out var domainRequest, out var failure))
        {
            var malformed = MalformedRequestResult(failure!);
            return PromotionDryRunApiResponse.FromValidationOnly(malformed);
        }

        var mappedRequest = domainRequest!;
        var validationResult = workflow.Validate(mappedRequest);
        if (!validationResult.IsPublishable)
        {
            return PromotionDryRunApiResponse.FromValidationOnly(validationResult);
        }

        var publishResult = publisher.Publish(new PromotionPublishRequest(
            mappedRequest.Decision,
            validationResult,
            mappedRequest.WorkspacePath,
            mappedRequest.Submission?.CanonicalRemoteUrl ?? string.Empty));
        return PromotionDryRunApiResponse.FromPublishResult(validationResult, publishResult);
    }

    private static PromotionValidationWorkflowResult MalformedRequestResult(ValidationFailure failure)
        => new(
            PublishValidationResult.Rejected(
                "promotion validation request is malformed",
                failure),
            LocalRef: null,
            FetchedHeadCommit: null);

    private static PromotionValidationWorkflowResult FailedRequestResult(string summary, ValidationFailure failure)
        => new(
            PublishValidationResult.Failed(summary, failure),
            LocalRef: null,
            FetchedHeadCommit: null);

    private static PromotionPolicyContext ResolvePolicyContext(
        IPromotionPolicyContextResolver resolver,
        PromotionValidationApiRequest request,
        IHeaderDictionary? headers) =>
        headers is null ? resolver.Resolve(request) : resolver.Resolve(request, headers);

    private static bool TryMapRequest(
        PromotionValidationApiRequest request,
        string workspacePath,
        PromotionPolicyContext policyContext,
        out PromotionValidationRequest? domainRequest,
        out ValidationFailure? failure)
    {
        domainRequest = null;
        failure = null;

        if (!TryParseOperation(request.Decision.Operation, out var operation))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Unsupported publish operation '{request.Decision.Operation}'.");
            return false;
        }

        if (!GitSha.TryCreate(request.Decision.ExpectedHeadCommit, out var expectedHead))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, "Decision expectedHeadCommit must be a full Git SHA.");
            return false;
        }

        var decision = new PublishDecision(
            DecisionId: request.Decision.DecisionId,
            ProjectId: request.Decision.ProjectId,
            TaskId: request.Decision.TaskId,
            SubmissionId: request.Decision.SubmissionId,
            RequestedBy: request.Decision.RequestedBy,
            Operation: operation,
            TargetRemote: request.Decision.TargetRemote,
            TargetBranch: request.Decision.TargetBranch,
            ExpectedHeadCommit: expectedHead,
            ExpectedBaseBranch: request.Decision.ExpectedBaseBranch,
            ReviewRoundId: request.Decision.ReviewRoundId,
            ScopeOverrideIds: request.Decision.ScopeOverrideIds,
            ValidateOnly: request.Decision.ValidateOnly,
            CreatedAt: request.Decision.CreatedAt,
            ScopeOverrides: request.Decision.ScopeOverrides
                .Select(scopeOverride => new PublishScopeOverride(scopeOverride.OverrideId, scopeOverride.Reason, scopeOverride.ApprovedBy))
                .ToArray(),
            OrchestratorOverride: request.Decision.OrchestratorOverride is null
                ? null
                : new PublishOrchestratorOverride(
                    request.Decision.OrchestratorOverride.UnclassifiedFailurePolicy,
                    request.Decision.OrchestratorOverride.Reason,
                    request.Decision.OrchestratorOverride.ExpectedRiskCategories));

        CodeSubmission? submission = null;
        if (request.Submission is not null)
        {
            if (!TryMapSubmission(request.Submission, out submission, out failure))
            {
                return false;
            }
        }

        domainRequest = new PromotionValidationRequest(
            decision,
            submission,
            workspacePath,
            new ChangedFileScopePolicy(request.AllowedPathPrefixes),
            policyContext);
        return true;
    }

    private static bool TryMapSubmission(CodeSubmissionApiModel submission, out CodeSubmission? domainSubmission, out ValidationFailure? failure)
    {
        domainSubmission = null;
        failure = null;

        if (!GitSha.TryCreate(submission.BaseCommit, out var baseCommit))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, "Submission baseCommit must be a full Git SHA.");
            return false;
        }

        if (!GitSha.TryCreate(submission.HeadCommit, out var headCommit))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, "Submission headCommit must be a full Git SHA.");
            return false;
        }

        if (!TryParseStatus(submission.Status, out var status))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Unsupported submission status '{submission.Status}'.");
            return false;
        }

        if (!TryMapReview(submission.Review, out var review, out failure))
        {
            return false;
        }

        domainSubmission = new CodeSubmission(
            SubmissionId: submission.SubmissionId,
            ProjectId: submission.ProjectId,
            TaskId: submission.TaskId,
            WorkerRunId: submission.WorkerRunId,
            SubmittedBy: submission.SubmittedBy,
            Role: submission.Role,
            AttemptOrdinal: submission.AttemptOrdinal,
            ParentSubmissionId: submission.ParentSubmissionId,
            CodeGateInstance: submission.CodeGateInstance,
            CodeGateRepo: submission.CodeGateRepo,
            CodeGateRemoteUrl: submission.CodeGateRemoteUrl,
            IngressRef: submission.IngressRef,
            ConvenienceRef: submission.ConvenienceRef,
            BaseBranch: submission.BaseBranch,
            BaseCommit: baseCommit,
            HeadCommit: headCommit,
            CanonicalRemoteUrl: submission.CanonicalRemoteUrl,
            TargetBranch: submission.TargetBranch,
            ChangedFilesClaim: submission.ChangedFilesClaim,
            TestsRun: submission.TestsRun,
            Status: status,
            CreatedAt: submission.CreatedAt,
            Review: review);
        return true;
    }

    private static bool TryMapReview(
        PublishReviewApiModel? review,
        out PublishReviewState? domainReview,
        out ValidationFailure? failure)
    {
        domainReview = null;
        failure = null;
        if (review is null)
        {
            return true;
        }

        if (!TryParseReviewVerdict(review.Verdict, out var verdict))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Unsupported review verdict: {review.Verdict}.");
            return false;
        }

        domainReview = new PublishReviewState(
            review.ReviewRoundId,
            verdict,
            review.Findings
                .Select(finding => new PublishReviewFinding(
                    finding.FindingId,
                    finding.Blocking,
                    finding.Resolved,
                    finding.OverrideId))
                .ToArray());
        return true;
    }

    private static bool TryParseReviewVerdict(string value, out PublishReviewVerdict verdict)
    {
        verdict = value switch
        {
            "looks_good" => PublishReviewVerdict.LooksGood,
            "changes_requested" => PublishReviewVerdict.ChangesRequested,
            "follow_up_needed" => PublishReviewVerdict.FollowUpNeeded,
            "blocked_by_dependency" => PublishReviewVerdict.BlockedByDependency,
            _ => default
        };
        return value is "looks_good" or "changes_requested" or "follow_up_needed" or "blocked_by_dependency";
    }

    private static bool TryParseOperation(string value, out PublishOperation operation)
    {
        operation = value switch
        {
            "push_branch" => PublishOperation.PushBranch,
            "fast_forward_main" => PublishOperation.FastForwardMain,
            _ => default
        };
        return value is "push_branch" or "fast_forward_main";
    }

    private static bool TryParseStatus(string value, out CodeSubmissionStatus status)
    {
        status = value switch
        {
            "submitted" => CodeSubmissionStatus.Submitted,
            "review_requested" => CodeSubmissionStatus.ReviewRequested,
            "changes_requested" => CodeSubmissionStatus.ChangesRequested,
            "superseded" => CodeSubmissionStatus.Superseded,
            "approved" => CodeSubmissionStatus.Approved,
            "publish_requested" => CodeSubmissionStatus.PublishRequested,
            "published" => CodeSubmissionStatus.Published,
            "rejected" => CodeSubmissionStatus.Rejected,
            "failed" => CodeSubmissionStatus.Failed,
            _ => default
        };
        return value is "submitted" or "review_requested" or "changes_requested" or "superseded" or "approved" or "publish_requested" or "published" or "rejected" or "failed";
    }
}

public sealed record PromotionValidationApiRequest(
    string WorkspacePath,
    IReadOnlyList<string> AllowedPathPrefixes,
    PublishDecisionApiModel Decision,
    CodeSubmissionApiModel? Submission);

public sealed record PublishDecisionApiModel(
    string DecisionId,
    string ProjectId,
    int TaskId,
    string SubmissionId,
    string RequestedBy,
    string Operation,
    string TargetRemote,
    string TargetBranch,
    string ExpectedHeadCommit,
    string ExpectedBaseBranch,
    int ReviewRoundId,
    IReadOnlyList<string> ScopeOverrideIds,
    bool ValidateOnly,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PublishScopeOverrideApiModel>? ScopeOverrides = null,
    PublishOrchestratorOverrideApiModel? OrchestratorOverride = null)
{
    public IReadOnlyList<PublishScopeOverrideApiModel> ScopeOverrides { get; init; } = ScopeOverrides ?? [];
}

public sealed record PublishOrchestratorOverrideApiModel(
    string UnclassifiedFailurePolicy,
    string Reason,
    IReadOnlyList<string> ExpectedRiskCategories);

public sealed record PublishScopeOverrideApiModel(
    string OverrideId,
    string Reason,
    string ApprovedBy);

public sealed record CodeSubmissionApiModel(
    string SubmissionId,
    string ProjectId,
    int TaskId,
    string WorkerRunId,
    string SubmittedBy,
    string Role,
    int AttemptOrdinal,
    string? ParentSubmissionId,
    string CodeGateInstance,
    string CodeGateRepo,
    string CodeGateRemoteUrl,
    string IngressRef,
    string ConvenienceRef,
    string BaseBranch,
    string BaseCommit,
    string HeadCommit,
    string CanonicalRemoteUrl,
    string TargetBranch,
    IReadOnlyList<string> ChangedFilesClaim,
    IReadOnlyList<string> TestsRun,
    string Status,
    DateTimeOffset CreatedAt,
    PublishReviewApiModel? Review = null);

public sealed record PublishReviewApiModel(
    int ReviewRoundId,
    string Verdict,
    IReadOnlyList<PublishReviewFindingApiModel> Findings);

public sealed record PublishReviewFindingApiModel(
    string FindingId,
    bool Blocking,
    bool Resolved,
    string? OverrideId);

public sealed record PromotionValidationApiResponse(
    bool IsPublishable,
    string Status,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<PromotionValidationFailureApiModel> Failures,
    string? LocalRef,
    string? FetchedHeadCommit,
    IReadOnlyList<PromotionValidationWarningApiModel>? Warnings = null)
{
    public IReadOnlyList<PromotionValidationWarningApiModel> Warnings { get; init; } = Warnings ?? [];

    public static PromotionValidationApiResponse FromResult(PromotionValidationWorkflowResult result)
        => new(
            IsPublishable: result.IsPublishable,
            Status: ToApiString(result.Validation.Status),
            Summary: result.Validation.Summary,
            Decisions: result.Validation.Decisions,
            Failures: result.Validation.Failures
                .Select(failure => new PromotionValidationFailureApiModel(ToApiString(failure.Code), failure.Message))
                .ToArray(),
            LocalRef: result.LocalRef,
            FetchedHeadCommit: result.FetchedHeadCommit?.Value,
            Warnings: result.Validation.Warnings
                .Select(warning => new PromotionValidationWarningApiModel(
                    ToApiString(warning.Code),
                    warning.Message,
                    warning.Reason,
                    warning.Severity,
                    warning.StrictAction,
                    warning.PermissiveAction,
                    warning.ObservedValues))
                .ToArray());

    public static string ToApiString(PublishValidationStatus status)
        => status switch
        {
            PublishValidationStatus.Validated => "validated",
            PublishValidationStatus.Rejected => "rejected",
            PublishValidationStatus.Failed => "failed",
            _ => status.ToString().ToLowerInvariant()
        };

    public static string ToApiString(PublishFailureCode code)
        => code switch
        {
            PublishFailureCode.InvalidRequest => "invalid_request",
            PublishFailureCode.MissingSubmission => "missing_submission",
            PublishFailureCode.StaleSubmission => "stale_submission",
            PublishFailureCode.MissingReview => "missing_review",
            PublishFailureCode.ReviewNotApproved => "review_not_approved",
            PublishFailureCode.UnresolvedBlockingFindings => "unresolved_blocking_findings",
            PublishFailureCode.MissingRequiredValidation => "missing_required_validation",
            PublishFailureCode.CodeGateFetchFailed => "code_gate_fetch_failed",
            PublishFailureCode.CodeGateHeadMismatch => "code_gate_head_mismatch",
            PublishFailureCode.CanonicalRemoteMismatch => "canonical_remote_mismatch",
            PublishFailureCode.ScopeViolation => "scope_violation",
            PublishFailureCode.NonFastForward => "non_fast_forward",
            PublishFailureCode.CredentialUnavailable => "credential_unavailable",
            PublishFailureCode.GitPushFailed => "git_push_failed",
            PublishFailureCode.AuditFailed => "audit_failed",
            PublishFailureCode.UnclassifiedSoftFailure => "unclassified_soft_failure",
            _ => code.ToString().ToLowerInvariant()
        };
}

public sealed record PromotionValidationFailureApiModel(string Code, string Message);

public sealed record PromotionValidationWarningApiModel(
    string Code,
    string Message,
    string Reason,
    string Severity = "warning",
    string StrictAction = "reject",
    string PermissiveAction = "allow_with_warning",
    IReadOnlyDictionary<string, string>? ObservedValues = null)
{
    public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = ObservedValues ?? new Dictionary<string, string>();
}

public sealed record PromotionDryRunApiResponse(
    bool Succeeded,
    string PublishStatus,
    string PublishSummary,
    PromotionValidationApiResponse Validation,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<PromotionValidationFailureApiModel> PublishFailures)
{
    public static PromotionDryRunApiResponse FromValidationOnly(PromotionValidationWorkflowResult validation)
    {
        var validationResponse = PromotionValidationApiResponse.FromResult(validation);
        return new PromotionDryRunApiResponse(
            Succeeded: false,
            PublishStatus: validationResponse.Status,
            PublishSummary: validationResponse.Summary,
            Validation: validationResponse,
            PlannedCommands: [],
            PublishFailures: validationResponse.Failures);
    }

    public static PromotionDryRunApiResponse FromPublishResult(
        PromotionValidationWorkflowResult validation,
        PromotionPublishResult publish)
        => new(
            Succeeded: publish.Succeeded,
            PublishStatus: ToApiString(publish.Status),
            PublishSummary: publish.Summary,
            Validation: PromotionValidationApiResponse.FromResult(validation),
            PlannedCommands: publish.PlannedCommands,
            PublishFailures: publish.Failures
                .Select(failure => new PromotionValidationFailureApiModel(PromotionValidationApiResponse.ToApiString(failure.Code), failure.Message))
                .ToArray());

    private static string ToApiString(PromotionPublishStatus status)
        => status switch
        {
            PromotionPublishStatus.DryRun => "dry_run",
            PromotionPublishStatus.Published => "published",
            PromotionPublishStatus.Rejected => "rejected",
            PromotionPublishStatus.Failed => "failed",
            _ => status.ToString().ToLowerInvariant()
        };
}

