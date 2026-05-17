namespace DenPublish.Core;

public enum CodeSubmissionStatus
{
    Submitted,
    ReviewRequested,
    ChangesRequested,
    Superseded,
    Approved,
    PublishRequested,
    Published,
    Rejected,
    Failed
}

public enum PublishOperation
{
    PushBranch,
    FastForwardMain
}

public enum PublishReviewVerdict
{
    LooksGood,
    ChangesRequested,
    FollowUpNeeded,
    BlockedByDependency
}

public sealed record PublishReviewFinding(
    string FindingId,
    bool Blocking,
    bool Resolved,
    string? OverrideId);

public sealed record PublishReviewState(
    int ReviewRoundId,
    PublishReviewVerdict Verdict,
    IReadOnlyList<PublishReviewFinding> Findings);

public sealed record PublishScopeOverride(
    string OverrideId,
    string Reason,
    string ApprovedBy);

public sealed record PublishOrchestratorOverride(
    string UnclassifiedFailurePolicy,
    string Reason,
    IReadOnlyList<string> ExpectedRiskCategories);

public enum PromotionCallerTrust
{
    Worker,
    TrustedOrchestrator,
    Untrusted
}

public enum PromotionPolicyMode
{
    Strict,
    AuditWarn,
    Defensive
}

public sealed record PromotionPolicyContext(PromotionCallerTrust CallerTrust, PromotionPolicyMode Mode)
{
    public static PromotionPolicyContext StrictWorker { get; } = new(PromotionCallerTrust.Worker, PromotionPolicyMode.Strict);
}

public sealed record CodeSubmission(
    string SubmissionId,
    string ProjectId,
    int TaskId,
    string WorkerRunId,
    string SubmittedBy,
    string Role,
    int AttemptOrdinal,
    string? ParentSubmissionId,
    string CodeGateInstance,
    string CodeGateRepo,
    string CodeGateRemoteUrl,
    string IngressRef,
    string ConvenienceRef,
    string BaseBranch,
    GitSha BaseCommit,
    GitSha HeadCommit,
    string CanonicalRemoteUrl,
    string TargetBranch,
    IReadOnlyList<string> ChangedFilesClaim,
    IReadOnlyList<string> TestsRun,
    CodeSubmissionStatus Status,
    DateTimeOffset CreatedAt,
    PublishReviewState? Review = null);

public sealed record PublishDecision(
    string DecisionId,
    string ProjectId,
    int TaskId,
    string SubmissionId,
    string RequestedBy,
    PublishOperation Operation,
    string TargetRemote,
    string TargetBranch,
    GitSha ExpectedHeadCommit,
    string ExpectedBaseBranch,
    int ReviewRoundId,
    IReadOnlyList<string> ScopeOverrideIds,
    bool ValidateOnly,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PublishScopeOverride> ScopeOverrides = null!,
    PublishOrchestratorOverride? OrchestratorOverride = null);
