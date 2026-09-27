using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfc.Core;

public sealed record ClaudeRunRequest(string WorkingDirectory, string SystemPrompt, string Prompt, TimeSpan Timeout, double MaxBudgetUsd);

public sealed record ClaudeRunResult(bool Succeeded, string Output, string? Error)
{
    public static ClaudeRunResult Failure(string error) => new(false, "", error);
}

/// <summary>Runs one headless Claude for one message. An interface so the node's tests can answer without spawning anything.</summary>
public interface IClaudeRunner
{
    Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// <c>claude -p</c> in a fresh, isolated, read-only process (spec §7.3). The answering
/// Claude has no session context, no write tools, no shell, no network tools, no MCP
/// servers (so no rtfc <c>send</c>), no plugins, no hooks, and can read only under the
/// scope directory minus files that look like secrets. Capped by budget and wall clock,
/// since this build of Claude Code has no <c>--max-turns</c>.
/// </summary>
public sealed class ClaudeProcessRunner(string executable) : IClaudeRunner
{
    public const string ReadOnlyTools = "Read,Grep,Glob";

    /// <summary>
    /// Read rules for files that hold secrets, relative to the scope. Grep and Glob have no
    /// path rules, so the system prompt and a scope that contains no secrets carry the rest.
    /// </summary>
    public static readonly string[] DenyRules =
    [
        "Read(./.env)", "Read(./.env.*)", "Read(./**/.env)", "Read(./**/.env.*)",
        "Read(./**/*.pem)", "Read(./**/*.key)", "Read(./**/*.p12)", "Read(./**/*.pfx)", "Read(./**/*.jks)", "Read(./**/*.keystore)",
        "Read(./**/id_rsa*)", "Read(./**/id_ed25519*)", "Read(./**/id_ecdsa*)",
        "Read(./**/*credentials*)", "Read(./**/*secret*)", "Read(./**/*.tfstate)", "Read(./**/*.tfvars)",
        "Read(./**/.netrc)", "Read(./**/.npmrc)", "Read(./**/.pypirc)", "Read(./**/.git-credentials)",
        "Read(./**/.aws/**)", "Read(./**/.ssh/**)", "Read(./**/.gnupg/**)",
    ];

    /// <summary>The argument list, separate from the process so it can be tested on every OS.</summary>
    public static IReadOnlyList<string> Arguments(ClaudeRunRequest request)
    {
        var deny = new JsonArray([.. DenyRules.Select(r => (JsonNode)r)]);
        var settings = new JsonObject { ["permissions"] = new JsonObject { ["deny"] = deny } };
        return
        [
            "-p",
            "--output-format", "json",
            "--restricted",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--disable-slash-commands",
            "--tools", ReadOnlyTools,
            "--allowedTools", ReadOnlyTools,
            "--disallowedTools", "Bash,Edit,Write,MultiEdit,NotebookEdit,WebFetch,WebSearch,Task,Agent",
            "--permission-mode", "default",
            "--max-budget-usd", request.MaxBudgetUsd.ToString("0.00", CultureInfo.InvariantCulture),
            "--settings", settings.ToJsonString(),
            "--system-prompt", request.SystemPrompt,
        ];
    }

    /// <summary>
    /// The variables Claude Code sets in a session. The daemon inherits them when a
    /// session's <c>rtfc mcp</c> started it, and a nested Claude refuses to run under them.
    /// </summary>
    public static bool IsSessionVariable(string name) =>
        name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase);

    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments(request))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(IsSessionVariable).ToList())
        {
            start.Environment.Remove(name);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ClaudeRunResult.Failure($"could not start '{executable}': {ex.Message}");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return Parse(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return cancellationToken.IsCancellationRequested
                    ? ClaudeRunResult.Failure("cancelled")
                    : ClaudeRunResult.Failure($"timed out after {request.Timeout.TotalSeconds:0}s");
            }
        }
    }

    /// <summary>
    /// <c>--output-format json</c> prints one object with <c>type: "result"</c>, the answer in
    /// <c>result</c> and <c>is_error</c>. Anything else is treated as a failure with whatever
    /// the process said.
    /// </summary>
    public static ClaudeRunResult Parse(string stdout, string stderr, int exitCode)
    {
        try
        {
            if (JsonNode.Parse(stdout) is JsonObject result && result["type"]?.GetValue<string>() == "result")
            {
                var text = result["result"]?.GetValue<string>() ?? "";
                var isError = result["is_error"]?.GetValue<bool>() ?? false;
                return isError || exitCode != 0
                    ? ClaudeRunResult.Failure(Trim(text.Length > 0 ? text : stderr, "claude reported an error"))
                    : new ClaudeRunResult(true, text.Trim(), null);
            }
        }
        catch (JsonException)
        {
        }

        return ClaudeRunResult.Failure(Trim(stderr.Length > 0 ? stderr : stdout, $"claude exited with {exitCode} and no result"));
    }

    private static string Trim(string text, string fallback)
    {
        var line = text.Trim();
        if (line.Length == 0)
        {
            return fallback;
        }

        return line.Length <= 300 ? line : line[..300] + "...";
    }
}
