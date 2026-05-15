namespace DenPublish.Core;

public interface ICodeGateAccessPolicyProvider
{
    CodeGateAccessPolicy Resolve(CodeSubmission submission);
}

public sealed record CodeGateAccessPolicy(
    bool Succeeded,
    string RemoteUrl,
    IReadOnlyDictionary<string, string> Environment,
    ValidationFailure? Failure)
{
    public static CodeGateAccessPolicy FromSubmission(CodeSubmission submission)
        => new(true, submission.CodeGateRemoteUrl, new Dictionary<string, string>(), null);

    public static CodeGateAccessPolicy Configured(string remoteUrl, IReadOnlyDictionary<string, string> environment)
        => new(true, remoteUrl, environment, null);

    public static CodeGateAccessPolicy Rejected(ValidationFailure failure)
        => new(false, string.Empty, new Dictionary<string, string>(), failure);
}

public sealed record CodeGateProjectAccessPolicy(
    string ProjectId,
    string? CodeGateRemoteUrl,
    string? GitSshCommand);

public sealed class SubmissionCodeGateAccessPolicyProvider : ICodeGateAccessPolicyProvider
{
    public static SubmissionCodeGateAccessPolicyProvider Instance { get; } = new();

    private SubmissionCodeGateAccessPolicyProvider()
    {
    }

    public CodeGateAccessPolicy Resolve(CodeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return CodeGateAccessPolicy.FromSubmission(submission);
    }
}

public sealed class ConfiguredCodeGateAccessPolicyProvider(
    IReadOnlyDictionary<string, CodeGateProjectAccessPolicy> projectPolicies,
    ICodeGateAccessPolicyProvider? fallback = null) : ICodeGateAccessPolicyProvider
{
    private readonly ICodeGateAccessPolicyProvider _fallback = fallback ?? SubmissionCodeGateAccessPolicyProvider.Instance;

    public CodeGateAccessPolicy Resolve(CodeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (!projectPolicies.TryGetValue(submission.ProjectId, out var policy))
        {
            return _fallback.Resolve(submission);
        }

        var configuredRemote = policy.CodeGateRemoteUrl;
        if (!string.IsNullOrWhiteSpace(configuredRemote)
            && !string.Equals(configuredRemote, submission.CodeGateRemoteUrl, StringComparison.Ordinal))
        {
            return CodeGateAccessPolicy.Rejected(new ValidationFailure(
                PublishFailureCode.InvalidRequest,
                $"Submission code-gate remote '{submission.CodeGateRemoteUrl}' does not match configured code-gate remote for project '{submission.ProjectId}'."));
        }

        var remote = string.IsNullOrWhiteSpace(configuredRemote)
            ? submission.CodeGateRemoteUrl
            : configuredRemote;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(policy.GitSshCommand))
        {
            environment["GIT_SSH_COMMAND"] = policy.GitSshCommand;
            environment["GIT_TERMINAL_PROMPT"] = "0";
        }

        return CodeGateAccessPolicy.Configured(remote, environment);
    }
}
