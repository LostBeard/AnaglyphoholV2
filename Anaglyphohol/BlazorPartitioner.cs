using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using System.Security.Cryptography;

namespace Anaglyphohol
{
    public enum BlazorPartitionType
    {
        None,
        ShadowRootOpen,
        ShadowRootClosed,
    }
    public static class BlazorPartitioner
    {
        static string tempId = RandomNumberGenerator.GetHexString(16);
        static Dictionary<string, Element> SelectorOverrides = new Dictionary<string, Element>();
        static BlazorJSRuntime JS => BlazorJSRuntime.JS;
        static Document? document = null;
        static Function? querySelector = null;
        static bool RestoreAfterPickup = false;
        static bool Created = false;
        public static bool Verbose = false;
        static bool UsingShadowRoot = false;
        static HTMLDivElement? BlazorApp = null;
        static HTMLDivElement? BlazorHeadOutlet = null;
        public static void CreatePartition<TApp>(this WebAssemblyHostBuilder builder, BlazorPartitionType partitionType = BlazorPartitionType.None, bool restoreAfterPickup = true) where TApp : Microsoft.AspNetCore.Components.IComponent
        {
            if (Created) return;
            Created = true;
            if (!JS.IsWindow) return;
            // put temporary document.querySelector override in place so we can capture Blazor's search for the element we specify for the App and HeadOutlet classes.
            var headSelector = $"#blazor-head-{tempId}";
            RestoreAfterPickup = restoreAfterPickup;
            var appSelector = $"#blazor-app-{tempId}";
            document = JS.Get<Document>("document");
            using var blazorDiv = document.CreateElement<HTMLDivElement>("div");
            document.Body!.AppendChild(blazorDiv);
            blazorDiv.SetAttribute("style", "position: fixed; top: 0; left: 0; min-width: 100vw; min-height: 100vh; z-index: 65536; pointer-events: none;");
            // create Blazor HeadOutlet element div
            BlazorHeadOutlet = document.CreateElement<HTMLDivElement>("div");
            BlazorHeadOutlet.SetAttribute("style", "display: none;");
            SelectorOverrides.Add(headSelector, BlazorHeadOutlet);
            // create Blazor App element div
            BlazorApp = document.CreateElement<HTMLDivElement>("div");
            BlazorApp.SetAttribute("style", "pointer-events: initial;");
            SelectorOverrides.Add(appSelector, BlazorApp);
            // check if ShadowRoot is supported
            var attachShadowSupported = !blazorDiv.JSRef!.IsUndefined("attachShadow");
            if (attachShadowSupported && partitionType != BlazorPartitionType.None)
            {
                using var blazorShadowRoot = blazorDiv.AttachShadow(new AttachShadowRootOptions { Mode = partitionType == BlazorPartitionType.ShadowRootOpen ? "open" : "closed" });
                blazorShadowRoot.AppendChild(BlazorApp);
                blazorShadowRoot.AppendChild(BlazorHeadOutlet);
                UsingShadowRoot = true;
            }
            else
            {
                blazorDiv.AppendChild(BlazorApp);
                blazorDiv.AppendChild(BlazorHeadOutlet);
            }
            // override the querySelector method
            querySelector = document.JSRef!.Get<Function>("querySelector");
            var cb = new FuncCallback<string, Element?>(QuerySelectorOverride);
            // assign the custom querySelector method
            document.JSRef!.Set("querySelector", cb);
            // add the components with the selectors
            builder.RootComponents.Add<TApp>(appSelector);
            builder.RootComponents.Add<HeadOutlet>(headSelector);
        }
        static Element? QuerySelectorOverride(string selector)
        {
            if (Verbose)
            {
                JS.Log("QuerySelectorOverride", selector);
            }
            if (!SelectorOverrides.TryGetValue(selector, out var el))
            {
                el = querySelector!.Apply<Element?>(document, new object[] { selector });
                if (el == null && UsingShadowRoot)
                {
                    // could be Blazor trying to select something... try the shadowRoot
                    el = BlazorApp?.QuerySelector(selector);
                    if (Verbose)
                    {
                        if (el != null)
                        {
                            JS.Log("Found inside of BlazorApp", selector);
                        }
                    }
                }
            }
            else
            {
                if (RestoreAfterPickup)
                {
                    SelectorOverrides.Remove(selector);
                    if (SelectorOverrides.Count == 0)
                    {
                        // restore original querySelector
                        document!.JSRef!.Set("querySelector", querySelector);
                    }
                }
            }
            return el;
        }
    }
}
