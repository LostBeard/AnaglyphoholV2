using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.TransformersJS;
using SpawnDev.BlazorJS.TransformersJS.DepthAnythingV2;
using System.Diagnostics;

namespace Anaglyphohol.Services
{
    public class TrackedMediaElementFrameData
    {
        // source frame
        //public long _frameIndex = 0;
        public double frameTime = 0;
        public int width = 0;
        public int height = 0;
        public OffscreenCanvas? _frameRgb;
        // generated frame depth map
        public float frameDepthScale = 1.0f;
        public int depthWidth = 0;
        public int depthHeight = 0;
        public OffscreenCanvas? _frameDepth;
        // generated 3d frame
        public int frameProfile = 0;
        public float frameLevel3D = 1.0f;
        public float frameFocus3D = 0.5f;
        public OffscreenCanvas? frameFinal;
    }
    public class TrackedMediaElement : IDisposable
    {
        public int AnaglyphProfileId { get; private set; }
        public float Level3D { get; private set; }
        public float Focus3D { get; private set; }
        public float DepthScale { get; private set; } = 0.8f;
        public const string ElementUIDKey = "__extensionElementId";
        public const string DoNotTrackElementKey = "__doNotTrackElement";
        public static string? GetElementUID(HTMLElement imageElement, bool allowCreate = false)
        {

            var ret = imageElement.JSRef!.Get<string?>(ElementUIDKey);
            if (allowCreate && string.IsNullOrEmpty(ret))
            {
                ret = Guid.NewGuid().ToString();
                SetElementUID(imageElement, ret);
            }
            return ret;
        }
        public static bool? GetElementDoNotTrack(HTMLElement imageElement) => imageElement.JSRef!.Get<bool?>(DoNotTrackElementKey);
        public static void SetElementDoNotTrack(HTMLElement imageElement, bool value) => imageElement.JSRef!.Set(DoNotTrackElementKey, value);
        public static void RemoveElementDoNotTrack(HTMLElement imageElement) => imageElement.JSRef!.Delete(DoNotTrackElementKey);
        public static void SetElementUID(HTMLElement imageElement, string videoId) => imageElement.JSRef!.Set(ElementUIDKey, videoId);
        public string UID { get; private set; }
        BlazorJSRuntime JS;
        public HTMLElement Element { get; private set; }
        public HTMLImageElement? ImageElement { get; private set; }
        public HTMLVideoElement? VideoElement { get; private set; }
        CSSStyleDeclaration? OverlayStyle { get; set; }
        public int MinWidth { get; set; } = 100;
        public int MinHeight { get; set; } = 100;
        /// <summary>
        /// Returns true if the image loading is complete and it meets the minimum size requirements
        /// </summary>
        public bool MeetsMinSizeRequirements => FrameWidth >= MinWidth && FrameHeight >= MinHeight;
        public int FrameWidth => VideoElement?.VideoWidth ?? ImageElement?.NaturalWidth ?? 0;
        public int FrameHeight => VideoElement?.VideoHeight ?? ImageElement?.NaturalHeight ?? 0;
        public bool IsImageLoaded => ImageElement?.Complete == true && ImageElement.Width >= 0 && ImageElement.Height >= 0;
        public bool IsVideoLoaded => VideoElement != null && VideoElement.ReadyState >= 2 && VideoElement.VideoWidth >= 0 && VideoElement.VideoHeight >= 0;
        public bool IsHTMLDivElement => TagName == "DIV";
        public bool IsHTMLImageElement => TagName == "IMG";
        public bool IsHTMLVideoElement => TagName == "VIDEO";
        public string TagName { get; }
        Window window;
        Document document;
        public const string StateAttributeName = "anaglyphohol-state";
        string State = "";
        HTMLCanvasElement? OverlayCanvasElement { get; set; }
        HTMLElement parent;
        //long currentFrameIndexLastRedraw = 0;
        //public long CurrentFrameIndex { get; private set; } = 0;
        double currentTimeLastRedraw = -1;
        double currentTimeLastCheck = -1;
        static bool? supportsWindowRequestAnimationFrame = null;
        static bool? supportsRequestVideoFrameCallback = null;
        long ImageIndexCount = 0;
        public TrackedMediaElementFrameData? LastDraw { get; set; } = null;
        public TrackedMediaElement(string videoId, HTMLElement htmlElement, BlazorJSRuntime js)
        {
            UID = videoId;
            JS = js;
            TagName = htmlElement.TagName.ToUpperInvariant();
            window = JS.Get<Window>("window")!;
            document = JS.Get<Document>("document")!;
            parent = htmlElement.ParentElementAs<HTMLElement>()!;
            switch (TagName)
            {
                case "IMG":
                    ImageElement = htmlElement.JSRefAs<HTMLImageElement>();
                    Element = ImageElement;
                    ImageElement.OnLoad += ImageElement_OnLoad;
                    if (IsImageLoaded)
                    {
                        ImageIndexCount++;
                    }
                    break;
                case "VIDEO":
                    JS.Log("video found", UID);
                    VideoElement = htmlElement.JSRefAs<HTMLVideoElement>();
                    Element = VideoElement;
                    supportsWindowRequestAnimationFrame ??= !window.JSRef!.IsUndefined("requestAnimationFrame");
                    supportsRequestVideoFrameCallback ??= VideoElement.SupportsRequestVideoFrameCallback;
                    VideoElement.OnLoadedMetadata += VideoElement_OnLoadedMetadata;
                    VideoElement.OnLoadedData += VideoElement_OnLoadedData;
                    break;
                default:
                    // if it's not an image or video, then it must be a div or something else
                    Element = htmlElement;
                    break;
            }
            Element.OnMouseOver += Element_OnMouseOver;
        }
        void Element_OnMouseOver()
        {
            UpdateFrame(true);
        }
        public bool awaitingRedraw { get; set; } = false;
        public double RedrawTime { get; set; }
        /// <summary>
        /// TrackedMedia will call this method when is this elements turn to use the depth estimation service
        /// </summary>
        /// <returns></returns>
        public async Task Redraw(TrackedMedia trackedMedia)
        {
            // called by trackedmedia when it is this element's turn to redraw.
            if (IsDisposed || !awaitingRedraw) return;
            awaitingRedraw = false;
            if (OverlayCanvasElement == null) return;
            try
            {
                var anaglyphRenderer = trackedMedia.AnaglyphRenderer;
                if (anaglyphRenderer == null) return;
                var depthAnythingService = trackedMedia.DepthAnythingService;

                AnaglyphProfileId = trackedMedia.AnaglyphProfile;
                Level3D = trackedMedia.Level3D;
                Focus3D = trackedMedia.Focus3D;
                if (ImageElement != null && IsImageLoaded)
                {
                    DepthScale = trackedMedia.DepthScale;
                    SetState("active");
                    currentTimeLastRedraw = ImageIndexCount;
                    var rgbWidth = FrameWidth;
                    var rgbHeight = FrameHeight;
                    // get an untainted copy of image (if possible)
                    var usableImage = await ImageElement.GetUsableImage();
                    if (usableImage != null)
                    {
                        using var depthResult = await depthAnythingService.GenerateDepth(usableImage);
                        using var depth = depthResult!.Depth;
                        using var depthmapData = depth.Data;
                        anaglyphRenderer!.Level3D = Level3D;
                        anaglyphRenderer.Focus3D = Focus3D;
                        anaglyphRenderer.ProfileIndex = AnaglyphProfileId;
                        anaglyphRenderer!.SetInput(usableImage);
                        anaglyphRenderer.SetDepth(depth.Width, depth.Height, depthmapData);
                        anaglyphRenderer.Render();
                        using var ctx = OverlayCanvasElement.Get2DContext();
                        ctx.DrawImage(anaglyphRenderer.OffscreenCanvas!, 0, 0);
                        SetState("anaglyph");
                    }
                    else
                    {
                        // failed
                        SetState("failed");
                    }
                }
                else if (VideoElement != null && IsVideoLoaded)
                {
                    currentTimeLastRedraw = VideoElement.CurrentTime;
                    // get a full size copy of the current 2D frame
                    var rgbWidth = FrameWidth;
                    var rgbHeight = FrameHeight;
                    using var rgbCanvas = new OffscreenCanvas(rgbWidth, rgbHeight);
                    using var rgbCtx = rgbCanvas.Get2DContext();
                    rgbCtx.DrawImage(VideoElement, 0, 0, rgbWidth, rgbHeight);
                    if (DepthScale < 1.0d)
                    {
                        // will generate using a scaled source
                        // get scaled rgb for "faster" depth generation
                        var depthWidth = (int)Math.Round(DepthScale * rgbWidth);
                        var depthHeight = (int)Math.Round(DepthScale * rgbHeight);
                        using var rgbScaledCanvas = new OffscreenCanvas(depthWidth, depthHeight);
                        using var rgbScaledCanvasCtx = rgbScaledCanvas.Get2DContext();
                        rgbScaledCanvasCtx.DrawImage(rgbCanvas, 0, 0, depthWidth, depthHeight);
                        // generate depthmap
                        using var depthResult = await depthAnythingService.GenerateDepth(rgbScaledCanvas);
                        using var depth = depthResult!.Depth;
                        using var depthmapData = depth.Data;
                        anaglyphRenderer.SetDepth(depth.Width, depth.Height, depthmapData);
                    }
                    else
                    {
                        // will generate at full source resolution
                        // generate depthmap
                        using var depthResult = await depthAnythingService.GenerateDepth(rgbCanvas);
                        using var depth = depthResult!.Depth;
                        using var depthmapData = depth.Data;
                        anaglyphRenderer.SetDepth(depth.Width, depth.Height, depthmapData);
                    }
                    anaglyphRenderer.SetInput(rgbCanvas);
                    anaglyphRenderer.Level3D = Level3D;
                    anaglyphRenderer.Focus3D = Focus3D;
                    anaglyphRenderer.ProfileIndex = AnaglyphProfileId;
                    anaglyphRenderer.Render();
                    using var ctx = OverlayCanvasElement.Get2DContext();
                    ctx.DrawImage(anaglyphRenderer.OffscreenCanvas!, 0, 0);
                    var fontSize = 16;
                    var x = 50;
                    var y = 50;
                    var boxBorderSize = 2;
                    var txt = $"FPS: {Math.Round(FPS)} Depth Scale: {Math.Round(DepthScale * 100f)}%";
                    var boxColor = "#ffffff80";
                    var textColor = "#000";
                    //
                    ctx.Font = $"{fontSize}px serif";
                    ctx.FillStyle = boxColor;
                    var textSize = ctx.MeasureText(txt);
                    var textWidth = textSize.Width;
                    ctx.FillRect(x, y, (int)Math.Round(textWidth + boxBorderSize * 2), fontSize + boxBorderSize * 2);
                    ctx.FillStyle = textColor;
                    ctx.FillText(txt, x + boxBorderSize, y + boxBorderSize + fontSize);
                }
                else
                {
                    SetState("");
                }
            }
            catch (Exception ex)
            {
                JS.Log($"UpdateFrame failed: {ex.Message}");
                SetState("failed");
            }
            framesThisSecond++;
            var elapsedSeconds = waitTime.Elapsed.TotalSeconds;
            if (elapsedSeconds >= 1d)
            {
                waitTime.Restart();
                var fps = (double)framesThisSecond / elapsedSeconds;
                var pad = 2d;
                FPS = (FPS * pad + fps) / (pad + 1);
                framesThisSecond = 0;
                RedrawTime = 1000d / FPS; 
                if (IsHTMLVideoElement)
                {
                    // decrease depth scale if the fps is below a certain level
                    // and increase the depth scale if the fps is above a certain level and the depth scale is < max
                    if (FPS < FPSDecreaseDepthScaleTrigger && DepthScale > MinDepthScale)
                    {
                        // lower depth scale
                        DepthScale = Math.Max(DepthScale - autoAdjustDepthScaleAmount, MinDepthScale);
                    }
                    else if (FPS > FPSIncreaseDepthScaleTrigger && DepthScale < 1.0f)
                    {
                        // increase depth scale
                        DepthScale = Math.Min(1f, DepthScale + autoAdjustDepthScaleAmount);
                    }
                }
            }
            if (IsHTMLVideoElement)
            {
                // if it is a video element request redraw after every draw
                if (OverlayVisible)
                {
                    RequestVideoFrameCallback(UpdateFrame);
                }
            }
        }
        int framesThisSecond = 0;
        float autoAdjustDepthScaleAmount = 0.02f;
        public double FPSDecreaseDepthScaleTrigger { get; set; } = 15;
        public double FPSIncreaseDepthScaleTrigger { get; set; } = 20;
        public float MinDepthScale { get; set; } = 0.10f;
        public double FPS { get; private set; }
        Stopwatch waitTime = new Stopwatch();
        public void UpdateFrame()
        {
            UpdateFrame(false);
        }
        /// <summary>
        /// Checks if anything has changed since the last draw
        /// Calling this notifies TrackedMedia that a redraw is needed.<br/>
        /// The request is queued. Video elements are done asap with images done intermittently.
        /// </summary>
        public void UpdateFrame(bool urgent)
        {
            if (IsDisposed) return;
            if (!urgent && awaitingRedraw) return;
            if (!MeetsMinSizeRequirements)
            {
                return;
            }
            var redrawNeeded = true;
            if (LastDraw != null)
            {

            }
            if (redrawNeeded || urgent)
            {
                UpdateCanvasOverlayPositionAndSize(true, true);    // true with updateExisting == true if there are issues with size and placement
                if (OverlayCanvasElement == null) return;
                awaitingRedraw = true;
                RequestRedraw?.Invoke(this, urgent);
            }
        }
        /// <summary>
        /// True if a new source frame has been drawn since 
        /// </summary>
        public TrackedMediaElement(HTMLElement imageElement, BlazorJSRuntime js) : this(GetElementUID(imageElement, true)!, imageElement, js) { }
        public void SetState(string state)
        {
            if (State == state) return;
            State = state;
            Element.SetAttribute(StateAttributeName, state);
        }
        bool UpdateCanvasOverlayPositionAndSize(bool allowCreate = false, bool updateExisting = false)
        {
            var created = false;
            if (OverlayCanvasElement == null)
            {
                // check if the overlay already exists
                OverlayCanvasElement = Element.JSRef!.Get<HTMLCanvasElement>("overlayCanvasElement");
                if (OverlayCanvasElement == null)
                {
                    if (!allowCreate || MeetsMinSizeRequirements != true) return false;
                    // create it
                    OverlayCanvasElement = document!.CreateElement<HTMLCanvasElement>("canvas");
                    parent.Style["position"] = "relative";
                    if (_DebugShow)
                    {
                        OverlayCanvasElement.SetAttribute("style", "position: absolute; pointer-events: none; background-color: red;");
                    }
                    else
                    {
                        OverlayCanvasElement.SetAttribute("style", "position: absolute; pointer-events: none;");
                    }
                    OverlayCanvasElement.SetAttribute("class", "custom-media-overlay-canvas");
                    Element.JSRef!.Set("overlayCanvasElement", OverlayCanvasElement);
                    Element.After(OverlayCanvasElement);
                    created = true;
                }
            }
            OverlayStyle ??= OverlayCanvasElement.Style;
            if (!created && !updateExisting)
            {
                return true;
            }
            using var vRect = Element.GetBoundingClientRect();
            using var vStyle = window.GetComputedStyle(Element);
            int frameWidth = FrameWidth;
            int frameHeight = FrameHeight;
            var width = (int)Math.Round(vRect.Width) + "px";
            var height = (int)Math.Round(vRect.Height) + "px";
            // only accurate way found to get the offset is to use the bounding rect of the element and its parent
            using var parentRect = parent.GetBoundingClientRect();
            var offsetTop = vRect.Top - parentRect.Top;
            var offsetLeft = vRect.Left - parentRect.Left;
            var top = offsetTop + "px";
            var left = offsetLeft + "px";
            OverlayStyle["aspectRatio"] = $"{vRect.Width} / {vRect.Height}";
            var zIndex = vStyle["zIndex"];
            zIndex = !float.TryParse(zIndex, out var zIndexFloat) ? zIndex : (zIndexFloat + 1).ToString();
            var display = _OverlayVisible ? "" : "none";
            var vObjectFit = vStyle["objectFit"];
            vObjectFit = string.IsNullOrEmpty(vObjectFit) ? "contain" : vObjectFit;
            if (OverlayStyle["objectFit"] != vObjectFit) OverlayStyle["objectFit"] = vObjectFit;
            if (OverlayStyle["display"] != display) OverlayStyle["display"] = display;
            if (OverlayStyle["top"] != top) OverlayStyle["top"] = top;
            if (OverlayStyle["left"] != left) OverlayStyle["left"] = left;
            if (OverlayStyle["zIndex"] != zIndex) OverlayStyle["zIndex"] = zIndex;
            if (OverlayStyle["width"] != width) OverlayStyle["width"] = width;
            if (OverlayStyle["height"] != height) OverlayStyle["height"] = height;
            if (OverlayCanvasElement.Width != frameWidth) OverlayCanvasElement.Width = frameWidth;
            if (OverlayCanvasElement.Height != frameHeight) OverlayCanvasElement.Height = frameHeight;
            return true;
        }
        bool _DebugShow = false;
        public event Action<TrackedMediaElement, bool> RequestRedraw = default!;
        void RequestVideoFrameCallback(Action callback)
        {
            if (IsDisposed) return;
            if (supportsRequestVideoFrameCallback == true && VideoElement != null)
            {
                VideoElement.RequestVideoFrameCallback(callback);
            }
            else if (supportsWindowRequestAnimationFrame == true && window != null)
            {
                window.RequestAnimationFrame(callback);
            }
            else
            {
                JS.SetTimeout(callback, 1000 / 30); // fallback to 30 FPS if requestVideoFrameCallback is not supported
            }
        }
        void DetachImageElementEvents()
        {
            if (ImageElement != null)
            {
                ImageElement.OnLoad -= ImageElement_OnLoad;
            }
            else if (VideoElement != null)
            {
                VideoElement.OnLoadedMetadata -= VideoElement_OnLoadedMetadata;
                VideoElement.OnLoadedData -= VideoElement_OnLoadedData;
            }
            //Element.OnClick -= ImageElement_OnClick;
            //Element.OnMouseEnter -= ImageElement_OnMouseEnter;
            //Element.OnMouseLeave -= ImageElement_OnMouseLeave;
            //Element.OnMouseMove -= ImageElement_OnMouseMove;
        }
        public bool IsDisposed { get; private set; } = false;
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (Element != null)
            {
                // detach events 
                DetachImageElementEvents();
                Element.Dispose();
            }
            if (OverlayStyle != null)
            {
                OverlayStyle.Dispose();
                OverlayStyle = null;
            }
        }
        bool _OverlayVisible = false;
        public bool OverlayVisible
        {
            get => _OverlayVisible;
            set
            {
                if (_OverlayVisible == value) return;
                _OverlayVisible = value;
                waitTime.Restart();
                if (OverlayStyle != null)
                {
                    OverlayStyle["display"] = _OverlayVisible ? "" : "none";
                }
                if (OverlayCanvasElement != null)
                {
                    if (_OverlayVisible)
                    {
                        UpdateFrame();
                    }
                    else
                    {
                        using var ctx = OverlayCanvasElement.Get2DContext();
                        ctx.ClearRect(0, 0, OverlayCanvasElement.Width, OverlayCanvasElement.Height);
                    }
                }
            }
        }
        //public event Action<TrackedMediaElement> OnImageLoaded = default!;
        void ImageElement_OnLoad(Event e)
        {
            ImageIndexCount++;
            UpdateFrame();
            //OnImageLoaded?.Invoke(this);
        }
        void VideoElement_OnLoadedMetadata()
        {
            JS.Log("VideoElement_OnLoadedMetadata");
        }
        void VideoElement_OnLoadedData()
        {
            JS.Log("VideoElement_OnLoadedData");

            if (IsHTMLVideoElement)
            {
                JS.Log("IsHTMLVideoElement MeetsMinSizeRequirements != true");
            }
            UpdateFrame();
            //OnImageLoaded?.Invoke(this);
        }
    }
}
