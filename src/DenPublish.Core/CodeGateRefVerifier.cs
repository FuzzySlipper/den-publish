using System.Diagnostics;

namespace DenPublish.Core;

public interface ICodeGateRefResolver
{
    CodeGateRefResolution ResolveHead(CodeSubmission submission);
}

public sealed record CodeGateRefResolution(bool Succeeded, GitSha HeadCommit, string ErrorMessage)
{
    public static CodeGateRefResolution Found(GitSha headCommit)
        => new(true, headCommit, string.Empty);

    public static CodeGateRefResolution Failed(string errorMessage)
        => new(false, new GitSha(string.Empty), errorMessage);
}

public sealed class CodeGatePublishValidationEngine(IPublishEngine contractValidator, ICodeGateRefResolver refResolver) : IPublishEngine
{
    public PublishValidationResult Validate(PublishDecision decision, CodeSubmission? submission)
    {
        var contractResult = contractValidator.Validate(decision, submission);
        if (!contractResult.IsPublishable || submission is null)
        {
            return contractResult;
        }

        var refResolution = refResolver.ResolveHead(submission);
        if (!refResolution.Succeeded)
        {
            return PublishValidationResult.Failed(
                "failed to resolve immutable code-gate submission ref",
                new ValidationFailure(
                    PublishFailureCode.CodeGateFetchFailed,
                    $"Could not resolve {submission.IngressRef} from {submission.CodeGateRemoteUrl}: {refResolution.ErrorMessage}"));
        }

        if (refResolution.HeadCommit != submission.HeadCommit || refResolution.HeadCommit != decision.ExpectedHeadCommit)
        {
            return PublishValidationResult.Rejected(
                "code-gate ref head does not match reviewed submission head",
                new ValidationFailure(
                    PublishFailureCode.CodeGateHeadMismatch,
                    $"Code-gate ref {submission.IngressRef} resolved to {refResolution.HeadCommit.Value}, expected {submission.HeadCommit.Value}."));
        }

        var decisions = contractResult.Decisions
            .Concat(["code-gate immutable ref resolved to reviewed head"])
            .ToArray();

        return PublishValidationResult.Approved(
            "publish decision validated against Den submission and code-gate ref",
            decisions);
    }
}

public interface IGitCommandRunner
{
    GitCommandResult Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null);
}

public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class GitLsRemoteCodeGateRefResolver(IGitCommandRunner commandRunner) : ICodeGateRefResolver
{
    public CodeGateRefResolution ResolveHead(CodeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var result = commandRunner.Run([
            "ls-remote",
            "--exit-code",
            submission.CodeGateRemoteUrl,
            submission.IngressRef
        ]);

        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"git ls-remote exited with code {result.ExitCode}."
                : result.StandardError.Trim();
            return CodeGateRefResolution.Failed(error);
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !string.Equals(parts[1], submission.IngressRef, StringComparison.Ordinal))
            {
                continue;
            }

            if (GitSha.TryCreate(parts[0], out var headCommit))
            {
                return CodeGateRefResolution.Found(headCommit);
            }

            return CodeGateRefResolution.Failed($"git ls-remote returned an invalid SHA for exact ref {submission.IngressRef}.");
        }

        return CodeGateRefResolution.Failed($"git ls-remote did not return exact ref {submission.IngressRef}.");
    }
}

public sealed class ProcessGitCommandRunner(TimeSpan? timeout = null) : IGitCommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public GitCommandResult Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        if (environment is not null)
        {
            foreach (var item in environment)
            {
                process.StartInfo.Environment[item.Key] = item.Value;
            }
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var completed = process.WaitForExit(_timeout);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return new GitCommandResult(124, string.Empty, $"git command timed out after {_timeout.TotalSeconds:0} seconds");
        }

        return new GitCommandResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }
}
