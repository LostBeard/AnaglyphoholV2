using Anaglyphohol;
using Anaglyphohol.Background;
using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Anaglyphohol.Services.Converter;
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
// the stored WebGPU kernel shaders (one store for the whole extension: pages read it, the background warms it)
builder.Services.AddSingleton<ShaderCacheService>();
// MessagePorts from content scripts to the background (the shared converter's transport; SpawnDev.SpawnJS.BrowserExtension)
builder.Services.AddSingleton<ExtensionPortService>();

switch (extensionMode)
{
    case ExtensionMode.Background:
        // the kernel shader warm-up at install / update (ShaderWarmupService) runs the depth models here once
        builder.Services.AddSingleton<GpuService>();
        builder.Services.AddSingleton<DepthService>();
        builder.Services.AddSingleton<ShaderWarmupService>();
        builder.Services.AddSingleton<BackgroundService>();
        // fetches no-CORS page images for content scripts (Chrome; see ImageRelay)
        builder.Services.AddSingleton<ImageRelayBackgroundService>();
        // the shared converter (opt-in, Chrome): hands page ports to the offscreen document
        builder.Services.AddSingleton<ConverterRelayService>();
        // LAST: releases the runtime events background.js held during the cold start, after every listener is attached
        builder.Services.AddSingleton<StartupFinalizerBackgroundService>();
        break;
    case ExtensionMode.Content:
        // FIRST: removes a previous version's dead toolbar / overlays (Firefox runs a new version in already open tabs)
        builder.Services.AddSingleton<LeftoverCleanupService>();
        // Toolbar overlay in its own shadow root, out of reach of the host page's CSS and scripts. "open" so CDP
        // tooling (_tools/) can reach it; the host page's own scripts never look for it.
        builder.RootComponents.Add<ContentOverlay>(new AttachShadowRootOptions { Mode = "open" })
            .SetHostStyle("all: revert; position: fixed; top: 0; left: 0; width: 100%; height: 0; overflow: visible; z-index: 2147483646; pointer-events: none; font-size: 16px; font-weight: normal; line-height: 1; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;")
            // marked, so the NEXT instance on this page (an update in Firefox) can find and remove it (LeftoverCleanupService)
            .ConfigureHost(m => { m.Host?.SetAttribute(LeftoverCleanupService.ToolbarHostAttribute, ""); return Task.CompletedTask; });
        builder.RootComponents.AddSharedStyleSheet("css/MaterialIcons.css", "Anaglyphohol.styles.css");
        builder.Services.AddRazorUI();
        builder.Services.AddSingleton<ContentOverlayService>();
        builder.Services.AddSingleton<PageStyleService>();
        builder.Services.AddSingleton<GpuService>();
        builder.Services.AddSingleton<DepthService>();
        builder.Services.AddSingleton<ThreeDRenderer>();
        builder.Services.AddSingleton<DimencoHeaderService>();
        builder.Services.AddSingleton<SharedConverterClient>();
        builder.Services.AddSingleton<TrackedMedia>();
        break;
    case ExtensionMode.ExtensionPage when IsConverterPage():
        // the shared converter's host: the offscreen document app/index.html?$=converter - no UI, one GPU pipeline for every tab
        builder.Services.AddSingleton<GpuService>();
        builder.Services.AddSingleton<DepthService>();
        builder.Services.AddSingleton<ThreeDRenderer>();
        builder.Services.AddSingleton<ConverterHostService>();
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

bool IsConverterPage() => new Uri(JS.Get<string>("location.href")).Query.Contains("$=" + SharedConverter.PageKey, StringComparison.Ordinal);
