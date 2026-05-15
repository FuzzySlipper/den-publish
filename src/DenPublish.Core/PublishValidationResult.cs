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
    AuditFailed
}

public sealed record ValidationFailure(PublishFailureCode Code, string Message);

public sealed record PublishValidationResult(
    PublishValidationStatus Status,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ValidationFailure> Failures)
{
    public bool IsPublishable => Status == PublishValidationStatus.Validated && Failures.Count == 0;

    public static PublishValidationResult Approved(string summary, IReadOnlyList<string>? decisions = null)
        => new(PublishValidationStatus.Validated, summary, decisions ?? [], []);

    public static PublishValidationResult Rejected(string summary, params ValidationFailure[] failures)
        => new(PublishValidationStatus.Rejected, summary, [], failures);

    public static PublishValidationResult Failed(string summary, params ValidationFailure[] failures)
        => new(PublishValidationStatus.Failed, summary, [], failures);
}
