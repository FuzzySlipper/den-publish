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
            FetchedHeadCommit: Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = store.Append(record);

        Assert.True(result.Succeeded);
        var line = Assert.Single(File.ReadAllLines(auditFile));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("pub_1424_001", doc.RootElement.GetProperty("decision_id").GetString());
        Assert.Equal("sub_1424_001", doc.RootElement.GetProperty("submission_id").GetString());
        Assert.Equal("validated", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", doc.RootElement.GetProperty("fetched_head_commit").GetString());
    }

    private sealed class RecordingPromotionWorkflow(PromotionValidationWorkflowResult result) : IPromotionValidationWorkflow
    {
        public PromotionValidationRequest? CapturedRequest { get; private set; }

        public PromotionValidationWorkflowResult Validate(PromotionValidationRequest request)
        {
            CapturedRequest = request;
            return result;
        }
    }

    private sealed class RecordingAuditStore(PromotionAuditAppendResult appendResult) : IPromotionAuditStore
    {
        public List<PromotionAuditRecord> Records { get; } = [];

        public PromotionAuditAppendResult Append(PromotionAuditRecord record)
        {
            Records.Add(record);
            return appendResult;
        }
    }

    private static PublishDecision Decision()
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
            ScopeOverrideIds: [],
            ValidateOnly: true,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:05:00Z"));

    private static CodeSubmission ApprovedSubmission()
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
            CreatedAt: DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
