namespace DenPublish.Core;

public sealed record WorkspacePreflightEntry(
    string RelativePath,
    string Owner,
    string Group,
    bool IsDirectory,
    bool IsSymlink,
    string Mode,
    string? SymlinkTarget);

public sealed record WorkspacePreflightSnapshot(
    string WorkspacePath,
    string ExpectedOwner,
    IReadOnlyList<WorkspacePreflightEntry> Entries);

public sealed record WorkspacePreflightFinding(string Code, string Message, string Guidance);

public sealed record WorkspacePreflightReport(bool IsHealthy, IReadOnlyList<WorkspacePreflightFinding> Findings)
{
    public static WorkspacePreflightReport Healthy { get; } = new(true, []);
}

public static class WorkspacePreflightAnalyzer
{
    public static WorkspacePreflightReport Analyze(WorkspacePreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<WorkspacePreflightFinding>();
        var expectedOwner = snapshot.ExpectedOwner;
        var mismatched = snapshot.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(expectedOwner)
                            && !string.Equals(entry.Owner, expectedOwner, StringComparison.Ordinal))
            .ToArray();

        if (mismatched.Length > 0)
        {
            findings.Add(new WorkspacePreflightFinding(
                "mixed_workspace_ownership",
                $"Workspace '{snapshot.WorkspacePath}' contains entries not owned by expected owner '{expectedOwner}'.",
                "Do not repair automatically. Inspect with the read-only checker; if approved, run ownership repair under sysadmin control, usually by operating as the repo/service owner via sudo -n -u agent or by chowning only the managed workspace path."));
        }

        if (snapshot.Entries.Any(entry => IsGitControlPath(entry.RelativePath) && !string.Equals(entry.Owner, expectedOwner, StringComparison.Ordinal)))
        {
            findings.Add(new WorkspacePreflightFinding(
                "git_config_lock_risk",
                "Git control files are not owned by the expected service user; git may fail to create .git/config.lock or ref locks.",
                "Pause promotion and repair the managed workspace ownership with explicit sysadmin approval. Prefer recreating the workspace or running Git as the owning service account; do not hide this with audit-warn."));
        }

        foreach (var entry in snapshot.Entries.Where(entry => IsSshConfigPath(entry.RelativePath)))
        {
            if (entry.IsSymlink)
            {
                findings.Add(new WorkspacePreflightFinding(
                    "ssh_config_symlink_review_required",
                    $"OpenSSH config path '{entry.RelativePath}' is a symlink and must be reviewed before use.",
                    "Prefer ssh -F /dev/null plus explicit command-line options. If a symlink is intentional, verify its target owner and permissions before promotion."));
            }

            if (PermissionsTooOpen(entry.Mode))
            {
                findings.Add(new WorkspacePreflightFinding(
                    "ssh_config_permissions_too_open",
                    $"OpenSSH config path '{entry.RelativePath}' has permissions '{entry.Mode}', which are too open for safe non-interactive Git SSH.",
                    "Set OpenSSH config/key material to service-owner-only permissions, or avoid reading config entirely with ssh -F /dev/null."));
            }
        }

        return findings.Count == 0
            ? WorkspacePreflightReport.Healthy
            : new WorkspacePreflightReport(false, findings);
    }

    private static bool IsGitControlPath(string relativePath)
        => string.Equals(relativePath, ".git", StringComparison.Ordinal)
           || relativePath.StartsWith(".git/", StringComparison.Ordinal);

    private static bool IsSshConfigPath(string relativePath)
        => string.Equals(relativePath, ".ssh/config", StringComparison.Ordinal)
           || relativePath.EndsWith("/.ssh/config", StringComparison.Ordinal);

    private static bool PermissionsTooOpen(string mode)
    {
        var normalized = mode.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.Length > 3)
        {
            normalized = normalized[^3..];
        }

        return normalized.Length == 3
               && int.TryParse(normalized[1].ToString(), out var group)
               && int.TryParse(normalized[2].ToString(), out var other)
               && ((group & 2) != 0 || (other & 2) != 0);
    }
}
