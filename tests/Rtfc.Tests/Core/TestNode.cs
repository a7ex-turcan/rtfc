using Microsoft.Extensions.Logging.Abstractions;
using Rtfc.Core;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>Answers without spawning anything. Records every request; the handler decides the outcome.</summary>
public sealed class FakeClaudeRunner : IClaudeRunner
{
    private readonly Lock _lock = new();
    private readonly List<ClaudeRunRequest> _requests = [];

    public IReadOnlyList<ClaudeRunRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public Func<ClaudeRunRequest, Task<ClaudeRunResult>> Handler { get; set; } =
        _ => Task.FromResult(new ClaudeRunResult(true, "Poison messages go to a dead-letter queue after 5 attempts.", null));

    public Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }

        return Handler(request);
    }
}

/// <summary>One person on one device, in a temp home, listening on a free loopback port. Two of these make an integration test (spec §16).</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly TempHome _temp;

    private TestNode(TempHome temp, Node node, Database database, TcpTransport transport, FakeClaudeRunner claude)
    {
        _temp = temp;
        Node = node;
        Database = database;
        Transport = transport;
        Claude = claude;
    }

    public Node Node { get; }
    public Database Database { get; }
    public TcpTransport Transport { get; }
    public FakeClaudeRunner Claude { get; }
    public RtfcHome Home => _temp.Home;

    /// <summary>Tests want the pump to run often and entries to live long unless a test says otherwise.</summary>
    public static readonly OutboxSettings DefaultOutbox = new(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(500), TimeSpan.FromDays(30));

    public static async Task<TestNode> StartAsync(string handle, string device, AutoAnswerConfig? autoAnswer = null, OutboxSettings? outbox = null)
    {
        var temp = new TempHome();
        var self = IdentityStore.Create(temp.Home, handle, device, DateTimeOffset.UtcNow);
        var db = Database.Open(temp.Home.DatabasePath);
        var transport = new TcpTransport(0, connectTimeout: TimeSpan.FromSeconds(2));
        var claude = new FakeClaudeRunner();
        var node = new Node(
            temp.Home, self, db, transport, new NodeOptions(["127.0.0.1"], autoAnswer ?? new AutoAnswerConfig(), outbox ?? DefaultOutbox), claude,
            TimeProvider.System, NullLogger.Instance);
        await node.StartAsync(TestContext.Current.CancellationToken);
        return new TestNode(temp, node, db, transport, claude);
    }

    /// <summary>A task that completes with the message id the next time an auto-answer attempt finishes. Subscribe before acting.</summary>
    public Task<string> NextAutoAnswer()
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.AutoAnswered += Handler;
        return done.Task;

        void Handler(string id)
        {
            Node.AutoAnswered -= Handler;
            done.TrySetResult(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Node.DisposeAsync();
        _temp.Dispose();
    }
}
