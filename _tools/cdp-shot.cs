// cdp-shot.cs - screenshot a target page (Page.captureScreenshot) to a PNG.
// Usage: dotnet run cdp-shot.cs <targetUrlSubstr> <out.png>    (CDP_PORT env, default 9224)
// Name the file per run (e.g. _shots/<run-tag>_<name>.png) so a baseline is never overwritten.
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length < 2) { Console.Error.WriteLine("usage: cdp-shot.cs <urlSubstr> <out.png>"); return 1; }
var port = Environment.GetEnvironmentVariable("CDP_PORT") ?? "9224";
using var http = new HttpClient();
using var list = JsonDocument.Parse(await http.GetStringAsync($"http://localhost:{port}/json"));
string? wsUrl = null;
foreach (var t in list.RootElement.EnumerateArray())
    if (t.GetProperty("type").GetString() == "page" && (t.GetProperty("url").GetString() ?? "").Contains(args[0], StringComparison.OrdinalIgnoreCase))
    { wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString(); break; }
if (wsUrl == null) { Console.Error.WriteLine($"no page matching '{args[0]}'"); return 2; }
using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
await ws.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Page.captureScreenshot\",\"params\":{\"format\":\"png\"}}"), WebSocketMessageType.Text, true, CancellationToken.None);
var buf = new byte[1 << 20];
while (true)
{
    var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
    using var doc = JsonDocument.Parse(ms.ToArray());
    if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1) continue;
    var data = doc.RootElement.GetProperty("result").GetProperty("data").GetString()!;
    var path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllBytesAsync(path, Convert.FromBase64String(data));
    Console.WriteLine(path);
    return 0;
}
