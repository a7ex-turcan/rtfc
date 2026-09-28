using Rtfc.Core;

namespace Rtfc.Cli;

/// <summary>
/// The scope <c>rtfc auto … headless</c> uses when none is given (spec §7.3): the directory Claude is running in. Never a
/// filesystem root, the home directory or anything above it, or anything inside <c>~/.claude</c>: the answering Claude's
/// <c>Grep</c> has no deny rules, and those hold SSH keys, cloud credentials and rtfc's own keys. An explicit
/// <c>--scope</c> is the user's choice and is not second-guessed here.
/// </summary>
public static class AutoScope
{
    /// <summary>
    /// <paramref name="claudeProjectDir"/> is <c>CLAUDE_PROJECT_DIR</c>, which Claude Code sets to where the session started;
    /// outside a session the working directory stands in. Null, with <paramref name="refusal"/> saying why, when the
    /// directory is too broad to be a default.
    /// </summary>
    public static string? Default(string? claudeProjectDir, string workingDirectory, string homeDirectory, out string? refusal)
    {
        var directory = ProjectPaths.Full(string.IsNullOrEmpty(claudeProjectDir) ? workingDirectory : claudeProjectDir);
        var key = ProjectPaths.Key(directory);

        refusal = Path.GetPathRoot(directory) is { Length: > 0 } root && ProjectPaths.Key(root) == key
            ? $"{directory} is the root of a drive"
            : ProjectPaths.Contains(key, ProjectPaths.Key(homeDirectory))
                ? $"{directory} is your home folder or contains it"
                : ProjectPaths.Contains(ProjectPaths.Key(Path.Combine(homeDirectory, ".claude")), key)
                    ? $"{directory} is inside ~/.claude, next to your keys and credentials"
                    : null;
        if (refusal is null)
        {
            return directory;
        }

        refusal += ", so the answering Claude could search far more than one project. Run it from a project folder, or pass --scope <dir>.";
        return null;
    }
}
