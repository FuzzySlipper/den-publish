namespace DenPublish.Core;

public sealed record GitPromotionCredentialPolicy(
    bool IsConfigured,
    string Mode,
    IReadOnlyDictionary<string, string> Environment)
{
    public static GitPromotionCredentialPolicy Unconfigured { get; } = new(false, "unconfigured", new Dictionary<string, string>());

    public static GitPromotionCredentialPolicy ExplicitSshCommand(string sshCommand)
    {
        if (string.IsNullOrWhiteSpace(sshCommand))
        {
            return Unconfigured;
        }

        return new GitPromotionCredentialPolicy(
            true,
            "ssh_command",
            new Dictionary<string, string>
            {
                ["GIT_SSH_COMMAND"] = sshCommand,
                ["GIT_TERMINAL_PROMPT"] = "0"
            });
    }

    public static GitPromotionCredentialPolicy LocalFileRemoteForTesting()
        => new(true, "local_file_remote_for_testing", new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0"
        });
}

public sealed class DisabledLivePromotionPublisher : ILivePromotionPublisher
{
    public bool IsEnabled => false;

    public PromotionPublishResult Publish(PromotionPublishRequest request)
        => PromotionPublishResult.Failed(
            "live publishing is disabled by service configuration",
            new ValidationFailure(
                PublishFailureCode.CredentialUnavailable,
                "Live publishing requires an explicitly enabled credential-backed publisher configuration."));
}

public sealed class GitPromotionPublisher(IGitCommandRunner git, GitPromotionCredentialPolicy credentialPolicy) : ILivePromotionPublisher
{
    private static readonly char[] UnsafeBranchCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];
    private static readonly char[] UnsafeRefCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];

    public bool IsEnabled => true;

    public PromotionPublishResult Publish(PromotionPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ready = ValidateReadyForLivePublish(request, credentialPolicy);
        if (ready is not null)
        {
            return ready;
        }

        var localRef = request.Validation.LocalRef!;
        var pushRefSpec = $"{localRef}:refs/heads/{request.Decision.TargetBranch}";
        var push = git.Run([
            "-C",
            request.WorkspacePath,
            "push",
            request.TargetRemoteUrl,
            pushRefSpec
        ], credentialPolicy.Environment);

        if (push.ExitCode != 0)
        {
            return PromotionPublishResult.Failed(
                "git push failed while publishing the validated submission",
                new ValidationFailure(PublishFailureCode.GitPushFailed, GitError(push, "git push failed while publishing the validated submission")));
        }

        return PromotionPublishResult.Published("published validated submission to canonical remote");
    }

    private static PromotionPublishResult? ValidateReadyForLivePublish(PromotionPublishRequest request, GitPromotionCredentialPolicy credentialPolicy)
    {
        if (request.Decision.ValidateOnly)
        {
            return PromotionPublishResult.Rejected(
                "live publisher requires a non-validate-only decision",
                new ValidationFailure(PublishFailureCode.InvalidRequest, "Live publishing requires decision.validateOnly=false."));
        }

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
                new ValidationFailure(PublishFailureCode.MissingRequiredValidation, "A managed local ref is required before live promotion."));
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
                new ValidationFailure(PublishFailureCode.MissingRequiredValidation, "Fetched head commit is required before live promotion."));
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
                "publish decision target branch is not safe for live push",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Target branch '{request.Decision.TargetBranch}' is not safe for service-owned publish operations."));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            return PromotionPublishResult.Rejected(
                "validated promotion result is missing the managed workspace path",
                new ValidationFailure(PublishFailureCode.MissingRequiredValidation, "Managed workspace path is required before live promotion."));
        }

        if (string.IsNullOrWhiteSpace(request.TargetRemoteUrl))
        {
            return PromotionPublishResult.Failed(
                "live publishing is unavailable because target remote URL is missing",
                new ValidationFailure(PublishFailureCode.CredentialUnavailable, "Canonical target remote URL is required before live promotion."));
        }

        if (!credentialPolicy.IsConfigured)
        {
            return PromotionPublishResult.Failed(
                "live publishing is unavailable because no explicit credential policy is configured",
                new ValidationFailure(PublishFailureCode.CredentialUnavailable, "Live Git publishing must be configured with an explicit credential policy; ambient Git or SSH credentials are not accepted."));
        }

        return null;
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

    private static string GitError(GitCommandResult result, string fallback)
        => string.IsNullOrWhiteSpace(result.StandardError)
            ? $"{fallback}; git exited with code {result.ExitCode}."
            : result.StandardError.Trim();
}
