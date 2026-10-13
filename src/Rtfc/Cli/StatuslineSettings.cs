using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rtfc.Cli;

/// <summary>
/// The <c>statusLine</c> entry in Claude Code's user settings (spec §11), written only by <c>rtfc statusline install</c>, which the
/// user runs. Claude Code shows one status line, so a status line that is not rtfc's is left alone unless the user says to
/// replace it, and <c>uninstall</c> removes only rtfc's own. The rest of the file is kept; the previous copy is saved beside it.
/// </summary>
public static partial class StatuslineSettings
{
    /// <summary>The same command the README and the plugin's own wiring use: <c>rtfc</c> from the PATH Claude Code sees.</summary>
    public const string Command = "rtfc statusline";

    public const int RefreshSeconds = 5;

    public enum Outcome
    {
        Installed,
        AlreadyInstalled,
        OtherStatusLine,
        Removed,
        NotInstalled,
        Unreadable,
    }

    /// <summary><c>Existing</c> is the status line that was there, as its command or, failing that, its JSON.</summary>
    public sealed record Result(Outcome Outcome, string? Existing = null, string? Reason = null);

    /// <summary>Claude Code's user settings: <c>$CLAUDE_CONFIG_DIR/settings.json</c>, else <c>~/.claude/settings.json</c>.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            "settings.json");

    /// <summary>The copy of the settings as they were before rtfc last changed them.</summary>
    public static string BackupPath(string path) => path + ".rtfc-backup";

    /// <summary>Whether a status line command runs rtfc's segment directly, by name, by path or as rtfc.exe. A script that calls it is the user's own.</summary>
    public static bool IsRtfc(string? command) => command is not null && RtfcCommand().IsMatch(command);

    public static Result Install(string path, bool replace)
    {
        if (!TryRead(path, out var root, out var original, out var reason))
        {
            return new Result(Outcome.Unreadable, Reason: reason);
        }

        var existing = Describe(root["statusLine"]);
        if (existing is not null && !replace)
        {
            return new Result(IsRtfc(existing) ? Outcome.AlreadyInstalled : Outcome.OtherStatusLine, existing);
        }

        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command,
            ["refreshInterval"] = RefreshSeconds,
        };
        Write(path, root, original);
        return new Result(Outcome.Installed, existing);
    }

    public static Result Uninstall(string path)
    {
        if (!TryRead(path, out var root, out var original, out var reason))
        {
            return new Result(Outcome.Unreadable, Reason: reason);
        }

        var existing = Describe(root["statusLine"]);
        if (existing is null)
        {
            return new Result(Outcome.NotInstalled);
        }

        if (!IsRtfc(existing))
        {
            return new Result(Outcome.OtherStatusLine, existing);
        }

        root.Remove("statusLine");
        Write(path, root, original);
        return new Result(Outcome.Removed, existing);
    }

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A missing or empty file is an empty object; anything that is not a JSON object is not ours to rewrite.</summary>
    private static bool TryRead(string path, out JsonObject root, out string? original, out string? reason)
    {
        root = [];
        original = File.Exists(path) ? File.ReadAllText(path) : null;
        reason = null;
        if (string.IsNullOrWhiteSpace(original))
        {
            return true;
        }

        try
        {
            if (JsonNode.Parse(original, documentOptions: ReadOptions) is JsonObject parsed)
            {
                root = parsed;
                return true;
            }

            reason = "it is not a JSON object";
        }
        catch (JsonException ex)
        {
            reason = ex.Message;
        }

        return false;
    }

    private static string? Describe(JsonNode? statusLine) => statusLine switch
    {
        null => null,
        JsonObject o when o["command"] is JsonValue v && v.TryGetValue<string>(out var command) => command,
        _ => statusLine.ToJsonString(),
    };

    /// <summary>Keeps the file's line endings, saves what was there, and replaces the file in one rename so Claude Code never reads half of it.</summary>
    private static void Write(string path, JsonObject root, string? original)
    {
        var newline = original?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = newline, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!string.IsNullOrWhiteSpace(original))
        {
            File.WriteAllText(BackupPath(path), original);
        }

        var temp = path + ".rtfc-tmp";
        File.WriteAllText(temp, json + newline);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"(^|[\s""'/\\])rtfc(\.exe)?[""']?\s+statusline\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RtfcCommand();
}
