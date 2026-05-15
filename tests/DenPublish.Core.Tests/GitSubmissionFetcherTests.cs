using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class GitSubmissionFetcherTests
{
    [Fact]
    public void Fetch_PreparesWorkspaceAndRunsExactRefIntoDenPublishLocalRef()
    {
        var submission = ApprovedSubmission();
        var workspace = "/var/lib/den-publish/workspaces/den-channels";
        var runner = new RecordingGitCommandRunner(
            new GitCommandResult(0, "Initialized empty Git repository", string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, $"{submission.HeadCommit.Value}\n", string.Empty));
        var preparer = new RecordingWorkspaceDirectoryPreparer();
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, preparer);

        var result = fetcher.Fetch(submission, workspace);

        Assert.True(result.Succeeded);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", result.LocalRef);
        Assert.Equal([workspace], preparer.PreparedPaths);
        Assert.Equal(["-C", workspace, "init"], runner.Commands[0]);
        Assert.Equal(["-C", workspace, "fetch", "--no-tags", submission.CodeGateRemoteUrl, $"+{submission.IngressRef}:refs/den-publish/submissions/sub_1424_001"], runner.Commands[1]);
        Assert.Equal(["-C", workspace, "rev-parse", "refs/den-publish/submissions/sub_1424_001^{commit}"], runner.Commands[2]);
    }

    [Fact]
    public void Fetch_UsesProjectCodeGateCredentialEnvironment()
    {
        var submission = ApprovedSubmission();
        var runner = new RecordingGitCommandRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, $"{submission.HeadCommit.Value}\n", string.Empty));
        var provider = new ConfiguredCodeGateAccessPolicyProvider(new Dictionary<string, CodeGateProjectAccessPolicy>
        {
            ["den-channels"] = new(
                ProjectId: "den-channels",
                CodeGateRemoteUrl: submission.CodeGateRemoteUrl,
                GitSshCommand: "ssh -F /dev/null -i /runtime/den-channels-codegate")
        });
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer(), provider);

        var result = fetcher.Fetch(submission, "/workspace");

        Assert.True(result.Succeeded);
        Assert.Equal("ssh -F /dev/null -i /runtime/den-channels-codegate", runner.Environments[1]!["GIT_SSH_COMMAND"]);
        Assert.Equal("0", runner.Environments[1]!["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void Fetch_RejectsCodeGateRemoteMismatchBeforeGitCommands()
    {
        var submission = ApprovedSubmission() with { CodeGateRemoteUrl = "ssh://git@192.168.1.10:3022/evil/evil.git" };
        var runner = new RecordingGitCommandRunner();
        var provider = new ConfiguredCodeGateAccessPolicyProvider(new Dictionary<string, CodeGateProjectAccessPolicy>
        {
            ["den-channels"] = new(
                ProjectId: "den-channels",
                CodeGateRemoteUrl: "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
                GitSshCommand: null)
        });
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer(), provider);

        var result = fetcher.Fetch(submission, "/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.InvalidRequest, result.Failure.Code);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Fetch_VerifiesLocalObjectSha()
    {
        var submission = ApprovedSubmission();
        var runner = new RecordingGitCommandRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, $"{submission.HeadCommit.Value}\n", string.Empty));
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer());

        var result = fetcher.Fetch(submission, "/workspace");

        Assert.True(result.Succeeded);
        Assert.Equal(submission.HeadCommit, result.HeadCommit);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Fetch_FailsWhenGitFetchFails()
    {
        var runner = new RecordingGitCommandRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(128, string.Empty, "could not read from remote repository"));
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer());

        var result = fetcher.Fetch(ApprovedSubmission(), "/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.CodeGateFetchFailed, result.Failure.Code);
        Assert.Contains("could not read", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fetch_RejectsWhenLocalHeadDiffers()
    {
        var runner = new RecordingGitCommandRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n", string.Empty));
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer());

        var result = fetcher.Fetch(ApprovedSubmission(), "/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.CodeGateHeadMismatch, result.Failure.Code);
    }



    [Fact]
    public void Fetch_FailsClosedWhenWorkspaceDirectoryPreparationFails()
    {
        var runner = new RecordingGitCommandRunner();
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(
            runner,
            new FailingWorkspaceDirectoryPreparer("permission denied"));

        var result = fetcher.Fetch(ApprovedSubmission(), "/managed/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.CodeGateFetchFailed, result.Failure.Code);
        Assert.Contains("permission denied", result.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Fetch_FailsClosedWhenWorkspaceGitInitFails()
    {
        var runner = new RecordingGitCommandRunner(new GitCommandResult(128, string.Empty, "not a git directory"));
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(runner, new RecordingWorkspaceDirectoryPreparer());

        var result = fetcher.Fetch(ApprovedSubmission(), "/managed/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.CodeGateFetchFailed, result.Failure.Code);
        Assert.Contains("not a git directory", result.Failure.Message, StringComparison.Ordinal);
        Assert.Single(runner.Commands);
        Assert.Equal(["-C", "/managed/workspace", "init"], runner.Commands[0]);
    }


    [Fact]
    public void Fetch_RejectsUnsafeSubmissionIdForLocalRef()
    {
        ISubmissionFetcher fetcher = new GitSubmissionFetcher(new RecordingGitCommandRunner());
        var submission = ApprovedSubmission() with { SubmissionId = "../evil" };

        var result = fetcher.Fetch(submission, "/workspace");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(PublishFailureCode.InvalidRequest, result.Failure.Code);
    }



    private sealed class RecordingWorkspaceDirectoryPreparer : IWorkspaceDirectoryPreparer
    {
        public List<string> PreparedPaths { get; } = [];

        public WorkspaceDirectoryPreparationResult Prepare(string workspacePath)
        {
            PreparedPaths.Add(workspacePath);
            return WorkspaceDirectoryPreparationResult.Prepared();
        }
    }

    private sealed class FailingWorkspaceDirectoryPreparer(string message) : IWorkspaceDirectoryPreparer
    {
        public WorkspaceDirectoryPreparationResult Prepare(string workspacePath)
            => WorkspaceDirectoryPreparationResult.Failed(message);
    }


    private sealed class RecordingGitCommandRunner(params GitCommandResult[] results) : IGitCommandRunner
    {
        private int _index;

        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];

        public GitCommandResult Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments.ToArray());
            Environments.Add(environment);
            return results[_index++];
        }
    }

    private static CodeSubmission ApprovedSubmission()
        => new(
            SubmissionId: "sub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            WorkerRunId: "run-20260514-def456",
            SubmittedBy: "den-channels-runner",
            Role: "coder",
            AttemptOrdinal: 1,
            ParentSubmissionId: null,
            CodeGateInstance: "den-code-gate",
            CodeGateRepo: "den-channels.git",
            CodeGateRemoteUrl: "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
            IngressRef: "refs/heads/submissions/den-channels/tasks/1416/runs/run-20260514-def456/attempt-001",
            ConvenienceRef: "refs/heads/submissions/den-channels/tasks/1416/current",
            BaseBranch: "main",
            BaseCommit: Sha("cccccccccccccccccccccccccccccccccccccccc"),
            HeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            TargetBranch: "task/1416-den-channels",
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Approved,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
