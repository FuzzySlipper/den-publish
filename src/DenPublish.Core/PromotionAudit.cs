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
    IReadOnlyList<PromotionAuditScopeOverride> ScopeOverrides = null!);

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
            new PublishValidationResult(record.Status, $"replayed audited result: {record.Summary}", record.Decisions, record.Failures),
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
            Status: result.Validation.Status,
            Summary: result.Validation.Summary,
            Decisions: result.Validation.Decisions,
            Failures: result.Validation.Failures,
            LocalRef: result.LocalRef,
            FetchedHeadCommit: result.FetchedHeadCommit,
            ScopeOverrides: CollectUsedScopeOverrides(request));

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
