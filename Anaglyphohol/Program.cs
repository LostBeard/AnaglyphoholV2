using Anaglyphohol;
using Anaglyphohol.Background;
using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Anaglyphohol.WebSiteExtensions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.AccountsShared.Services;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension.Services;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.Toolbox;
using SpawnDev.BlazorJS.TransformersJS.DepthAnythingV2;
using SpawnDev.BlazorJS.WebWorkers;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddBlazorJSRuntime(out var JS);
builder.Services.AddWebWorkerService();

var extensionMode = BrowserExtensionService.GetExtensionMode();
var extensionId = BrowserExtensionService.GetExtensionId();
var isRunningAsExtension = !string.IsNullOrEmpty(extensionId);
JS.Log("Blazor loaded", JS.GlobalThisTypeName, builder.HostEnvironment.BaseAddress);
JS.Log("Extension", isRunningAsExtension, extensionMode.ToString(), extensionId);

#if DEBUG && false
JS.Log("Blazor loaded", JS.GlobalThisTypeName, builder.HostEnvironment.BaseAddress);
JS.Log("Extension", isRunningAsExtension, extensionMode.ToString(), extensionId);
#endif


builder.Services.AddAuthorizationCore();
builder.Services.AddSingleton<AppIdentityService>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<AppIdentityService>());

builder.Services.AddSingleton<MediaDevicesService>();

builder.Services.AddSingleton<BrowserExtensionService>();

//builder.Services.AddSingleton<VideoTracker>();
//builder.Services.AddSingleton<ImageTracker>();
builder.Services.AddSingleton<TrackedMedia>();

// depth estimation service
builder.Services.AddDepthAnything((depthAnythingService, serviceProvider) =>
{
    var browserExtensionService = serviceProvider.GetRequiredService<BrowserExtensionService>();
    // set the base URI for the depth estimation service to the Blazor base URI of the browser extension
    depthAnythingService.AppBaseUri = new Uri(browserExtensionService.BlazorBaseURI);
    depthAnythingService.UseBrowserCache = false; // browser cache would be redundant as this is an installed browser extension
    JS.Log($"depthAnythingService.AppBaseUri set: {depthAnythingService.AppBaseUri.ToString()}");
});
//builder.Services.AddSingleton<AnaglyphImageMakerService>();

builder.Services.AddSingleton<ContentOverlayService>();

builder.Services.AddSingleton<BrowserWASMCrypto>();

builder.Services.AddSingleton<SyncStorageService>();

// may be running in a background page (Firefox) or a background service (Chrome)
// Register is set to none because the ServiceWorker is registered via the manifest and here we are telling WebWorkerService what class to handle ServiceWorkerEvents
// GlobalScope is set to all because is Firefox the background script runs in a window, and in Chrome the background script runs in a ServiceWorker
// ExtensionServiceWorker can essentially ignore

//if (extensionMode == ExtensionMode.Background)
//{
//    // only used for extension background
//    builder.Services.RegisterServiceWorker<BackgroundWorker>(GlobalScope.All, new ServiceWorkerConfig { Register = ServiceWorkerStartupRegistration.None });
//}
//else
//{
//    // when not in an extension BackgroundWorker is added as a regular singleton (it will auto-start though due being an IAsyncBackgroundService)
//    builder.Services.AddSingleton<BackgroundWorker>();
//}
if (extensionMode == ExtensionMode.None)
{
    builder.Services.RegisterServiceWorker<BackgroundService>(GlobalScope.All);
}
else
{
    builder.Services.RegisterServiceWorker<BackgroundService>(GlobalScope.All, new ServiceWorkerConfig { Register = ServiceWorkerStartupRegistration.None });
}

// browser extension service workers are registered via the manifest.json file so set Register = None
// registering ExtensionServiceWorker here will tell Blazor to create a singleton of ExtensionServiceWorker
// and start it when running in a ServiceWorkerGlobalScope so it can handle service worker events
switch (extensionMode)
{
    case ExtensionMode.Background:
        //// may be running in a background page (Firefox) or a background service (Chrome)
        //// Register is set to none because the ServiceWorker is registered via the manifest and here we are telling WebWorkerService what class to handle ServiceWorkerEvents
        //// GlobalScope is set to all because is Firefox the background script runs in a window, and in Chrome the background script runs in a ServiceWorker
        //// ExtensionServiceWorker can essentially ignore
        //builder.Services.RegisterServiceWorker<BackgroundWorker>(GlobalScope.All, new ServiceWorkerConfig { Register = ServiceWorkerStartupRegistration.None });
        break;
    case ExtensionMode.Content:
        builder.Services.AddSingleton<ContentBridgeService>();
        break;
}
builder.Services.AddSingleton<AppService>();

// use the new id set in index.html. originally #app which could have a conflict with the page it is loaded into
builder.RootComponents.Add<App>("#spawndev-extension");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var host = builder.Build();
await host.StartBackgroundServices();

//#region DEBUG
//// Fix 
//// https://register.ubisoft.com/skullandbones-trial/en-US?platform=PC
//// Program.cs failed: The URI 'https://register.ubisoft.com/skullandbones-trial/?platform=PC' is not contained by the base URI 'https://static-live.ubisoft.com//sitegen/PROD/FO/register/skullandbones-trial/'.    at Microsoft.AspNetCore.Components.NavigationManager.Validate(Uri , String )
JS.Set("_deep", Callback.Create<string, List<Element>>((selector) =>
{
    using var document = JS.GetDocument();
    var els = document!.DeepQuerySelectorAll(selector, ShadowRootQueryMode.Wide);
    return els;
}));
//#endregion


try
{
    var isDefined = !JS.IsUndefined("finalizeAsyncStartup");
    if (isDefined)
    {
        JS.Log($"finalizeAsyncStartup running...");
        JS.CallVoid("finalizeAsyncStartup");
        JS.Log($"finalizeAsyncStartup done.");
    }
}
catch (Exception ex)
{
    JS.Log($"finalizeAsyncStartup failed:", ex.Message);
}



await host.BlazorJSRunAsync();