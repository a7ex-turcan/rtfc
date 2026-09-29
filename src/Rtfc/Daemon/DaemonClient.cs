using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Rtfc.Core;

namespace Rtfc.Daemon;

/// <summary>Talks to rtfcd over its Unix socket. Used by <c>rtfc mcp</c> and the management CLI; stateless.</summary>
public sealed class DaemonClient : IDisposable
{
    private readonly HttpClient _http;

    public DaemonClient(RtfcHome home, TimeSpan? timeout = null)
    {
        var socketPath = home.SocketPath;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://rtfcd/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Null when nothing answers on the socket.</summary>
    public async Task<DaemonStatus?> TryStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            return await _http.GetFromJsonAsync(IpcRoutes.Status, IpcJson.Default.DaemonStatus, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Holds a lease until the returned object is disposed, or until the daemon lets go of it (<see cref="DaemonLease.Ended"/>).</summary>
    public Task<DaemonLease> AcquireLeaseAsync(CancellationToken cancellationToken) => AcquireLeaseAsync(session: null, onPush: null, cancellationToken);

    /// <summary>
    /// A lease that names its Claude Code <paramref name="session"/>: messages the daemon pushes into that session (spec §7.3)
    /// arrive on it and are handed to <paramref name="onPush"/>, in order.
    /// </summary>
    public async Task<DaemonLease> AcquireLeaseAsync(string? session, Func<SessionEvent, Task>? onPush, CancellationToken cancellationToken)
    {
        var route = string.IsNullOrEmpty(session) ? IpcRoutes.Lease : $"{IpcRoutes.Lease}?session={Uri.EscapeDataString(session)}";
        var response = await _http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new DaemonLease(response, stream, onPush);
    }

    public Task<ContactView[]> ContactsAsync(bool probe, CancellationToken cancellationToken) =>
        GetAsync($"{IpcRoutes.Contacts}?probe={(probe ? "true" : "false")}", IpcJson.Default.ContactViewArray, cancellationToken);

    public Task<SendResult> SendAsync(string to, string text, bool leave, CancellationToken cancellationToken) =>
        SendAsync(new SendRequest(to, text, leave), cancellationToken);

    public Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Send, request, IpcJson.Default.SendRequest, IpcJson.Default.SendResult, cancellationToken);

    /// <summary>After <c>rtfc hints</c> wrote config.json: the daemon re-reads it and returns what it advertises now (spec §8.4).</summary>
    public async Task<string[]> ReloadHintsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.Hints, content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(IpcJson.Default.StringArray, cancellationToken).ConfigureAwait(false)) ?? [];
    }

    /// <summary>Tells the daemon which project a session runs in (spec §10.2).</summary>
    public Task<ProjectView> RegisterProjectAsync(string directory, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Projects, new ProjectRequest(directory), IpcJson.Default.ProjectRequest, IpcJson.Default.ProjectView, cancellationToken);

    /// <summary>False when there is no such message.</summary>
    public async Task<bool> DismissAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.InboxDismiss(id), content: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<OutboxView[]> OutboxAsync(CancellationToken cancellationToken) =>
        GetAsync(IpcRoutes.Outbox, IpcJson.Default.OutboxViewArray, cancellationToken);

    public Task<ManagementResult> AwayAsync(bool on, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Away, new ToggleRequest(on), IpcJson.Default.ToggleRequest, IpcJson.Default.ManagementResult, cancellationToken);

    public Task<ManagementResult> RenameAsync(string handle, string newHandle, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.ContactRename(handle), new RenameRequest(newHandle), IpcJson.Default.RenameRequest, IpcJson.Default.ManagementResult, cancellationToken);

    public Task<ManagementResult> ReceiptsAsync(string handle, bool on, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.ContactReceipts(handle), new ToggleRequest(on), IpcJson.Default.ToggleRequest, IpcJson.Default.ManagementResult, cancellationToken);

    public Task<InboxListing> InboxAsync(string state, CancellationToken cancellationToken) => InboxAsync(state, projectDirectory: null, cancellationToken);

    /// <summary>Every project's messages, or with <paramref name="projectDirectory"/>, the shared inbox and that project's, plus counts for the others.</summary>
    public Task<InboxListing> InboxAsync(string state, string? projectDirectory, CancellationToken cancellationToken) =>
        GetAsync(
            $"{IpcRoutes.Inbox}?state={Uri.EscapeDataString(state)}" + (projectDirectory is null ? "" : $"&project={Uri.EscapeDataString(projectDirectory)}"),
            IpcJson.Default.InboxListing, cancellationToken);

    public async Task<InboxOpened?> OpenAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.InboxOpen(id), content: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(IpcJson.Default.InboxOpened, cancellationToken).ConfigureAwait(false);
    }

    public Task<SendResult> ReplyAsync(string id, string text, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.InboxReply(id), new ReplyRequest(text), IpcJson.Default.ReplyRequest, IpcJson.Default.SendResult, cancellationToken);

    public async Task<InviteResult> InviteAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.Invite, content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(IpcJson.Default.InviteResult, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<AcceptResult> AcceptAsync(string token, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Accept, new AcceptRequest(token), IpcJson.Default.AcceptRequest, IpcJson.Default.AcceptResult, cancellationToken);

    public Task<ManagementResult> SetAutoAsync(string handle, string mode, string? scope, CancellationToken cancellationToken) =>
        SetAutoAsync(handle, mode, scope, session: null, cancellationToken);

    public Task<ManagementResult> SetAutoAsync(string handle, string mode, string? scope, string? session, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.ContactAuto(handle), new AutoRequest(mode, scope, session), IpcJson.Default.AutoRequest, IpcJson.Default.ManagementResult, cancellationToken);

    /// <summary>For the plugin's hook: the user accepted or declined a pushed message in their session (spec §7.3). False when there is no such message.</summary>
    public async Task<bool> GateDecisionAsync(string id, bool accepted, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new GateRequest(accepted), IpcJson.Default.GateRequest);
        using var response = await _http.PostAsync(IpcRoutes.InboxGate(id), content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ManagementResult> RemoveAsync(string handle, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.ContactRemove(handle), content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(IpcJson.Default.ManagementResult, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<ManagementResult> BlockAsync(string handle, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.ContactBlock(handle), content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(IpcJson.Default.ManagementResult, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(IpcRoutes.Shutdown, content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string route, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string route, TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(request, requestInfo);
        using var response = await _http.PostAsync(route, content, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(responseInfo, cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            message = (await response.Content.ReadFromJsonAsync(IpcJson.Default.IpcError, cancellationToken).ConfigureAwait(false))?.Error
                ?? $"HTTP {(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            message = $"HTTP {(int)response.StatusCode}";
        }

        throw new DaemonException(message);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// One session's hold on the daemon (spec §3.1): an IPC request the daemon keeps open. <see cref="Ended"/> completes when the
/// daemon lets go of it, because it stopped or the connection broke, so the holder can tell a live lease from a dead one. A lease
/// that names its session also carries the messages pushed into it (spec §7.3), one JSON line each after the first line.
/// </summary>
public sealed class DaemonLease : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly Func<SessionEvent, Task>? _onPush;
    private readonly CancellationTokenSource _stop = new();

    internal DaemonLease(HttpResponseMessage response, Stream stream, Func<SessionEvent, Task>? onPush = null)
    {
        _response = response;
        _stream = stream;
        _onPush = onPush;
        Ended = WatchAsync();
    }

    public Task Ended { get; }

    private async Task WatchAsync()
    {
        try
        {
            using var reader = new StreamReader(_stream, leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (_onPush is null || !line.StartsWith('{'))
                {
                    continue;
                }

                SessionEvent? pushed;
                try
                {
                    pushed = JsonSerializer.Deserialize(line, IpcJson.Default.SessionEvent);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (pushed is not null)
                {
                    await _onPush(pushed).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        await Ended.ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>The daemon answered with an error, or could not be reached.</summary>
public sealed class DaemonException(string message) : Exception(message);
