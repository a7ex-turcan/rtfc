using System.Net.Sockets;
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
    public static async Task<int> RunAsync(RtfcHome home, DaemonOptions options, CancellationToken cancellationToken)
    {
        if (!IdentityStore.Exists(home))
        {
            Console.Error.WriteLine($"rtfc daemon: no identity in {home.KeysDirectory}. Run `rtfc init` first.");
            return 1;
        }

        home.EnsureCreated();
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
        builder.Services.AddSingleton(leases);
        builder.Services.AddSingleton(self);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<ITransport>(transport);
        var claude = new ClaudeProcessRunner(config.ClaudePath ?? "claude");
        builder.Services.AddSingleton(sp => new Node(
            home, self, db, transport, new NodeOptions(HintHosts.Resolve(config), config.AutoAnswer ?? new AutoAnswerConfig()), claude,
            TimeProvider.System, sp.GetRequiredService<ILogger<Node>>()));

        var app = builder.Build();
        var node = app.Services.GetRequiredService<Node>();
        var logger = app.Services.GetRequiredService<ILogger<Node>>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        MapEndpoints(app, node, leases, lifetime, options);

        await node.StartAsync(cancellationToken).ConfigureAwait(false);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        RestrictSocket(home.SocketPath);
        logger.LogInformation("IPC on {Socket} (pid {Pid}, idle exit {IdleExit})", home.SocketPath, Environment.ProcessId, options.IdleExit);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            while (!stop.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                if (options.IdleExit && leases.IsIdle())
                {
                    logger.LogInformation("No session has held a lease for {Grace}; exiting", Leases.Grace);
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
    private static void MapEndpoints(WebApplication app, Node node, Leases leases, IHostApplicationLifetime lifetime, DaemonOptions options)
    {
        app.MapGet(IpcRoutes.Status, IResult () => Results.Json(new DaemonStatus(
            EntryPoint.Version, Environment.ProcessId, node.Self.PersonId, node.Self.Handle, node.Self.DeviceId, node.Self.DeviceName,
            (node.Transport as TcpTransport)?.Port ?? 0, node.AdvertisedHints(), leases.Count, options.IdleExit), IpcJson.Default.DaemonStatus));

        // Held open for as long as the caller keeps the connection: that is the lease.
        app.MapGet(IpcRoutes.Lease, async Task (HttpContext context) =>
        {
            using var lease = leases.Acquire();
            context.Response.ContentType = "text/plain";
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.WriteAsync("lease\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
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

            return Results.Json(await node.SendAsync(request.To, request.Text, context.RequestAborted), IpcJson.Default.SendResult);
        });

        app.MapGet(IpcRoutes.Inbox, IResult (HttpContext context) =>
        {
            var state = context.Request.Query["state"].ToString();
            return Results.Json(node.ListInbox(state is "" or "parked" ? InboxState.Parked : state == "all" ? null : state), IpcJson.Default.InboxSummaryArray);
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

            return Results.Json(node.SetAutoMode(handle, request.Mode, request.Scope), IpcJson.Default.ManagementResult);
        });

        app.MapPost(IpcRoutes.Contacts + "/{handle}/remove", IResult (string handle) =>
            Results.Json(node.Remove(handle), IpcJson.Default.ManagementResult));

        app.MapPost(IpcRoutes.Contacts + "/{handle}/block", IResult (string handle) =>
            Results.Json(node.Block(handle), IpcJson.Default.ManagementResult));

        app.MapPost(IpcRoutes.Shutdown, IResult () =>
        {
            lifetime.StopApplication();
            return Results.StatusCode(202);
        });
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

/// <summary>The hosts this device advertises in <c>tcp:</c> hints: configured ones, else the hostname plus every non-loopback IPv4 address.</summary>
public static class HintHosts
{
    public static IReadOnlyList<string> Resolve(RtfcConfig config)
    {
        if (config.HintHosts is { Length: > 0 })
        {
            return config.HintHosts;
        }

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
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        hosts.Add(address.Address.ToString());
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        return hosts.Count > 0 ? hosts : ["localhost"];
    }
}
