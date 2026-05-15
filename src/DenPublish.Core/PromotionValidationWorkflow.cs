namespace DenPublish.Core;

public interface IPromotionValidationWorkflow
{
    PromotionValidationWorkflowResult Validate(PromotionValidationRequest request);
}

public sealed record PromotionValidationRequest(
    PublishDecision Decision,
    CodeSubmission? Submission,
    string WorkspacePath,
    ChangedFileScopePolicy ScopePolicy);

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
            return new PromotionValidationWorkflowResult(scope, fetch.LocalRef, fetch.HeadCommit);
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

        return new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved(
                "promotion validation workflow completed without publishing",
                decisions),
            fetch.LocalRef,
            fetch.HeadCommit);
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
}
