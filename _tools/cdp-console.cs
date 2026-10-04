// cdp-console.cs - capture a target's console + exceptions for N seconds (optionally reloading it first).
// Sees EVERY execution context of the target, including an extension content script's isolated world.
// Usage: dotnet run cdp-console.cs <targetUrlSubstr> <seconds> [reload | nav:<url>] [--states]
//   nav:<url>  navigate the target there instead of reloading it (a new origin = a fresh content script + origin)
//   --states   log every anaglyphohol-state change from document start (an observer injected before the page's scripts),
//              so "[state] <id> anaglyph" lines time the first 3D image. Every line carries +ms since the reload/nav.
// Port: CDP_PORT env (default 9224 - the debug Chrome started by launch-chrome.ps1).
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length < 2) { Console.Error.WriteLine("usage: cdp-console.cs <urlSubstr> <seconds> [reload | nav:<url>] [--states]"); return 1; }
var port = Environment.GetEnvironmentVariable("CDP_PORT") ?? "9224";
string sub = args[0];
int seconds = int.Parse(args[1]);
bool reload = args.Skip(2).Contains("reload");
string? navUrl = args.Skip(2).FirstOrDefault(a => a.StartsWith("nav:"))?.Substring(4);
bool states = args.Skip(2).Contains("--states");

using var http = new HttpClient();
using var list = JsonDocument.Parse(await http.GetStringAsync($"http://localhost:{port}/json"));
string? wsUrl = null, chosen = null;
foreach (var t in list.RootElement.EnumerateArray())
{
    var url = t.GetProperty("url").GetString() ?? "";
    var type = t.GetProperty("type").GetString() ?? "";
    if (type is not ("page" or "service_worker" or "background_page" or "worker")) continue;
    if (!url.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
    wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString(); chosen = $"{url} ({type})";
    if (type == "page") break;
}
if (wsUrl == null) { Console.Error.WriteLine($"[cdp-console] no target matching '{sub}'"); return 2; }
Console.Error.WriteLine($"[cdp-console] {chosen}, {seconds}s{(reload ? ", reloading" : "")}{(navUrl != null ? $", navigating to {navUrl}" : "")}");

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
int id = 0;
async Task Send(string method, string paramsJson = "{}") =>
    await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{++id},\"method\":\"{method}\",\"params\":{paramsJson}}}"), WebSocketMessageType.Text, true, CancellationToken.None);
await Send("Runtime.enable");
await Send("Log.enable");
if (states)
{
    await Send("Page.enable");   // addScriptToEvaluateOnNewDocument scripts run only with the Page domain enabled
    const string observer = "new MutationObserver(ms => { for (const m of ms) console.log('[state]', m.target.id || m.target.tagName, " +
        "m.target.getAttribute('anaglyphohol-state')); }).observe(document, { subtree: true, attributes: true, " +
        "attributeFilter: ['anaglyphohol-state'] });";
    await Send("Page.addScriptToEvaluateOnNewDocument", $"{{\"source\":\"{JsonEncodedText.Encode(observer)}\"}}");
}
double startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
if (navUrl != null) await Send("Page.navigate", $"{{\"url\":\"{JsonEncodedText.Encode(navUrl)}\"}}");
else if (reload) await Send("Page.reload", "{\"ignoreCache\":true}");
string Stamp(JsonElement p) => p.TryGetProperty("timestamp", out var ts) ? $"+{ts.GetDouble() - startMs,6:0} " : "";

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var buf = new byte[1 << 20];
var contexts = new Dictionary<int, string>();
try
{
    while (true)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, cts.Token); sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); } while (!r.EndOfMessage);
        using var msg = JsonDocument.Parse(sb.ToString());
        if (!msg.RootElement.TryGetProperty("method", out var m)) continue;
        var p = msg.RootElement.GetProperty("params");
        switch (m.GetString())
        {
            // Runtime.enable REPLAYS a context's earlier console messages, so without this marker a reload run cannot
            // tell old lines from new ones (2026-09-30: "still capturing" was the replayed pre-reload history).
            case "Runtime.executionContextsCleared":
                Console.WriteLine("---------- contexts cleared (page reloaded); lines below are NEW ----------");
                break;
            case "Runtime.executionContextCreated":
                var c = p.GetProperty("context");
                contexts[c.GetProperty("id").GetInt32()] = c.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : "page";
                break;
            case "Runtime.consoleAPICalled":
                var ctx = contexts.GetValueOrDefault(p.GetProperty("executionContextId").GetInt32(), "?");
                var parts = p.GetProperty("args").EnumerateArray().Select(a =>
                    a.TryGetProperty("value", out var v) ? v.ToString() : a.TryGetProperty("description", out var d) ? d.GetString() : a.GetProperty("type").GetString());
                Console.WriteLine($"{Stamp(p)}[{p.GetProperty("type").GetString()}] ({ctx}) {string.Join(" ", parts)}");
                break;
            case "Runtime.exceptionThrown":
                var ed = p.GetProperty("exceptionDetails");
                var desc = ed.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var dd) ? dd.GetString() : ed.GetProperty("text").GetString();
                Console.WriteLine($"[EXCEPTION] {desc}");
                break;
            case "Log.entryAdded":
                var e = p.GetProperty("entry");
                Console.WriteLine($"[log.{e.GetProperty("level").GetString()}] {e.GetProperty("text").GetString()} {(e.TryGetProperty("url", out var eu) ? eu.GetString() : "")}");
                break;
        }
    }
}
catch (OperationCanceledException) { }
return 0;
