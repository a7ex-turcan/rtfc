using Rtfc.Core;
using Rtfc.Daemon;
using Rtfc.Identity;

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
        Assert.Empty(await client.InboxAsync("parked", Ct));
        Assert.Empty(await client.InboxAsync("all", Ct));
        Assert.Null(await client.OpenAsync("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", Ct));

        var invite = await client.InviteAsync(Ct);
        Assert.StartsWith("rtfc1_", invite.Token);
        Assert.Equal(AcceptStatus.Invalid, (await client.AcceptAsync("nonsense", Ct)).Status);
        Assert.Equal(SendStatus.Rejected, (await client.SendAsync("nobody", "hi", Ct)).Status);
        Assert.Equal(SendStatus.Rejected, (await client.ReplyAsync("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", "hi", Ct)).Status);

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
    public async Task Ensure_reports_no_identity_instead_of_starting_a_daemon()
    {
        using var temp = new TempHome();

        Assert.Equal(DaemonLauncher.Outcome.NoIdentity, await DaemonLauncher.EnsureAsync(temp.Home, Ct));
        Assert.False(File.Exists(temp.Home.SocketPath));
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
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
