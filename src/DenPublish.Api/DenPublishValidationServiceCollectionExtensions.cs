using DenPublish.Core;

namespace DenPublish.Api;

public static class DenPublishValidationServiceCollectionExtensions
{
    public static IServiceCollection AddDenPublishValidation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IGitCommandRunner>(new ProcessGitCommandRunner(TimeSpan.FromSeconds(30)));
        services.AddSingleton<ICodeGateRefResolver, GitLsRemoteCodeGateRefResolver>();
        services.AddSingleton<ISubmissionFetcher, GitSubmissionFetcher>();
        services.AddSingleton<IChangedFileScopeValidator, GitChangedFileScopeValidator>();
        services.AddSingleton<ISubmissionAncestryValidator, GitSubmissionAncestryValidator>();
        services.AddSingleton<IPromotionPublisher, DryRunPromotionPublisher>();
        services.AddSingleton<IWorkspacePathResolver>(_ => ReadWorkspacePathResolver(configuration));

        services.AddSingleton<IPromotionAuditStore>(_ => new FilePromotionAuditStore(ReadAuditFilePath(configuration)));
        services.AddSingleton<IPromotionValidationWorkflow>(provider =>
        {
            IPublishEngine preflight = new PublishValidationEngine();
            preflight = new CodeGatePublishValidationEngine(preflight, provider.GetRequiredService<ICodeGateRefResolver>());
            preflight = new PublishPolicyValidationEngine(preflight, ReadTargetPolicy(configuration));

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

    private static PublishTargetPolicy ReadTargetPolicy(IConfiguration configuration)
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
            fastForwardBranches);
    }

    private static IWorkspacePathResolver ReadWorkspacePathResolver(IConfiguration configuration)
    {
        var workspaceRoot = configuration["DenPublish:WorkspaceRoot"];
        return string.IsNullOrWhiteSpace(workspaceRoot)
            ? RequestWorkspacePathResolver.Instance
            : new ConfiguredWorkspacePathResolver(workspaceRoot);
    }

    private static string ReadAuditFilePath(IConfiguration configuration)
        => configuration["DenPublish:AuditFilePath"]
           ?? "/var/lib/den-publish/audit/promotion-validation.jsonl";
}
