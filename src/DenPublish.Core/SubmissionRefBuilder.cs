using System.Text.RegularExpressions;

namespace DenPublish.Core;

public static class SubmissionRefBuilder
{
    private static readonly Regex SafeToken = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildImmutableRef(string projectId, int taskId, string runId, int attemptOrdinal)
    {
        ValidatePositive(taskId, nameof(taskId));
        ValidatePositive(attemptOrdinal, nameof(attemptOrdinal));
        var safeProject = SafeComponent(projectId, nameof(projectId));
        var safeRun = SafeComponent(runId, nameof(runId));

        return $"refs/heads/submissions/{safeProject}/tasks/{taskId}/runs/{safeRun}/attempt-{attemptOrdinal:000}";
    }

    public static string BuildCurrentRef(string projectId, int taskId)
    {
        ValidatePositive(taskId, nameof(taskId));
        var safeProject = SafeComponent(projectId, nameof(projectId));

        return $"refs/heads/submissions/{safeProject}/tasks/{taskId}/current";
    }

    private static string SafeComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || !SafeToken.IsMatch(value))
        {
            throw new ArgumentException($"{parameterName} must be a safe code-gate ref token.", parameterName);
        }

        return value;
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be positive.");
        }
    }
}
