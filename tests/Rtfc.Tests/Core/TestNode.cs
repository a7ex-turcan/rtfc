using Microsoft.Extensions.Logging.Abstractions;
using Rtfc.Core;
using Rtfc.Identity;
using Rtfc.Net;
using Rtfc.Storage;

namespace Rtfc.Tests.Core;

/// <summary>One person on one device, in a temp home, listening on a free loopback port. Two of these make an integration test (spec §16).</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly TempHome _temp;

    private TestNode(TempHome temp, Node node, Database database, TcpTransport transport)
    {
        _temp = temp;
        Node = node;
        Database = database;
        Transport = transport;
    }

    public Node Node { get; }
    public Database Database { get; }
    public TcpTransport Transport { get; }
    public RtfcHome Home => _temp.Home;

    public static async Task<TestNode> StartAsync(string handle, string device)
    {
        var temp = new TempHome();
        var self = IdentityStore.Create(temp.Home, handle, device, DateTimeOffset.UtcNow);
        var db = Database.Open(temp.Home.DatabasePath);
        var transport = new TcpTransport(0, connectTimeout: TimeSpan.FromSeconds(2));
        var node = new Node(temp.Home, self, db, transport, new NodeOptions(["127.0.0.1"]), TimeProvider.System, NullLogger.Instance);
        await node.StartAsync(TestContext.Current.CancellationToken);
        return new TestNode(temp, node, db, transport);
    }

    public async ValueTask DisposeAsync()
    {
        await Node.DisposeAsync();
        _temp.Dispose();
    }
}
