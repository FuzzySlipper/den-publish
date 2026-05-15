using System.Text.RegularExpressions;

namespace DenPublish.Core;

public readonly record struct GitSha(string Value)
{
    private static readonly Regex FullSha = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryCreate(string? value, out GitSha sha)
    {
        if (!string.IsNullOrWhiteSpace(value) && FullSha.IsMatch(value))
        {
            sha = new GitSha(value.ToLowerInvariant());
            return true;
        }

        sha = new GitSha(string.Empty);
        return false;
    }
}
