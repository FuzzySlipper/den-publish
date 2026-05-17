using DenPublish.Api;
using Microsoft.Extensions.Configuration;

namespace DenPublish.Core.Tests;

public sealed class DenPublishRuntimeConfigurationStatusTests
{
    [Fact]
    public void GetStatus_ReportsRedactedProjectPolicies()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DenPublish:WorkspaceRoot"] = "/home/agents/runtime/den-publish/workspaces",
                ["DenPublish:AuditFilePath"] = "/home/agents/runtime/den-publish/audit/promotion-validation.jsonl",
                ["DenPublish:Projects:den-channels:CanonicalRemoteUrl"] = "git@github.com:FuzzySlipper/den-channels.git",
                ["DenPublish:Projects:den-channels:CodeGateRemoteUrl"] = "ssh://192.168.1.10:3022/den-channels/den-channels.git",
                ["DenPublish:Projects:den-channels:CodeGateGitSshCommand"] = "ssh -F /dev/null -i /runtime/key -o UserKnownHostsFile=/runtime/known_hosts -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes",
                ["DenPublish:Projects:den-channels:PushBranchPrefixes:0"] = "task/",
                ["DenPublish:Projects:den-channels:FastForwardBranches:0"] = "main"
            })
            .Build();
        var provider = new DenPublishRuntimeConfigurationStatusProvider(configuration);

        var status = provider.GetStatus();

        Assert.Equal("den-publish-runtime-config-v2", status.ConfigurationContract);
        var project = Assert.Single(status.ProjectPolicies);
        Assert.Equal("den-channels", project.ProjectId);
        Assert.Equal("github.com:FuzzySlipper/den-channels.git", project.CanonicalRemoteUrl.Display);
        Assert.Equal("ssh://192.168.1.10:3022/den-channels/den-channels.git", project.CodeGateRemoteUrl.Display);
        Assert.Equal("ssh_command", project.CodeGateReadCredential.Display);
        Assert.NotNull(project.CodeGateReadCredential.Fingerprint);
        Assert.DoesNotContain("/runtime/key", project.CodeGateReadCredential.Value, StringComparison.Ordinal);
        Assert.Equal(["task/"], project.PushBranchPrefixes);
    }

    [Fact]
    public void GetStatus_ReportsIndexedProjectPoliciesWithExplicitProjectId()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DenPublish:WorkspaceRoot"] = "/home/agents/runtime/den-publish/workspaces",
                ["DenPublish:AuditFilePath"] = "/home/agents/runtime/den-publish/audit/promotion-validation.jsonl",
                ["DenPublish:Projects:0:ProjectId"] = "den-channels",
                ["DenPublish:Projects:0:CanonicalRemoteUrl"] = "git@github.com:FuzzySlipper/den-channels.git",
                ["DenPublish:Projects:0:CodeGateRemoteUrl"] = "ssh://192.168.1.10:3022/den-channels/den-channels.git",
                ["DenPublish:Projects:0:CodeGateGitSshCommand"] = "ssh -F /dev/null -i /runtime/key -o UserKnownHostsFile=/runtime/known_hosts -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes",
                ["DenPublish:Projects:0:PushBranchPrefixes:0"] = "task/",
                ["DenPublish:Projects:0:FastForwardBranches:0"] = "main"
            })
            .Build();
        var provider = new DenPublishRuntimeConfigurationStatusProvider(configuration);

        var status = provider.GetStatus();

        var project = Assert.Single(status.ProjectPolicies);
        Assert.Equal("den-channels", project.ProjectId);
        Assert.Equal("DenPublish__Projects__0__CanonicalRemoteUrl", project.CanonicalRemoteUrl.EnvironmentKey);
        Assert.Equal("github.com:FuzzySlipper/den-channels.git", project.CanonicalRemoteUrl.Display);
        Assert.Equal("ssh_command", project.CodeGateReadCredential.Display);
        Assert.Equal(["task/"], project.PushBranchPrefixes);
    }

}
