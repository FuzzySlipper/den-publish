using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class CodeSubmissionContractTests
{
    [Fact]
    public void CodeSubmission_CarriesDenSubmissionContractFields()
    {
        var baseCommit = Sha("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var headCommit = Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var submission = new CodeSubmission(
            SubmissionId: "sub_1423_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            WorkerRunId: "run-20260514-abc123",
            SubmittedBy: "den-channels-runner",
            Role: "coder",
            AttemptOrdinal: 1,
            ParentSubmissionId: null,
            CodeGateInstance: "den-code-gate",
            CodeGateRepo: "den-channels.git",
            CodeGateRemoteUrl: "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
            IngressRef: "refs/heads/submissions/den-channels/tasks/1416/runs/run-20260514-abc123/attempt-001",
            ConvenienceRef: "refs/heads/submissions/den-channels/tasks/1416/current",
            BaseBranch: "main",
            BaseCommit: baseCommit,
            HeadCommit: headCommit,
            CanonicalRemoteUrl: "git@github.com:FuzzySlipper/den-channels.git",
            TargetBranch: "task/1416-den-channels",
            ChangedFilesClaim: ["src/DenChannels/Bridge.cs"],
            TestsRun: ["dotnet test --no-restore: passed"],
            Status: CodeSubmissionStatus.Submitted,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T19:00:00Z"));

        Assert.Equal("den-code-gate", submission.CodeGateInstance);
        Assert.Equal("ssh://git@192.168.1.10:3022/den-channels/den-channels.git", submission.CodeGateRemoteUrl);
        Assert.Equal("git@github.com:FuzzySlipper/den-channels.git", submission.CanonicalRemoteUrl);
        Assert.Equal(CodeSubmissionStatus.Submitted, submission.Status);
        Assert.Equal(baseCommit, submission.BaseCommit);
        Assert.Equal(headCommit, submission.HeadCommit);
        Assert.Contains("src/DenChannels/Bridge.cs", submission.ChangedFilesClaim);
        Assert.Contains("dotnet test --no-restore: passed", submission.TestsRun);
    }

    [Theory]
    [InlineData(CodeSubmissionStatus.Submitted)]
    [InlineData(CodeSubmissionStatus.ReviewRequested)]
    [InlineData(CodeSubmissionStatus.ChangesRequested)]
    [InlineData(CodeSubmissionStatus.Superseded)]
    [InlineData(CodeSubmissionStatus.Approved)]
    [InlineData(CodeSubmissionStatus.PublishRequested)]
    [InlineData(CodeSubmissionStatus.Published)]
    [InlineData(CodeSubmissionStatus.Rejected)]
    [InlineData(CodeSubmissionStatus.Failed)]
    public void CodeSubmissionStatus_IncludesWorkflowStates(CodeSubmissionStatus status)
    {
        Assert.True(Enum.IsDefined(status));
    }

    [Fact]
    public void PublishDecision_CarriesOrchestratorDecisionFields()
    {
        var decision = new PublishDecision(
            DecisionId: "pub_1423_001",
            ProjectId: "den-channels",
            TaskId: 1416,
            SubmissionId: "sub_1423_001",
            RequestedBy: "den-channels-orchestrator",
            Operation: PublishOperation.PushBranch,
            TargetRemote: "canonical",
            TargetBranch: "task/1416-den-channels",
            ExpectedHeadCommit: Sha("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            ExpectedBaseBranch: "main",
            ReviewRoundId: 680,
            ScopeOverrideIds: ["override_scope_1"],
            ValidateOnly: false,
            CreatedAt: DateTimeOffset.Parse("2026-05-14T19:05:00Z"));

        Assert.Equal("den-channels", decision.ProjectId);
        Assert.Equal(1416, decision.TaskId);
        Assert.Equal(PublishOperation.PushBranch, decision.Operation);
        Assert.Equal("canonical", decision.TargetRemote);
        Assert.Equal("main", decision.ExpectedBaseBranch);
        Assert.Contains("override_scope_1", decision.ScopeOverrideIds);
    }

    private static GitSha Sha(string value)
    {
        Assert.True(GitSha.TryCreate(value, out var sha));
        return sha;
    }
}
