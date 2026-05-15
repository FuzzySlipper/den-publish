using DenPublish.Core;

namespace DenPublish.Api;

public interface IWorkspacePathResolver
{
    WorkspacePathResolutionResult Resolve(PromotionValidationApiRequest request);
}

public sealed record WorkspacePathResolutionResult(bool Succeeded, string? WorkspacePath, ValidationFailure? Failure)
{
    public static WorkspacePathResolutionResult Resolved(string workspacePath)
        => new(true, workspacePath, null);

    public static WorkspacePathResolutionResult Failed(ValidationFailure failure)
        => new(false, null, failure);
}

public sealed class RequestWorkspacePathResolver : IWorkspacePathResolver
{
    public static RequestWorkspacePathResolver Instance { get; } = new();

    private RequestWorkspacePathResolver()
    {
    }

    public WorkspacePathResolutionResult Resolve(PromotionValidationApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.WorkspacePath)
            ? WorkspacePathResolutionResult.Failed(new ValidationFailure(
                PublishFailureCode.InvalidRequest,
                "Workspace path is required when no service-owned workspace root is configured."))
            : WorkspacePathResolutionResult.Resolved(request.WorkspacePath);
    }
}

public sealed class ConfiguredWorkspacePathResolver(string workspaceRoot) : IWorkspacePathResolver
{
    private readonly string _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
        ? throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot))
        : Path.GetFullPath(workspaceRoot);

    public WorkspacePathResolutionResult Resolve(PromotionValidationApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidatePathComponent(request.Decision.ProjectId, "project id", out var projectFailure)
            || !TryValidatePathComponent(request.Decision.SubmissionId, "submission id", out projectFailure))
        {
            return WorkspacePathResolutionResult.Failed(projectFailure!);
        }

        if (request.Decision.TaskId <= 0)
        {
            return WorkspacePathResolutionResult.Failed(new ValidationFailure(
                PublishFailureCode.InvalidRequest,
                "Decision task id must be positive to derive a service-owned workspace path."));
        }

        var workspacePath = Path.Combine(
            _workspaceRoot,
            request.Decision.ProjectId,
            "tasks",
            request.Decision.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "submissions",
            request.Decision.SubmissionId);

        return WorkspacePathResolutionResult.Resolved(workspacePath);
    }

    private static bool TryValidatePathComponent(string value, string label, out ValidationFailure? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Decision {label} is required to derive a service-owned workspace path.");
            return false;
        }

        if (value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal))
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Decision {label} contains unsafe path characters.");
            return false;
        }

        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            failure = new ValidationFailure(PublishFailureCode.InvalidRequest, $"Decision {label} contains invalid path characters.");
            return false;
        }

        return true;
    }
}
