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

    /// <summary>Holds a lease until the returned object is disposed.</summary>
    public async Task<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(IpcRoutes.Lease, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(response, stream);
    }

    public Task<ContactView[]> ContactsAsync(bool probe, CancellationToken cancellationToken) =>
        GetAsync($"{IpcRoutes.Contacts}?probe={(probe ? "true" : "false")}", IpcJson.Default.ContactViewArray, cancellationToken);

    public Task<SendResult> SendAsync(string to, string text, bool leave, CancellationToken cancellationToken) =>
        SendAsync(new SendRequest(to, text, leave), cancellationToken);

    public Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Send, request, IpcJson.Default.SendRequest, IpcJson.Default.SendResult, cancellationToken);

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
        PostAsync(IpcRoutes.ContactAuto(handle), new AutoRequest(mode, scope), IpcJson.Default.AutoRequest, IpcJson.Default.ManagementResult, cancellationToken);

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

    private sealed class Lease(HttpResponseMessage response, Stream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }
    }
}

/// <summary>The daemon answered with an error, or could not be reached.</summary>
public sealed class DaemonException(string message) : Exception(message);
