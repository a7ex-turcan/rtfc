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
    [InlineData("not-a-mode")]
    public void Unknown_or_missing_mode_prints_usage_to_stderr(params string[] args)
    {
        var (code, stdout, stderr) = Run(args);

        Assert.Equal(EntryPoint.Usage, code);
        Assert.Contains("usage: rtfc", stderr);
        Assert.Empty(stdout);
    }

    // stdout is the MCP protocol in `rtfc mcp` and the status bar in `rtfc statusline`,
    // so nothing but real output may ever be written there.
    [Theory]
    [InlineData("daemon")]
    [InlineData("mcp")]
    [InlineData("statusline")]
    public void Unimplemented_modes_fail_without_touching_stdout(string mode)
    {
        var (code, stdout, stderr) = Run(mode);

        Assert.Equal(EntryPoint.NotImplemented, code);
        Assert.Contains("not implemented", stderr);
        Assert.Empty(stdout);
    }

    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
