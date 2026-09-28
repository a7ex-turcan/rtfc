using System.Text;
using Rtfc;

// Claude Code speaks UTF-8 on every pipe: the MCP protocol, the status line, the output of a
// skill's `!rtfc`. On Windows .NET would use the console's code page instead, 437 on an en-US
// machine, which turns 📨 into ?? on the way out and é into ├⌐ on the way in. So a redirected
// stream is UTF-8 whatever the console says; a real console keeps .NET's own handling.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var stdin = Console.IsInputRedirected ? TextReader.Synchronized(new StreamReader(Console.OpenStandardInput(), utf8)) : Console.In;
var stdout = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
var stderr = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;

return EntryPoint.Run(args, stdout, stderr, stdin: stdin);

TextWriter Utf8Writer(Stream stream) => TextWriter.Synchronized(new StreamWriter(stream, utf8) { AutoFlush = true });
