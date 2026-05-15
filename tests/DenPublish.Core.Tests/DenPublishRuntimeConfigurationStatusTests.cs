using DenPublish.Api;
using Microsoft.Extensions.Configuration;

namespace DenPublish.Core.Tests;

public sealed class DenPublishRuntimeConfigurationStatusTests
{
    [Fact]
    public void GetStatus_RedactsCanonicalRemoteUrlAndReportsLiveDisabled()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["DenPublish:WorkspaceRoot"] = "/home/agents/runtime/den-publish/workspaces",
            ["DenPublish:AuditFilePath"] = "/home/agents/runtime/den-publish/audit/promotion-validation.jsonl",
            ["DenPublish:TargetPolicy:CanonicalRemoteUrl"] = "https://deploy-user:super-secret-token@example.invalid/FuzzySlipper/den-publish.git",
        });
        var provider = new DenPublishRuntimeConfigurationStatusProvider(configuration);

        var status = provider.GetStatus();

        Assert.Equal("den-publish", status.Service);
        Assert.Equal("den-publish-runtime-config-v1", status.ConfigurationContract);
        Assert.False(status.LivePublishing.Enabled);
        Assert.False(status.LivePublishing.Configured);
        Assert.False(status.LiveCredentialPolicy.Configured);
        Assert.Equal("[missing]", status.LiveCredentialPolicy.Display);
        Assert.True(status.WorkspaceRoot.Configured);
        Assert.Equal("/home/agents/runtime/den-publish/workspaces", status.WorkspaceRoot.Value);
        Assert.True(status.AuditFilePath.Configured);
        Assert.Equal("/home/agents/runtime/den-publish/audit/promotion-validation.jsonl", status.AuditFilePath.Value);
        Assert.True(status.CanonicalRemoteUrl.Configured);
        Assert.Equal("[redacted]", status.CanonicalRemoteUrl.Value);
        Assert.NotNull(status.CanonicalRemoteUrl.Fingerprint);
        Assert.DoesNotContain("super-secret-token", status.CanonicalRemoteUrl.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", status.CanonicalRemoteUrl.Display, StringComparison.Ordinal);
        Assert.Contains("example.invalid", status.CanonicalRemoteUrl.Display, StringComparison.Ordinal);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void GetStatus_WarnsWhenManagedWorkspaceRootIsMissing()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["DenPublish:AuditFilePath"] = "/tmp/audit.jsonl",
            ["DenPublish:Publishing:Enabled"] = "true",
        });
        var provider = new DenPublishRuntimeConfigurationStatusProvider(configuration);

        var status = provider.GetStatus();

        Assert.False(status.WorkspaceRoot.Configured);
        Assert.True(status.LivePublishing.Enabled);
        Assert.True(status.LivePublishing.Configured);
        Assert.Contains(status.Warnings, warning => warning.Code == "workspace_root_missing");
        Assert.Contains(status.Warnings, warning => warning.Code == "canonical_remote_url_missing");
        Assert.Contains(status.Warnings, warning => warning.Code == "live_credential_policy_missing");
    }

    [Fact]
    public void GetStatus_RedactsExplicitCredentialPolicyWhenConfigured()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["DenPublish:WorkspaceRoot"] = "/home/agents/runtime/den-publish/workspaces",
            ["DenPublish:AuditFilePath"] = "/home/agents/runtime/den-publish/audit/promotion-validation.jsonl",
            ["DenPublish:TargetPolicy:CanonicalRemoteUrl"] = "git@github.com:FuzzySlipper/den-publish.git",
            ["DenPublish:Publishing:Enabled"] = "true",
            ["DenPublish:Publishing:CredentialMode"] = "ssh_command",
            ["DenPublish:Publishing:GitSshCommand"] = "ssh -i /run/den-publish/private-key -o IdentitiesOnly=yes",
        });
        var provider = new DenPublishRuntimeConfigurationStatusProvider(configuration);

        var status = provider.GetStatus();

        Assert.True(status.LivePublishing.Enabled);
        Assert.True(status.LiveCredentialPolicy.Configured);
        Assert.Equal("[redacted]", status.LiveCredentialPolicy.Value);
        Assert.Equal("ssh_command", status.LiveCredentialPolicy.Display);
        Assert.NotNull(status.LiveCredentialPolicy.Fingerprint);
        Assert.DoesNotContain("private-key", status.LiveCredentialPolicy.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", status.LiveCredentialPolicy.Display, StringComparison.Ordinal);
        Assert.DoesNotContain(status.Warnings, warning => warning.Code == "live_credential_policy_missing");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
