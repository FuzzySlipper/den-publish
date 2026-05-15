using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenPublish.Core;

public interface IPromotionAuditStore
{
    PromotionAuditAppendResult Append(PromotionAuditRecord record);
}

public sealed record PromotionAuditAppendResult(bool Succeeded, string ErrorMessage)
{
    public static PromotionAuditAppendResult Appended()
        => new(true, string.Empty);

    public static PromotionAuditAppendResult Failed(string errorMessage)
        => new(false, errorMessage);
}

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
    GitSha? FetchedHeadCommit);

public sealed class AuditedPromotionValidationWorkflow(
    IPromotionValidationWorkflow inner,
    IPromotionAuditStore auditStore,
    Func<DateTimeOffset>? now = null) : IPromotionValidationWorkflow
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            FetchedHeadCommit: result.FetchedHeadCommit);
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
