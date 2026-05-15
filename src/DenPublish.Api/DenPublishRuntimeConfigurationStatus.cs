using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DenPublish.Api;

public interface IDenPublishRuntimeConfigurationStatusProvider
{
    DenPublishRuntimeConfigurationStatus GetStatus();
}

public sealed class DenPublishRuntimeConfigurationStatusProvider(IConfiguration configuration) : IDenPublishRuntimeConfigurationStatusProvider
{
    public DenPublishRuntimeConfigurationStatus GetStatus()
    {
        var workspaceRoot = ReadPlainSetting("DenPublish:WorkspaceRoot", "DenPublish__WorkspaceRoot", requiredForProduction: true);
        var auditFilePath = ReadPlainSetting("DenPublish:AuditFilePath", "DenPublish__AuditFilePath", requiredForProduction: true);
        var canonicalRemoteUrl = ReadRedactedSetting("DenPublish:TargetPolicy:CanonicalRemoteUrl", "DenPublish__TargetPolicy__CanonicalRemoteUrl", requiredForProduction: true);
        var livePublishing = ReadBooleanSetting("DenPublish:Publishing:Enabled", "DenPublish__Publishing__Enabled");
        var liveCredentialPolicy = ReadCredentialPolicySetting();

        var warnings = new List<DenPublishRuntimeConfigurationWarning>();
        if (!workspaceRoot.Configured)
        {
            warnings.Add(new DenPublishRuntimeConfigurationWarning(
                "workspace_root_missing",
                "DenPublish:WorkspaceRoot is not configured; service-owned managed workspaces are required for production."));
        }

        if (!auditFilePath.Configured)
        {
            warnings.Add(new DenPublishRuntimeConfigurationWarning(
                "audit_file_path_missing",
                "DenPublish:AuditFilePath is not configured; durable audit persistence should be explicit for production."));
        }

        if (!canonicalRemoteUrl.Configured)
        {
            warnings.Add(new DenPublishRuntimeConfigurationWarning(
                "canonical_remote_url_missing",
                "DenPublish:TargetPolicy:CanonicalRemoteUrl is not configured; publish target policy cannot verify canonical remotes."));
        }

        if (livePublishing.Enabled)
        {
            warnings.Add(new DenPublishRuntimeConfigurationWarning(
                "live_publishing_enabled",
                "Live publishing is enabled; verify credential storage, branch scope, audit, and rollback posture before using /promotion/publish."));

            if (!liveCredentialPolicy.Configured)
            {
                warnings.Add(new DenPublishRuntimeConfigurationWarning(
                    "live_credential_policy_missing",
                    "Live publishing is enabled but no explicit credential policy is configured; ambient Git/SSH credentials are not accepted."));
            }
        }

        return new DenPublishRuntimeConfigurationStatus(
            Service: "den-publish",
            ConfigurationContract: "den-publish-runtime-config-v1",
            WorkspaceRoot: workspaceRoot,
            AuditFilePath: auditFilePath,
            CanonicalRemoteUrl: canonicalRemoteUrl,
            LivePublishing: livePublishing,
            LiveCredentialPolicy: liveCredentialPolicy,
            Warnings: warnings);
    }

    private DenPublishRuntimeConfigurationSetting ReadPlainSetting(string key, string environmentKey, bool requiredForProduction)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? DenPublishRuntimeConfigurationSetting.Missing(key, environmentKey, requiredForProduction)
            : DenPublishRuntimeConfigurationSetting.Plain(key, environmentKey, value, requiredForProduction);
    }

    private DenPublishRuntimeConfigurationSetting ReadRedactedSetting(string key, string environmentKey, bool requiredForProduction)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? DenPublishRuntimeConfigurationSetting.Missing(key, environmentKey, requiredForProduction)
            : DenPublishRuntimeConfigurationSetting.Redacted(key, environmentKey, DisplayRemoteWithoutCredentials(value), Fingerprint(value), requiredForProduction);
    }

    private DenPublishRuntimeConfigurationSetting ReadCredentialPolicySetting()
    {
        var mode = configuration["DenPublish:Publishing:CredentialMode"];
        if (!string.Equals(mode, "ssh_command", StringComparison.Ordinal))
        {
            return DenPublishRuntimeConfigurationSetting.Missing(
                "DenPublish:Publishing:CredentialMode",
                "DenPublish__Publishing__CredentialMode",
                requiredForProduction: false);
        }

        var command = configuration["DenPublish:Publishing:GitSshCommand"];
        if (string.IsNullOrWhiteSpace(command))
        {
            return DenPublishRuntimeConfigurationSetting.Missing(
                "DenPublish:Publishing:GitSshCommand",
                "DenPublish__Publishing__GitSshCommand",
                requiredForProduction: false);
        }

        return DenPublishRuntimeConfigurationSetting.Redacted(
            "DenPublish:Publishing:GitSshCommand",
            "DenPublish__Publishing__GitSshCommand",
            display: "ssh_command",
            fingerprint: Fingerprint($"ssh_command:{command}"),
            requiredForProduction: false);
    }

    private DenPublishRuntimeBooleanSetting ReadBooleanSetting(string key, string environmentKey)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new DenPublishRuntimeBooleanSetting(key, environmentKey, Configured: false, Enabled: false);
        }

        return new DenPublishRuntimeBooleanSetting(key, environmentKey, Configured: true, Enabled: bool.TryParse(raw, out var enabled) && enabled);
    }

    private static string DisplayRemoteWithoutCredentials(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty
            };
            return builder.Uri.ToString();
        }

        var at = value.IndexOf('@');
        var colon = value.IndexOf(':', at + 1);
        if (at > 0 && colon > at)
        {
            return value[(at + 1)..];
        }

        return "[configured]";
    }

    private static string Fingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

public sealed record DenPublishRuntimeConfigurationStatus(
    string Service,
    string ConfigurationContract,
    DenPublishRuntimeConfigurationSetting WorkspaceRoot,
    DenPublishRuntimeConfigurationSetting AuditFilePath,
    DenPublishRuntimeConfigurationSetting CanonicalRemoteUrl,
    DenPublishRuntimeBooleanSetting LivePublishing,
    DenPublishRuntimeConfigurationSetting LiveCredentialPolicy,
    IReadOnlyList<DenPublishRuntimeConfigurationWarning> Warnings);

public sealed record DenPublishRuntimeConfigurationSetting(
    string Key,
    string EnvironmentKey,
    bool Configured,
    string Value,
    string Display,
    string? Fingerprint,
    bool RequiredForProduction)
{
    public static DenPublishRuntimeConfigurationSetting Missing(string key, string environmentKey, bool requiredForProduction)
        => new(key, environmentKey, Configured: false, Value: string.Empty, Display: "[missing]", Fingerprint: null, requiredForProduction);

    public static DenPublishRuntimeConfigurationSetting Plain(string key, string environmentKey, string value, bool requiredForProduction)
        => new(key, environmentKey, Configured: true, Value: value, Display: value, Fingerprint: null, requiredForProduction);

    public static DenPublishRuntimeConfigurationSetting Redacted(string key, string environmentKey, string display, string fingerprint, bool requiredForProduction)
        => new(key, environmentKey, Configured: true, Value: "[redacted]", Display: display, Fingerprint: fingerprint, requiredForProduction);
}

public sealed record DenPublishRuntimeBooleanSetting(
    string Key,
    string EnvironmentKey,
    bool Configured,
    bool Enabled);

public sealed record DenPublishRuntimeConfigurationWarning(string Code, string Message);

public static class DenPublishRuntimeConfigurationStatusEndpoints
{
    public static IEndpointRouteBuilder MapDenPublishRuntimeConfigurationStatusEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/config/status", static (IDenPublishRuntimeConfigurationStatusProvider provider) => Results.Ok(provider.GetStatus()));
        return app;
    }
}
