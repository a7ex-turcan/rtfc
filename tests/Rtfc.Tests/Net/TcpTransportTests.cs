using System.Diagnostics;
using Rtfc.Net;

namespace Rtfc.Tests.Net;

/// <summary>Spec §8.3: a device's hints are tried together, so one dead address never hides a live one behind a timeout.</summary>
public class TcpTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 192.0.2.0/24 is reserved for documentation and routes nowhere: a connect to it fails or hangs, and never answers.
    private static readonly EndpointHint[] Dead = [EndpointHint.ForTcp("192.0.2.1", 47821), EndpointHint.ForTcp("192.0.2.2", 47821)];

    [Fact]
    public async Task The_first_hint_to_answer_wins_even_when_dead_ones_are_listed_before_it()
    {
        var listener = new TcpTransport(0, connectTimeout: TimeSpan.FromSeconds(3));
        await listener.StartAsync(async _ => await Task.Delay(100, Ct), Ct);
        try
        {
            var transport = new TcpTransport(0, connectTimeout: TimeSpan.FromSeconds(3));
            var hints = Dead.Append(EndpointHint.ForTcp("127.0.0.1", listener.Port)).ToArray();

            var watch = Stopwatch.StartNew();
            var stream = await transport.ConnectAsync("d_x", hints, Ct);
            watch.Stop();

            Assert.NotNull(stream);
            await stream.DisposeAsync();
            // One after another, the two dead hints alone would have cost six seconds.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"connecting took {watch.Elapsed}");
            Assert.True(await transport.IsReachableAsync("d_x", hints, Ct));
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task No_hint_answering_means_null_and_the_callers_cancellation_is_reported_as_such()
    {
        var transport = new TcpTransport(0, connectTimeout: TimeSpan.FromMilliseconds(500));

        Assert.Null(await transport.ConnectAsync("d_x", Dead, Ct));
        Assert.False(await transport.IsReachableAsync("d_x", Dead, Ct));
        Assert.Null(await transport.ConnectAsync("d_x", [new EndpointHint("relay", "wss://relay.example/v1")], Ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync("d_x", Dead, cancelled.Token));
    }
}
