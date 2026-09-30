using Anaglyphohol.Services.Gpu;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// Draws the Philips / Dimenco 2D+Z header (<see cref="Philips2DZHeader"/>) at the SCREEN's top-left while the
    /// Dimenco 3D mode is in use, so a Dimenco display switches into 2D+Z. ONE canvas for the page: it lives on the body,
    /// and moves into the fullscreen element while one exists (anything outside it is not drawn in fullscreen).
    /// </summary>
    /// <remarks>
    /// DI singleton. The pre-SpawnJS build created a new instance - and a new canvas on the body - on every Dimenco
    /// frame; this one is created once and redraws only when the header changes.
    /// </remarks>
    public sealed class DimencoHeaderService : IDisposable
    {
        readonly SpawnJSRuntime JS;
        Document? _document;
        HTMLCanvasElement? _canvas;
        bool _enabled;

        public Philips2DZHeader Header { get; } = new Philips2DZHeader();
        public bool Enabled => _enabled;

        public DimencoHeaderService(SpawnJSRuntime js)
        {
            JS = js;
            Header.OnHeaderDirty += Redraw;
        }

        /// <summary>Shows or hides the header (created on first show).</summary>
        public void Show(bool enable)
        {
            if (_enabled == enable) return;
            _enabled = enable;
            if (enable && _canvas == null) Attach();
            if (_canvas != null)
            {
                using var style = _canvas.Style;
                style["display"] = enable ? "block" : "none";
                if (enable) Redraw();
            }
        }

        void Attach()
        {
            if (!JS.IsWindow) return;
            _document = JS.Get<Document>("document");
            _document.OnFullscreenChange += Document_OnFullscreenChange;
            _canvas = _document.CreateElement<HTMLCanvasElement>("canvas");
            _canvas.SetAttribute("class", "anaglyphohol-dimenco-header");
            _canvas.SetAttribute("style", "position: fixed; left: 0; top: 0; margin: 0; border: 0; padding: 0; z-index: 2147483647; pointer-events: none; display: block;");
            _canvas.Width = Header.Width;
            _canvas.Height = Header.Height;
            Reparent();
        }

        void Reparent()
        {
            if (_canvas == null || _document == null) return;
            using var fullscreenElement = _document.FullscreenElement;
            if (fullscreenElement != null)
            {
                fullscreenElement.AppendChild(_canvas);
            }
            else
            {
                using var body = _document.Body;
                body?.AppendChild(_canvas);
            }
        }

        void Document_OnFullscreenChange() => Reparent();

        void Redraw()
        {
            if (_canvas == null || !_enabled) return;
            using var ctx = _canvas.Get2DContext();
            // 512 x 1 x 4 = 2 KB of header pixels, built in C#: the one place pixels legitimately start in .NET.
            ctx.PutImageBytes(Header.HeaderData, Header.Width, Header.Height);
        }

        public void Dispose()
        {
            Header.OnHeaderDirty -= Redraw;
            if (_document != null)
            {
                _document.OnFullscreenChange -= Document_OnFullscreenChange;
                _document.Dispose();
                _document = null;
            }
            if (_canvas != null)
            {
                _canvas.Remove();
                _canvas.Dispose();
                _canvas = null;
            }
        }
    }
}
