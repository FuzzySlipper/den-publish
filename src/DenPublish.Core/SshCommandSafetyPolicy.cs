namespace DenPublish.Core;

public sealed record SshCommandSafetyIssue(string Code, string Message, string RequiredOption);

public sealed record SshCommandSafetyResult(bool IsSafe, IReadOnlyList<SshCommandSafetyIssue> Issues)
{
    public static SshCommandSafetyResult Safe { get; } = new(true, []);

    public static SshCommandSafetyResult Unsafe(params SshCommandSafetyIssue[] issues)
        => new(false, issues);
}

public static class SshCommandSafetyPolicy
{
    public static SshCommandSafetyResult Validate(string? command)
    {
        var tokens = SplitCommand(command);
        var issues = new List<SshCommandSafetyIssue>();

        if (tokens.Count == 0 || !IsSshExecutable(tokens[0]))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_command_missing",
                "Git SSH command must explicitly invoke ssh.",
                "ssh"));
        }

        if (!HasConfigDisabled(tokens))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_config_not_disabled",
                "Git SSH command must disable ambient OpenSSH config with ssh -F /dev/null.",
                "ssh -F /dev/null"));
        }

        if (!HasIdentity(tokens))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_identity_missing",
                "Git SSH command must specify an explicit identity file.",
                "-i <identity-file>"));
        }

        if (!HasSshOption(tokens, "UserKnownHostsFile", IsExplicitKnownHostsPath))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_known_hosts_missing",
                "Git SSH command must specify an explicit known_hosts file.",
                "-o UserKnownHostsFile=<known-hosts-file>"));
        }

        if (!HasSshOption(tokens, "IdentitiesOnly", value => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_identities_only_missing",
                "Git SSH command must force IdentitiesOnly=yes.",
                "-o IdentitiesOnly=yes"));
        }

        if (!HasSshOption(tokens, "BatchMode", value => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_batch_mode_missing",
                "Git SSH command must force BatchMode=yes to prevent prompts.",
                "-o BatchMode=yes"));
        }

        if (!HasSshOption(tokens, "StrictHostKeyChecking", value => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new SshCommandSafetyIssue(
                "ssh_strict_host_key_checking_missing",
                "Git SSH command must force StrictHostKeyChecking=yes.",
                "-o StrictHostKeyChecking=yes"));
        }

        return issues.Count == 0
            ? SshCommandSafetyResult.Safe
            : new SshCommandSafetyResult(false, issues);
    }

    public static string DescribeRequiredOptions(SshCommandSafetyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSafe)
        {
            return "SSH command satisfies den-publish hardening requirements.";
        }

        var options = result.Issues
            .Select(issue => issue.RequiredOption)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return "Git SSH command is not hardened; configure: " + string.Join(", ", options) + ".";
    }

    private static bool IsSshExecutable(string token)
        => string.Equals(token, "ssh", StringComparison.Ordinal)
           || token.EndsWith("/ssh", StringComparison.Ordinal);

    private static bool HasConfigDisabled(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] == "-F" && index + 1 < tokens.Count && tokens[index + 1] == "/dev/null")
            {
                return true;
            }

            if (tokens[index] == "-F/dev/null")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasIdentity(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] == "-i" && index + 1 < tokens.Count && !string.IsNullOrWhiteSpace(tokens[index + 1]))
            {
                return true;
            }

            if (tokens[index].StartsWith("-i/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return HasSshOption(tokens, "IdentityFile", value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsExplicitKnownHostsPath(string value)
    {
        var normalized = value.Trim();
        return !string.IsNullOrWhiteSpace(normalized)
               && normalized.StartsWith("/", StringComparison.Ordinal)
               && !string.Equals(normalized, "/dev/null", StringComparison.Ordinal)
               && !string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSshOption(IReadOnlyList<string> tokens, string name, Func<string, bool> predicate)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] == "-o" && index + 1 < tokens.Count && TryParseOption(tokens[index + 1], out var optionName, out var value))
            {
                if (string.Equals(optionName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return predicate(value);
                }
            }

            if (tokens[index].StartsWith("-o", StringComparison.Ordinal) && TryParseOption(tokens[index][2..], out var compactName, out var compactValue))
            {
                if (string.Equals(compactName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return predicate(compactValue);
                }
            }
        }

        return false;
    }

    private static bool TryParseOption(string token, out string name, out string value)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }

        name = token[..equals];
        value = token[(equals + 1)..];
        return true;
    }

    private static IReadOnlyList<string> SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var ch in command)
        {
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if ((ch == '\'' || ch == '"') && (quote == '\0' || quote == ch))
            {
                quote = quote == '\0' ? ch : '\0';
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
