namespace DenPublish.Core;

public interface IPromotionValidationWorkflow
{
    PromotionValidationWorkflowResult Validate(PromotionValidationRequest request);
}

public sealed record PromotionValidationRequest(
    PublishDecision Decision,
    CodeSubmission? Submission,
    string WorkspacePath,
    ChangedFileScopePolicy ScopePolicy,
    PromotionPolicyContext? PolicyContext = null)
{
    public PromotionPolicyContext EffectivePolicyContext => PolicyContext ?? PromotionPolicyContext.StrictWorker;
}

public sealed record PromotionValidationWorkflowResult(
    PublishValidationResult Validation,
    string? LocalRef,
    GitSha? FetchedHeadCommit)
{
    public bool IsPublishable => Validation.IsPublishable;
}

public sealed class PromotionValidationWorkflow(
    IPublishEngine preflightValidator,
    ISubmissionFetcher submissionFetcher,
    IChangedFileScopeValidator scopeValidator,
    ISubmissionAncestryValidator ancestryValidator) : IPromotionValidationWorkflow
{
    public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var preflight = preflightValidator.Validate(request.Decision, request.Submission);
        if (!preflight.IsPublishable || request.Submission is null)
        {
            return new PromotionValidationWorkflowResult(preflight, LocalRef: null, FetchedHeadCommit: null);
        }

        var fetch = submissionFetcher.Fetch(request.Submission, request.WorkspacePath);
        if (!fetch.Succeeded)
        {
            return new PromotionValidationWorkflowResult(
                ToValidationResult("failed to fetch submission into managed workspace", fetch.Failure),
                fetch.LocalRef,
                FetchedHeadCommit: null);
        }

        var scope = scopeValidator.ValidateScope(request.Submission, request.WorkspacePath, fetch.LocalRef, request.ScopePolicy);
        if (!scope.IsPublishable)
        {
            if (!TryDowngradeSoftFailure(request, scope, out scope))
            {
                return new PromotionValidationWorkflowResult(scope, fetch.LocalRef, fetch.HeadCommit);
            }
        }

        var ancestry = ancestryValidator.ValidateAncestry(request.Submission, request.WorkspacePath, fetch.LocalRef);
        if (!ancestry.IsPublishable)
        {
            return new PromotionValidationWorkflowResult(ancestry, fetch.LocalRef, fetch.HeadCommit);
        }

        var decisions = preflight.Decisions
            .Concat(["submission fetched into managed local ref"])
            .Concat(scope.Decisions)
            .Concat(ancestry.Decisions)
            .ToArray();
        var warnings = preflight.Warnings
            .Concat(scope.Warnings)
            .Concat(ancestry.Warnings)
            .ToArray();

        return new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved(
                "promotion validation workflow completed without publishing",
                decisions,
                warnings),
            fetch.LocalRef,
            fetch.HeadCommit);
    }

    private static bool TryDowngradeSoftFailure(
        PromotionValidationRequest request,
        PublishValidationResult rejectedResult,
        out PublishValidationResult downgradedResult)
    {
        downgradedResult = rejectedResult;
        var context = request.EffectivePolicyContext;
        if (context.CallerTrust != PromotionCallerTrust.TrustedOrchestrator
            || context.Mode != PromotionPolicyMode.AuditWarn
            || rejectedResult.Failures.Count == 0)
        {
            return false;
        }

        if (rejectedResult.Failures.Any(failure => !CanDowngradeFailure(request.Decision, failure)))
        {
            return false;
        }

        var warnings = rejectedResult.Failures
            .Select(failure => new ValidationWarning(
                failure.Code,
                failure.Message,
                WarningReason(request.Decision, failure),
                ObservedValues: WarningObservedValues(request, failure)))
            .ToArray();
        var decisions = rejectedResult.Failures
            .Select(failure => $"audit_warn downgraded {ToSnakeCase(failure.Code)}")
            .ToArray();

        downgradedResult = PublishValidationResult.Approved(
            $"{rejectedResult.Summary}; trusted-orchestrator audit_warn policy downgraded soft failure(s) to warning(s)",
            decisions,
            warnings);
        return true;
    }

    private static bool CanDowngradeFailure(PublishDecision decision, ValidationFailure failure)
        => failure.Code switch
        {
            PublishFailureCode.ScopeViolation => true,
            PublishFailureCode.UnclassifiedSoftFailure => HasValidUnclassifiedSoftFailureOverride(decision),
            _ => false
        };

    private static bool HasValidUnclassifiedSoftFailureOverride(PublishDecision decision)
        => decision.OrchestratorOverride is
        {
            UnclassifiedFailurePolicy: "warn_and_audit",
            Reason: { Length: > 0 },
            ExpectedRiskCategories.Count: > 0
        } overrideRequest
        && !string.IsNullOrWhiteSpace(overrideRequest.Reason)
        && overrideRequest.ExpectedRiskCategories.All(category => !string.IsNullOrWhiteSpace(category));

    private static string WarningReason(PublishDecision decision, ValidationFailure failure)
    {
        if (failure.Code == PublishFailureCode.UnclassifiedSoftFailure && decision.OrchestratorOverride is not null)
        {
            return $"trusted orchestrator override: {decision.OrchestratorOverride.Reason}; expected risks: {string.Join(", ", decision.OrchestratorOverride.ExpectedRiskCategories)}";
        }

        return "trusted orchestrator audit_warn policy allows this soft validation failure as an audited warning";
    }
    private static IReadOnlyDictionary<string, string> WarningObservedValues(
        PromotionValidationRequest request,
        ValidationFailure failure)
    {
        var context = request.EffectivePolicyContext;
        return new Dictionary<string, string>
        {
            ["policy_mode"] = ToSnakeCase(context.Mode),
            ["caller_trust"] = ToSnakeCase(context.CallerTrust),
            ["failure_code"] = ToSnakeCase(failure.Code),
            ["strict_action"] = "reject",
            ["permissive_action"] = "allow_with_warning",
            ["strict_status"] = "rejected",
            ["permissive_status"] = "validated",
            ["target_branch"] = request.Decision.TargetBranch,
            ["requested_by"] = request.Decision.RequestedBy,
            ["review_round_id"] = request.Decision.ReviewRoundId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }


    private static PublishValidationResult ToValidationResult(string summary, ValidationFailure? failure)
    {
        var resolvedFailure = failure ?? new ValidationFailure(
            PublishFailureCode.MissingRequiredValidation,
            "Submission fetch failed without a structured validation failure.");

        return resolvedFailure.Code switch
        {
            PublishFailureCode.CodeGateFetchFailed => PublishValidationResult.Failed(summary, resolvedFailure),
            PublishFailureCode.MissingRequiredValidation => PublishValidationResult.Failed(summary, resolvedFailure),
            PublishFailureCode.AuditFailed => PublishValidationResult.Failed(summary, resolvedFailure),
            PublishFailureCode.CredentialUnavailable => PublishValidationResult.Failed(summary, resolvedFailure),
            PublishFailureCode.GitPushFailed => PublishValidationResult.Failed(summary, resolvedFailure),
            _ => PublishValidationResult.Rejected(summary, resolvedFailure)
        };
    }

    private static string ToSnakeCase(PromotionCallerTrust trust)
        => trust switch
        {
            PromotionCallerTrust.TrustedOrchestrator => "trusted_orchestrator",
            PromotionCallerTrust.Untrusted => "untrusted",
            PromotionCallerTrust.Worker => "worker",
            _ => trust.ToString().ToLowerInvariant()
        };

    private static string ToSnakeCase(PromotionPolicyMode mode)
        => mode switch
        {
            PromotionPolicyMode.AuditWarn => "audit_warn",
            PromotionPolicyMode.Strict => "strict",
            PromotionPolicyMode.Defensive => "defensive",
            _ => mode.ToString().ToLowerInvariant()
        };

    private static string ToSnakeCase(PublishFailureCode code)
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
