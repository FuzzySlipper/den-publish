namespace DenPublish.Core;

public enum PublishValidationStatus
{
    Validated,
    Rejected,
    Failed
}

public enum PublishFailureCode
{
    InvalidRequest,
    MissingSubmission,
    StaleSubmission,
    MissingReview,
    ReviewNotApproved,
    UnresolvedBlockingFindings,
    MissingRequiredValidation,
    CodeGateFetchFailed,
    CodeGateHeadMismatch,
    CanonicalRemoteMismatch,
    ScopeViolation,
    NonFastForward,
    CredentialUnavailable,
    GitPushFailed,
    AuditFailed,
    UnclassifiedSoftFailure
}

public sealed record ValidationFailure(PublishFailureCode Code, string Message);

public sealed record ValidationWarning(
    PublishFailureCode Code,
    string Message,
    string Reason,
    string Severity = "warning",
    string StrictAction = "reject",
    string PermissiveAction = "allow_with_warning",
    IReadOnlyDictionary<string, string>? ObservedValues = null)
{
    public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = ObservedValues ?? new Dictionary<string, string>();
}

public sealed record PublishValidationResult(
    PublishValidationStatus Status,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ValidationFailure> Failures,
    IReadOnlyList<ValidationWarning>? Warnings = null)
{
    public IReadOnlyList<ValidationWarning> Warnings { get; init; } = Warnings ?? [];

    public bool IsPublishable => Status == PublishValidationStatus.Validated && Failures.Count == 0;

    public static PublishValidationResult Approved(
        string summary,
        IReadOnlyList<string>? decisions = null,
        IReadOnlyList<ValidationWarning>? warnings = null)
        => new(PublishValidationStatus.Validated, summary, decisions ?? [], [], warnings ?? []);

    public static PublishValidationResult Rejected(string summary, params ValidationFailure[] failures)
        => new(PublishValidationStatus.Rejected, summary, [], failures, []);

    public static PublishValidationResult Failed(string summary, params ValidationFailure[] failures)
        => new(PublishValidationStatus.Failed, summary, [], failures, []);
}
