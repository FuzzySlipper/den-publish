using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class GitPromotionPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"den-publish-git-publisher-tests-{Guid.NewGuid():N}");

    public GitPromotionPublisherTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Publish_PushesValidatedLocalRefToCanonicalUrlForLiveDecision()
    {
        var workspace = Path.Combine(_root, "workspace");
        var canonical = Path.Combine(_root, "canonical.git");
        Directory.CreateDirectory(workspace);
        RunGit(["init", "-q"], workspace);
        RunGit(["config", "user.email", "agent@example.invalid"], workspace);
        RunGit(["config", "user.name", "Den Publish Test"], workspace);
        File.WriteAllText(Path.Combine(workspace, "README.md"), "live publish test\n");
        RunGit(["add", "README.md"], workspace);
        RunGit(["commit", "-q", "-m", "test commit"], workspace);
        var head = RunGit(["rev-parse", "HEAD"], workspace).Trim();
        const string localRef = "refs/den-publish/submissions/sub_live_001";
        RunGit(["update-ref", localRef, head], workspace);
        RunGit(["init", "--bare", "-q", canonical], _root);

        var decision = Decision(validateOnly: false, targetBranch: "task/1424-live-publisher", expectedHead: Sha(head));
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, localRef);
        var publisher = new GitPromotionPublisher(new ProcessGitCommandRunner(TimeSpan.FromSeconds(10)), GitPromotionCredentialPolicy.LocalFileRemoteForTesting());

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation, workspace, canonical));

        Assert.True(result.Succeeded);
        Assert.Equal(PromotionPublishStatus.Published, result.Status);
        Assert.Empty(result.Failures);
        Assert.Empty(result.PlannedCommands);
        Assert.Contains("published", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(head, RunGit(["--git-dir", canonical, "rev-parse", "refs/heads/task/1424-live-publisher^{commit}"], _root).Trim());
    }

    [Fact]
    public void Publish_RejectsValidateOnlyDecisionWithoutRunningGitPush()
    {
        var git = new RecordingGitCommandRunner(new GitCommandResult(0, string.Empty, string.Empty));
        var decision = Decision(validateOnly: true, targetBranch: "task/1424-live-publisher", expectedHead: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_live_001");
        var publisher = new GitPromotionPublisher(git, GitPromotionCredentialPolicy.LocalFileRemoteForTesting());

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation, "/tmp/workspace", "/tmp/canonical.git"));

        Assert.Equal(PromotionPublishStatus.Rejected, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(PublishFailureCode.InvalidRequest, Assert.Single(result.Failures).Code);
        Assert.Empty(git.Commands);
    }

    [Fact]
    public void Publish_FailsClosedWhenGitPushFails()
    {
        var git = new RecordingGitCommandRunner(new GitCommandResult(1, string.Empty, "permission denied"));
        var decision = Decision(validateOnly: false, targetBranch: "task/1424-live-publisher", expectedHead: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_live_001");
        var publisher = new GitPromotionPublisher(git, GitPromotionCredentialPolicy.ExplicitSshCommand("ssh -F /dev/null -i /run/den-publish/id_ed25519 -o UserKnownHostsFile=/run/den-publish/known_hosts -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes"));

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation, "/tmp/workspace", "git@example.invalid:repo.git"));

        Assert.Equal(PromotionPublishStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(PublishFailureCode.GitPushFailed, Assert.Single(result.Failures).Code);
        Assert.Equal([["-C", "/tmp/workspace", "push", "git@example.invalid:repo.git", "refs/den-publish/submissions/sub_live_001:refs/heads/task/1424-live-publisher"]], git.Commands);
    }



    [Fact]
    public void Publish_FailsClosedWithoutExplicitCredentialPolicyBeforeRunningGit()
    {
        var git = new RecordingGitCommandRunner(new GitCommandResult(0, string.Empty, string.Empty));
        var decision = Decision(validateOnly: false, targetBranch: "task/1424-live-publisher", expectedHead: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_live_001");
        var publisher = new GitPromotionPublisher(git, GitPromotionCredentialPolicy.Unconfigured);

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation, "/tmp/workspace", "git@example.invalid:repo.git"));

        Assert.Equal(PromotionPublishStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(PublishFailureCode.CredentialUnavailable, Assert.Single(result.Failures).Code);
        Assert.Empty(git.Commands);
    }

    [Fact]
    public void Publish_RunsGitWithExplicitCredentialEnvironment()
    {
        var git = new RecordingGitCommandRunner(new GitCommandResult(0, string.Empty, string.Empty));
        var decision = Decision(validateOnly: false, targetBranch: "task/1424-live-publisher", expectedHead: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var validation = ValidatedWorkflowResult(decision.ExpectedHeadCommit, "refs/den-publish/submissions/sub_live_001");
        var publisher = new GitPromotionPublisher(git, GitPromotionCredentialPolicy.ExplicitSshCommand("ssh -F /dev/null -i /run/den-publish/id_ed25519 -o UserKnownHostsFile=/run/den-publish/known_hosts -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes"));

        var result = publisher.Publish(new PromotionPublishRequest(decision, validation, "/tmp/workspace", "git@example.invalid:repo.git"));

        Assert.True(result.Succeeded);
        Assert.Equal("ssh -F /dev/null -i /run/den-publish/id_ed25519 -o UserKnownHostsFile=/run/den-publish/known_hosts -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes", git.Environments.Single()["GIT_SSH_COMMAND"]);
        Assert.Equal("0", git.Environments.Single()["GIT_TERMINAL_PROMPT"]);
    }

    private sealed class RecordingGitCommandRunner(params GitCommandResult[] results) : IGitCommandRunner
    {
        private int _index;
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public GitCommandResult Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments.ToArray());
            Environments.Add(environment ?? new Dictionary<string, string>());
            return results[Math.Min(_index++, results.Length - 1)];
        }
    }

    private static PromotionValidationWorkflowResult ValidatedWorkflowResult(GitSha head, string localRef)
        => new(
            PublishValidationResult.Approved("workflow ok", ["all checks passed"]),
            LocalRef: localRef,
            FetchedHeadCommit: head);

    private static PublishDecision Decision(bool validateOnly, string targetBranch, GitSha expectedHead)
        => new(
            DecisionId: "pub_live_001",
            ProjectId: "den-publish-smoke",
            TaskId: 1424,
            SubmissionId: "sub_live_001",
            RequestedBy: "sysadmin-test",
            Operation: PublishOperation.PushBranch,
            TargetRemote: "canonical",
            TargetBranch: targetBranch,
            ExpectedHeadCommit: expectedHead,
            ExpectedBaseBranch: "main",
            ReviewRoundId: 0,
            ScopeOverrideIds: [],
            ValidateOnly: validateOnly,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T22:10:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }

    private static string RunGit(string[] arguments, string workingDirectory)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
        return stdout;
    }
}
