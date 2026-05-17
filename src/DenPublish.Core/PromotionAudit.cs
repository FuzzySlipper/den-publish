using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenPublish.Core;

public interface IPromotionAuditStore
{
    PromotionAuditLookupResult FindByDecisionId(string decisionId);
    PromotionAuditAppendResult Append(PromotionAuditRecord record);
}

public sealed record PromotionAuditLookupResult(bool Succeeded, PromotionAuditRecord? Record, string ErrorMessage)
{
    public bool Found => Record is not null;

    public static PromotionAuditLookupResult Missing()
        => new(true, null, string.Empty);

    public static PromotionAuditLookupResult FoundRecord(PromotionAuditRecord record)
        => new(true, record, string.Empty);

    public static PromotionAuditLookupResult Failed(string errorMessage)
        => new(false, null, errorMessage);
}

public sealed record PromotionAuditAppendResult(bool Succeeded, string ErrorMessage)
{
    public static PromotionAuditAppendResult Appended()
        => new(true, string.Empty);

    public static PromotionAuditAppendResult Failed(string errorMessage)
        => new(false, errorMessage);
}

public sealed record PromotionAuditScopeOverride(
    string OverrideId,
    string FindingId,
    string Reason,
    string ApprovedBy);

public sealed record PromotionAuditWarning(
    PublishFailureCode Code,
    string Message,
    string Reason);

public sealed record PromotionAuditOrchestratorOverride(
    string UnclassifiedFailurePolicy,
    string Reason,
    IReadOnlyList<string> ExpectedRiskCategories);

public sealed record PromotionAuditPolicyContext(
    PromotionCallerTrust CallerTrust,
    PromotionPolicyMode Mode);

public sealed record PromotionAuditRecord(
    DateTimeOffset RecordedAt,
    string DecisionId,
    string ProjectId,
    int TaskId,
    string SubmissionId,
    PublishValidationStatus Status,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ValidationFailure> Failures,
    string? LocalRef,
    GitSha? FetchedHeadCommit,
    IReadOnlyList<PromotionAuditScopeOverride>? ScopeOverrides = null,
    IReadOnlyList<PromotionAuditWarning>? Warnings = null,
    PromotionAuditOrchestratorOverride? OrchestratorOverride = null,
    PromotionAuditPolicyContext? PolicyContext = null,
    string? RequestedBy = null,
    PublishOperation? Operation = null,
    string? TargetRemote = null,
    string? TargetBranch = null,
    bool? ValidateOnly = null,
    int? ReviewRoundId = null,
    string? ExpectedBaseBranch = null,
    IReadOnlyList<string>? ScopeOverrideIds = null)
{
    public IReadOnlyList<PromotionAuditScopeOverride> ScopeOverrides { get; init; } = ScopeOverrides ?? [];

    public IReadOnlyList<PromotionAuditWarning> Warnings { get; init; } = Warnings ?? [];

    public IReadOnlyList<string> ScopeOverrideIds { get; init; } = ScopeOverrideIds ?? [];
}

public sealed class AuditedPromotionValidationWorkflow(
    IPromotionValidationWorkflow inner,
    IPromotionAuditStore auditStore,
    Func<DateTimeOffset>? now = null) : IPromotionValidationWorkflow
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existingAudit = auditStore.FindByDecisionId(request.Decision.DecisionId);
        if (!existingAudit.Succeeded)
        {
            return new PromotionValidationWorkflowResult(
                PublishValidationResult.Failed(
                    "promotion validation audit lookup failed",
                    new ValidationFailure(
                        PublishFailureCode.AuditFailed,
                        $"Promotion validation audit lookup failed: {existingAudit.ErrorMessage}")),
                LocalRef: null,
                FetchedHeadCommit: null);
        }

        if (existingAudit.Record is not null)
        {
            return ReplayOrRejectConflict(request, existingAudit.Record);
        }

        var result = inner.Validate(request);
        var auditRecord = ToAuditRecord(request, result, _now());
        var appendResult = auditStore.Append(auditRecord);

        if (appendResult.Succeeded)
        {
            return result;
        }

        return new PromotionValidationWorkflowResult(
            PublishValidationResult.Failed(
                "promotion validation audit could not be persisted",
                new ValidationFailure(
                    PublishFailureCode.AuditFailed,
                    $"Promotion validation result was not persisted to audit storage: {appendResult.ErrorMessage}")),
            result.LocalRef,
            result.FetchedHeadCommit);
    }

    private static PromotionValidationWorkflowResult ReplayOrRejectConflict(
        PromotionValidationRequest request,
        PromotionAuditRecord record)
    {
        var expectedHead = request.Decision.ExpectedHeadCommit;
        var conflicts = new List<string>();

        if (!string.Equals(record.ProjectId, request.Decision.ProjectId, StringComparison.Ordinal))
        {
            conflicts.Add("project id");
        }

        if (record.TaskId != request.Decision.TaskId)
        {
            conflicts.Add("task id");
        }

        if (!string.Equals(record.SubmissionId, request.Decision.SubmissionId, StringComparison.Ordinal))
        {
            conflicts.Add("submission id");
        }

        if (record.FetchedHeadCommit is not null && record.FetchedHeadCommit != expectedHead)
        {
            conflicts.Add("expected head commit");
        }

        if (record.RequestedBy is not null && !string.Equals(record.RequestedBy, request.Decision.RequestedBy, StringComparison.Ordinal))
        {
            conflicts.Add("requested by");
        }

        if (record.Operation is not null && record.Operation != request.Decision.Operation)
        {
            conflicts.Add("operation");
        }

        if (record.TargetRemote is not null && !string.Equals(record.TargetRemote, request.Decision.TargetRemote, StringComparison.Ordinal))
        {
            conflicts.Add("target remote");
        }

        if (record.TargetBranch is not null && !string.Equals(record.TargetBranch, request.Decision.TargetBranch, StringComparison.Ordinal))
        {
            conflicts.Add("target branch");
        }

        if (record.ValidateOnly is not null && record.ValidateOnly != request.Decision.ValidateOnly)
        {
            conflicts.Add("validate only");
        }

        if (!PolicyContextMatches(record.PolicyContext, request.EffectivePolicyContext))
        {
            conflicts.Add("policy context");
        }

        if (!OrchestratorOverrideMatches(record.OrchestratorOverride, request.Decision.OrchestratorOverride))
        {
            conflicts.Add("orchestrator override");
        }

        if (record.ReviewRoundId is not null && record.ReviewRoundId != request.Decision.ReviewRoundId)
        {
            conflicts.Add("review round id");
        }

        if (record.ExpectedBaseBranch is not null && !string.Equals(record.ExpectedBaseBranch, request.Decision.ExpectedBaseBranch, StringComparison.Ordinal))
        {
            conflicts.Add("expected base branch");
        }

        if (!record.ScopeOverrideIds.SequenceEqual(request.Decision.ScopeOverrideIds, StringComparer.Ordinal))
        {
            conflicts.Add("scope override ids");
        }

        if (!ScopeOverridesMatch(record.ScopeOverrides, CollectUsedScopeOverrides(request)))
        {
            conflicts.Add("scope overrides");
        }

        if (conflicts.Count > 0)
        {
            return new PromotionValidationWorkflowResult(
                PublishValidationResult.Rejected(
                    "promotion decision replay conflicts with existing audit record",
                    new ValidationFailure(
                        PublishFailureCode.InvalidRequest,
                        $"Decision id '{request.Decision.DecisionId}' already has an audit record with different {string.Join(", ", conflicts)}.")),
                record.LocalRef,
                record.FetchedHeadCommit);
        }

        return new PromotionValidationWorkflowResult(
            new PublishValidationResult(record.Status, $"replayed audited result: {record.Summary}", record.Decisions, record.Failures, record.Warnings
                .Select(warning => new ValidationWarning(warning.Code, warning.Message, warning.Reason))
                .ToArray()),
            record.LocalRef,
            record.FetchedHeadCommit);
    }

    private static PromotionAuditRecord ToAuditRecord(
        PromotionValidationRequest request,
        PromotionValidationWorkflowResult result,
        DateTimeOffset recordedAt)
        => new(
            RecordedAt: recordedAt,
            DecisionId: request.Decision.DecisionId,
            ProjectId: request.Decision.ProjectId,
            TaskId: request.Decision.TaskId,
            SubmissionId: request.Decision.SubmissionId,
            RequestedBy: request.Decision.RequestedBy,
            Operation: request.Decision.Operation,
            TargetRemote: request.Decision.TargetRemote,
            TargetBranch: request.Decision.TargetBranch,
            ValidateOnly: request.Decision.ValidateOnly,
            ReviewRoundId: request.Decision.ReviewRoundId,
            ExpectedBaseBranch: request.Decision.ExpectedBaseBranch,
            ScopeOverrideIds: request.Decision.ScopeOverrideIds,
            Status: result.Validation.Status,
            Summary: result.Validation.Summary,
            Decisions: result.Validation.Decisions,
            Failures: result.Validation.Failures,
            LocalRef: result.LocalRef,
            FetchedHeadCommit: result.FetchedHeadCommit,
            ScopeOverrides: CollectUsedScopeOverrides(request),
            Warnings: result.Validation.Warnings
                .Select(warning => new PromotionAuditWarning(warning.Code, warning.Message, warning.Reason))
                .ToArray(),
            OrchestratorOverride: request.Decision.OrchestratorOverride is null
                ? null
                : new PromotionAuditOrchestratorOverride(
                    request.Decision.OrchestratorOverride.UnclassifiedFailurePolicy,
                    request.Decision.OrchestratorOverride.Reason,
                    request.Decision.OrchestratorOverride.ExpectedRiskCategories),
            PolicyContext: new PromotionAuditPolicyContext(
                request.EffectivePolicyContext.CallerTrust,
                request.EffectivePolicyContext.Mode));

    private static bool PolicyContextMatches(PromotionAuditPolicyContext? recorded, PromotionPolicyContext current)
    {
        var recordedContext = recorded ?? new PromotionAuditPolicyContext(PromotionCallerTrust.Worker, PromotionPolicyMode.Strict);
        return recordedContext.CallerTrust == current.CallerTrust
            && recordedContext.Mode == current.Mode;
    }

    private static bool OrchestratorOverrideMatches(PromotionAuditOrchestratorOverride? recorded, PublishOrchestratorOverride? current)
    {
        if (recorded is null || current is null)
        {
            return recorded is null && current is null;
        }

        return string.Equals(recorded.UnclassifiedFailurePolicy, current.UnclassifiedFailurePolicy, StringComparison.Ordinal)
            && string.Equals(recorded.Reason, current.Reason, StringComparison.Ordinal)
            && recorded.ExpectedRiskCategories.SequenceEqual(current.ExpectedRiskCategories, StringComparer.Ordinal);
    }

    private static bool ScopeOverridesMatch(
        IReadOnlyList<PromotionAuditScopeOverride> recorded,
        IReadOnlyList<PromotionAuditScopeOverride> current)
    {
        if (recorded.Count != current.Count)
        {
            return false;
        }

        return recorded.Zip(current).All(pair =>
            string.Equals(pair.First.OverrideId, pair.Second.OverrideId, StringComparison.Ordinal)
            && string.Equals(pair.First.FindingId, pair.Second.FindingId, StringComparison.Ordinal)
            && string.Equals(pair.First.Reason, pair.Second.Reason, StringComparison.Ordinal)
            && string.Equals(pair.First.ApprovedBy, pair.Second.ApprovedBy, StringComparison.Ordinal));
    }

    private static IReadOnlyList<PromotionAuditScopeOverride> CollectUsedScopeOverrides(PromotionValidationRequest request)
    {
        if (request.Submission?.Review is null || request.Decision.ScopeOverrides is null)
        {
            return [];
        }

        return request.Submission.Review.Findings
            .Where(finding => finding.Blocking && !finding.Resolved && !string.IsNullOrWhiteSpace(finding.OverrideId))
            .Select(finding => new
            {
                Finding = finding,
                Override = request.Decision.ScopeOverrides.FirstOrDefault(scopeOverride =>
                    string.Equals(scopeOverride.OverrideId, finding.OverrideId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(scopeOverride.Reason)
                    && !string.IsNullOrWhiteSpace(scopeOverride.ApprovedBy))
            })
            .Where(item => item.Override is not null
                && request.Decision.ScopeOverrideIds.Contains(item.Override.OverrideId, StringComparer.Ordinal))
            .Select(item => new PromotionAuditScopeOverride(
                item.Override!.OverrideId,
                item.Finding.FindingId,
                item.Override.Reason,
                item.Override.ApprovedBy))
            .ToArray();
    }
}

public sealed class FilePromotionAuditStore(string auditFilePath) : IPromotionAuditStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
            new GitShaJsonConverter()
        }
    };

    public PromotionAuditLookupResult FindByDecisionId(string decisionId)
    {
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            return PromotionAuditLookupResult.Missing();
        }

        try
        {
            if (!File.Exists(auditFilePath))
            {
                return PromotionAuditLookupResult.Missing();
            }

            PromotionAuditRecord? latestMatch = null;
            foreach (var line in File.ReadLines(auditFilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<PromotionAuditRecord>(line, SerializerOptions);
                if (record is not null && string.Equals(record.DecisionId, decisionId, StringComparison.Ordinal))
                {
                    latestMatch = record;
                }
            }

            return latestMatch is null
                ? PromotionAuditLookupResult.Missing()
                : PromotionAuditLookupResult.FoundRecord(latestMatch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or JsonException)
        {
            return PromotionAuditLookupResult.Failed(ex.Message);
        }
    }

    public PromotionAuditAppendResult Append(PromotionAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            var directory = Path.GetDirectoryName(auditFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(record, SerializerOptions);
            File.AppendAllText(auditFilePath, json + Environment.NewLine);
            return PromotionAuditAppendResult.Appended();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return PromotionAuditAppendResult.Failed(ex.Message);
        }
    }

    private sealed class GitShaJsonConverter : JsonConverter<GitSha>
    {
        public override GitSha Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (GitSha.TryCreate(value, out var sha))
            {
                return sha;
            }

            throw new JsonException("Invalid Git SHA in audit record.");
        }

        public override void Write(Utf8JsonWriter writer, GitSha value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}
