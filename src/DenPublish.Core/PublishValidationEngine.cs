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
            "submission head matches publish decision"
        ];

        if (decision.ValidateOnly)
        {
            decisions.Add("validate-only decision accepted without pushing");
        }

        return PublishValidationResult.Approved("publish decision validated against Den submission contract", decisions);
    }

    private static bool DecisionMatchesSubmission(PublishDecision decision, CodeSubmission submission)
        => string.Equals(decision.SubmissionId, submission.SubmissionId, StringComparison.Ordinal)
            && string.Equals(decision.ProjectId, submission.ProjectId, StringComparison.Ordinal)
            && decision.TaskId == submission.TaskId;
}
