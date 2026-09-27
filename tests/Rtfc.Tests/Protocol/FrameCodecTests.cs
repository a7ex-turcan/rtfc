using System.IO.Pipelines;
using System.Text;
using Rtfc.Protocol;

namespace Rtfc.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public async Task Frames_round_trip_even_when_they_arrive_one_byte_at_a_time()
    {
        var first = Frames.Serialize(new HelloFrame(1, 7));
        var second = Frames.Serialize(new AckFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", AckStatus.Ok));
        var wire = FrameCodec.Encode(first).Concat(FrameCodec.Encode(second)).ToArray();

        var pipe = new Pipe();
        var reading = Task.Run(async () =>
        {
            var a = await FrameCodec.ReadAsync(pipe.Reader, TestContext.Current.CancellationToken);
            var b = await FrameCodec.ReadAsync(pipe.Reader, TestContext.Current.CancellationToken);
            var end = await FrameCodec.ReadAsync(pipe.Reader, TestContext.Current.CancellationToken);
            return (a, b, end);
        });

        foreach (var b in wire)
        {
            await pipe.Writer.WriteAsync(new[] { b }, TestContext.Current.CancellationToken);
        }

        await pipe.Writer.CompleteAsync();
        var (a, bFrame, end) = await reading;

        Assert.Equal(new HelloFrame(1, 7), Frames.Parse(a!.Value));
        Assert.Equal(new AckFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", AckStatus.Ok), Frames.Parse(bFrame!.Value));
        Assert.Null(end);
    }

    [Fact]
    public async Task A_frame_that_announces_more_than_the_limit_is_refused_before_it_is_buffered()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 0x7f, 0xff, 0xff, 0xff }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stream_that_ends_mid_frame_is_an_error_not_a_clean_close()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(FrameCodec.Encode(Frames.Serialize(new ByeFrame())).AsMemory(0, 5), TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<ProtocolException>(async () => await FrameCodec.ReadAsync(pipe.Reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Encoding_refuses_an_oversized_payload()
    {
        Assert.Throws<ProtocolException>(() => FrameCodec.Encode(new byte[FrameCodec.MaxFrameBytes + 1]));
    }

    [Fact]
    public void Unknown_frame_types_are_ignored_and_malformed_frames_are_not()
    {
        var unknown = Frames.Parse(Encoding.UTF8.GetBytes("""{"type":"typing_indicator","who":"sasha"}"""));
        Assert.Equal(new UnknownFrame("typing_indicator"), unknown);

        Assert.Throws<ProtocolException>(() => Frames.Parse(Encoding.UTF8.GetBytes("""{"no":"type"}""")));
        Assert.Throws<ProtocolException>(() => Frames.Parse(Encoding.UTF8.GetBytes("[1,2]")));
    }

    [Fact]
    public void The_envelope_matches_the_spec()
    {
        var message = new MessageFrame(
            V: 1, Id: "01J8ZQ4Y7K3M9V2T6H0XWBNC5R",
            From: new Address("p_3fa9", "d_77c1"), To: new Address("p_b204", "d_19ae"),
            Seq: 42, Thread: "01J8ZQ3", ReplyTo: null, Origin: "human", Hop: 0,
            SentAt: "2026-09-10T14:03:11.000Z", Body: new MessageBody("How does your retry policy handle poison messages?"));

        var json = Encoding.UTF8.GetString(Frames.Serialize(message));

        Assert.Contains("\"type\":\"message\"", json);
        Assert.Contains("\"from\":{\"person\":\"p_3fa9\",\"device\":\"d_77c1\"}", json);
        Assert.DoesNotContain("\"replyTo\":null", json);
        Assert.Contains("\"sentAt\":\"2026-09-10T14:03:11.000Z\"", json);
        Assert.Contains("\"body\":{\"text\":\"How does", json);
        Assert.Equal(message, Frames.Parse(Frames.Serialize(message)));
    }

    [Fact]
    public void A_receipt_round_trips()
    {
        var receipt = new ReceiptFrame("01J8ZQ4Y7K3M9V2T6H0XWBNC5S", "01J8ZQ4Y7K3M9V2T6H0XWBNC5R", "2026-09-27T12:00:00.000Z", new Address("p_a", "d_a"), new Address("p_b", "d_b"));

        Assert.Equal(receipt, Frames.Parse(Frames.Serialize(receipt)));
        Assert.Contains("\"type\":\"receipt\"", Encoding.UTF8.GetString(Frames.Serialize(receipt)));
    }

    [Fact]
    public void Ulids_are_valid_and_sort_by_time()
    {
        var earlier = Ulid.NewUlid(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var later = Ulid.NewUlid(new DateTimeOffset(2026, 9, 27, 12, 0, 1, TimeSpan.Zero));

        Assert.True(Ulid.IsValid(earlier));
        Assert.True(Ulid.IsValid(later));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.NotEqual(Ulid.NewUlid(), Ulid.NewUlid());
        Assert.False(Ulid.IsValid("not-a-ulid"));
    }
}
