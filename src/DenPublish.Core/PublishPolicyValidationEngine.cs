namespace DenPublish.Core;

public sealed record PublishTargetPolicy(
    string TargetRemoteName,
    string CanonicalRemoteUrl,
    IReadOnlyList<string> PushBranchPrefixes,
    IReadOnlyList<string> FastForwardBranches);

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

        if (!string.Equals(decision.TargetRemote, policy.TargetRemoteName, StringComparison.Ordinal)
            || !string.Equals(submission.CanonicalRemoteUrl, policy.CanonicalRemoteUrl, StringComparison.Ordinal))
        {
            return PublishValidationResult.Rejected(
                "publish target remote does not match configured canonical remote",
                new ValidationFailure(
                    PublishFailureCode.CanonicalRemoteMismatch,
                    $"Decision target remote '{decision.TargetRemote}' and submission remote '{submission.CanonicalRemoteUrl}' must match configured target '{policy.TargetRemoteName}' / '{policy.CanonicalRemoteUrl}'."));
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
            PublishOperation.PushBranch => ValidatePushBranch(decision.TargetBranch),
            PublishOperation.FastForwardMain => ValidateFastForwardBranch(decision.TargetBranch),
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
                "target remote matches configured canonical remote",
                operationDecision.Decisions.Single()
            ])
            .ToArray();

        return PublishValidationResult.Approved(
            "publish decision validated against configured target policy",
            decisions);
    }

    private PublishValidationResult ValidatePushBranch(string targetBranch)
    {
        if (policy.PushBranchPrefixes.Any(prefix => targetBranch.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return PublishValidationResult.Approved("push branch target accepted", ["target branch is allowed for push_branch"]);
        }

        return PublishValidationResult.Rejected(
            "push_branch target branch is outside configured allow-list",
            new ValidationFailure(
                PublishFailureCode.ScopeViolation,
                $"Target branch '{targetBranch}' is not under an allowed push_branch prefix."));
    }

    private PublishValidationResult ValidateFastForwardBranch(string targetBranch)
    {
        if (policy.FastForwardBranches.Contains(targetBranch, StringComparer.Ordinal))
        {
            return PublishValidationResult.Approved("fast-forward branch target accepted", ["target branch is allowed for fast_forward_main"]);
        }

        return PublishValidationResult.Rejected(
            "fast_forward_main target branch is outside configured allow-list",
            new ValidationFailure(
                PublishFailureCode.ScopeViolation,
                $"Target branch '{targetBranch}' is not an allowed fast-forward branch."));
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
