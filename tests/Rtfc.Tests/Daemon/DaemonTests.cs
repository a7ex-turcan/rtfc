using Rtfc.Cli;
using Rtfc.Core;
using Rtfc.Daemon;
using Rtfc.Identity;
using Rtfc.Mcp;
using Rtfc.Storage;

namespace Rtfc.Tests.Daemon;

/// <summary>The real daemon host on a real Unix socket, driven through the real client: the same path <c>rtfc mcp</c> and the CLI take.</summary>
public class DaemonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_ipc_surface_answers_over_the_socket_and_shuts_down_on_request()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));

        var daemon = Task.Run(() => DaemonHost.RunAsync(temp.Home, new DaemonOptions(IdleExit: false, LogToConsole: false), Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        var status = await WaitForStatusAsync(client, daemon);

        Assert.Equal("alex", status.Handle);
        Assert.NotEqual(0, status.Port);
        Assert.Equal([$"tcp:127.0.0.1:{status.Port}"], status.Hints);
        Assert.Equal(0, status.Leases);

        // Every read endpoint answers with a body, not an empty 200.
        Assert.Empty(await client.ContactsAsync(probe: true, Ct));
        Assert.Empty(await client.ContactsAsync(probe: false, Ct));
        Assert.Empty((await client.InboxAsync("parked", Ct)).Messages);
        Assert.Empty((await client.InboxAsync("all", Ct)).Messages);

        var projectDirectory = Directory.CreateDirectory(Path.Combine(temp.Home.Root, "payments-api", "src")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Home.Root, "payments-api", ".git"));
        var project = await client.RegisterProjectAsync(projectDirectory, Ct);
        Assert.Equal("payments-api", project.Name);
        Assert.Equal(Path.Combine(temp.Home.Root, "payments-api"), project.Root);
        var scoped = await client.InboxAsync("parked", projectDirectory, Ct);
        Assert.Empty(scoped.Messages);
        Assert.Empty(scoped.Elsewhere);
        Assert.Equal(SendStatus.Rejected, (await client.SendAsync(new SendRequest("nobody", "hi", Project: "../etc", From: projectDirectory), Ct)).Status);
        Assert.Null(await client.OpenAsync("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", Ct));

        var invite = await client.InviteAsync(Ct);
        Assert.StartsWith("rtfc1_", invite.Token);
        Assert.Equal(AcceptStatus.Invalid, (await client.AcceptAsync("nonsense", Ct)).Status);
        Assert.Equal(SendStatus.Rejected, (await client.SendAsync("nobody", "hi", leave: false, Ct)).Status);
        Assert.False(await client.DismissAsync("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", Ct));
        Assert.Empty(await client.OutboxAsync(Ct));
        Assert.Equal(ManagementStatus.Ok, (await client.AwayAsync(true, Ct)).Status);
        Assert.True((await client.TryStatusAsync(Ct))!.Away);
        Assert.Equal(ManagementStatus.Ok, (await client.AwayAsync(false, Ct)).Status);
        Assert.Equal(ManagementStatus.NotAContact, (await client.RenameAsync("nobody", "x", Ct)).Status);
        Assert.Equal(ManagementStatus.NotAContact, (await client.ReceiptsAsync("nobody", true, Ct)).Status);
        Assert.Equal(SendStatus.Rejected, (await client.ReplyAsync("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", "hi", Ct)).Status);

        // Management goes through the socket too; it is the CLI's path, never a tool's.
        Assert.Equal(ManagementStatus.NotAContact, (await client.SetAutoAsync("nobody", "headless", temp.Home.Root, Ct)).Status);
        Assert.Equal(ManagementStatus.NotAContact, (await client.RemoveAsync("nobody", Ct)).Status);
        Assert.Equal(ManagementStatus.NotAContact, (await client.BlockAsync("nobody", Ct)).Status);

        await using (await client.AcquireLeaseAsync(Ct))
        {
            Assert.Equal(1, (await client.TryStatusAsync(Ct))!.Leases);
        }

        await WaitUntilAsync(async () => (await client.TryStatusAsync(Ct))!.Leases == 0);

        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Null(await client.TryStatusAsync(Ct));
        Assert.False(File.Exists(temp.Home.SocketPath));
    }

    [Fact]
    public async Task A_held_lease_ends_as_soon_as_the_daemon_stops()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));
        var daemon = Task.Run(() => DaemonHost.RunAsync(temp.Home, new DaemonOptions(IdleExit: false, LogToConsole: false), Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        await WaitForStatusAsync(client, daemon);

        await using var lease = await client.AcquireLeaseAsync(Ct);
        await WaitUntilAsync(async () => (await client.TryStatusAsync(Ct))!.Leases == 1);
        Assert.False(lease.Ended.IsCompleted);

        // A session must notice at once, and the daemon must not sit on its port waiting for sessions to hang up.
        await client.ShutdownAsync(Ct);
        await lease.Ended.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_session_holds_a_lease_on_a_daemon_restarted_under_it_but_never_starts_one()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));
        using var client = new DaemonClient(temp.Home);
        Task<int> StartDaemon() => Task.Run(() => DaemonHost.RunAsync(temp.Home, new DaemonOptions(IdleExit: false, LogToConsole: false), Ct), Ct);
        async Task<bool> Leases(int count) => (await client.TryStatusAsync(Ct))?.Leases == count;

        var first = StartDaemon();
        await WaitForStatusAsync(client, first);
        var input = new System.IO.Pipelines.Pipe();
        var session = Task.Run(() => new McpServer(temp.Home, new StreamReader(input.Reader.AsStream()), new StringWriter(), new StringWriter(), temp.Home.Root).RunAsync(Ct), Ct);
        await WaitUntilAsync(() => Leases(1));

        // Stopped by someone else, e.g. `rtfc daemon stop`: the session does not bring it back on its own.
        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Null(await client.TryStatusAsync(Ct));

        // Started again by someone else, e.g. the CLI: the session takes a lease on it, so it stays home and the daemon stays up.
        var second = StartDaemon();
        await WaitForStatusAsync(client, second);
        await WaitUntilAsync(() => Leases(1), TimeSpan.FromSeconds(10));

        await input.Writer.CompleteAsync();
        await session.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await WaitUntilAsync(() => Leases(0));
        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await second.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Auto_all_covers_every_active_contact_and_nobody_else()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));
        using (var db = Database.Open(temp.Home.DatabasePath))
        {
            foreach (var (handle, status) in new[] { ("sasha", ContactStatus.Active), ("dan", ContactStatus.Active), ("eve", ContactStatus.Removed) })
            {
                using var other = new TempHome();
                using var person = IdentityStore.Create(other.Home, handle, "laptop", DateTimeOffset.UtcNow);
                db.UpsertContact(new ContactRow(
                    Ids.Person(person.PersonCa), handle, person.PersonCa.RawData, status, DateTimeOffset.UtcNow, InboundMode.Park,
                    AutoScope: null, AutoOwnerDevice: null, ReadReceipts: true, DeviceListVersion: 0, Rev: 1));
            }
        }

        var scope = Directory.CreateDirectory(Path.Combine(temp.Home.Root, "payments-api")).FullName;
        var daemon = Task.Run(() => DaemonHost.RunAsync(temp.Home, new DaemonOptions(IdleExit: false, LogToConsole: false), Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        await WaitForStatusAsync(client, daemon);
        var ctx = new CommandContext(temp.Home, new StringWriter(), new StringWriter(), Ct);

        Assert.Equal(0, await Commands.AutoAsync(ctx, ["--all", "headless", "--scope", scope]));
        var contacts = (await client.ContactsAsync(probe: false, Ct)).ToDictionary(c => c.Handle);
        Assert.Equal((InboundMode.AutoHeadless, scope), (contacts["sasha"].InboundMode, contacts["sasha"].AutoScope));
        Assert.Equal((InboundMode.AutoHeadless, scope), (contacts["dan"].InboundMode, contacts["dan"].AutoScope));
        Assert.Equal(InboundMode.Park, contacts["eve"].InboundMode);
        Assert.Contains("Messages from dan, sasha are now answered", ctx.Out.ToString());
        Assert.Contains("Contacts you accept later still park", ctx.Out.ToString());

        Assert.Equal(0, await Commands.AutoAsync(ctx, ["--all", "off"]));
        Assert.All(await client.ContactsAsync(probe: false, Ct), c => Assert.Equal(InboundMode.Park, c.InboundMode));
        Assert.Equal(2, await Commands.AutoAsync(ctx, ["--all", "sasha", "off"]));

        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    [Fact]
    public async Task Ensure_reports_no_identity_instead_of_starting_a_daemon()
    {
        using var temp = new TempHome();

        Assert.Equal(DaemonLauncher.Outcome.NoIdentity, await DaemonLauncher.EnsureAsync(temp.Home, Ct));
        Assert.False(File.Exists(temp.Home.SocketPath));
    }

    [Fact]
    public async Task Ensure_does_not_hand_its_own_stdout_to_the_daemon()
    {
        // Claude Code runs the SessionStart hook with pipes for stdout and stderr and waits for
        // them to close. A daemon that inherited them held the session up for its whole life.
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));

        using var client = new DaemonClient(temp.Home);
        try
        {
            using var ensure = RtfcProcess.Start(temp.Home, "daemon", "ensure");
            ensure.StandardInput.Close();
            var stdout = ensure.StandardOutput.ReadToEndAsync(Ct);
            var stderr = ensure.StandardError.ReadToEndAsync(Ct);
            await ensure.WaitForExitAsync(Ct);

            await stdout.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await stderr.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            // The pipes closing with ensure is the point. ensure gives the daemon ten seconds,
            // which a cold CI runner once exceeded, so here it only has to come up at all.
            try
            {
                await WaitUntilAsync(async () => await client.TryStatusAsync(Ct) is not null, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"The daemon never answered. ensure exited with {ensure.ExitCode}, stderr: {await stderr}\nrtfcd.log:\n{ReadShared(temp.Home.LogPath)}");
            }
        }
        finally
        {
            if (await client.TryStatusAsync(Ct) is not null)
            {
                await client.ShutdownAsync(Ct);
                await WaitUntilAsync(async () => await client.TryStatusAsync(Ct) is null);
            }
        }
    }

    [Fact]
    public void Leases_go_idle_only_after_the_grace_period()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var leases = new Leases(clock);

        Assert.False(leases.IsIdle());
        clock.Advance(Leases.Grace);
        Assert.True(leases.IsIdle());

        var lease = leases.Acquire();
        Assert.Equal(1, leases.Count);
        Assert.False(leases.IsIdle());
        lease.Dispose();
        lease.Dispose(); // idempotent
        Assert.Equal(0, leases.Count);
        Assert.False(leases.IsIdle());
        clock.Advance(Leases.Grace);
        Assert.True(leases.IsIdle());
    }

    private static async Task<DaemonStatus> WaitForStatusAsync(DaemonClient client, Task<int> daemon)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (daemon.IsCompleted)
            {
                throw new InvalidOperationException($"The daemon exited early with {await daemon}.");
            }

            if (await client.TryStatusAsync(Ct) is { } status)
            {
                return status;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The daemon did not answer on its socket.");
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return "(none)";
        }

        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
