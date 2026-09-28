using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfc.Core;
using Rtfc.Daemon;

namespace Rtfc.Mcp;

/// <summary>
/// <c>rtfc mcp</c> (spec §9.2): JSON-RPC 2.0 over stdio, one message per line, forwarding
/// the seven messaging tools to the daemon. Stateless apart from the lease it holds so the
/// daemon knows a session is open. Hand-rolled, as in rtfq: the protocol is small, still
/// moving, and reflection-based tool discovery would not survive Native AOT.
/// </summary>
/// <remarks>stdout is the protocol. Diagnostics go to <c>log</c> (stderr), never to <c>output</c>.</remarks>
public sealed class McpServer(RtfcHome home, TextReader input, TextWriter output, TextWriter log, string? directory = null)
{
    private const string DefaultProtocolVersion = "2025-06-18";
    private static readonly string[] KnownProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>How often a session without a lease looks for a daemon to hold one on. Well inside the daemon's idle grace.</summary>
    private static readonly TimeSpan LeaseRetry = TimeSpan.FromSeconds(2);

    private readonly DaemonClient _client = new(home);
    private readonly SemaphoreSlim _leaseLock = new(1, 1);

    /// <summary>
    /// The session's directory (spec §10.2). Claude Code sets <c>CLAUDE_PROJECT_DIR</c> to where the session started and starts
    /// this server there too (checked against Claude Code 2.1.283); the daemon resolves it to the enclosing git root.
    /// </summary>
    private readonly string _directory = directory
        ?? (Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } projectDir ? projectDir : Environment.CurrentDirectory);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private DaemonLease? _lease;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await EnsureDaemonAsync(cancellationToken).ConfigureAwait(false);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keeper = KeepLeaseAsync(stopping.Token);
        try
        {
            while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
                if (response is not null)
                {
                    await WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await keeper.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (_lease is not null)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }

            _client.Dispose();
        }
    }

    /// <summary>
    /// Keeps this session's lease on whichever daemon is running (spec §3.1). When the daemon lets go, because it stopped, the
    /// session takes a lease on the next daemon that answers, whoever started it, so the session stays home and that daemon does
    /// not idle out under it. It never starts a daemon: after <c>rtfc daemon stop</c>, the next tool call does that.
    /// </summary>
    private async Task KeepLeaseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_lease is { } lease)
            {
                await lease.Ended.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(LeaseRetry, cancellationToken).ConfigureAwait(false);
            if (await _client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                await TakeLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonObject?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        if (node is not JsonObject request)
        {
            return Error(null, -32600, "Invalid request: batches are not supported.");
        }

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"] as JsonObject;
        if (method is null)
        {
            return id is null ? null : Error(id, -32600, "Invalid request: no method.");
        }

        if (id is null)
        {
            // Notifications: nothing to answer.
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(parameters)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools.List() }),
                "tools/call" => Result(id, await CallToolAsync(parameters, cancellationToken).ConfigureAwait(false)),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (McpException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && KnownProtocolVersions.Contains(requested) ? requested : DefaultProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "rtfc", ["version"] = EntryPoint.Version },
            ["instructions"] = Tools.Instructions,
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new McpException(-32602, "tools/call needs a name.");
        var arguments = parameters["arguments"] as JsonObject ?? [];

        if (!Tools.Exists(name))
        {
            throw new McpException(-32602, $"Unknown tool: {name}");
        }

        if (!await EnsureDaemonAsync(cancellationToken).ConfigureAwait(false))
        {
            return ToolError("rtfcd is not running and could not be started. If this machine has no rtfc identity yet, run /rtfc:init. "
                + $"Otherwise see {home.LogPath}.");
        }

        try
        {
            var text = await Tools.CallAsync(_client, name, arguments, _directory, cancellationToken).ConfigureAwait(false);
            return ToolText(text);
        }
        catch (McpException ex) when (ex.Code == -32602)
        {
            throw;
        }
        catch (Exception ex) when (ex is DaemonException or HttpRequestException or IOException or JsonException)
        {
            log.WriteLine($"rtfc mcp: {name} failed: {ex.Message}");
            return ToolError($"{name} failed: {ex.Message}");
        }
    }

    /// <summary>True when a daemon answers and this server holds a live lease on it. Starts one if none answers, so a daemon that died comes back on the next call.</summary>
    private async Task<bool> EnsureDaemonAsync(CancellationToken cancellationToken)
    {
        if (_lease is { Ended.IsCompleted: false } && await _client.TryStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        var outcome = await DaemonLauncher.EnsureAsync(home, cancellationToken).ConfigureAwait(false);
        if (outcome is not (DaemonLauncher.Outcome.AlreadyRunning or DaemonLauncher.Outcome.Started))
        {
            log.WriteLine($"rtfc mcp: daemon {outcome}");
            return false;
        }

        return await TakeLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes a lease on the running daemon unless a live one is held, replacing one the daemon let go of, and registers the project with it.</summary>
    private async Task<bool> TakeLeaseAsync(CancellationToken cancellationToken)
    {
        await _leaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is { Ended.IsCompleted: false })
            {
                return true;
            }

            if (_lease is not null)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
                _lease = null;
            }

            try
            {
                _lease = await _client.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                log.WriteLine($"rtfc mcp: could not take a lease: {ex.Message}");
                return false;
            }

            try
            {
                // So a contact can address this project before anyone here has called a tool (spec §7.6).
                await _client.RegisterProjectAsync(_directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DaemonException or HttpRequestException or IOException or JsonException)
            {
                log.WriteLine($"rtfc mcp: could not register the project at {_directory}: {ex.Message}");
            }

            return true;
        }
        finally
        {
            _leaseLock.Release();
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(message.ToJsonString()).ConfigureAwait(false);
            await output.WriteAsync('\n').ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static JsonObject Result(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static JsonObject ToolText(string text) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    private static JsonObject ToolError(string text) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = true };

    /// <summary>Re-indents compact JSON for a reader that is a model, not a parser. JsonNode needs no type info, so this stays AOT-clean.</summary>
    public static string Pretty(byte[] compactJson) => JsonNode.Parse(compactJson)!.ToJsonString(Indented);
}

public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
