using Rtfc.Core;

namespace Rtfc.Tests.Core;

public class ClaudeProcessRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClaudeRunRequest Request = new(
        WorkingDirectory: Path.GetTempPath(), SystemPrompt: "You answer for alex.", Prompt: "<contact_message>hi</contact_message>",
        Timeout: TimeSpan.FromSeconds(180), MaxBudgetUsd: 0.5);

    [Fact]
    public void The_arguments_confine_the_run_to_reading_one_directory()
    {
        var args = ClaudeProcessRunner.Arguments(Request);

        Assert.Equal("-p", args[0]);
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--disable-slash-commands", args);
        Assert.Equal("Read,Grep,Glob", After(args, "--tools"));
        Assert.Equal("Read,Grep,Glob", After(args, "--allowedTools"));
        Assert.Contains("Bash", After(args, "--disallowedTools"));
        Assert.Contains("WebFetch", After(args, "--disallowedTools"));
        Assert.Equal("default", After(args, "--permission-mode"));
        Assert.Equal("0.50", After(args, "--max-budget-usd"));
        Assert.Equal("You answer for alex.", After(args, "--system-prompt"));

        var settings = After(args, "--settings");
        Assert.Contains("\"deny\"", settings);
        Assert.Contains("Read(./.env)", settings);
        Assert.Contains("Read(./**/*.pem)", settings);
        Assert.Contains("Read(./**/.ssh/**)", settings);

        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("bypassPermissions", args);
        Assert.DoesNotContain(Request.Prompt, args); // the message goes in on stdin, never on the command line
    }

    [Fact]
    public void The_result_object_is_parsed_and_anything_else_is_a_failure()
    {
        var ok = ClaudeProcessRunner.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"  Forty-two.  ","num_turns":2}""", "", 0);
        Assert.True(ok.Succeeded);
        Assert.Equal("Forty-two.", ok.Output);

        var error = ClaudeProcessRunner.Parse("""{"type":"result","subtype":"error_max_budget","is_error":true,"result":"Budget exceeded"}""", "", 1);
        Assert.False(error.Succeeded);
        Assert.Equal("Budget exceeded", error.Error);

        var garbage = ClaudeProcessRunner.Parse("not json", "Not logged in.", 1);
        Assert.False(garbage.Succeeded);
        Assert.Equal("Not logged in.", garbage.Error);

        var empty = ClaudeProcessRunner.Parse("", "", 127);
        Assert.False(empty.Succeeded);
        Assert.Contains("127", empty.Error);
    }

    [Fact]
    public void Session_variables_are_the_ones_claude_code_sets()
    {
        Assert.True(ClaudeProcessRunner.IsSessionVariable("CLAUDECODE"));
        Assert.True(ClaudeProcessRunner.IsSessionVariable("CLAUDE_CODE_SESSION_ID"));
        Assert.True(ClaudeProcessRunner.IsSessionVariable("CLAUDE_PID"));
        Assert.False(ClaudeProcessRunner.IsSessionVariable("PATH"));
        Assert.False(ClaudeProcessRunner.IsSessionVariable("HOME"));
        Assert.False(ClaudeProcessRunner.IsSessionVariable("RTFC_HOME"));
    }

    [Fact]
    public async Task The_process_runs_in_the_scope_reads_the_prompt_from_stdin_and_sees_no_session_variables()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the fake claude is a bash script
        }

        using var temp = new TempHome();
        var scope = Path.Combine(temp.Home.Root, "scope-x7f3");
        Directory.CreateDirectory(scope);
        var fake = await WriteFakeClaudeAsync(temp.Home.Root, $$"""
            #!/bin/bash
            printf '%s\n' "$PWD" > "{{temp.Home.Root}}/cwd"
            printf '%s\n' "$@" > "{{temp.Home.Root}}/args"
            echo "${CLAUDECODE:-unset}" > "{{temp.Home.Root}}/env"
            cat > "{{temp.Home.Root}}/stdin"
            printf '{"type":"result","subtype":"success","is_error":false,"result":"Answer: 42","num_turns":1}'
            """);

        var previous = Environment.GetEnvironmentVariable("CLAUDECODE");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        try
        {
            var result = await new ClaudeProcessRunner(fake).RunAsync(Request with { WorkingDirectory = scope }, Ct);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("Answer: 42", result.Output);
            Assert.EndsWith("scope-x7f3", (await File.ReadAllTextAsync(Path.Combine(temp.Home.Root, "cwd"), Ct)).Trim());
            Assert.Contains("--restricted", await File.ReadAllTextAsync(Path.Combine(temp.Home.Root, "args"), Ct));
            Assert.Equal(Request.Prompt, await File.ReadAllTextAsync(Path.Combine(temp.Home.Root, "stdin"), Ct));
            Assert.Equal("unset", (await File.ReadAllTextAsync(Path.Combine(temp.Home.Root, "env"), Ct)).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDECODE", previous);
        }
    }

    [Fact]
    public async Task A_run_that_hangs_is_killed_at_the_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempHome();
        var fake = await WriteFakeClaudeAsync(temp.Home.Root, "#!/bin/bash\ncat > /dev/null\nsleep 30\n");

        var result = await new ClaudeProcessRunner(fake).RunAsync(Request with { Timeout = TimeSpan.FromSeconds(1) }, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task A_missing_executable_is_a_failure_not_a_crash()
    {
        var result = await new ClaudeProcessRunner(Path.Combine(Path.GetTempPath(), "no-such-claude-" + Guid.NewGuid().ToString("N"))).RunAsync(Request, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("could not start", result.Error);
    }

    private static async Task<string> WriteFakeClaudeAsync(string directory, string script)
    {
        var path = Path.Combine(directory, "claude");
        await File.WriteAllTextAsync(path, script, Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string After(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, $"{flag} with a value");
        return args[index + 1];
    }
}
