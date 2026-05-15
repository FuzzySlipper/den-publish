using System.Text.RegularExpressions;

namespace DenPublish.Core;

public interface ISubmissionFetcher
{
    SubmissionFetchResult Fetch(CodeSubmission submission, string workspacePath);
}

public sealed record SubmissionFetchResult(
    bool Succeeded,
    string LocalRef,
    GitSha HeadCommit,
    ValidationFailure? Failure)
{
    public static SubmissionFetchResult Fetched(string localRef, GitSha headCommit)
        => new(true, localRef, headCommit, Failure: null);

    public static SubmissionFetchResult Failed(string localRef, PublishFailureCode code, string message)
        => new(false, localRef, new GitSha(string.Empty), new ValidationFailure(code, message));
}

public sealed class GitSubmissionFetcher(IGitCommandRunner git) : ISubmissionFetcher
{
    private static readonly Regex SafeSubmissionId = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SubmissionFetchResult Fetch(CodeSubmission submission, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (!TryBuildLocalRef(submission.SubmissionId, out var localRef, out var localRefError))
        {
            return SubmissionFetchResult.Failed(
                string.Empty,
                PublishFailureCode.InvalidRequest,
                localRefError);
        }

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return SubmissionFetchResult.Failed(
                localRef,
                PublishFailureCode.InvalidRequest,
                "Workspace path is required to fetch a submission ref.");
        }

        var fetch = git.Run([
            "-C",
            workspacePath,
            "fetch",
            "--no-tags",
            submission.CodeGateRemoteUrl,
            $"+{submission.IngressRef}:{localRef}"
        ]);

        if (fetch.ExitCode != 0)
        {
            return SubmissionFetchResult.Failed(
                localRef,
                PublishFailureCode.CodeGateFetchFailed,
                GitError(fetch, "git fetch failed while importing the code-gate submission ref"));
        }

        var revParse = git.Run([
            "-C",
            workspacePath,
            "rev-parse",
            $"{localRef}^{{commit}}"
        ]);

        if (revParse.ExitCode != 0)
        {
            return SubmissionFetchResult.Failed(
                localRef,
                PublishFailureCode.CodeGateFetchFailed,
                GitError(revParse, "git rev-parse failed after importing the code-gate submission ref"));
        }

        var parsed = revParse.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!GitSha.TryCreate(parsed, out var localHead))
        {
            return SubmissionFetchResult.Failed(
                localRef,
                PublishFailureCode.CodeGateFetchFailed,
                $"git rev-parse did not return a full SHA for {localRef}.");
        }

        if (localHead != submission.HeadCommit)
        {
            return SubmissionFetchResult.Failed(
                localRef,
                PublishFailureCode.CodeGateHeadMismatch,
                $"Fetched local ref {localRef} resolved to {localHead.Value}, expected {submission.HeadCommit.Value}.");
        }

        return SubmissionFetchResult.Fetched(localRef, localHead);
    }

    private static bool TryBuildLocalRef(string submissionId, out string localRef, out string error)
    {
        if (string.IsNullOrWhiteSpace(submissionId)
            || submissionId.Contains("..", StringComparison.Ordinal)
            || submissionId.Contains('/', StringComparison.Ordinal)
            || submissionId.Contains('\\', StringComparison.Ordinal)
            || !SafeSubmissionId.IsMatch(submissionId))
        {
            localRef = string.Empty;
            error = "SubmissionId must be a safe local den-publish ref token.";
            return false;
        }

        localRef = $"refs/den-publish/submissions/{submissionId}";
        error = string.Empty;
        return true;
    }

    private static string GitError(GitCommandResult result, string fallback)
        => string.IsNullOrWhiteSpace(result.StandardError)
            ? $"{fallback}; git exited with code {result.ExitCode}."
            : result.StandardError.Trim();
}
