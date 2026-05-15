namespace DenPublish.Core;

public sealed record PublishTargetPolicy(
    string TargetRemoteName,
    string CanonicalRemoteUrl,
    IReadOnlyList<string> PushBranchPrefixes,
    IReadOnlyList<string> FastForwardBranches,
    IReadOnlyDictionary<string, ProjectPublishTargetPolicy>? ProjectPolicies = null)
{
    public ResolvedPublishTargetPolicy ResolveFor(PublishDecision decision, CodeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(submission);

        var projectId = !string.IsNullOrWhiteSpace(submission.ProjectId)
            ? submission.ProjectId
            : decision.ProjectId;

        if (ProjectPolicies is not null && ProjectPolicies.TryGetValue(projectId, out var projectPolicy))
        {
            return new ResolvedPublishTargetPolicy(
                ProjectId: projectId,
                TargetRemoteName: string.IsNullOrWhiteSpace(projectPolicy.TargetRemoteName) ? TargetRemoteName : projectPolicy.TargetRemoteName,
                CanonicalRemoteUrl: string.IsNullOrWhiteSpace(projectPolicy.CanonicalRemoteUrl) ? CanonicalRemoteUrl : projectPolicy.CanonicalRemoteUrl,
                PushBranchPrefixes: projectPolicy.PushBranchPrefixes is { Count: > 0 } ? projectPolicy.PushBranchPrefixes : PushBranchPrefixes,
                FastForwardBranches: projectPolicy.FastForwardBranches is { Count: > 0 } ? projectPolicy.FastForwardBranches : FastForwardBranches,
                Source: $"project:{projectId}");
        }

        return new ResolvedPublishTargetPolicy(
            ProjectId: projectId,
            TargetRemoteName: TargetRemoteName,
            CanonicalRemoteUrl: CanonicalRemoteUrl,
            PushBranchPrefixes: PushBranchPrefixes,
            FastForwardBranches: FastForwardBranches,
            Source: "global");
    }
}

public sealed record ProjectPublishTargetPolicy(
    string ProjectId,
    string? TargetRemoteName,
    string? CanonicalRemoteUrl,
    IReadOnlyList<string>? PushBranchPrefixes,
    IReadOnlyList<string>? FastForwardBranches);

public sealed record ResolvedPublishTargetPolicy(
    string ProjectId,
    string TargetRemoteName,
    string CanonicalRemoteUrl,
    IReadOnlyList<string> PushBranchPrefixes,
    IReadOnlyList<string> FastForwardBranches,
    string Source);

public sealed class PublishPolicyValidationEngine(IPublishEngine inner, PublishTargetPolicy policy) : IPublishEngine
{
    private static readonly char[] UnsafeBranchCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\'];

    public PublishValidationResult Validate(PublishDecision decision, CodeSubmission? submission)
    {
        var innerResult = inner.Validate(decision, submission);
        if (!innerResult.IsPublishable || submission is null)
        {
            return innerResult;
        }

        var resolvedPolicy = policy.ResolveFor(decision, submission);
        if (string.IsNullOrWhiteSpace(resolvedPolicy.CanonicalRemoteUrl))
        {
            return PublishValidationResult.Rejected(
                "publish target policy is missing a canonical remote",
                new ValidationFailure(
                    PublishFailureCode.CanonicalRemoteMismatch,
                    $"No canonical remote is configured for project '{resolvedPolicy.ProjectId}'."));
        }

        if (!string.Equals(decision.TargetRemote, resolvedPolicy.TargetRemoteName, StringComparison.Ordinal)
            || !string.Equals(submission.CanonicalRemoteUrl, resolvedPolicy.CanonicalRemoteUrl, StringComparison.Ordinal))
        {
            return PublishValidationResult.Rejected(
                "publish target remote does not match configured canonical remote",
                new ValidationFailure(
                    PublishFailureCode.CanonicalRemoteMismatch,
                    $"Decision target remote '{decision.TargetRemote}' and submission remote '{submission.CanonicalRemoteUrl}' must match configured target '{resolvedPolicy.TargetRemoteName}' / '{resolvedPolicy.CanonicalRemoteUrl}' from {resolvedPolicy.Source}."));
        }

        if (!string.Equals(decision.TargetBranch, submission.TargetBranch, StringComparison.Ordinal))
        {
            return PublishValidationResult.Rejected(
                "publish decision target branch does not match submission target branch",
                new ValidationFailure(
                    PublishFailureCode.InvalidRequest,
                    "Decision and submission target branches must match before promotion."));
        }

        if (!IsSafeBranchName(decision.TargetBranch))
        {
            return PublishValidationResult.Rejected(
                "publish target branch is not a safe Git branch name",
                new ValidationFailure(
                    PublishFailureCode.InvalidRequest,
                    $"Target branch '{decision.TargetBranch}' is not safe for service-owned Git operations."));
        }

        var operationDecision = decision.Operation switch
        {
            PublishOperation.PushBranch => ValidatePushBranch(decision.TargetBranch, resolvedPolicy),
            PublishOperation.FastForwardMain => ValidateFastForwardBranch(decision.TargetBranch, resolvedPolicy),
            _ => PublishValidationResult.Rejected(
                "publish operation is not supported",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Unsupported publish operation '{decision.Operation}'."))
        };

        if (!operationDecision.IsPublishable)
        {
            return operationDecision;
        }

        var decisions = innerResult.Decisions
            .Concat([
                $"target remote matches configured canonical remote ({resolvedPolicy.Source})",
                operationDecision.Decisions.Single()
            ])
            .ToArray();

        return PublishValidationResult.Approved(
            "publish decision validated against configured target policy",
            decisions);
    }

    private static PublishValidationResult ValidatePushBranch(string targetBranch, ResolvedPublishTargetPolicy resolvedPolicy)
    {
        if (resolvedPolicy.PushBranchPrefixes.Any(prefix => targetBranch.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return PublishValidationResult.Approved("push branch target accepted", ["target branch is allowed for push_branch"]);
        }

        return PublishValidationResult.Rejected(
            "push_branch target branch is outside configured allow-list",
            new ValidationFailure(
                PublishFailureCode.ScopeViolation,
                $"Target branch '{targetBranch}' is not under an allowed push_branch prefix for {resolvedPolicy.Source}."));
    }

    private static PublishValidationResult ValidateFastForwardBranch(string targetBranch, ResolvedPublishTargetPolicy resolvedPolicy)
    {
        if (resolvedPolicy.FastForwardBranches.Contains(targetBranch, StringComparer.Ordinal))
        {
            return PublishValidationResult.Approved("fast-forward branch target accepted", ["target branch is allowed for fast_forward_main"]);
        }

        return PublishValidationResult.Rejected(
            "fast_forward_main target branch is outside configured allow-list",
            new ValidationFailure(
                PublishFailureCode.ScopeViolation,
                $"Target branch '{targetBranch}' is not an allowed fast-forward branch for {resolvedPolicy.Source}."));
    }

    private static bool IsSafeBranchName(string value)
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
