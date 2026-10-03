// cdp-click.cs - a TRUSTED mouse click (Input.dispatchMouseEvent) on the first element matching a CSS selector.
// Usage: dotnet run cdp-click.cs <targetUrlSubstr> "<css selector>"    (CDP_PORT env, default 9224)
// Use it where a script click is not enough: Element.requestFullscreen() from Runtime.evaluate (even with
// userGesture) is refused on youtube.com ("not granted"); a real click on the player's own button is not.
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length < 2) { Console.Error.WriteLine("usage: cdp-click.cs <urlSubstr> <css selector>"); return 1; }
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
int nextId = 1;

// JSON built by hand: file-based apps run with reflection-based JsonSerializer disabled.
async Task<JsonElement> Call(string method, string paramsJson)
{
    int id = nextId++;
    var json = "{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    var buf = new byte[1 << 16];
    while (true)
    {
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        using var doc = JsonDocument.Parse(ms.ToArray());
        if (doc.RootElement.TryGetProperty("id", out var got) && got.GetInt32() == id) return doc.RootElement.Clone();
    }
}

// the element's centre in viewport CSS px (what Input.dispatchMouseEvent takes)
var sel = "'" + args[1].Replace("\\", "\\\\").Replace("'", "\\'") + "'";
var expr = "(()=>{const e=document.querySelector(" + sel + ");if(!e)return null;" +
           "e.scrollIntoView({block:'nearest'});const r=e.getBoundingClientRect();return [r.left+r.width/2,r.top+r.height/2];})()";
var res = await Call("Runtime.evaluate", "{\"expression\":" + JsonString(expr) + ",\"returnByValue\":true}");
var value = res.GetProperty("result").GetProperty("result");
if (!value.TryGetProperty("value", out var xy) || xy.ValueKind != JsonValueKind.Array) { Console.Error.WriteLine($"no element matching '{args[1]}'"); return 3; }
double x = xy[0].GetDouble(), y = xy[1].GetDouble();
string Mouse(string type) => "{\"type\":\"" + type + "\",\"x\":" + x.ToString(System.Globalization.CultureInfo.InvariantCulture) +
    ",\"y\":" + y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"button\":\"left\",\"clickCount\":1}";
await Call("Input.dispatchMouseEvent", Mouse("mouseMoved"));
await Call("Input.dispatchMouseEvent", Mouse("mousePressed"));
await Call("Input.dispatchMouseEvent", Mouse("mouseReleased"));
Console.WriteLine($"clicked '{args[1]}' at {x:0},{y:0}");
return 0;

static string JsonString(string v)
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
