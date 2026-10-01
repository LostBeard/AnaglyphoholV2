using Anaglyphohol;
using Anaglyphohol.Background;
using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Anaglyphohol.Services.Gpu;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using SpawnDev.SpawnJS.RazorUI;

// One WASM app, three extension contexts: the content script on every page, the background worker (Chrome service
// worker / Firefox background page), and extension pages (index.html?$=options ...). No Blazor JS runtime: UI renders
// through SpawnDev.SpawnJS.RazorRenderer.
var builder = SpawnJSAppBuilder.CreateDefault(args, out var JS);
var extensionMode = BrowserExtensionService.GetExtensionMode();

builder.Services.AddSingleton<BrowserExtensionService>();

switch (extensionMode)
{
    case ExtensionMode.Background:
        builder.Services.AddSingleton<BackgroundService>();
        // LAST: releases the runtime events background.js held during the cold start, after every listener is attached
        builder.Services.AddSingleton<StartupFinalizerBackgroundService>();
        break;
    case ExtensionMode.Content:
        // Toolbar overlay in its own shadow root, out of reach of the host page's CSS and scripts. "open" so CDP
        // tooling (_tools/) can reach it; the host page's own scripts never look for it.
        builder.RootComponents.Add<ContentOverlay>(new AttachShadowRootOptions { Mode = "open" })
            .SetHostStyle("all: revert; position: fixed; top: 0; left: 0; width: 100%; height: 0; overflow: visible; z-index: 2147483646; pointer-events: none; font-size: 16px; font-weight: normal; line-height: 1; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;");
        builder.RootComponents.AddSharedStyleSheet("css/MaterialIcons.css", "Anaglyphohol.styles.css");
        builder.Services.AddRazorUI();
        builder.Services.AddSingleton<ContentOverlayService>();
        builder.Services.AddSingleton<PageStyleService>();
        builder.Services.AddSingleton<GpuService>();
        builder.Services.AddSingleton<DepthService>();
        builder.Services.AddSingleton<ThreeDRenderer>();
        builder.Services.AddSingleton<DimencoHeaderService>();
        builder.Services.AddSingleton<TrackedMedia>();
        break;
    default:
        // Extension pages (popup, options, info, viewer) and a plain dev page
        builder.RootComponents.Add<ExtensionPageApp>();
        builder.RootComponents.AddSharedStyleSheet("css/bootstrap/bootstrap.min.css", "css/MaterialIcons.css", "css/app.css", "Anaglyphohol.styles.css");
        builder.Services.AddRazorUI();
        break;
}

// autostarts IBackgroundService / IAsyncBackgroundService services, then renders the root components
await builder.Build().RunAsync();
