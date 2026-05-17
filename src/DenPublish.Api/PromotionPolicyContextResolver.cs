using DenPublish.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace DenPublish.Api;

public interface IPromotionPolicyContextResolver
{
    PromotionPolicyContext Resolve(PromotionValidationApiRequest request);

    PromotionPolicyContext Resolve(PromotionValidationApiRequest request, IHeaderDictionary headers) =>
        Resolve(request);
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
    bool TrustRequestBodyRequestedBy = false,
    bool TrustForwardedCallerHeaders = false,
    string ForwardedRequestedByHeaderName = "X-Den-Requested-By",
    string ForwardedCallerTrustHeaderName = "X-Den-Caller-Trust",
    string ForwardedPolicyModeHeaderName = "X-Den-Promotion-Policy-Mode")
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

    public PromotionPolicyContext Resolve(PromotionValidationApiRequest request, IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(headers);

        if (options.TrustForwardedCallerHeaders && IsTrustedForwardedCaller(request, headers))
        {
            return new PromotionPolicyContext(PromotionCallerTrust.TrustedOrchestrator, options.TrustedMode);
        }

        return Resolve(request);
    }

    private bool IsTrustedForwardedCaller(PromotionValidationApiRequest request, IHeaderDictionary headers)
    {
        var requestedBy = SingleHeader(headers, options.ForwardedRequestedByHeaderName);
        if (string.IsNullOrWhiteSpace(requestedBy)
            || !string.Equals(requestedBy, request.Decision.RequestedBy, StringComparison.Ordinal)
            || !options.TrustedOrchestrators.Contains(requestedBy))
        {
            return false;
        }

        var callerTrust = SingleHeader(headers, options.ForwardedCallerTrustHeaderName);
        if (!string.Equals(callerTrust, "trusted_orchestrator", StringComparison.Ordinal))
        {
            return false;
        }

        var policyMode = SingleHeader(headers, options.ForwardedPolicyModeHeaderName);
        return string.IsNullOrWhiteSpace(policyMode) || TryParsePolicyMode(policyMode) == options.TrustedMode;
    }

    private static string? SingleHeader(IHeaderDictionary headers, string name)
    {
        return headers.TryGetValue(name, out StringValues values) && values.Count == 1
            ? values[0]
            : null;
    }

    private static PromotionPolicyMode? TryParsePolicyMode(string value) => value switch
    {
        "strict" => PromotionPolicyMode.Strict,
        "audit_warn" => PromotionPolicyMode.AuditWarn,
        "defensive" => PromotionPolicyMode.Defensive,
        _ => null
    };
}
