using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfc.Core.Sources;

/// <summary>What one run of a feed's command gave: its exit code, what it printed, and the first line of its error output.</summary>
public sealed record FeedRun(int ExitCode, string Output, string Error);

/// <summary>Runs a feed's command. An interface so the adapter's tests can answer without spawning anything.</summary>
public interface IFeedRunner
{
    Task<FeedRun> RunAsync(string command, CancellationToken cancellationToken);
}

/// <summary>
/// A generic feed (spec §10.1): a command the user typed into <c>rtfc account add &lt;name&gt; --type feed</c> prints JSON, and
/// what is new in it becomes inbox items. One item per feed item <c>id</c>, events deduplicated by their <c>id</c>. The first
/// poll only takes note of what is there, so a feed's history never floods the inbox. Everything the feed says is untrusted.
/// The command is the account's secret: it lives in the account's private file, never in a project's subscriptions file,
/// which anything that can write to the project can edit.
/// <code>
/// { "v": 1, "items": [ { "id": "…", "entity": "…", "url": "https://…",
///     "events": [ { "id": "…", "at": "2026-11-23T10:42:00Z", "kind": "message", "actor": "…", "text": "…" } ] } ] }
/// </code>
/// </summary>
public sealed class FeedAdapter(IFeedRunner runner) : ISourceAdapter
{
    public const string TypeName = "feed";
    public const int Version = 1;

    /// <summary>Every type a feed's events can have. All of them land: a subscription's <c>events</c> does not apply to a feed.</summary>
    public static readonly string[] EventTypes = [SourceEventType.Message, SourceEventType.Mentioned, SourceEventType.ReplyToMe];

    /// <summary>A feed that fails is tried again soon: the usual cause is the other machine asleep or offline.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    private const int MaxIdLength = 120;
    private const int MaxTitleLength = 200;
    private const int MaxSummaryLength = 300;
    private const int MaxActorLength = 100;
    private const int MaxUrlLength = 2000;

    public string Type => TypeName;

    /// <summary>Runs the command once and checks what it prints, so a broken command is caught when the account is added.</summary>
    public async Task<SourceIdentity> IdentifyAsync(SourceAccount account, CancellationToken cancellationToken) =>
        new(TypeName, Describe(await RunAsync(account, cancellationToken).ConfigureAwait(false)), TimeZone: null);

    /// <summary>What a feed document holds right now, for <c>rtfc account add</c> to show. Throws as <see cref="Read"/> does.</summary>
    public static string Describe(string document)
    {
        var items = Parse(document);
        var events = items.Sum(i => i.Events.Count);
        return $"{items.Count.ToString(CultureInfo.InvariantCulture)} item(s) with {events.ToString(CultureInfo.InvariantCulture)} event(s) right now";
    }

    public async Task<PollResult> PollAsync(SourceAccount account, JsonObject selector, SourceCursor cursor, DateTimeOffset now, CancellationToken cancellationToken) =>
        Read(account.Name, await RunAsync(account, cancellationToken).ConfigureAwait(false), cursor, now);

    /// <summary>
    /// One feed document against the cursor: what is new in it, and the cursor to continue from. A poll's output and every
    /// line of a stream go through here. The cursor's boundary ids are the fingerprints of every event in the previous
    /// document; an event is new when its fingerprint is not among them, and the first document ever only takes note. An
    /// event's own time comes from the other machine's clock, so it orders events and is never compared with ours (spec §15).
    /// </summary>
    public static PollResult Read(string accountName, string document, SourceCursor cursor, DateTimeOffset now)
    {
        var items = Parse(document);
        var first = cursor.Watermark is null;
        var seen = new HashSet<string>(cursor.BoundaryIds, StringComparer.Ordinal);
        var current = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<SourceEvent>();
        foreach (var item in items)
        {
            var key = $"{TypeName}:{accountName}/{item.Id}";
            foreach (var e in item.Events)
            {
                var fingerprint = Fingerprint(item.RawId, e.Id);
                if (!current.Add(fingerprint) || first || seen.Contains(fingerprint))
                {
                    continue;
                }

                events.Add(new SourceEvent($"{key}:{fingerprint}", key, e.Type, e.Actor, item.Title, e.Summary, item.Url, e.At ?? now));
            }
        }

        events.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
        return new PollResult(events, new SourceCursor(now, [.. current]));
    }

    internal sealed record FeedItem(string RawId, string Id, string Title, string Url, List<FeedEvent> Events);

    internal sealed record FeedEvent(string Id, string Type, string Actor, string Summary, DateTimeOffset? At);

    private async Task<string> RunAsync(SourceAccount account, CancellationToken cancellationToken)
    {
        var run = await runner.RunAsync(account.Token, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new SourceException($"The feed command exited with {run.ExitCode.ToString(CultureInfo.InvariantCulture)}"
                + (run.Error.Length > 0 ? $": {run.Error}" : "."), retryAfter: RetryAfterFailure);
        }

        return run.Output;
    }

    /// <summary>
    /// The feed contract, version 1. A wrong shape fails the poll; a malformed item or event is skipped. A document that says
    /// <c>{"v": 1, "error": "…"}</c> is the feed reporting its own trouble, which fails the poll with that reason.
    /// </summary>
    internal static List<FeedItem> Parse(string output)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(output.TrimStart('﻿'));
        }
        catch (JsonException ex)
        {
            throw new SourceException($"The feed command printed something other than JSON: {ex.Message}", retryAfter: RetryAfterFailure);
        }

        if (root is not JsonObject feed)
        {
            throw new SourceException("The feed command printed JSON that is not an object; the feed is { \"v\": 1, \"items\": [...] }.", retryAfter: RetryAfterFailure);
        }

        if (feed["v"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version != Version)
        {
            throw new SourceException($"The feed has no \"v\": {Version.ToString(CultureInfo.InvariantCulture)}; this rtfc reads feed version {Version.ToString(CultureInfo.InvariantCulture)} only.", retryAfter: RetryAfterFailure);
        }

        if (Text(feed, "error") is { } error)
        {
            throw new SourceException($"The feed reports: {Clean(error, MaxErrorLength)}", retryAfter: RetryAfterFailure);
        }

        if (feed["items"] is not JsonArray list)
        {
            throw new SourceException("The feed has no \"items\" list.", retryAfter: RetryAfterFailure);
        }

        var items = new List<FeedItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.OfType<JsonObject>())
        {
            if (Text(entry, "id") is not { } rawId || !ids.Add(rawId))
            {
                continue;
            }

            var events = new List<FeedEvent>();
            foreach (var e in (entry["events"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(e, "id") is not { } eventId)
                {
                    continue;
                }

                var kind = Text(e, "kind");
                var type = kind switch
                {
                    "mention" or "mentioned" => SourceEventType.Mentioned,
                    "reply" or "reply_to_me" => SourceEventType.ReplyToMe,
                    _ => SourceEventType.Message,
                };
                events.Add(new FeedEvent(eventId, type, Shorten(Text(e, "actor") ?? "Someone", MaxActorLength),
                    Shorten(Text(e, "text") ?? kind ?? "", MaxSummaryLength), ParseTime(Text(e, "at"))));
            }

            items.Add(new FeedItem(rawId, EntityId(rawId), Shorten(Text(entry, "entity") ?? rawId, MaxTitleLength), Link(Text(entry, "url")), events));
        }

        return items;
    }

    /// <summary>
    /// The part of the entity key that comes from the feed. It is shown outside the untrusted wrapper, so it keeps to the entity
    /// key's small alphabet; an id that does not is replaced by a stable hash of it.
    /// </summary>
    internal static string EntityId(string id) =>
        id.Length <= MaxIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.' or '#' or '@')
            ? id
            : "h" + Hash(id);

    private static string Fingerprint(string itemId, string eventId) => Hash(itemId + "\n" + eventId);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Link(string? url) =>
        url is { Length: <= MaxUrlLength } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : "";

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static string Shorten(string text, int length)
    {
        var line = new string([.. text.ReplaceLineEndings(" ").Where(c => !char.IsControl(c))]).Trim();
        return line.Length <= length ? line : line[..(length - 3)] + "...";
    }

    /// <summary>How long a reason from the feed or its error output may be: it reaches the <c>sources</c> view, outside any wrapper.</summary>
    internal const int MaxErrorLength = 160;

    /// <summary>The first non-empty line of <paramref name="text"/>, without control characters and short.</summary>
    internal static string Clean(string text, int length)
    {
        var line = text.Split('\n').Select(l => new string([.. l.Where(c => !char.IsControl(c))]).Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= length ? line : line[..(length - 3)] + "...";
    }

    /// <summary>How a feed's command is started, polled or streamed: through the platform's shell, from the home directory, without Claude Code's session variables.</summary>
    internal static ProcessStartInfo StartInfo(string command)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { Arguments = $"/d /s /c \"{command}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var name in start.Environment.Keys.Where(ClaudeProcessRunner.IsSessionVariable).ToList())
        {
            start.Environment.Remove(name);
        }

        return start;
    }

    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Runs a feed's command the way a terminal would, through <c>cmd /c</c> on Windows and <c>/bin/sh -c</c> elsewhere, from the
/// user's home directory, with no input, a wall-clock limit and a cap on what it may print. Claude Code's session variables
/// are left out, as for the headless runner.
/// </summary>
public sealed class ProcessFeedRunner(TimeSpan timeout, int maxOutputBytes) : IFeedRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaxOutputBytes = 1024 * 1024;
    private const int MaxErrorBytes = 16 * 1024;

    public ProcessFeedRunner()
        : this(DefaultTimeout, DefaultMaxOutputBytes)
    {
    }

    public async Task<FeedRun> RunAsync(string command, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(FeedAdapter.StartInfo(command)) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SourceException($"The feed command could not start: {ex.Message}", retryAfter: FeedAdapter.RetryAfterFailure);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                process.StandardInput.Close();
                var output = ReadCappedAsync(process.StandardOutput.BaseStream, maxOutputBytes, drain: false, limit.Token);
                var error = ReadCappedAsync(process.StandardError.BaseStream, MaxErrorBytes, drain: true, limit.Token);
                var (bytes, overflowed) = await output.ConfigureAwait(false);
                if (overflowed)
                {
                    FeedAdapter.Kill(process);
                    throw new SourceException($"The feed command printed more than {(maxOutputBytes / 1024).ToString(CultureInfo.InvariantCulture)} KiB.", retryAfter: FeedAdapter.RetryAfterFailure);
                }

                var (errorBytes, _) = await error.ConfigureAwait(false);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return new FeedRun(process.ExitCode, Encoding.UTF8.GetString(bytes), FeedAdapter.Clean(Encoding.UTF8.GetString(errorBytes), FeedAdapter.MaxErrorLength));
            }
            catch (OperationCanceledException)
            {
                FeedAdapter.Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
                throw new SourceException($"The feed command did not finish within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.", retryAfter: FeedAdapter.RetryAfterFailure);
            }
        }
    }

    /// <summary>
    /// Reads a stream, keeping at most <paramref name="cap"/> bytes. Past the cap it either stops, and the caller kills the
    /// process, or with <paramref name="drain"/> reads on and discards, so the child never blocks on a full pipe.
    /// </summary>
    private static async Task<(byte[] Bytes, bool Overflowed)> ReadCappedAsync(Stream stream, int cap, bool drain, CancellationToken cancellationToken)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var overflowed = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = cap - (int)kept.Length;
            if (read > room)
            {
                overflowed = true;
                kept.Write(buffer, 0, Math.Max(room, 0));
                if (!drain)
                {
                    break;
                }

                continue;
            }

            kept.Write(buffer, 0, read);
        }

        return (kept.ToArray(), overflowed);
    }
}

/// <summary>How a stream ended, in words for the subscription's error.</summary>
public sealed record FeedStreamEnd(string Reason);

/// <summary>Runs a streaming feed's command. An interface so the node's tests can hand it lines without spawning anything.</summary>
public interface IFeedStreamer
{
    /// <summary>
    /// Runs <paramref name="command"/> until it exits, goes <paramref name="stallAfter"/> without a line, or is cancelled, and
    /// hands every line it prints to <paramref name="onLine"/> as it arrives, one at a time. Returns why it ended; cancellation throws.
    /// </summary>
    Task<FeedStreamEnd> RunAsync(string command, Func<string, Task> onLine, TimeSpan stallAfter, CancellationToken cancellationToken);
}

/// <summary>
/// A streaming feed (spec §10.1): the account's command keeps running and prints one complete feed document per line whenever
/// something changes, and at least every minute while nothing does. Started like a polled feed's command, with no input; a line
/// may be at most 1 MiB. When the daemon goes away without stopping it, the pipe closes and the producer's next write fails.
/// </summary>
public sealed class ProcessFeedStreamer(int maxLineBytes) : IFeedStreamer
{
    public const int DefaultMaxLineBytes = 1024 * 1024;
    private const int MaxErrorLineBytes = 4 * 1024;

    public ProcessFeedStreamer()
        : this(DefaultMaxLineBytes)
    {
    }

    public async Task<FeedStreamEnd> RunAsync(string command, Func<string, Task> onLine, TimeSpan stallAfter, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(FeedAdapter.StartInfo(command)) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new FeedStreamEnd($"The feed stream could not start: {ex.Message}");
        }

        using (process)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            run.CancelAfter(stallAfter);
            var lastError = "";
            var tooLong = false;
            var errors = ReadLinesAsync(process.StandardError.BaseStream, MaxErrorLineBytes, (line, _) =>
            {
                if (FeedAdapter.Clean(line, FeedAdapter.MaxErrorLength) is { Length: > 0 } clean)
                {
                    lastError = clean;
                }

                return Task.CompletedTask;
            }, run.Token);
            try
            {
                process.StandardInput.Close();
                await ReadLinesAsync(process.StandardOutput.BaseStream, maxLineBytes, async (line, overlong) =>
                {
                    if (overlong)
                    {
                        tooLong = true;
                        await run.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    run.CancelAfter(stallAfter);
                    if (line.Length > 0)
                    {
                        await onLine(line).ConfigureAwait(false);
                    }
                }, run.Token).ConfigureAwait(false);

                // The output closed, so the command is ending: give it a moment to say why.
                using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exit.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
                await errors.WaitAsync(exit.Token).ConfigureAwait(false);
                return new FeedStreamEnd($"The feed stream ended with exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}" + (lastError.Length > 0 ? $": {lastError}" : "."));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FeedAdapter.Kill(process);
                return new FeedStreamEnd(tooLong
                    ? $"The feed stream printed a line longer than {(maxLineBytes / 1024).ToString(CultureInfo.InvariantCulture)} KiB, so it was stopped."
                    : $"The feed stream printed nothing for {Span(stallAfter)}, so it was stopped.");
            }
            catch (OperationCanceledException)
            {
                FeedAdapter.Kill(process);
                throw;
            }
        }
    }

    private static string Span(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minute(s)"
        : $"{span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} second(s)";

    /// <summary>
    /// Splits a stream into lines (LF or CRLF, UTF-8) and hands each to <paramref name="onLine"/>. A line longer than
    /// <paramref name="maxBytes"/> is handed over empty with <c>overlong</c> set, and the rest of it is skipped.
    /// </summary>
    internal static async Task ReadLinesAsync(Stream stream, int maxBytes, Func<string, bool, Task> onLine, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var line = new MemoryStream();
        var overlong = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                var end = newline < 0 ? read : newline;
                if (!overlong && line.Length + (end - start) > maxBytes)
                {
                    overlong = true;
                    line.SetLength(0);
                }

                if (!overlong)
                {
                    line.Write(buffer, start, end - start);
                }

                if (newline < 0)
                {
                    break;
                }

                await onLine(overlong ? "" : Decode(line), overlong).ConfigureAwait(false);
                line.SetLength(0);
                overlong = false;
                start = newline + 1;
            }
        }

        if (line.Length > 0 || overlong)
        {
            await onLine(overlong ? "" : Decode(line), overlong).ConfigureAwait(false);
        }

        static string Decode(MemoryStream bytes) => Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('\r').TrimStart('﻿');
    }
}
