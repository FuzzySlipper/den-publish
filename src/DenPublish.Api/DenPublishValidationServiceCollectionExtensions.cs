using DenPublish.Core;

namespace DenPublish.Api;

public static class DenPublishValidationServiceCollectionExtensions
{
    public static IServiceCollection AddDenPublishValidation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var projectTargetPolicies = ReadProjectTargetPolicies(configuration);
        var codeGateAccessPolicies = ReadCodeGateAccessPolicies(configuration);
        ICodeGateAccessPolicyProvider codeGateAccessPolicyProvider = codeGateAccessPolicies.Count == 0
            ? SubmissionCodeGateAccessPolicyProvider.Instance
            : new ConfiguredCodeGateAccessPolicyProvider(codeGateAccessPolicies);

        services.AddSingleton<IGitCommandRunner>(new ProcessGitCommandRunner(TimeSpan.FromSeconds(30)));
        services.AddSingleton<ICodeGateAccessPolicyProvider>(codeGateAccessPolicyProvider);
        services.AddSingleton<ICodeGateRefResolver, GitLsRemoteCodeGateRefResolver>();
        services.AddSingleton<ISubmissionFetcher, GitSubmissionFetcher>();
        services.AddSingleton<IChangedFileScopeValidator, GitChangedFileScopeValidator>();
        services.AddSingleton<ISubmissionAncestryValidator, GitSubmissionAncestryValidator>();
        services.AddSingleton<IPromotionPublisher, DryRunPromotionPublisher>();
        services.AddSingleton<ILivePromotionPublisher>(provider => ReadLivePromotionPublisher(configuration, provider.GetRequiredService<IGitCommandRunner>()));
        services.AddSingleton<IWorkspacePathResolver>(_ => ReadWorkspacePathResolver(configuration));
        services.AddSingleton<IPromotionPolicyContextResolver>(_ => ReadPromotionPolicyContextResolver(configuration));
        services.AddSingleton<IDenPublishRuntimeConfigurationStatusProvider>(_ => new DenPublishRuntimeConfigurationStatusProvider(configuration));

        services.AddSingleton<IPromotionAuditStore>(_ => new FilePromotionAuditStore(ReadAuditFilePath(configuration)));
        services.AddSingleton<IPromotionValidationWorkflow>(provider =>
        {
            IPublishEngine preflight = new PublishValidationEngine();
            preflight = new CodeGatePublishValidationEngine(preflight, provider.GetRequiredService<ICodeGateRefResolver>());
            preflight = new PublishPolicyValidationEngine(preflight, ReadTargetPolicy(configuration, projectTargetPolicies));

            var coreWorkflow = new PromotionValidationWorkflow(
                preflight,
                provider.GetRequiredService<ISubmissionFetcher>(),
                provider.GetRequiredService<IChangedFileScopeValidator>(),
                provider.GetRequiredService<ISubmissionAncestryValidator>());

            return new AuditedPromotionValidationWorkflow(
                coreWorkflow,
                provider.GetRequiredService<IPromotionAuditStore>());
        });

        return services;
    }

    private static PublishTargetPolicy ReadTargetPolicy(
        IConfiguration configuration,
        IReadOnlyDictionary<string, ProjectPublishTargetPolicy> projectTargetPolicies)
    {
        var section = configuration.GetSection("DenPublish:TargetPolicy");
        var targetRemoteName = section["TargetRemoteName"] ?? "canonical";
        var canonicalRemoteUrl = section["CanonicalRemoteUrl"] ?? string.Empty;
        var pushBranchPrefixes = section.GetSection("PushBranchPrefixes").Get<string[]>() ?? ["task/"];
        var fastForwardBranches = section.GetSection("FastForwardBranches").Get<string[]>() ?? ["main"];

        return new PublishTargetPolicy(
            targetRemoteName,
            canonicalRemoteUrl,
            pushBranchPrefixes,
            fastForwardBranches,
            projectTargetPolicies);
    }

    private static IReadOnlyDictionary<string, ProjectPublishTargetPolicy> ReadProjectTargetPolicies(IConfiguration configuration)
    {
        var result = new Dictionary<string, ProjectPublishTargetPolicy>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection("DenPublish:Projects").GetChildren())
        {
            var projectId = child["ProjectId"] ?? child.Key;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                continue;
            }

            result[projectId] = new ProjectPublishTargetPolicy(
                ProjectId: projectId,
                TargetRemoteName: child["TargetRemoteName"],
                CanonicalRemoteUrl: child["CanonicalRemoteUrl"],
                PushBranchPrefixes: child.GetSection("PushBranchPrefixes").Get<string[]>(),
                FastForwardBranches: child.GetSection("FastForwardBranches").Get<string[]>());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, CodeGateProjectAccessPolicy> ReadCodeGateAccessPolicies(IConfiguration configuration)
    {
        var result = new Dictionary<string, CodeGateProjectAccessPolicy>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection("DenPublish:Projects").GetChildren())
        {
            var projectId = child["ProjectId"] ?? child.Key;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                continue;
            }

            var codeGateRemoteUrl = child["CodeGateRemoteUrl"];
            var gitSshCommand = child["CodeGateGitSshCommand"];
            if (string.IsNullOrWhiteSpace(codeGateRemoteUrl) && string.IsNullOrWhiteSpace(gitSshCommand))
            {
                continue;
            }

            result[projectId] = new CodeGateProjectAccessPolicy(projectId, codeGateRemoteUrl, gitSshCommand);
        }

        return result;
    }

    private static ILivePromotionPublisher ReadLivePromotionPublisher(IConfiguration configuration, IGitCommandRunner git)
    {
        var enabled = configuration.GetValue<bool>("DenPublish:Publishing:Enabled");
        if (!enabled)
        {
            return new DisabledLivePromotionPublisher();
        }

        var credentialPolicy = ReadCredentialPolicy(configuration);
        return credentialPolicy.IsConfigured
            ? new GitPromotionPublisher(git, credentialPolicy)
            : new DisabledLivePromotionPublisher();
    }

    private static GitPromotionCredentialPolicy ReadCredentialPolicy(IConfiguration configuration)
    {
        var section = configuration.GetSection("DenPublish:Publishing");
        var credentialMode = section["CredentialMode"];
        if (string.Equals(credentialMode, "ssh_command", StringComparison.Ordinal))
        {
            return GitPromotionCredentialPolicy.ExplicitSshCommand(section["GitSshCommand"] ?? string.Empty);
        }

        return GitPromotionCredentialPolicy.Unconfigured;
    }

    private static IWorkspacePathResolver ReadWorkspacePathResolver(IConfiguration configuration)
    {
        var workspaceRoot = configuration["DenPublish:WorkspaceRoot"];
        return string.IsNullOrWhiteSpace(workspaceRoot)
            ? RequestWorkspacePathResolver.Instance
            : new ConfiguredWorkspacePathResolver(workspaceRoot);
    }

    private static IPromotionPolicyContextResolver ReadPromotionPolicyContextResolver(IConfiguration configuration)
    {
        var section = configuration.GetSection("DenPublish:PromotionPolicy");
        var trustedOrchestrators = section.GetSection("TrustedOrchestrators").Get<string[]>() ?? [];
        var trustedMode = ParsePolicyMode(section["TrustedOrchestratorMode"]);
        var trustRequestBodyRequestedBy = section.GetValue<bool>("TrustRequestBodyRequestedBy");
        if (trustedOrchestrators.Length == 0)
        {
            return DefaultPromotionPolicyContextResolver.Instance;
        }

        return new ConfiguredPromotionPolicyContextResolver(new TrustedOrchestratorPolicyOptions(
            new HashSet<string>(trustedOrchestrators.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal),
            trustedMode,
            TrustRequestBodyRequestedBy: trustRequestBodyRequestedBy));
    }

    private static PromotionPolicyMode ParsePolicyMode(string? value)
        => value switch
        {
            "audit_warn" => PromotionPolicyMode.AuditWarn,
            "defensive" => PromotionPolicyMode.Defensive,
            _ => PromotionPolicyMode.Strict
        };

    private static string ReadAuditFilePath(IConfiguration configuration)
        => configuration["DenPublish:AuditFilePath"]
           ?? "/var/lib/den-publish/audit/promotion-validation.jsonl";
}
