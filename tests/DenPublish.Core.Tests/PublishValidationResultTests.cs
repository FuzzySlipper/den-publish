using DenPublish.Core;

namespace DenPublish.Core.Tests;

public sealed class PublishValidationResultTests
{
    [Fact]
    public void ApprovedResult_IsPublishableWhenThereAreNoFailures()
    {
        var result = PublishValidationResult.Approved("ready to publish", ["review head matched"]);

        Assert.True(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Validated, result.Status);
        Assert.Contains("review head matched", result.Decisions);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void RejectedResult_IsNotPublishableAndCarriesFailureCode()
    {
        var result = PublishValidationResult.Rejected(
            "missing review",
            new ValidationFailure(PublishFailureCode.MissingReview, "No matching review round was found."));

        Assert.False(result.IsPublishable);
        Assert.Equal(PublishValidationStatus.Rejected, result.Status);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PublishFailureCode.MissingReview, failure.Code);
    }
}
