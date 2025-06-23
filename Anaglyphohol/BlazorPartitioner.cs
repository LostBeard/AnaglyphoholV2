using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using System.Security.Cryptography;

namespace Anaglyphohol
{
    public enum BlazorPartitionType
    {
        /// <summary>
        /// The Blazor div will be added to 'body > div' instead of 'body > div > shadowRoot'
        /// </summary>
        None,
        /// <summary>
        /// The Blazor div will be added to 'body > div > shadowRoot:open'
        /// </summary>
        ShadowRootOpen,
        /// <summary>
        /// The Blazor div will be added to 'body > div > shadowRoot:closed'
        /// </summary>
        ShadowRootClosed,
    }
    public static class BlazorPartitioner
    {
        static Dictionary<string, Element> SelectorOverrides = new Dictionary<string, Element>();
        static BlazorJSRuntime JS => BlazorJSRuntime.JS;
        static Document? document = null;
        static Function? querySelector = null;
        public static bool RestoreAfterPickup { get; private set; } = false;
        public static bool Created { get; private set; } = false;
        public static bool Verbose { get; private set; } = false;
        public static bool UsingShadowRoot { get; private set; } = false;
        public static HTMLDivElement? BlazorDiv { get; private set; } = null;
        public static HTMLDivElement? BlazorApp { get; private set; } = null;
        public static HTMLDivElement? BlazorHeadOutlet { get; private set; } = null;
        public static ShadowRoot? ShadowRoot { get; private set; } = null;
        static FuncCallback<string, Element?>? cb = null;
        public static string BlazorDivAttribute { get; private set; }
        public static string BlazorAppDivAttribute { get; private set; }
        public static string BlazorHeadDivAttribute { get; private set; }
        public static void CreatePartition<TApp>(this WebAssemblyHostBuilder builder, BlazorPartitionType partitionType = BlazorPartitionType.None, bool restoreAfterPickup = true) where TApp : Microsoft.AspNetCore.Components.IComponent
        {
            if (Created) return;
            Created = true;
            if (!JS.IsWindow) return;
            RestoreAfterPickup = restoreAfterPickup;
            BlazorDivAttribute = $"_{RandomNumberGenerator.GetHexString(8)}";
            BlazorHeadDivAttribute = $"_{RandomNumberGenerator.GetHexString(8)}";
            BlazorAppDivAttribute = $"_{RandomNumberGenerator.GetHexString(8)}";
            var headSelector = $"[{BlazorHeadDivAttribute}]";
            var appSelector = $"[{BlazorAppDivAttribute}]";
            document = JS.Get<Document>("document");
            BlazorDiv = document.CreateElement<HTMLDivElement>("div");
            document.Body!.AppendChild(BlazorDiv);
            //BlazorDiv.SetAttribute("style", "position: fixed; top: 0; left: 0; min-width: 100vw; min-height: 100vh; z-index: 65536; pointer-events: none;");
            BlazorDiv.SetAttribute(BlazorDivAttribute, "");
            // base style
            using var baseStyle = document.CreateElement<HTMLDivElement>("div");
            baseStyle.SetAttribute("style", "display: none;");
            baseStyle.InnerHTML = $@"<style>
[{BlazorDivAttribute}] {{
    position: fixed; 
    top: 0; 
    left: 0; 
    min-width: 100vw; 
    min-height: 100vh; 
    z-index: 65536; 
    pointer-events: none;
}}
{appSelector} > * {{
    pointer-events: initial;
}}
</style>";
            // create Blazor HeadOutlet element div
            BlazorHeadOutlet = document.CreateElement<HTMLDivElement>("div");
            BlazorHeadOutlet.SetAttribute("style", "display: none;");
            // set the attribute that may be used to find the element
            BlazorHeadOutlet.SetAttribute(BlazorHeadDivAttribute, "");
            // create Blazor App element div
            BlazorApp = document.CreateElement<HTMLDivElement>("div");
            // set the attribute that may be used to find the element
            BlazorApp.SetAttribute(BlazorAppDivAttribute, "");
            //BlazorApp.SetAttribute("style", "pointer-events: initial;");
            // check if ShadowRoot is supported
            var attachShadowSupported = !BlazorDiv.JSRef!.IsUndefined("attachShadow");
            if (attachShadowSupported && partitionType != BlazorPartitionType.None)
            {
                SelectorOverrides.Add(headSelector, BlazorHeadOutlet);
                SelectorOverrides.Add(appSelector, BlazorApp);
                ShadowRoot = BlazorDiv.AttachShadow(new AttachShadowRootOptions { Mode = partitionType == BlazorPartitionType.ShadowRootOpen ? "open" : "closed" });
                ShadowRoot.AppendChild(baseStyle);
                ShadowRoot.AppendChild(BlazorApp);
                ShadowRoot.AppendChild(BlazorHeadOutlet);
                UsingShadowRoot = true;
                // because our elements are in a shadow root, we have to override the querySelector method so Blazor can find them
                querySelector = document.JSRef!.Get<Function>("querySelector");
                cb = new FuncCallback<string, Element?>(QuerySelectorOverride);
                // assign the custom querySelector method
                document.JSRef!.Set("querySelector", cb);
            }
            else
            {
                BlazorDiv.AppendChild(baseStyle);
                BlazorDiv.AppendChild(BlazorApp);
                BlazorDiv.AppendChild(BlazorHeadOutlet);
            }
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
