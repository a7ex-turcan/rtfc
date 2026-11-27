using System.Text.Json.Nodes;
using Rtfc.Cli;

namespace Rtfc.Tests;

/// <summary>
/// Spec §11: <c>rtfc statusline install</c> writes the one <c>statusLine</c> setting, keeps everything else, and never overwrites a
/// status line that is not rtfc's unless told to. Every test points <c>--settings</c> at a temp file, never the real one.
/// </summary>
public class StatuslineInstallTests
{
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var temp = new TempHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr, temp.Home);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string? StatusLineCommand(string settings) => JsonNode.Parse(File.ReadAllText(settings))!["statusLine"]?["command"]?.GetValue<string>();

    [Fact]
    public void Install_adds_the_status_line_and_keeps_the_rest_of_the_settings()
    {
        using var temp = new TempHome();
        var settings = Path.Combine(temp.Home.Root, "settings.json");
        const string original = "{\n  \"model\": \"opus\",\n  \"permissions\": { \"allow\": [\"Bash(git status:*)\"] },\n  \"env\": { \"GREETING\": \"📨 héllo\" }\n}\n";
        File.WriteAllText(settings, original);

        var (code, output, _) = Run("statusline", "install", "--settings", settings);

        Assert.Equal(0, code);
        Assert.Contains("Added the rtfc status line", output);
        var written = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal(("command", "rtfc statusline", 5), (
            written["statusLine"]!["type"]!.GetValue<string>(), written["statusLine"]!["command"]!.GetValue<string>(), written["statusLine"]!["refreshInterval"]!.GetValue<int>()));
        Assert.Equal("opus", written["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status:*)", written["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.Equal("📨 héllo", written["env"]!["GREETING"]!.GetValue<string>());
        Assert.Contains("héllo", File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(StatuslineSettings.BackupPath(settings)));

        // Once is enough: a second install changes nothing.
        var before = File.ReadAllText(settings);
        (code, output, _) = Run("statusline", "install", "--settings", settings);
        Assert.Equal(0, code);
        Assert.Contains("already shows the rtfc status line", output);
        Assert.Equal(before, File.ReadAllText(settings));
    }

    [Fact]
    public void Install_creates_the_settings_file_when_there_is_none()
    {
        using var temp = new TempHome();
        var settings = Path.Combine(temp.Home.Root, "claude-config", "settings.json");

        Assert.Equal(0, Run("statusline", "install", "--settings", settings).Code);

        Assert.Equal("rtfc statusline", StatusLineCommand(settings));
        Assert.False(File.Exists(StatuslineSettings.BackupPath(settings)));
    }

    [Fact]
    public void Someone_elses_status_line_is_replaced_only_when_asked_and_uninstall_takes_back_only_rtfcs()
    {
        using var temp = new TempHome();
        var settings = Path.Combine(temp.Home.Root, "settings.json");
        const string original = """{ "statusLine": { "type": "command", "command": "bash \"$HOME/.claude/statusline-hud.sh\"" } }""";
        File.WriteAllText(settings, original);

        var (code, _, error) = Run("statusline", "install", "--settings", settings);
        Assert.Equal(1, code);
        Assert.Contains("already has a status line: bash \"$HOME/.claude/statusline-hud.sh\"", error);
        Assert.Contains("run `rtfc statusline` and append its output", error);
        Assert.Equal(original, File.ReadAllText(settings));
        Assert.Equal(1, Run("statusline", "uninstall", "--settings", settings).Code);
        Assert.Equal(original, File.ReadAllText(settings));

        (code, var output, _) = Run("statusline", "install", "--replace", "--settings", settings);
        Assert.Equal(0, code);
        Assert.Contains("It replaced: bash \"$HOME/.claude/statusline-hud.sh\"", output);
        Assert.Equal("rtfc statusline", StatusLineCommand(settings));
        Assert.Equal(original, File.ReadAllText(StatuslineSettings.BackupPath(settings)));

        (code, output, _) = Run("statusline", "uninstall", "--settings", settings);
        Assert.Equal(0, code);
        Assert.Contains("Removed the rtfc status line", output);
        Assert.Null(JsonNode.Parse(File.ReadAllText(settings))!["statusLine"]);
        Assert.Contains("has no status line", Run("statusline", "uninstall", "--settings", settings).Output);
    }

    [Fact]
    public void A_longer_command_that_runs_rtfcs_segment_counts_as_installed_and_is_never_cut_down()
    {
        using var temp = new TempHome();
        var settings = Path.Combine(temp.Home.Root, "settings.json");

        // The shape another tool on the owner's machine composed: save Claude Code's input, then hand it to rtfc.
        const string original = """
            {
              "statusLine": {
                "type": "command",
                "command": "IFS= read -r -d '' j; printf '%s' \"$j\" > \"$APPDATA/deephack/claude-usage.json\"; rtfc statusline <<< \"$j\"",
                "refreshInterval": 5
              }
            }
            """;
        File.WriteAllText(settings, original);

        var (code, output, _) = Run("statusline", "install", "--settings", settings);
        Assert.Equal(0, code);
        Assert.Contains("already runs `rtfc statusline` as part of a longer command", output);
        Assert.Contains("Nothing to do", output);

        (code, _, var error) = Run("statusline", "install", "--replace", "--settings", settings);
        Assert.Equal(1, code);
        Assert.Contains("replacing it would drop the rest of that command", error);
        Assert.Contains("claude-usage.json", error);

        (code, _, error) = Run("statusline", "uninstall", "--settings", settings);
        Assert.Equal(1, code);
        Assert.Contains("remove the `rtfc statusline` call from that command yourself", error);

        Assert.Equal(original, File.ReadAllText(settings));
        Assert.False(File.Exists(StatuslineSettings.BackupPath(settings)));
    }

    [Fact]
    public void A_settings_file_that_is_not_json_is_left_alone()
    {
        using var temp = new TempHome();
        var settings = Path.Combine(temp.Home.Root, "settings.json");
        File.WriteAllText(settings, "{ \"model\": ");

        var (code, _, error) = Run("statusline", "install", "--settings", settings);

        Assert.Equal(1, code);
        Assert.Contains("could not be read as JSON", error);
        Assert.Equal("{ \"model\": ", File.ReadAllText(settings));
        Assert.Equal(2, Run("statusline", "install", "extra", "--settings", settings).Code);
    }

    [Theory]
    [InlineData("rtfc statusline", true)]
    [InlineData("/Users/max/.local/share/rtfc/rtfc statusline", true)]
    [InlineData("\"C:/Users/alex/AppData/Local/Programs/rtfc/rtfc.exe\" statusline", true)]
    [InlineData(@"C:\Users\alex\AppData\Local\Programs\rtfc\rtfc.exe statusline", true)]
    [InlineData("bash \"$HOME/.claude/statusline-hud-rtfc.sh\"", false)]
    [InlineData("rtfc statusline | tr a-z A-Z", false)]
    [InlineData("myrtfc statusline", false)]
    public void Only_a_command_that_runs_rtfcs_segment_itself_counts_as_rtfcs(string command, bool rtfc) =>
        Assert.Equal(rtfc, StatuslineSettings.IsRtfc(command));

    [Theory]
    [InlineData("rtfc statusline", true)]
    [InlineData("IFS= read -r -d '' j; printf '%s' \"$j\" > \"$APPDATA/deephack/claude-usage.json\"; rtfc statusline <<< \"$j\"", true)]
    [InlineData("rtfc statusline | tr a-z A-Z", true)]
    [InlineData("bash -c 'rtfc statusline'", true)]
    [InlineData("\"C:/Users/alex/AppData/Local/Programs/rtfc/rtfc.exe\" statusline<<<\"$j\"", true)]
    [InlineData("out=$(rtfc statusline 2>/dev/null); echo \"hud $out\"", true)]
    [InlineData("hud; rtfc statusline && echo", true)]
    [InlineData("bash \"$HOME/.claude/statusline-hud-rtfc.sh\"", false)]
    [InlineData("rtfc statusline install", false)]
    [InlineData("rtfc statuslines", false)]
    [InlineData("myrtfc statusline; echo", false)]
    public void A_command_that_runs_rtfcs_segment_among_other_things_calls_rtfc(string command, bool calls) =>
        Assert.Equal(calls, StatuslineSettings.CallsRtfc(command));
}
