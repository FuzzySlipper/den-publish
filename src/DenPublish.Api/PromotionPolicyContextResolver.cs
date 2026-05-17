using DenPublish.Core;

namespace DenPublish.Api;

public interface IPromotionPolicyContextResolver
{
    PromotionPolicyContext Resolve(PromotionValidationApiRequest request);
}

public sealed class DefaultPromotionPolicyContextResolver : IPromotionPolicyContextResolver
{
    public static DefaultPromotionPolicyContextResolver Instance { get; } = new();

    private DefaultPromotionPolicyContextResolver()
    {
    }

    public PromotionPolicyContext Resolve(PromotionValidationApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PromotionPolicyContext.StrictWorker;
    }
}

public sealed record TrustedOrchestratorPolicyOptions(
    IReadOnlySet<string> TrustedOrchestrators,
    PromotionPolicyMode TrustedMode,
    bool TrustRequestBodyRequestedBy = false)
{
    public static TrustedOrchestratorPolicyOptions Strict { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        PromotionPolicyMode.Strict);
}

public sealed class ConfiguredPromotionPolicyContextResolver(TrustedOrchestratorPolicyOptions options) : IPromotionPolicyContextResolver
{
    public PromotionPolicyContext Resolve(PromotionValidationApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // `requestedBy` is request-body audit metadata, not an authentication boundary.
        // Only honor it when an operator explicitly opts into this local/LAN trust model.
        if (options.TrustRequestBodyRequestedBy && options.TrustedOrchestrators.Contains(request.Decision.RequestedBy))
        {
            return new PromotionPolicyContext(PromotionCallerTrust.TrustedOrchestrator, options.TrustedMode);
        }

        return PromotionPolicyContext.StrictWorker;
    }
}
