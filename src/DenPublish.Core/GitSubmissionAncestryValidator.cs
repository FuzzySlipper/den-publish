namespace DenPublish.Core;

public interface ISubmissionAncestryValidator
{
    PublishValidationResult ValidateAncestry(CodeSubmission submission, string workspacePath, string localRef);
}

public sealed class GitSubmissionAncestryValidator(IGitCommandRunner git) : ISubmissionAncestryValidator
{
    private static readonly char[] UnsafeRefCharacters = [' ', '\t', '\n', '\r', '~', '^', ':', '?', '*', '[', '\\', ';'];

    public PublishValidationResult ValidateAncestry(CodeSubmission submission, string workspacePath, string localRef)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return PublishValidationResult.Rejected(
                "workspace path is required for submission ancestry validation",
                new ValidationFailure(PublishFailureCode.InvalidRequest, "Workspace path is required to check submission ancestry."));
        }

        if (!IsSafeLocalRef(localRef))
        {
            return PublishValidationResult.Rejected(
                "fetched submission local ref is not safe for ancestry validation",
                new ValidationFailure(PublishFailureCode.InvalidRequest, $"Local ref '{localRef}' is not safe for service-owned Git operations."));
        }

        var mergeBase = git.Run([
            "-C",
            workspacePath,
            "merge-base",
            "--is-ancestor",
            submission.BaseCommit.Value,
            localRef
        ]);

        return mergeBase.ExitCode switch
        {
            0 => PublishValidationResult.Approved(
                "fetched submission ancestry validated",
                ["submission head descends from recorded base commit"]),
            1 => PublishValidationResult.Rejected(
                "fetched submission is not a fast-forward from recorded base",
                new ValidationFailure(
                    PublishFailureCode.NonFastForward,
                    $"Submission head {submission.HeadCommit.Value} does not descend from recorded base {submission.BaseCommit.Value}.")),
            _ => PublishValidationResult.Failed(
                "failed to validate fetched submission ancestry",
                new ValidationFailure(
                    PublishFailureCode.MissingRequiredValidation,
                    GitError(mergeBase, "git merge-base failed while checking submission ancestry")))
        };
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

    private static string GitError(GitCommandResult result, string fallback)
        => string.IsNullOrWhiteSpace(result.StandardError)
            ? $"{fallback}; git exited with code {result.ExitCode}."
            : result.StandardError.Trim();
}
