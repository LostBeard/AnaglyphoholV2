using Anaglyphohol;
using Anaglyphohol.Background;
using Anaglyphohol.Layout;
using Anaglyphohol.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev;
using SpawnDev.AccountsShared.Services;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension.Services;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Toolbox;
using SpawnDev.BlazorJS.TransformersJS.DepthAnythingV2;
using SpawnDev.BlazorJS.WebWorkers;

try
{
    var builder = WebAssemblyHostBuilder.CreateDefault(args);
    builder.Logging.ClearProviders();
    builder.Services.AddBlazorJSRuntime(out var JS);
#if DEBUG && true
    JS.Log("Blazor loaded", JS.GlobalThisTypeName, JS.IsWindow, builder.HostEnvironment.BaseAddress);
#endif
    builder.Services.AddWebWorkerService();
    var extensionMode = BrowserExtensionService.GetExtensionMode();
    var extensionId = BrowserExtensionService.GetExtensionId();
    var isRunningAsExtension = !string.IsNullOrEmpty(extensionId);
    //JS.Log("Blazor loaded", JS.GlobalThisTypeName, builder.HostEnvironment.BaseAddress);
    //JS.Log("Extension", isRunningAsExtension, extensionMode.ToString(), extensionId);
#if DEBUG && true
    JS.Log("Extension", isRunningAsExtension, extensionMode.ToString(), extensionId);
#endif
    builder.Services.AddAuthorizationCore();
    builder.Services.AddSingleton<AppIdentityService>();
    builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<AppIdentityService>());
    builder.Services.AddSingleton<MediaDevicesService>();
    builder.Services.AddSingleton<BrowserExtensionService>();
    builder.Services.AddSingleton<TrackedMedia>();
    // depth estimation service
    builder.Services.AddDepthAnything((depthAnythingService, serviceProvider) =>
    {
        var browserExtensionService = serviceProvider.GetRequiredService<BrowserExtensionService>();
        // set the base URI for the depth estimation service to the Blazor base URI of the browser extension
        depthAnythingService.AppBaseUri = new Uri(browserExtensionService.BlazorBaseURI);
        depthAnythingService.UseBrowserCache = false; // browser cache would be redundant as this is an installed browser extension
                                                      //JS.Log($"depthAnythingService.AppBaseUri set: {depthAnythingService.AppBaseUri.ToString()}");
    });
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
    builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
    // Create the div for the App to render into using PartitionManager
    // if running in extension Content mode, partition as a FixedOverlay, otherwise create normal div for the app
    BlazorPartitioner.Verbose = true;
    builder.CreatePartition<App>(extensionMode != ExtensionMode.Content ? BlazorPartitionType.None : BlazorPartitionType.FixedOverlay, restoreAfterPickup: false);
    // build the host
    var host = builder.Build();
    // start context aware background services that inherit from IAsyncBackgroundService or IBackgroundService
    await host.StartBackgroundServices();
    // this calls a method in Javascript that will redispatch web browser extension events that have been held (if any)
    try
    {
        var isDefined = !JS.IsUndefined("finalizeAsyncStartup");
        if (isDefined)
        {
            JS.Log($"finalizeAsyncStartup running...");
            JS.CallVoid("finalizeAsyncStartup");
            JS.Log($"finalizeAsyncStartup done.");
        }
        else
        {
            JS.Log($"finalizeAsyncStartup not found.");
        }
    }
    catch (Exception ex)
    {
        JS.Log($"finalizeAsyncStartup failed:", ex.Message);
    }

    //#if DEBUG

    //var r = GetWebGLRenderer();
    //JS.Log("WebGL Renderer:", r);


    //string GetWebGLRenderer()
    //{
    //    var renderer = "";
    //    // get the video renderer
    //    using var canvas = new OffscreenCanvas(1, 1);
    //    using var gl = canvas.GetWebGLContext();
    //    using var ext = gl.GetExtension<JSObject>("WEBGL_debug_renderer_info");
    //    if (ext != null)
    //    {
    //        var unmaskedRendererWebGLFlag = ext.JSRef!.Get<int>("UNMASKED_RENDERER_WEBGL");
    //        renderer = gl.GetParameter<string>(unmaskedRendererWebGLFlag);
    //    }
    //    renderer ??= "";
    //    return renderer;
    //}
    //#endif

    // Start the app using the context aware BlazorJSRuntime
    await host.BlazorJSRunAsync();
}
catch (Exception ex)
{
    Console.WriteLine("Fatal error during startup:");
    Console.WriteLine(ex.ToString());
    throw;
}