// bidi.cs - WebDriver BiDi client for the Anaglyphohol FIREFOX debug browser (launch-firefox.ps1, port 9225).
// The Firefox counterpart of cdp.cs / cdp-shot.cs. Each run opens a BiDi session, does ONE command, ends the session.
//   dotnet run bidi.cs install <extension dir>        temporary add-on (unsigned is fine), prints its id
//   dotnet run bidi.cs nav <url>                      navigates the first tab, waits for load
//   dotnet run bidi.cs eval <urlSubstr> <js | file:x.js>   evaluates in that tab's PAGE realm (awaits promises)
//   dotnet run bidi.cs shot <urlSubstr> <out.png>     screenshot of that tab
//   dotnet run bidi.cs logs <seconds> [url]           console entries (log.entryAdded) for N seconds, optionally
//                                                     navigating to url AFTER subscribing (startup logs included)
// BIDI_PORT env overrides 9225. JSON is built by hand: file-based apps run with reflection-based JsonSerializer off.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length < 1) { Console.Error.WriteLine("usage: bidi.cs install|nav|eval|shot|logs ..."); return 1; }
var port = Environment.GetEnvironmentVariable("BIDI_PORT") ?? "9225";
using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/session"), CancellationToken.None);
int nextId = 1;
var events = new List<JsonElement>();

async Task<JsonElement> Receive(CancellationToken ct)
{
    var buf = new byte[1 << 16];
    var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do { r = await ws.ReceiveAsync(buf, ct); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
    using var doc = JsonDocument.Parse(ms.ToArray());
    return doc.RootElement.Clone();
}

async Task<JsonElement> Call(string method, string paramsJson)
{
    int id = nextId++;
    var json = "{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    while (true)
    {
        var msg = await Receive(CancellationToken.None);
        if (msg.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
        {
            if (msg.TryGetProperty("error", out var err))
                throw new Exception($"{method}: {err.GetString()} - {(msg.TryGetProperty("message", out var m) ? m.GetString() : "")}");
            return msg.GetProperty("result");
        }
        if (msg.TryGetProperty("method", out _)) events.Add(msg);
    }
}

static string J(string v)
{
    var sb = new StringBuilder("\"");
    foreach (var ch in v)
    {
        if (ch == '"') sb.Append("\\\"");
        else if (ch == '\\') sb.Append("\\\\");
        else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
        else sb.Append(ch);
    }
    return sb.Append('"').ToString();
}

async Task<string> FindContext(string urlSubstr)
{
    var tree = await Call("browsingContext.getTree", "{\"maxDepth\":0}");
    foreach (var c in tree.GetProperty("contexts").EnumerateArray())
        if ((c.GetProperty("url").GetString() ?? "").Contains(urlSubstr, StringComparison.OrdinalIgnoreCase))
            return c.GetProperty("context").GetString()!;
    throw new Exception($"no tab whose url contains '{urlSubstr}'");
}

await Call("session.new", "{\"capabilities\":{}}");
int code = 0;
try
{
    switch (args[0])
    {
        case "install":
        {
            var path = Path.GetFullPath(args[1]);
            var r = await Call("webExtension.install", "{\"extensionData\":{\"type\":\"path\",\"path\":" + J(path) + "}}");
            Console.WriteLine("installed " + r.GetProperty("extension").GetString());
            break;
        }
        case "nav":
        {
            var tree = await Call("browsingContext.getTree", "{\"maxDepth\":0}");
            var ctx = tree.GetProperty("contexts")[0].GetProperty("context").GetString()!;
            await Call("browsingContext.navigate", "{\"context\":" + J(ctx) + ",\"url\":" + J(args[1]) + ",\"wait\":\"complete\"}");
            Console.WriteLine("navigated " + args[1]);
            break;
        }
        case "eval":
        {
            var ctx = await FindContext(args[1]);
            var expr = args[2].StartsWith("file:") ? File.ReadAllText(args[2][5..]) : args[2];
            var r = await Call("script.evaluate", "{\"expression\":" + J(expr) + ",\"target\":{\"context\":" + J(ctx) +
                "},\"awaitPromise\":true,\"resultOwnership\":\"none\"}");
            if (r.GetProperty("type").GetString() == "exception")
            {
                Console.WriteLine("EXCEPTION " + r.GetProperty("exceptionDetails").GetProperty("text").GetString());
                code = 3;
            }
            else
            {
                var v = r.GetProperty("result");
                Console.WriteLine(v.TryGetProperty("value", out var val) ? (val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText()) : v.GetRawText());
            }
            break;
        }
        case "shot":
        {
            var ctx = await FindContext(args[1]);
            var r = await Call("browsingContext.captureScreenshot", "{\"context\":" + J(ctx) + "}");
            var outPath = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllBytesAsync(outPath, Convert.FromBase64String(r.GetProperty("data").GetString()!));
            Console.WriteLine(outPath);
            break;
        }
        case "logs":
        {
            await Call("session.subscribe", "{\"events\":[\"log.entryAdded\"]}");
            if (args.Length > 2)
            {
                // optional URL: navigate AFTER subscribing (one BiDi session at a time - a separate nav would be refused)
                var tree = await Call("browsingContext.getTree", "{\"maxDepth\":0}");
                var ctx = tree.GetProperty("contexts")[0].GetProperty("context").GetString()!;
                await Call("browsingContext.navigate", "{\"context\":" + J(ctx) + ",\"url\":" + J(args[2]) + ",\"wait\":\"none\"}");
                foreach (var e in events)   // entries that arrived while the commands above were in flight
                    if (e.GetProperty("method").GetString() == "log.entryAdded")
                        Console.WriteLine($"[{e.GetProperty("params").GetProperty("level").GetString()}] {(e.GetProperty("params").TryGetProperty("text", out var t0) ? t0.GetString() : "")}");
            }
            // ⚠️ never CANCEL a pending ReceiveAsync: that aborts the socket, session.end is never sent, and Firefox's
            // single BiDi session stays taken ("Maximum number of active sessions") until the browser restarts.
            var deadline = DateTime.UtcNow.AddSeconds(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
            var pending = Receive(CancellationToken.None);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || await Task.WhenAny(pending, Task.Delay(left)) != pending) break;
                var msg = await pending;
                if (msg.TryGetProperty("method", out var m) && m.GetString() == "log.entryAdded")
                {
                    var p = msg.GetProperty("params");
                    Console.WriteLine($"[{p.GetProperty("level").GetString()}] {(p.TryGetProperty("text", out var t) ? t.GetString() : "")}");
                }
                pending = Receive(CancellationToken.None);
            }
            // end the session on the live socket; the receive already in flight picks up the reply
            int endId = nextId++;
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"id\":" + endId + ",\"method\":\"session.end\",\"params\":{}}"), WebSocketMessageType.Text, true, CancellationToken.None);
            while (true)
            {
                var msg = await pending;
                if (msg.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == endId) break;
                pending = Receive(CancellationToken.None);
            }
            return code;
        }
        default:
            Console.Error.WriteLine("unknown command " + args[0]);
            code = 1;
            break;
    }
}
finally
{
    try { await Call("session.end", "{}"); } catch { }
}
return code;
