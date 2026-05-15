namespace DenPublish.Core;

public sealed record CodeSubmission(
    string SubmissionId,
    string ProjectId,
    int TaskId,
    string WorkerRunId,
    int AttemptOrdinal,
    string CodeGateRepo,
    string IngressRef,
    GitSha BaseCommit,
    GitSha HeadCommit,
    string BaseBranch,
    string TargetBranch);

public sealed record PublishDecision(
    string DecisionId,
    string SubmissionId,
    string RequestedBy,
    string Operation,
    string TargetBranch,
    GitSha ExpectedHeadCommit,
    int ReviewRoundId,
    bool ValidateOnly);
