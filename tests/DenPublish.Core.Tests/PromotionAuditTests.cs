using System.Text.Json;
using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PromotionAuditTests
{
    [Fact]
    public void Validate_RecordsAuditEntryForWorkflowResult()
    {
        var decision = Decision();
        var submission = ApprovedSubmission();
        var innerResult = new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok", ["all checks passed"]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: submission.HeadCommit);
        var inner = new RecordingPromotionWorkflow(innerResult);
        var audit = new RecordingAuditStore(PromotionAuditAppendResult.Appended());
        var workflow = new AuditedPromotionValidationWorkflow(
            inner,
            audit,
            () => DateTimeOffset.Parse("2026-05-14T20:40:00Z"));
        var request = new PromotionValidationRequest(decision, submission, "/workspace", new ChangedFileScopePolicy(["src/DenChannels/"]));

        var result = workflow.Validate(request);

        Assert.True(result.IsPublishable);
        Assert.Same(innerResult, result);
        var record = Assert.Single(audit.Records);
        Assert.Equal("pub_1424_001", record.DecisionId);
        Assert.Equal("sub_1424_001", record.SubmissionId);
        Assert.Equal("den-channels", record.ProjectId);
        Assert.Equal(1416, record.TaskId);
        Assert.Equal(PublishValidationStatus.Validated, record.Status);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", record.LocalRef);
        Assert.Equal(submission.HeadCommit, record.FetchedHeadCommit);
        Assert.Equal("workflow ok", record.Summary);
        Assert.Equal(["all checks passed"], record.Decisions);
        Assert.Empty(record.Failures);
        Assert.Equal(DateTimeOffset.Parse("2026-05-14T20:40:00Z"), record.RecordedAt);
    }

    [Fact]
    public void Validate_FailsClosedWhenAuditCannotBeRecorded()
    {
        var innerResult = new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok", ["all checks passed"]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var workflow = new AuditedPromotionValidationWorkflow(
            new RecordingPromotionWorkflow(innerResult),
            new RecordingAuditStore(PromotionAuditAppendResult.Failed("disk full")),
            () => DateTimeOffset.Parse("2026-05-14T20:40:00Z"));

        var result = workflow.Validate(new PromotionValidationRequest(Decision(), ApprovedSubmission(), "/workspace", new ChangedFileScopePolicy(["src/DenChannels/"])));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Failed, result.Validation.Status);
        var failure = Assert.Single(result.Validation.Failures);
        Assert.Equal(PublishFailureCode.AuditFailed, failure.Code);
        Assert.Contains("disk full", failure.Message, StringComparison.Ordinal);
        Assert.Equal("refs/den-publish/submissions/sub_1424_001", result.LocalRef);
    }



    [Fact]
    public void Validate_ReplaysExistingAuditRecordWithoutCallingInnerOrAppendingDuplicate()
    {
        var decision = Decision();
        var submission = ApprovedSubmission();
        var existing = new PromotionAuditRecord(
            RecordedAt: DateTimeOffset.Parse("2026-05-14T20:40:00Z"),
            DecisionId: decision.DecisionId,
            ProjectId: decision.ProjectId,
            TaskId: decision.TaskId,
            SubmissionId: decision.SubmissionId,
            Status: PublishValidationStatus.Validated,
            Summary: "workflow ok",
            Decisions: ["all checks passed"],
            Failures: [],
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: decision.ExpectedHeadCommit);
        var inner = new RecordingPromotionWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Failed("inner should not run", new ValidationFailure(PublishFailureCode.CodeGateFetchFailed, "boom")),
            LocalRef: null,
            FetchedHeadCommit: null));
        var audit = new RecordingAuditStore(PromotionAuditAppendResult.Appended(), existing);
        var workflow = new AuditedPromotionValidationWorkflow(inner, audit);

        var result = workflow.Validate(new PromotionValidationRequest(decision, submission, "/workspace", new ChangedFileScopePolicy(["src/DenChannels/"])));

        Assert.True(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Validated, result.Validation.Status);
        Assert.Equal("replayed audited result: workflow ok", result.Validation.Summary);
        Assert.Equal(existing.LocalRef, result.LocalRef);
        Assert.Equal(existing.FetchedHeadCommit, result.FetchedHeadCommit);
        Assert.Equal(0, inner.CallCount);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public void Validate_RejectsConflictingDecisionReplayWithoutCallingInnerOrAppendingDuplicate()
    {
        var decision = Decision();
        var existing = new PromotionAuditRecord(
            RecordedAt: DateTimeOffset.Parse("2026-05-14T20:40:00Z"),
            DecisionId: decision.DecisionId,
            ProjectId: decision.ProjectId,
            TaskId: decision.TaskId,
            SubmissionId: decision.SubmissionId,
            Status: PublishValidationStatus.Validated,
            Summary: "workflow ok",
            Decisions: ["all checks passed"],
            Failures: [],
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var inner = new RecordingPromotionWorkflow(new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("inner should not run"),
            LocalRef: null,
            FetchedHeadCommit: null));
        var audit = new RecordingAuditStore(PromotionAuditAppendResult.Appended(), existing);
        var workflow = new AuditedPromotionValidationWorkflow(inner, audit);

        var result = workflow.Validate(new PromotionValidationRequest(decision, ApprovedSubmission(), "/workspace", new ChangedFileScopePolicy(["src/DenChannels/"])));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Rejected, result.Validation.Status);
        var failure = Assert.Single(result.Validation.Failures);
        Assert.Equal(PublishFailureCode.InvalidRequest, failure.Code);
        Assert.Contains("already has an audit record", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.CallCount);
        Assert.Empty(audit.Records);
    }


    [Fact]
    public void Validate_RecordsUsedScopeOverrideReasons()
    {
        var decision = Decision(
            scopeOverrideIds: ["override_scope_1"],
            scopeOverrides: [new PublishScopeOverride("override_scope_1", "Generated file outside normal prefix after tool regeneration", "planner")]);
        var submission = ApprovedSubmission(review: new PublishReviewState(
            680,
            PublishReviewVerdict.LooksGood,
            [new PublishReviewFinding("finding_1", Blocking: true, Resolved: false, OverrideId: "override_scope_1")]));
        var innerResult = new PromotionValidationWorkflowResult(
            PublishValidationResult.Approved("workflow ok", ["override accepted"]),
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: submission.HeadCommit);
        var audit = new RecordingAuditStore(PromotionAuditAppendResult.Appended());
        var workflow = new AuditedPromotionValidationWorkflow(
            new RecordingPromotionWorkflow(innerResult),
            audit,
            () => DateTimeOffset.Parse("2026-05-14T20:40:00Z"));

        var result = workflow.Validate(new PromotionValidationRequest(decision, submission, "/workspace", new ChangedFileScopePolicy(["src/DenChannels/"])));

        Assert.True(result.IsPublishable);
        var record = Assert.Single(audit.Records);
        var recordedOverride = Assert.Single(record.ScopeOverrides);
        Assert.Equal("override_scope_1", recordedOverride.OverrideId);
        Assert.Equal("finding_1", recordedOverride.FindingId);
        Assert.Equal("Generated file outside normal prefix after tool regeneration", recordedOverride.Reason);
        Assert.Equal("planner", recordedOverride.ApprovedBy);
    }

    [Fact]
    public void FileAuditStore_AppendsJsonLineAndCreatesParentDirectory()
    {
        var auditFile = Path.Combine(Path.GetTempPath(), $"den-publish-audit-{Guid.NewGuid():N}", "publish-audit.jsonl");
        var store = new FilePromotionAuditStore(auditFile);
        var record = new PromotionAuditRecord(
            RecordedAt: DateTimeOffset.Parse("2026-05-14T20:40:00Z"),
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1424_001",
            Status: PublishValidationStatus.Validated,
            Summary: "workflow ok",
            Decisions: ["all checks passed"],
            Failures: [],
            LocalRef: "refs/den-publish/submissions/sub_1424_001",
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ScopeOverrides: [new PromotionAuditScopeOverride("override_scope_1", "finding_1", "Generated file outside normal prefix after tool regeneration", "planner")]);

        var result = store.Append(record);

        Assert.True(result.Succeeded);
        var line = Assert.Single(File.ReadAllLines(auditFile));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("pub_1424_001", doc.RootElement.GetProperty("decision_id").GetString());
        Assert.Equal("sub_1424_001", doc.RootElement.GetProperty("submission_id").GetString());
        Assert.Equal("validated", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", doc.RootElement.GetProperty("fetched_head_commit").GetString());
        var overrideJson = Assert.Single(doc.RootElement.GetProperty("scope_overrides").EnumerateArray());
        Assert.Equal("override_scope_1", overrideJson.GetProperty("override_id").GetString());
        Assert.Equal("finding_1", overrideJson.GetProperty("finding_id").GetString());
        Assert.Equal("Generated file outside normal prefix after tool regeneration", overrideJson.GetProperty("reason").GetString());
        Assert.Equal("planner", overrideJson.GetProperty("approved_by").GetString());

        var lookup = store.FindByDecisionId("pub_1424_001");
        Assert.True(lookup.Succeeded);
        Assert.NotNull(lookup.Record);
        Assert.Equal("pub_1424_001", lookup.Record.DecisionId);
        Assert.Equal(Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), lookup.Record.FetchedHeadCommit);

    }

    private sealed class RecordingPromotionWorkflow(PromotionValidationWorkflowResult result) : IPromotionValidationWorkflow
    {
        public PromotionValidationRequest? CapturedRequest { get; private set; }
        public int CallCount { get; private set; }

        public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
        {
            CallCount++;
            CapturedRequest = request;
            return result;
        }
    }

    private sealed class RecordingAuditStore(PromotionAuditAppendResult appendResult, PromotionAuditRecord? existing = null) : IPromotionAuditStore
    {
        public List<PromotionAuditRecord> Records { get; } = [];

        public PromotionAuditLookupResult FindByDecisionId(string decisionId)
            => existing is not null && string.Equals(existing.DecisionId, decisionId, StringComparison.Ordinal)
                ? PromotionAuditLookupResult.FoundRecord(existing)
                : PromotionAuditLookupResult.Missing();

        public PromotionAuditAppendResult Append(PromotionAuditRecord record)
        {
            Records.Add(record);
            return appendResult;
        }
    }

    private static PublishDecision Decision(
        IReadOnlyList<string>? scopeOverrideIds = null,
        IReadOnlyList<PublishScopeOverride>? scopeOverrides = null)
        => new(
            DecisionId: "pub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1424_001",
            RequestedBy: "den-channels-orchestrator",
            Operation: PublishOperation.PushBranch,
            TargetRemote: "canonical",
            TargetBranch: "task/1416-den-channels",
            ExpectedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ExpectedBaseBranch: "main",
            ReviewRoundId: 680,
            ScopeOverrideIds: scopeOverrideIds ?? [],
            ValidateOnly: true,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"),
            ScopeOverrides: scopeOverrides ?? []);

    private static CodeSubmission ApprovedSubmission(PublishReviewState? review = null)
        => new(
            SubmissionId: "sub_1424_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            WorkerRunId: "run-20260514-def456",
            SubmittedBy: "den-channels-runner",
            Role: "coder",
            AttemptOrdinal: 1,
            ParentSubmissionId: null,
            CodeGateInstance: "den-code-gate",
            CodeGateRepo: "den-channels.git",
            CodeGateRemoteUrl: "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
            IngressRef: "refs/heads/submissions/den-channels/tasks/1416/runs/run-20260514-def456/attempt-001",
            ConvenienceRef: "refs/heads/submissions/den-channels/tasks/1416/current",
            BaseBranch: "main",
            BaseCommit: Sha("cccccccccccccccccccccccccccccccccccccccc"),
            HeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            TargetBranch: "task/1416-den-channels",
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Approved,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"),
            Review: review ?? new PublishReviewState(680, PublishReviewVerdict.LooksGood, []));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
