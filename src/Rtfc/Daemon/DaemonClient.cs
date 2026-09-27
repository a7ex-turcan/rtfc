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

    public Task<SendResult> SendAsync(string to, string text, CancellationToken cancellationToken) =>
        PostAsync(IpcRoutes.Send, new SendRequest(to, text), IpcJson.Default.SendRequest, IpcJson.Default.SendResult, cancellationToken);

    public Task<InboxSummary[]> InboxAsync(string state, CancellationToken cancellationToken) =>
        GetAsync($"{IpcRoutes.Inbox}?state={Uri.EscapeDataString(state)}", IpcJson.Default.InboxSummaryArray, cancellationToken);

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
