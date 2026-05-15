namespace DenPublish.Core;

public interface IPromotionPublisher
{
    PromotionPublishResult Publish(PromotionPublishRequest request);
}

public enum PromotionPublishStatus
{
    DryRun,
    Published,
    Rejected,
    Failed
}

public sealed record PromotionPublishRequest(
    PublishDecision Decision,
    PromotionValidationWorkflowResult Validation);

public sealed record PromotionPublishResult(
    PromotionPublishStatus Status,
    string Summary,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<ValidationFailure> Failures)
{
    public bool Succeeded => Status is PromotionPublishStatus.DryRun or PromotionPublishStatus.Published
                             && Failures.Count == 0;

    public static PromotionPublishResult DryRun(string summary, IReadOnlyList<string> plannedCommands)
        => new(PromotionPublishStatus.DryRun, summary, plannedCommands, []);

    public static PromotionPublishResult Rejected(string summary, params ValidationFailure[] failures)
        => new(PromotionPublishStatus.Rejected, summary, [], failures);

    public static PromotionPublishResult Failed(string summary, params ValidationFailure[] failures)
        => new(PromotionPublishStatus.Failed, summary, [], failures);
}

public sealed class DryRunPromotionPublisher : IPromotionPublisher
{
    private static readonly char[] UnsafeBranchCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];
    private static readonly char[] UnsafeRefCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];

    public PromotionPublishResult Publish(PromotionPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Validation.IsPublishable)
        {
            return PromotionPublishResult.Rejected(
                "promotion validation result is not publishable",
                request.Validation.Validation.Failures.ToArray());
        }

        if (request.Validation.LocalRef is not { Length: > 0 } localRef)
        {
            return PromotionPublishResult.Rejected(
                "validated promotion result is missing a managed local ref",
                new ValidationFailure(PublishFailureCode.MissingRequiredValidation, "A managed local ref is required before promotion can be planned."));
        }

        if (!IsSafeLocalRef(localRef))
        {
            return PromotionPublishResult.Rejected(
                "validated promotion result contains an unsafe managed local ref",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Local ref '{localRef}' is not safe for service-owned publish operations."));
        }

        if (request.Validation.FetchedHeadCommit is not { } fetchedHead)
        {
            return PromotionPublishResult.Rejected(
                "validated promotion result is missing the fetched head commit",
                new ValidationFailure(PublishFailureCode.MissingRequiredValidation, "Fetched head commit is required before promotion can be planned."));
        }

        if (fetchedHead != request.Decision.ExpectedHeadCommit)
        {
            return PromotionPublishResult.Rejected(
                "validated promotion head does not match requested decision head",
                new ValidationFailure(
                    PublishFailureCode.CodeGateHeadMismatch,
                    $"Validated fetched head {fetchedHead.Value} does not match decision expected head {request.Decision.ExpectedHeadCommit.Value}."));
        }

        if (!IsSafeTargetBranch(request.Decision.TargetBranch))
        {
            return PromotionPublishResult.Rejected(
                "publish decision target branch is not safe for push planning",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Target branch '{request.Decision.TargetBranch}' is not safe for service-owned publish operations."));
        }

        if (!request.Decision.ValidateOnly)
        {
            return PromotionPublishResult.Failed(
                "live publishing is not available until canonical credentials are configured",
                new ValidationFailure(
                    PublishFailureCode.CredentialUnavailable,
                    "This publisher only supports validate-only dry-runs; install and approve the credential-backed publisher before live promotion."));
        }

        var plannedCommand = $"git push {request.Decision.TargetRemote} {localRef}:refs/heads/{request.Decision.TargetBranch}";
        return PromotionPublishResult.DryRun(
            "validate-only promotion dry-run planned without pushing",
            [plannedCommand]);
    }

    private static bool IsSafeLocalRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("refs/den-publish/submissions/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.EndsWith(".lock", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || value.IndexOfAny(UnsafeRefCharacters) >= 0)
        {
            return false;
        }

        return true;
    }

    private static bool IsSafeTargetBranch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("-", StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.EndsWith(".lock", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains("@{", StringComparison.Ordinal)
            || value.IndexOfAny(UnsafeBranchCharacters) >= 0)
        {
            return false;
        }

        return true;
    }
}
