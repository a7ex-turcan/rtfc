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

    private static (int Code, string Stdout, string Stderr) Run(params string[] args) => Run(null, args);

    private static (int Code, string Stdout, string Stderr) Run(RtfcHome? home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr, home);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
