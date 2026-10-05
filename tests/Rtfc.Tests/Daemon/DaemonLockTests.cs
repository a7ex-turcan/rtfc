using System.Net;
using System.Net.Sockets;
using Rtfc.Core;
using Rtfc.Daemon;
using Rtfc.Identity;

namespace Rtfc.Tests.Daemon;

/// <summary>
/// One daemon per home (spec §3.1). A second daemon, from a login item, a session's start or an upgrade, must never take the socket
/// from a running one, at start or on the way out; and a daemon that cannot start says why in the log, because nobody reads its stderr.
/// </summary>
public class DaemonLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DaemonOptions Stay = new(IdleExit: false, LogToConsole: false);

    private static TempHome NewHome(int port = 0)
    {
        var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: port, HintHosts: ["127.0.0.1"]));
        return temp;
    }

    private static async Task<DaemonStatus> AnswersAsync(DaemonClient client, Task<int> daemon)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (daemon.IsCompleted)
            {
                Assert.Fail($"The daemon exited with {await daemon} before it answered.");
            }

            if (await client.TryStatusAsync(Ct) is { } status)
            {
                return status;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The daemon never answered.");
    }

    [Fact]
    public async Task A_second_daemon_for_the_same_home_leaves_the_running_ones_socket_alone()
    {
        using var temp = NewHome();
        var first = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        var running = await AnswersAsync(client, first);

        var second = await Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(0, second);
        Assert.True(File.Exists(temp.Home.SocketPath));
        Assert.Equal(running.Port, (await client.TryStatusAsync(Ct))?.Port);
        Assert.Contains("another daemon", await File.ReadAllTextAsync(temp.Home.LogPath, Ct), StringComparison.OrdinalIgnoreCase);

        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.False(File.Exists(temp.Home.SocketPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    public async Task Two_daemons_starting_together_leave_exactly_one_that_answers(int delayMs)
    {
        using var temp = NewHome();
        var a = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);
        await Task.Delay(delayMs, Ct);
        var b = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);

        // One of them gives way and exits cleanly; the other keeps the socket and answers.
        var gaveWay = await Task.WhenAny(a, b).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(0, await gaveWay);
        var stays = gaveWay == a ? b : a;
        using var client = new DaemonClient(temp.Home);
        await AnswersAsync(client, stays);
        await Task.Delay(1500, Ct);
        Assert.NotNull(await client.TryStatusAsync(Ct));
        Assert.True(File.Exists(temp.Home.SocketPath));

        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await stays.WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    [Fact]
    public async Task A_daemon_that_starts_while_the_last_one_is_leaving_waits_for_it_and_keeps_its_own_socket()
    {
        using var temp = NewHome();
        var leaving = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        var old = await AnswersAsync(client, leaving);

        await client.ShutdownAsync(Ct);
        var next = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);

        Assert.Equal(0, await leaving.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        var now = await AnswersAsync(client, next);
        Assert.NotEqual(old.Port, now.Port);
        await Task.Delay(1500, Ct);
        Assert.True(File.Exists(temp.Home.SocketPath));
        Assert.NotNull(await client.TryStatusAsync(Ct));

        await client.ShutdownAsync(Ct);
        Assert.Equal(0, await next.WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    [Fact]
    public async Task A_daemon_that_cannot_have_its_port_says_so_in_the_log_instead_of_dying_silently()
    {
        // Held the way the daemon holds it, every address on both families: on Windows a listener on 127.0.0.1 alone would not stop it.
        using var holder = new TcpListener(IPAddress.IPv6Any, 0);
        holder.Server.DualMode = true;
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var temp = NewHome(port);
        await File.WriteAllTextAsync(temp.Home.SocketPath, "", Ct);

        var code = await DaemonHost.RunAsync(temp.Home, Stay, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(1, code);
        var log = await File.ReadAllTextAsync(temp.Home.LogPath, Ct);
        Assert.Contains($"port {port}", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in use", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Port {port}", DaemonLauncher.Diagnose(temp.Home));
    }

    [Fact]
    public async Task A_socket_path_too_long_for_the_operating_system_is_refused_with_a_reason()
    {
        using var temp = new TempHome();
        var deep = new RtfcHome(Path.Combine(temp.Home.Root, new string('d', 60), new string('e', 60)));
        IdentityStore.Create(deep, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        ConfigFile.Save(deep, new RtfcConfig(Port: 0, HintHosts: ["127.0.0.1"]));

        var code = await DaemonHost.RunAsync(deep, Stay, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(1, code);
        Assert.Contains("socket path", await File.ReadAllTextAsync(deep.LogPath, Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_daemon_whose_socket_disappears_leaves_so_the_next_call_can_start_one_that_answers()
    {
        using var temp = NewHome();
        var daemon = Task.Run(() => DaemonHost.RunAsync(temp.Home, Stay, Ct), Ct);
        using var client = new DaemonClient(temp.Home);
        await AnswersAsync(client, daemon);

        // A live socket file survives the watchdog: the daemon is still there after more than one check.
        await Task.Delay(TimeSpan.FromSeconds(6), Ct);
        Assert.False(daemon.IsCompleted);

        File.Delete(temp.Home.SocketPath);

        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Contains("socket", await File.ReadAllTextAsync(temp.Home.LogPath, Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnose_points_at_the_log_when_the_port_is_free()
    {
        using var temp = NewHome();
        Assert.Contains(temp.Home.LogPath, DaemonLauncher.Diagnose(temp.Home));
    }
}
