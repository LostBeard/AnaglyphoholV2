namespace Anaglyphohol.Services.Converter
{
    /// <summary>
    /// The SHARED converter (opt-in, Chrome): ONE depth + 3D pipeline in the extension's offscreen document serves every tab,
    /// instead of every page loading its own models and kernels (TJ 2026-10-05: "worth doing even if only to see how it
    /// compares and have it optional"). Pages send frames as ImageBitmaps over a MessagePort
    /// (SpawnDev.SpawnJS.BrowserExtension ExtensionPortService) and get the 3D frame back as an ImageBitmap.
    /// <para>
    /// Route: content script --port + token--> background service worker (<see cref="Background.ConverterRelayService"/>)
    /// --port--> offscreen document app/index.html?$=converter (<see cref="ConverterHostService"/>). After that the page and
    /// the offscreen document talk directly. MEASURED 2026-10-05 (probe extension): a 1920x1080 ImageBitmap there and back
    /// in 0.5 ms median; the port setup ~65 ms once.
    /// </para>
    /// Messages are plain JS objects (built and read member by member, the bitmap transferred):
    /// <list type="bullet">
    /// <item>page -> host: {type:"render", id, bmp, w, h, mode, level, focus, video, videoLevel, videoKey, primary}</item>
    /// <item>page -> host: {type:"reset" | "release", videoKey} - a video seeked / went away</item>
    /// <item>host -> page: {type:"rendered", id, bmp, stats (FrameStats JSON)} or {type:"failed", id, error}</item>
    /// </list>
    /// </summary>
    public static class SharedConverter
    {
        /// <summary>ExtensionPortService connection name.</summary>
        public const string PortName = "anaglyphohol-converter";
        /// <summary>The offscreen document (relative to the extension root).</summary>
        public const string DocumentPath = "app/index.html?$=converter";
        /// <summary>The ?$= value that makes an extension page the converter host.</summary>
        public const string PageKey = "converter";
        /// <summary>storage.local key: true = this browser uses the shared converter (read once per page at start).</summary>
        public const string SettingKey = "sharedConverter";

        // service worker <-> offscreen document
        public const string MsgHostReady = "anaglyphohol-converter-ready";
        public const string MsgPort = "anaglyphohol-converter-port";
        /// <summary>host -> worker: frames keep failing here (a lost GPU device): close this document, the next page gets a new one.</summary>
        public const string MsgHostBroken = "anaglyphohol-converter-broken";
    }
}
