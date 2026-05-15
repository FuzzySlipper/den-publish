namespace DenPublish.Core;

public interface IPublishEngine
{
    PublishValidationResult Validate(PublishDecision decision, CodeSubmission? submission);
}

public sealed class PublishValidationEngine : IPublishEngine
{
    public PublishValidationResult Validate(PublishDecision decision, CodeSubmission? submission)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (submission is null)
        {
            return PublishValidationResult.Rejected(
                "publish decision does not reference an available submission",
                new ValidationFailure(PublishFailureCode.MissingSubmission, "No submission record was found for the publish decision."));
        }

        if (!DecisionMatchesSubmission(decision, submission))
        {
            return PublishValidationResult.Rejected(
                "publish decision does not match the submission record",
                new ValidationFailure(PublishFailureCode.InvalidRequest, "Decision project/task/submission identifiers must match the submission."));
        }

        if (submission.Status == CodeSubmissionStatus.Superseded)
        {
            return PublishValidationResult.Rejected(
                "submission was superseded before publish validation",
                new ValidationFailure(PublishFailureCode.StaleSubmission, "Superseded submissions cannot be published."));
        }

        if (submission.Status is not (CodeSubmissionStatus.Approved or CodeSubmissionStatus.PublishRequested))
        {
            return PublishValidationResult.Rejected(
                "submission has not been approved for publishing",
                new ValidationFailure(PublishFailureCode.ReviewNotApproved, "Submission status must be Approved or PublishRequested before promotion."));
        }

        if (submission.Review is null)
        {
            return PublishValidationResult.Rejected(
                "submission has no matching review state",
                new ValidationFailure(PublishFailureCode.MissingReview, "Submission must include the Den review round referenced by the publish decision."));
        }

        if (submission.Review.ReviewRoundId != decision.ReviewRoundId)
        {
            return PublishValidationResult.Rejected(
                "publish decision review round does not match submission review state",
                new ValidationFailure(PublishFailureCode.MissingReview, "Decision review round id must match the submission review round."));
        }

        if (submission.Review.Verdict != PublishReviewVerdict.LooksGood)
        {
            return PublishValidationResult.Rejected(
                "submission review verdict does not approve publishing",
                new ValidationFailure(PublishFailureCode.ReviewNotApproved, "Review verdict must be looks_good before promotion."));
        }

        var uncoveredBlockingFindings = submission.Review.Findings
            .Where(finding => finding.Blocking && !finding.Resolved && FindCoveredOverride(decision, finding) is null)
            .ToArray();
        if (uncoveredBlockingFindings.Length > 0)
        {
            var findingIds = string.Join(", ", uncoveredBlockingFindings.Select(finding => finding.FindingId));
            return PublishValidationResult.Rejected(
                "submission has unresolved blocking review findings",
                new ValidationFailure(PublishFailureCode.UnresolvedBlockingFindings, $"Unresolved blocking findings must be resolved or covered by an approved override before promotion: {findingIds}."));
        }

        if (decision.ExpectedHeadCommit != submission.HeadCommit)
        {
            return PublishValidationResult.Rejected(
                "publish decision head does not match submission head",
                new ValidationFailure(PublishFailureCode.CodeGateHeadMismatch, "The decision expected head SHA differs from the immutable submission head SHA."));
        }

        List<string> decisions =
        [
            "submission identity matches publish decision",
            "submission status is approved for publishing",
            "submission review round matches publish decision",
            "submission review verdict is looks_good",
            "submission has no uncovered blocking findings",
            "submission head matches publish decision"
        ];

        decisions.AddRange(submission.Review.Findings
            .Where(finding => finding.Blocking && !finding.Resolved)
            .Select(finding => new { Finding = finding, Override = FindCoveredOverride(decision, finding) })
            .Where(item => item.Override is not null)
            .Select(item => $"blocking finding {item.Finding.FindingId} covered by approved override {item.Override!.OverrideId}: {item.Override.Reason}"));

        if (decision.ValidateOnly)
        {
            decisions.Add("validate-only decision accepted without pushing");
        }

        return PublishValidationResult.Approved("publish decision validated against Den submission contract", decisions);
    }

    private static PublishScopeOverride? FindCoveredOverride(PublishDecision decision, PublishReviewFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.OverrideId)
            || !decision.ScopeOverrideIds.Contains(finding.OverrideId, StringComparer.Ordinal))
        {
            return null;
        }

        return decision.ScopeOverrides?.FirstOrDefault(scopeOverride =>
            string.Equals(scopeOverride.OverrideId, finding.OverrideId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(scopeOverride.Reason)
            && !string.IsNullOrWhiteSpace(scopeOverride.ApprovedBy));
    }

    private static bool DecisionMatchesSubmission(PublishDecision decision, CodeSubmission submission)
        => string.Equals(decision.SubmissionId, submission.SubmissionId, StringComparison.Ordinal)
            && string.Equals(decision.ProjectId, submission.ProjectId, StringComparison.Ordinal)
            && decision.TaskId == submission.TaskId;
}
