namespace DenPublish.Core;

public interface IChangedFileScopeValidator
{
    PublishValidationResult ValidateScope(
        CodeSubmission submission,
        string workspacePath,
        string localRef,
        ChangedFileScopePolicy policy);
}

public sealed record ChangedFileScopePolicy(IReadOnlyList<string> AllowedPathPrefixes);

public sealed class GitChangedFileScopeValidator(IGitCommandRunner git) : IChangedFileScopeValidator
{
    private static readonly char[] UnsafeRefCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];

    public PublishValidationResult ValidateScope(
        CodeSubmission submission,
        string workspacePath,
        string localRef,
        ChangedFileScopePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return PublishValidationResult.Rejected(
                "workspace path is required for changed-file scope validation",
                new ValidationFailure(PublishFailureCode.InvalidRequest, "Workspace path is required to diff a fetched submission."));
        }

        if (!IsSafeLocalRef(localRef))
        {
            return PublishValidationResult.Rejected(
                "fetched submission local ref is not safe for Git diff",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Local ref '{localRef}' is not safe for service-owned Git operations."));
        }

        var diff = git.Run([
            "-C",
            workspacePath,
            "diff",
            "--name-only",
            "--diff-filter=ACDMRTUXB",
            submission.BaseCommit.Value,
            localRef,
            "--"
        ]);

        if (diff.ExitCode != 0)
        {
            return PublishValidationResult.Failed(
                "failed to compute changed-file scope for fetched submission",
                new ValidationFailure(
                    PublishFailureCode.MissingRequiredValidation,
                    GitError(diff, "git diff failed while computing changed-file scope")));
        }

        var changedFiles = ParseChangedFiles(diff.StandardOutput);
        var unsafePath = changedFiles.FirstOrDefault(path => !IsSafeRepositoryPath(path));
        if (unsafePath is not null)
        {
            return PublishValidationResult.Rejected(
                "fetched submission changed-file list contains an unsafe path",
                new ValidationFailure(PublishFailureCode.ScopeViolation, $"Changed file path '{unsafePath}' is not safe for repository-scoped publishing."));
        }

        var outOfScope = changedFiles
            .Where(path => !IsAllowed(path, policy.AllowedPathPrefixes))
            .ToArray();
        if (outOfScope.Length > 0)
        {
            return PublishValidationResult.Rejected(
                "fetched submission changes files outside configured scope",
                new ValidationFailure(PublishFailureCode.ScopeViolation, $"Changed file(s) outside allowed scope: {string.Join(", ", outOfScope)}."));
        }

        var claimMismatch = FindClaimMismatch(changedFiles, submission.ChangedFilesClaim);
        if (claimMismatch is not null)
        {
            return PublishValidationResult.Rejected(
                "observed changed files do not match submission claim",
                new ValidationFailure(PublishFailureCode.ScopeViolation, claimMismatch));
        }

        return PublishValidationResult.Approved(
            "fetched submission changed-file scope validated",
            [
                "changed files are within configured scope",
                "observed changed files match submission claim"
            ]);
    }

    private static string[] ParseChangedFiles(string stdout)
        => stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsAllowed(string path, IReadOnlyList<string> allowedPathPrefixes)
        => allowedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    private static string? FindClaimMismatch(IReadOnlyList<string> observedFiles, IReadOnlyList<string> claimedFiles)
    {
        var observed = observedFiles.ToHashSet(StringComparer.Ordinal);
        var claimed = claimedFiles.ToHashSet(StringComparer.Ordinal);

        var unclaimed = observed.Except(claimed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unclaimed.Length > 0)
        {
            return $"Observed changed file(s) were not claimed by the submission: {string.Join(", ", unclaimed)}.";
        }

        var missing = claimed.Except(observed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            return $"Submission claimed changed file(s) that were not observed in the fetched diff: {string.Join(", ", missing)}.";
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

    private static bool IsSafeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("\\", StringComparison.Ordinal))
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
