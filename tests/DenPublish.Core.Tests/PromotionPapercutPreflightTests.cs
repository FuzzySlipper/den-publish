using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PromotionPapercutPreflightTests
{
    [Fact]
    public void SshCommandPolicy_AcceptsOnlyFullyHardenedSshCommand()
    {
        var command = "ssh -F /dev/null -i /run/den-publish/code-gate/id_ed25519 "
            + "-o UserKnownHostsFile=/run/den-publish/code-gate/known_hosts "
            + "-o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes";

        var result = SshCommandSafetyPolicy.Validate(command);

        Assert.True(result.IsSafe);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void SshCommandPolicy_ReportsEachMissingHardeningOption()
    {
        var result = SshCommandSafetyPolicy.Validate("ssh -i /run/key");

        Assert.False(result.IsSafe);
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_config_not_disabled");
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_known_hosts_missing");
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_identities_only_missing");
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_batch_mode_missing");
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_strict_host_key_checking_missing");
    }


    [Fact]
    public void SshCommandPolicy_RejectsKnownHostsNoneBecauseItDisablesHostPinning()
    {
        var command = "ssh -F /dev/null -i /run/key -o UserKnownHostsFile=none "
            + "-o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes";

        var result = SshCommandSafetyPolicy.Validate(command);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_known_hosts_missing");
    }


    [Fact]
    public void SshCommandPolicy_RejectsUnsafeFirstKnownHostsWhenDuplicateOptionLaterLooksSafe()
    {
        var command = "ssh -F /dev/null -i /run/key -o UserKnownHostsFile=none "
            + "-o UserKnownHostsFile=/run/known_hosts "
            + "-o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes";

        var result = SshCommandSafetyPolicy.Validate(command);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_known_hosts_missing");
    }


    [Fact]
    public void SshCommandPolicy_RejectsKnownHostsDevNullWithTrailingWhitespace()
    {
        var command = "ssh -F /dev/null -i /run/key -o 'UserKnownHostsFile=/dev/null ' "
            + "-o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes";

        var result = SshCommandSafetyPolicy.Validate(command);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Issues, issue => issue.Code == "ssh_known_hosts_missing");
    }

    [Fact]
    public void ConfiguredCodeGateAccessPolicyProvider_RejectsWeakProjectSshCommandBeforeGitUse()
    {
        var submission = ApprovedSubmission();
        var provider = new ConfiguredCodeGateAccessPolicyProvider(new Dictionary<string, CodeGateProjectAccessPolicy>
        {
            ["den-channels"] = new(
                ProjectId: "den-channels",
                CodeGateRemoteUrl: submission.CodeGateRemoteUrl,
                GitSshCommand: "ssh -i /run/key")
        });

        var access = provider.Resolve(submission);

        Assert.False(access.Succeeded);
        Assert.NotNull(access.Failure);
        Assert.Equal(PublishFailureCode.CredentialUnavailable, access.Failure.Code);
        Assert.Contains("ssh -F /dev/null", access.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/key", access.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspacePreflight_ReportsMixedOwnershipAndGitConfigLockRisk()
    {
        var snapshot = new WorkspacePreflightSnapshot(
            WorkspacePath: "/var/lib/den-publish/workspaces/den-channels",
            ExpectedOwner: "agent",
            Entries:
            [
                new WorkspacePreflightEntry(".", Owner: "agent", Group: "agents", IsDirectory: true, IsSymlink: false, Mode: "0755", SymlinkTarget: null),
                new WorkspacePreflightEntry(".git", Owner: "root", Group: "root", IsDirectory: true, IsSymlink: false, Mode: "0755", SymlinkTarget: null),
                new WorkspacePreflightEntry(".git/config", Owner: "root", Group: "root", IsDirectory: false, IsSymlink: false, Mode: "0644", SymlinkTarget: null)
            ]);

        var report = WorkspacePreflightAnalyzer.Analyze(snapshot);

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, finding => finding.Code == "mixed_workspace_ownership");
        Assert.Contains(report.Findings, finding => finding.Code == "git_config_lock_risk");
        Assert.Contains(report.Findings, finding => finding.Guidance.Contains("sudo -n -u agent", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspacePreflight_ReportsUnsafeOpenSshConfigSymlinkTarget()
    {
        var snapshot = new WorkspacePreflightSnapshot(
            WorkspacePath: "/var/lib/den-publish/workspaces/den-channels",
            ExpectedOwner: "agent",
            Entries:
            [
                new WorkspacePreflightEntry(".ssh/config", Owner: "agent", Group: "agents", IsDirectory: false, IsSymlink: true, Mode: "0777", SymlinkTarget: "/tmp/world-writable-ssh-config")
            ]);

        var report = WorkspacePreflightAnalyzer.Analyze(snapshot);

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, finding => finding.Code == "ssh_config_symlink_review_required");
        Assert.Contains(report.Findings, finding => finding.Code == "ssh_config_permissions_too_open");
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
