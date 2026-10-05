using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rtfc.Core;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Storage;

namespace Rtfc.Daemon;

public sealed record DaemonOptions(bool IdleExit, bool LogToConsole);

/// <summary>
/// rtfcd (spec §3.1): one per device, owning the node and exposing it on a Unix domain
/// socket through a Kestrel minimal API. Exits on its own once no session has held a
/// lease for the grace period, unless started with idle exit off.
/// </summary>
public static class DaemonHost
{
    /// <summary>How long a starting daemon waits for one that is leaving to let go of the home.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs the daemon until it is told to stop or idles out. A second daemon for the same home gives way without touching anything,
    /// and every way a start can fail is written to <c>rtfcd.log</c>, because <c>rtfc daemon ensure</c> closes the daemon's stderr.
    /// </summary>
    public static async Task<int> RunAsync(RtfcHome home, DaemonOptions options, CancellationToken cancellationToken)
    {
        if (!IdentityStore.Exists(home))
        {
            Console.Error.WriteLine($"rtfc daemon: no identity in {home.KeysDirectory}. Run `rtfc init` first.");
            return 1;
        }

        home.EnsureCreated();
        if (SocketPathProblem(home) is { } tooLong)
        {
            return Refuse(home, tooLong);
        }

        using var client = new DaemonClient(home);
        using var held = await DaemonLock.AcquireAsync(
            home, LockWait, async ct => await client.TryStatusAsync(ct).ConfigureAwait(false) is not null, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            WriteLog(home, "info", $"Another daemon has this home; pid {Environment.ProcessId} leaves it alone.");
            return 0;
        }

        try
        {
            return await RunHoldingLockAsync(home, options, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return Refuse(home, $"Port {ConfigFile.Load(home).Port} is in use by another program, most likely an rtfc daemon that lost its socket "
                + "(rtfc 0.10.0 and older could leave one behind). Stop that program, or set another `port` in config.json and send your contacts a new invite.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(home, $"The daemon could not start or stopped unexpectedly: {ex}");
        }
    }

    /// <summary>What the daemon does once it holds the home's lock: only then may it delete a stale socket and create its own.</summary>
    private static async Task<int> RunHoldingLockAsync(RtfcHome home, DaemonOptions options, CancellationToken cancellationToken)
    {
        var config = ConfigFile.Load(home);
        var self = IdentityStore.Load(home);
        var db = Database.Open(home.DatabasePath);
        var transport = new TcpTransport(config.Port);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(home.LogPath, LogLevel.Information));
        if (options.LogToConsole)
        {
            builder.Logging.AddConsole();
        }

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, IpcJson.Default));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            DeleteStaleSocket(home.SocketPath);
            kestrel.ListenUnixSocket(home.SocketPath);
        });

        var leases = new Leases(TimeProvider.System);
        var sessions = new SessionChannels();
        builder.Services.AddSingleton(leases);
        builder.Services.AddSingleton(self);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<ITransport>(transport);
        var claude = new ClaudeProcessRunner(config.ClaudePath ?? "claude");
        builder.Services.AddSingleton(sp => new Node(
            home, self, db, transport,
            new NodeOptions(HintHosts.Resolve(config), config.AutoAnswer ?? new AutoAnswerConfig(), (config.Outbox ?? new OutboxConfig()).ToSettings()), claude,
            TimeProvider.System, sp.GetRequiredService<ILogger<Node>>(), sessions));

        var app = builder.Build();
        var node = app.Services.GetRequiredService<Node>();
        var logger = app.Services.GetRequiredService<ILogger<Node>>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        MapEndpoints(app, node, leases, sessions, home, lifetime, options);

        await node.StartAsync(cancellationToken).ConfigureAwait(false);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        RestrictSocket(home.SocketPath);
        logger.LogInformation("IPC on {Socket} (pid {Pid}, idle exit {IdleExit})", home.SocketPath, Environment.ProcessId, IdleExit(options, home));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            while (!stop.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                if (leases.IsIdle() && IdleExit(options, home))
                {
                    logger.LogInformation("No session has held a lease for {Grace}; exiting", Leases.Grace);
                    break;
                }

                if (!File.Exists(home.SocketPath))
                {
                    // Nothing can reach a daemon without its socket. Leaving lets the next rtfc call start one that can be reached.
                    logger.LogWarning("The socket {Socket} is gone, so nothing can reach this daemon; exiting", home.SocketPath);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await node.DisposeAsync().ConfigureAwait(false);
        DeleteStaleSocket(home.SocketPath);
        return 0;
    }

    /// <summary>
    /// Every handler states its return type. The request delegate generator, which Native
    /// AOT needs, once turned an <c>async (HttpContext) => …</c> lambda without one into an
    /// endpoint that answered 200 with an empty body; the IPC test guards against a repeat.
    /// </summary>
    private static void MapEndpoints(WebApplication app, Node node, Leases leases, SessionChannels sessions, RtfcHome home, IHostApplicationLifetime lifetime, DaemonOptions options)
    {
        app.MapGet(IpcRoutes.Status, IResult () => Results.Json(new DaemonStatus(
            EntryPoint.Version, Environment.ProcessId, node.Self.PersonId, node.Self.Handle, node.Self.DeviceId, node.Self.DeviceName,
            (node.Transport as TcpTransport)?.Port ?? 0, node.AdvertisedHints(), leases.Count, IdleExit(options, home), node.IsAway), IpcJson.Default.DaemonStatus));

        // Held open for as long as the caller keeps the connection: that is the lease. It also ends the moment the daemon starts
        // stopping, so a session notices at once and shutdown does not wait out the host's timeout on every open session.
        // ?session=<Claude Code session id> also makes it the way messages are pushed into that session (spec §7.3), one JSON line each.
        app.MapGet(IpcRoutes.Lease, async Task<IResult> (HttpContext context) =>
        {
            using var lease = leases.Acquire();
            using var held = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            var session = context.Request.Query["session"].ToString();
            var events = session.Length > 0 ? sessions.Open(session) : null;
            try
            {
                context.Response.ContentType = "text/plain";
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.WriteAsync("lease\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (events is null)
                {
                    await Task.Delay(Timeout.Infinite, held.Token);
                }
                else
                {
                    await foreach (var pushed in events.ReadAllAsync(held.Token))
                    {
                        await context.Response.WriteAsync(JsonSerializer.Serialize(pushed, IpcJson.Default.SessionEvent) + "\n", held.Token);
                        await context.Response.Body.FlushAsync(held.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
            finally
            {
                if (events is not null)
                {
                    sessions.Close(session, events);
                    if (!sessions.IsOpen(session))
                    {
                        // Not when a newer lease of the same session has already taken over.
                        node.SessionClosed(session);
                    }
                }
            }

            // The response was written above; this only states the handler's type, which the request delegate generator needs.
            return Results.Empty;
        });

        app.MapGet(IpcRoutes.Contacts, async Task<IResult> (HttpContext context) =>
        {
            var probe = context.Request.Query["probe"] != "false";
            return Results.Json(await node.ContactsAsync(probe, context.RequestAborted), IpcJson.Default.ContactViewArray);
        });

        app.MapPost(IpcRoutes.Send, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.SendRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'to' and 'text' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(
                await node.SendAsync(request.To, request.Text, new SendOptions(request.Leave, request.Project, request.From), context.RequestAborted),
                IpcJson.Default.SendResult);
        });

        // ?state=parked|all, and ?project=<session directory> to see only the shared inbox and that project (spec §7.6).
        app.MapGet(IpcRoutes.Inbox, IResult (HttpContext context) =>
        {
            var state = context.Request.Query["state"].ToString();
            var project = context.Request.Query["project"].ToString();
            return Results.Json(
                node.ListInbox(state is "" or "parked" ? InboxState.Parked : state == "all" ? null : state, project is "" ? null : project, allProjects: project is ""),
                IpcJson.Default.InboxListing);
        });

        app.MapPost(IpcRoutes.Projects, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ProjectRequest, context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Directory))
            {
                return Results.Json(new IpcError("A JSON body with 'directory' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            var project = node.RegisterProject(request.Directory, request.Session);
            return Results.Json(new ProjectView(project.Name, ProjectPaths.Root(request.Directory)), IpcJson.Default.ProjectView);
        });

        app.MapPost(IpcRoutes.Inbox + "/{id}/open", IResult (string id) =>
            node.Open(id) is { } opened
                ? Results.Json(opened, IpcJson.Default.InboxOpened)
                : Results.Json(new IpcError($"No message with id '{id}'."), IpcJson.Default.IpcError, statusCode: 404));

        app.MapPost(IpcRoutes.Inbox + "/{id}/reply", async Task<IResult> (string id, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ReplyRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'text' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(await node.ReplyAsync(id, request.Text, context.RequestAborted), IpcJson.Default.SendResult);
        });

        app.MapPost(IpcRoutes.Inbox + "/{id}/dismiss", IResult (string id) =>
            node.Dismiss(id)
                ? Results.StatusCode(204)
                : Results.Json(new IpcError($"No message with id '{id}'."), IpcJson.Default.IpcError, statusCode: 404));

        app.MapPost(IpcRoutes.Inbox + "/{id}/gate", async Task<IResult> (string id, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.GateRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'accepted' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return node.RecordGateDecision(id, request.Outcome ?? (request.Accepted ? GateOutcome.Accepted : GateOutcome.Declined))
                ? Results.StatusCode(204)
                : Results.Json(new IpcError($"No message with id '{id}'."), IpcJson.Default.IpcError, statusCode: 404);
        });

        app.MapGet(IpcRoutes.Outbox, IResult () => Results.Json(node.ListOutbox(), IpcJson.Default.OutboxViewArray));

        app.MapPost(IpcRoutes.Away, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ToggleRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'on' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(await node.SetAwayAsync(request.On, context.RequestAborted), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Contacts + "/{handle}/rename", async Task<IResult> (string handle, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.RenameRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'handle' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.Rename(handle, request.Handle), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Contacts + "/{handle}/receipts", async Task<IResult> (string handle, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ToggleRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'on' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.SetReceipts(handle, request.On), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Invite, IResult () => Results.Json(node.CreateInvite(), IpcJson.Default.InviteResult));

        app.MapPost(IpcRoutes.Accept, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.AcceptRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'token' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(await node.AcceptAsync(request.Token, context.RequestAborted), IpcJson.Default.AcceptResult);
        });

        app.MapPost(IpcRoutes.Contacts + "/{handle}/auto", async Task<IResult> (string handle, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.AutoRequest, context.RequestAborted);
            if (request is null)
            {
                return Results.Json(new IpcError("A JSON body with 'mode' and optional 'scope' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.SetAutoMode(handle, request.Mode, request.Scope, request.Session), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Contacts + "/{handle}/remove", IResult (string handle) =>
            Results.Json(node.Remove(handle), IpcJson.Default.ManagementResult));

        app.MapPost(IpcRoutes.Contacts + "/{handle}/block", IResult (string handle) =>
            Results.Json(node.Block(handle), IpcJson.Default.ManagementResult));

        // rtfc hints wrote config.json; the daemon re-reads it and advertises the new hints from now on (spec §8.4).
        app.MapPost(IpcRoutes.Hints, IResult () =>
        {
            node.SetHintHosts(HintHosts.Resolve(ConfigFile.Load(home)));
            return Results.Json(node.AdvertisedHints(), IpcJson.Default.StringArray);
        });

        // Sources (spec §10). Reading is for the `sources` tool; approving, forgetting and accounts are the CLI's (spec §9.3).
        app.MapGet(IpcRoutes.Sources, IResult (HttpContext context) =>
        {
            var project = context.Request.Query["project"].ToString();
            return Results.Json(node.SourceViews(project is "" ? null : project), IpcJson.Default.SourceViewArray);
        });

        app.MapPost(IpcRoutes.SourcesApprove, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ProjectRequest, context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Directory))
            {
                return Results.Json(new IpcError("A JSON body with 'directory' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.ApproveSources(request.Directory), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.ProjectsForget, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.ProjectRequest, context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Directory))
            {
                return Results.Json(new IpcError("A JSON body with 'directory' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.ForgetProject(request.Directory), IpcJson.Default.ManagementResult);
        });

        app.MapGet(IpcRoutes.Accounts, IResult () => Results.Json(node.ListAccounts(), IpcJson.Default.AccountViewArray));

        app.MapPost(IpcRoutes.Accounts, async Task<IResult> (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(IpcJson.Default.AccountRequest, context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Json(new IpcError("A JSON body with 'name', 'type', 'baseUrl' and 'login' is required."), IpcJson.Default.IpcError, statusCode: 400);
            }

            return Results.Json(node.AddAccount(request.Name, request.Type, request.BaseUrl, request.Login, request.AccountId), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Accounts + "/{name}/remove", IResult (string name) =>
            Results.Json(node.RemoveAccount(name), IpcJson.Default.ManagementResult));

        app.MapPost(IpcRoutes.Shutdown, IResult () =>
        {
            lifetime.StopApplication();
            return Results.StatusCode(202);
        });
    }

    /// <summary>
    /// Whether the daemon leaves once no session holds a lease: not when started with <c>--stay</c>, and not while
    /// <c>config.json</c> says always on (spec §3.1). Read each time, so <c>rtfc daemon always-on</c> takes effect at once.
    /// </summary>
    public static bool IdleExit(DaemonOptions options, RtfcHome home)
    {
        if (!options.IdleExit)
        {
            return false;
        }

        try
        {
            return ConfigFile.Load(home).AlwaysOn != true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Half written, or being replaced: staying a little longer is the safe side.
            return false;
        }
    }

    /// <summary>A Unix socket path has a fixed size limit; past it the daemon could not listen at all.</summary>
    internal static string? SocketPathProblem(RtfcHome home)
    {
        var max = OperatingSystem.IsMacOS() ? 103 : 107; // bytes, without the terminating NUL
        var length = System.Text.Encoding.UTF8.GetByteCount(home.SocketPath);
        return length > max
            ? $"The socket path {home.SocketPath} is {length} bytes long, and a Unix socket path can be at most {max} here. Use a shorter RTFC_HOME."
            : null;
    }

    /// <summary>Writes why the daemon will not run to the log and stderr, and returns the exit code for it.</summary>
    private static int Refuse(RtfcHome home, string reason)
    {
        WriteLog(home, "fail", reason);
        Console.Error.WriteLine($"rtfc daemon: {reason}");
        return 1;
    }

    /// <summary>A line in <c>rtfcd.log</c> from outside the logging pipeline, in its format: before the host exists, or after it failed.</summary>
    internal static void WriteLog(RtfcHome home, string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)} {level} rtfcd: {message}{Environment.NewLine}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.AppendAllText(home.LogPath, line);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void DeleteStaleSocket(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The socket is the whole management surface, so only the user may connect (spec §3.1).</summary>
    private static void RestrictSocket(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>The hosts this device advertises in <c>tcp:</c> hints: configured ones, else the hostname plus every IPv4 address worth advertising.</summary>
public static class HintHosts
{
    public static IReadOnlyList<string> Resolve(RtfcConfig config) => config.HintHosts is { Length: > 0 } ? config.HintHosts : Detect();

    /// <summary>What this machine would advertise on its own: its hostname and its usable IPv4 addresses, in adapter order.</summary>
    public static IReadOnlyList<string> Detect()
    {
        var hosts = new List<string>();
        try
        {
            hosts.Add(System.Net.Dns.GetHostName());
        }
        catch (SocketException)
        {
        }

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (IsAdvertisable(address.Address))
                    {
                        hosts.Add(address.Address.ToString());
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        return hosts.Count > 0 ? [.. hosts.Distinct(StringComparer.Ordinal)] : ["localhost"];
    }

    /// <summary>
    /// IPv4, not loopback, not link-local: a Windows machine has a 169.254 address on every idle adapter, and each one advertised
    /// costs a contact a connection attempt. IPv6 addresses are usually temporary and are left to <c>rtfc hints add</c>.
    /// </summary>
    public static bool IsAdvertisable(System.Net.IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && !System.Net.IPAddress.IsLoopback(address)
        && !address.Equals(System.Net.IPAddress.Any)
        && address.GetAddressBytes() is not [169, 254, ..];
}
