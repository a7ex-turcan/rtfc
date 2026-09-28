using System.Text;
using System.Text.Json.Nodes;
using Rtfc.Core;

namespace Rtfc.Tests;

/// <summary>
/// Claude Code reads and writes UTF-8 on every pipe. On Windows .NET would otherwise use the
/// console's code page, 437 on an en-US machine: the status line showed <c>?? 1 � alex</c>,
/// and text sent through the MCP server arrived as <c>├⌐</c> where it said <c>é</c>.
/// </summary>
public class PipeEncodingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_status_line_is_utf8_whatever_the_console_code_page()
    {
        using var temp = new TempHome();
        StatusFile.Write(temp.Home.StatusPath, new StatusSnapshot(new StatusGlobal(1, ["Ștefan"]), [], Away: false));

        using var rtfc = RtfcProcess.Start(temp.Home, "statusline");
        rtfc.StandardInput.Close();
        using var output = new MemoryStream();
        await rtfc.StandardOutput.BaseStream.CopyToAsync(output, Ct);
        await rtfc.WaitForExitAsync(Ct);

        Assert.Equal("📨 1 · Ștefan", Encoding.UTF8.GetString(output.ToArray()).TrimEnd());
    }

    [Fact]
    public async Task The_mcp_server_reads_utf8_whatever_the_console_code_page()
    {
        using var temp = new TempHome();
        const string requests = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sénd_📨","arguments":{}}}

            """;

        using var rtfc = RtfcProcess.Start(temp.Home, "mcp");
        await rtfc.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(requests.ReplaceLineEndings("\n")), Ct);
        rtfc.StandardInput.Close();
        using var reader = new StreamReader(rtfc.StandardOutput.BaseStream, Encoding.UTF8);
        var responses = (await reader.ReadToEndAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await rtfc.WaitForExitAsync(Ct);

        var response = JsonNode.Parse(responses.Single(r => r.Contains("\"id\":2", StringComparison.Ordinal)))!;
        Assert.Equal("Unknown tool: sénd_📨", response["error"]!["message"]!.GetValue<string>());
    }
}
