using Anaglyphohol.Services.Gpu;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using OracleHeader = SpawnDev.BlazorJS.MultiView.Dimenco.Philips2DZHeader;
using OracleFormats = SpawnDev.BlazorJS.MultiView.Dimenco.HeaderDataFormats;

// Anaglyphohol 3D kernel + Philips header harness. Exit code = number of failed checks.
// The kernel reference below is a LITERAL port of TJ's GLSL from the 3.x store build (uv space, float math, a LINEAR
// clamp-to-edge sampler) - deliberately
// NOT the pixel-space formulation ThreeDKernels uses, so an indexing / unit mistake in the port shows up as a mismatch.
int failed = 0, passed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"PASS  {name}"); }
    else { failed++; Console.WriteLine($"FAIL  {name}  {detail}"); }
}

bool useGpu = args.Contains("-gpu");
bool dbg = args.Contains("-dbg");
int dbgLeft = 12;
using var context = Context.Create().AllAccelerators().EnableAlgorithms().ToContext();
var devices = context.Devices.Where(d => d.AcceleratorType == AcceleratorType.CPU || (useGpu && d.AcceleratorType is AcceleratorType.Cuda or AcceleratorType.OpenCL)).ToList();
foreach (var device in devices)
{
    using var acc = device.CreateAccelerator(context);
    Console.WriteLine($"== {acc.AcceleratorType}: {acc.Name}");
    RunKernelChecks(acc);
    RunUpsampleChecks(acc);
}
RunHeaderChecks();
Console.WriteLine($"RESULTS: passed {passed}, failed {failed}");
return failed;

void RunKernelChecks(Accelerator acc)
{
    var anaglyph = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>(ThreeDKernels.AnaglyphKernel);
    var twoDZ = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ThreeDKernels.TwoDZKernel);
    var twoDZScreen = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float>(ThreeDKernels.TwoDZScreenKernel);
    using var profiles = acc.Allocate1D(AnaglyphProfiles.Data);
    const float SepMaxPx = 0.02f * 900f;   // ThreeDRenderer.SepMaxPx (3.x: sepMax 0.02 x 900 / outWidth, in uv)

    foreach (var (w, h) in new[] { (97, 23), (640, 9), (33, 5) })
    {
        var rng = new Random(w * 31 + h);
        var rgba = new int[w * h];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = rng.Next(0, 0x1000000) | unchecked((int)0xFF000000);
        foreach (var (direct, noisy) in new[] { (false, false), (true, false), (false, true) })
        {
            // smooth ramp + a near "object" block, positive (DAv3 depth must be > 0); NOISY adds per-pixel random depth
            // (hostile: occlusion ties, holes and fills everywhere, not only at the block's two edges)
            var raw = new float[w * h];
            var nrng = new Random(w * 7 + h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = 1f + 3f * x / w + 0.5f * MathF.Sin(y * 0.7f);
                    if (x > w / 3 && x < w / 2) v = direct ? 0.6f : 5f;   // near block: small depth / large disparity
                    if (noisy) v += 2f * (float)nrng.NextDouble();
                    raw[y * w + x] = v;
                }
            float min = raw.Min(), max = raw.Max();
            // min/max reach the kernels as a 2-float DEVICE view (the depth pipeline's GPU reduction writes it in the app)
            using var minMaxBuf = acc.Allocate1D(new[] { min, max });
            using var rgbaBuf = acc.Allocate1D(rgba);
            using var rawBuf = acc.Allocate1D(raw);
            using var outBuf = acc.Allocate1D<int>(w * h);

            // includes 3.x's default (0.5, 0.66), a sub-pixel separation (level 0.05 = 0.9 px: source pixel) and the extremes
            foreach (var (level, conv, profile) in new[] { (0.5f, 0.66f, 0), (1f, 0.5f, 0), (0.8f, 0.2f, 1), (0.35f, 0.9f, 0), (0.05f, 0.5f, 0), (1f, 0f, 0), (1f, 1f, 1) })
            {
                float sepPx = SepMaxPx * level;
                anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, sepPx, conv, profile * ThreeDKernels.ProfileStride);
                acc.Synchronize();
                var got = outBuf.GetAsArray1D();
                int bad = 0, worst = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int exp = RefAnaglyph(x, y, w, rgba, raw, direct, min, max, level, conv, profile);
                        int diff = MaxChannelDiff(exp, got[y * w + x]);
                        if (diff > 1 && dbg && dbgLeft-- > 0)
                        {
                            ThreeDKernels.ScaleBias(min, max, direct ? 1 : 0, out float da, out float db);
                            int ke = ThreeDKernels.RightEyeSourceEdge(x, y * w, w, rawBuf.View, direct ? 1 : 0, da, db, sepPx, conv);
                            Console.WriteLine($"  DBG {w}x{h} lvl={level} f={conv} x={x} y={y} kernelEdge={ke} ref={RefDebug} exp={exp:X8} got={got[y * w + x]:X8}");
                        }
                        worst = Math.Max(worst, diff);
                        if (diff > 1) bad++;
                    }
                // uv-space vs pixel-space can round a hit test that sits exactly on the 1 px boundary differently;
                // anything beyond a handful of pixels is a real port error.
                double frac = (double)bad / (w * h);
                Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct}{(noisy ? " noisy" : "")} level={level} conv={conv} profile={profile}", frac <= 0.002,
                    $"{bad}/{w * h} pixels off by >1 (worst {worst})");
            }

            // NEGATIVE CONTROL: the 3D effect is real - level 1 must differ from level 0 (no parallax)
            anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, SepMaxPx, 0.5f, 0);
            acc.Synchronize();
            var with3D = outBuf.GetAsArray1D();
            anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, 0f, 0.5f, 0);
            acc.Synchronize();
            var flat = outBuf.GetAsArray1D();
            int changed = with3D.Zip(flat).Count(p => p.First != p.Second);
            Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct}{(noisy ? " noisy" : "")} parallax changes the image", w < 64 || changed > w * h / 50, $"only {changed} pixels changed");
            // zero separation = both eyes see the source: Dubois(src, src) exactly
            int flatBad = 0;
            for (int i = 0; i < w * h; i++) if (MaxChannelDiff(Mix(rgba[i], rgba[i], 0), flat[i]) > 1) flatBad++;
            Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct}{(noisy ? " noisy" : "")} zero separation = Dubois(src, src)", flatBad == 0, $"{flatBad} pixels differ");

            twoDZ(new Index2D(w, h), rgbaBuf.View, rawBuf.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0);
            acc.Synchronize();
            var dz = outBuf.GetAsArray1D();
            int dzBad = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (MaxChannelDiff(Ref2DZ(x, y, w, rgba, raw, direct, min, max), dz[y * w + x]) > 1) dzBad++;
            Check($"{acc.AcceleratorType} 2D+Z {w}x{h} direct={direct}{(noisy ? " noisy" : "")}", dzBad == 0, $"{dzBad} pixels differ");

            // NEGATIVE CONTROL: the kernels really normalize by the DEVICE min/max - a different range in the view must
            // change the depth half (a kernel that ignored the view, or read it at the wrong offset, would not).
            minMaxBuf.CopyFromCPU(new[] { min, max + (max - min) });
            twoDZ(new Index2D(w, h), rgbaBuf.View, rawBuf.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0);
            acc.Synchronize();
            var dzWide = outBuf.GetAsArray1D();
            int wideDiff = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (MaxChannelDiff(Ref2DZ(x, y, w, rgba, raw, direct, min, max + (max - min)), dzWide[y * w + x]) > 1) wideDiff++;
            int changedDz = dz.Zip(dzWide).Count(p => p.First != p.Second);
            Check($"{acc.AcceleratorType} 2D+Z {w}x{h} direct={direct}{(noisy ? " noisy" : "")} reads min/max from the device view", wideDiff == 0 && changedDz > w * h / 8,
                $"{wideDiff} pixels off the widened-range reference, {changedDz} changed");
            minMaxBuf.CopyFromCPU(new[] { min, max });

            // SCREEN 2D+Z (fullscreen Dimenco). (a) A video that exactly fills the screen must give TwoDZKernel's frame
            // (the bilinear sample at a column pair's centre IS the 2-tap average) - an independent cross-check.
            twoDZScreen(new Index2D(w, h), rgbaBuf.View, rawBuf.View, outBuf.View, minMaxBuf.View, w, h, w, direct ? 1 : 0, 0f, 0f, w, h);
            acc.Synchronize();
            var fill = outBuf.GetAsArray1D();
            int fillBad = 0;
            for (int i = 0; i < w * h; i++) if (MaxChannelDiff(dz[i], fill[i]) > 1) fillBad++;
            Check($"{acc.AcceleratorType} screen 2D+Z {w}x{h} direct={direct}{(noisy ? " noisy" : "")} filling the screen == 2D+Z", fillBad == 0, $"{fillBad} pixels differ");

            // (b) PILLARBOX: the frame shown in the middle of a wider screen, bars both sides. Every pixel vs the reference
            // (which pairs output x and x + W/2 with the SAME screen column - the property the display relies on), and the
            // bars exactly black in both halves.
            int pad = w / 3 + 1, sw = w + 2 * pad + ((w + 2 * pad) & 1), sh = h;   // even screen width
            using var screenBuf = acc.Allocate1D<int>(sw * sh);
            twoDZScreen(new Index2D(sw, sh), rgbaBuf.View, rawBuf.View, screenBuf.View, minMaxBuf.View, w, h, sw, direct ? 1 : 0, pad, 0f, w, h);
            acc.Synchronize();
            var scr = screenBuf.GetAsArray1D();
            int scrBad = 0, barBad = 0, bars = 0;
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (MaxChannelDiff(RefScreen2DZ(x, y, sw, w, h, pad, 0, w, h, rgba, raw, direct, min, max), scr[y * sw + x]) > 1) scrBad++;
                    int column = x < sw / 2 ? 2 * x + 1 : 2 * (x - sw / 2) + 1;
                    if (column + 1 > pad && column - 1 < pad + w) continue;   // the footprint touches the video: not a bar
                    bars++;
                    if (scr[y * sw + x] != unchecked((int)0xFF000000)) barBad++;
                }
            Check($"{acc.AcceleratorType} screen 2D+Z pillarbox {sw}x{sh} (frame {w}x{h} at x={pad}) direct={direct}{(noisy ? " noisy" : "")} == reference", scrBad == 0, $"{scrBad} pixels differ");
            Check($"{acc.AcceleratorType} screen 2D+Z pillarbox {sw}x{sh} direct={direct}{(noisy ? " noisy" : "")} bars black in both halves", bars > 0 && barBad == 0, $"{barBad}/{bars} bar pixels not black");
        }
    }
}

// Edge-aware (joint bilateral) depth upsampling: DepthUpsampleKernels vs a direct reference, plus the property it exists
// for - a depth step lands on the COLOUR edge - with a negative control (colour ignored) that must lose that property.
void RunUpsampleChecks(Accelerator acc)
{
    var guideK = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>(DepthUpsampleKernels.GuideKernel);
    var jbuK = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float>(DepthUpsampleKernels.JointBilateralUpsampleKernel);
    var bilK = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>(TemporalDepthKernels.UpsampleKernel);

    float[] Run(int[] rgba, int W, int H, float[] u, int w, int h, float ss, float sr, bool bilinear = false)
    {
        using var f = acc.Allocate1D(rgba);
        using var ub = acc.Allocate1D(u);
        using var g = acc.Allocate1D<float>(3 * w * h);
        using var o = acc.Allocate1D<float>(W * H);
        if (bilinear) bilK(new Index2D(W, H), ub.View, w, h, o.View, W, H);
        else
        {
            guideK(w * h, f.View, W, H, g.View, w, h);
            jbuK(new Index2D(W, H), ub.View, w, h, g.View, f.View, o.View, W, H, ss, sr);
        }
        acc.Synchronize();
        return o.GetAsArray1D();
    }

    // 1) kernel == reference, random frame + depth, non-integer scale factors
    foreach (var (W, H, w, h) in new[] { (97, 31, 13, 5), (200, 60, 37, 11), (64, 64, 64, 64) })
    {
        var rng = new Random(W + 1000 * H);
        var rgba = new int[W * H];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = rng.Next(0, 0x1000000) | unchecked((int)0xFF000000);
        var u = new float[w * h];
        for (int i = 0; i < u.Length; i++) u[i] = 1f + 100f * (float)rng.NextDouble();
        foreach (var (ss, sr) in new[] { (1f, 0.1f), (0.6f, 0.03f), (2f, 1f), (1f, 0.001f) })
        {
            var got = Run(rgba, W, H, u, w, h, ss, sr);
            var exp = RefJbu(rgba, W, H, u, w, h, ss, sr);
            float worst = 0f;
            for (int i = 0; i < got.Length; i++) worst = MathF.Max(worst, MathF.Abs(got[i] - exp[i]));
            Check($"{acc.AcceleratorType} edge-aware upsample {w}x{h}->{W}x{H} sigmas {ss}/{sr} == reference", worst < 2e-4f, $"worst |diff| {worst}");
        }
    }

    // 2) the property: frame = dark left / bright right, colour edge at x = 52 = the MIDDLE of depth sample 6 (8x scale),
    // depth near (0.9) left of the edge, far (0.1) right. The model's sample 6 straddles the edge (its depth is the mix).
    {
        int W = 96, H = 8, w = 12, h = 1, edge = 52;
        var rgba = new int[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) rgba[y * W + x] = x < edge ? unchecked((int)0xFF202020) : unchecked((int)0xFFE0E0E0);
        var u = new float[w * h];
        for (int i = 0; i < w; i++) u[i] = 1f + 100f * (i < 6 ? 0.9f : i > 6 ? 0.1f : 0.5f);
        float Err(float[] d) { float e = 0f; for (int y = 0; y < H; y++) for (int x = 40; x < 64; x++) e += MathF.Abs(d[y * W + x] - (x < edge ? 0.9f : 0.1f)); return e / (H * 24); }
        float eJbu = Err(Run(rgba, W, H, u, w, h, 1f, 0.1f));
        float eBil = Err(Run(rgba, W, H, u, w, h, 1f, 0.1f, bilinear: true));
        float eBlind = Err(Run(rgba, W, H, u, w, h, 1f, 1000f));   // NEGATIVE CONTROL: colour ignored
        Check($"{acc.AcceleratorType} edge-aware upsample puts the depth step on the colour edge", eJbu <= 0.5f * eBil,
            $"mean |err| near the edge: edge-aware {eJbu:0.000} vs bilinear {eBil:0.000}");
        Check($"{acc.AcceleratorType} edge-aware upsample control: colour ignored loses it", eBlind > 0.5f * eBil && eBlind > 2f * eJbu,
            $"colour-blind {eBlind:0.000}, bilinear {eBil:0.000}, edge-aware {eJbu:0.000}");
        Console.WriteLine($"      (edge error: edge-aware {eJbu:0.0000}, bilinear {eBil:0.0000}, colour-blind {eBlind:0.0000})");
    }
}

static float[] RefJbu(int[] rgba, int W, int H, float[] u, int w, int h, float ss, float sr)
{
    float C(int v, int sh) => ((v >> sh) & 255) / 255f;
    var guide = new float[3 * w * h];
    for (int ly = 0; ly < h; ly++)
        for (int lx = 0; lx < w; lx++)
        {
            int x0 = lx * W / w, x1 = Math.Min(W, Math.Max(x0 + 1, (lx + 1) * W / w));
            int y0 = ly * H / h, y1 = Math.Min(H, Math.Max(y0 + 1, (ly + 1) * H / h));
            double r = 0, g = 0, b = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int p = rgba[y * W + x]; r += C(p, 0); g += C(p, 8); b += C(p, 16); }
            int n = (x1 - x0) * (y1 - y0), q = ly * w + lx;
            guide[3 * q] = (float)(r / n); guide[3 * q + 1] = (float)(g / n); guide[3 * q + 2] = (float)(b / n);
        }
    var o = new float[W * H];
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double fx = (x + 0.5) * w / W - 0.5, fy = (y + 0.5) * h / H - 0.5;
            int cx = (int)Math.Floor(fx + 0.5), cy = (int)Math.Floor(fy + 0.5);
            int p = rgba[y * W + x];
            double sum = 0, ws = 0;
            for (int qy = cy - 2; qy <= cy + 2; qy++)
                for (int qx = cx - 2; qx <= cx + 2; qx++)
                {
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                    int q = qy * w + qx;
                    double dc = Math.Pow(C(p, 0) - guide[3 * q], 2) + Math.Pow(C(p, 8) - guide[3 * q + 1], 2) + Math.Pow(C(p, 16) - guide[3 * q + 2], 2);
                    double ds = (qx - fx) * (qx - fx) + (qy - fy) * (qy - fy);
                    double wt = Math.Exp(-ds / (2 * ss * ss) - dc / (2 * sr * sr));
                    sum += wt * u[q]; ws += wt;
                }
            double uu;
            if (ws > 1e-6) uu = sum / ws;
            else
            {
                double bx = Math.Max(0, fx), by = Math.Max(0, fy);
                int x0 = Math.Min((int)bx, w - 1), y0 = Math.Min((int)by, h - 1);
                int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                double tx = bx - x0, ty = by - y0;
                double top = u[y0 * w + x0] + (u[y0 * w + x1] - u[y0 * w + x0]) * tx, bot = u[y1 * w + x0] + (u[y1 * w + x1] - u[y1 * w + x0]) * tx;
                uu = top + (bot - top) * ty;
            }
            o[y * W + x] = (float)Math.Clamp((uu - 1) * 0.01, 0, 1);
        }
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// GLSL-literal reference (3.x store build: multiview.renderer.base.fs.glsl viewColor2DZ + anaglyph.fs.glsl + pseudo2DZ,
// uniforms as RenderBase.js sets them).
// Textures are sampled NEAREST at texel centres except where the GLSL's LINEAR sample lands between two texels.
// ---------------------------------------------------------------------------------------------------------------
static float RefDisparity(float raw, bool direct, float min, float max)
{
    // depth -> disparity by definition, normalized to [0, 1] over the frame (1 = near)
    float v, lo, hi;
    if (direct) { v = 1f / MathF.Max(raw, 1e-6f); lo = 1f / MathF.Max(max, 1e-6f); hi = 1f / MathF.Max(min, 1e-6f); }
    else { v = raw; lo = min; hi = max; }
    return Math.Clamp((v - lo) / (hi - lo), 0f, 1f);
}


static int RefAnaglyph(int x, int y, int w, int[] rgba, float[] raw, bool direct, float min, float max, float level3D, float focus3D, int profile)
{
    // RenderBase.js: outPixelWidth = 1 / outWidth; sep_max_x = sepMax * level3D * (900 / outWidth); rC0 = [sep, pw, pw/2, focus]
    float pixel_width = 1f / w;
    float sep_max_x = 0.02f * level3D * (900f / w);
    float offset_f = focus3D;
    int loop_cnt = (int)MathF.Ceiling(sep_max_x / pixel_width) + 2;
    float vUVx = (x + 0.5f) / w;
    int row = y * w;
    // LINEAR, CLAMP_TO_EDGE texture2D along this row (vUV.y is a texel centre, so only x interpolates)
    float Tex(Func<int, float> texel, float u) { float t = u * w - 0.5f; float f0 = MathF.Floor(t); float fr = t - f0; int i0 = Math.Clamp((int)f0, 0, w - 1), i1 = Math.Clamp((int)f0 + 1, 0, w - 1); return texel(i0) * (1f - fr) + texel(i1) * fr; }
    float GetDepth(float u) => Tex(i => RefDisparity(raw[row + i], direct, min, max), u);
    (float r, float g, float b) Video(float u) => (Tex(i => ((rgba[row + i] >> 0) & 255) / 255f, u), Tex(i => ((rgba[row + i] >> 8) & 255) / 255f, u), Tex(i => ((rgba[row + i] >> 16) & 255) / 255f, u));
    static float Mod(float a, float b) => a - b * MathF.Floor(a / b);   // GLSL mod

    var l = Video(vUVx);   // viewColor(0.0, vUV): view_index == 0 -> texture2D(videoSampler, view_uv)
    // viewColor(1.0, vUV), views_index_invert_x = false
    float view_index = 1f;
    (float r, float g, float b) o;
    float sep = sep_max_x * MathF.Abs(view_index);
    if (view_index == 0f || sep < pixel_width) o = Video(vUVx);
    else
    {
        float cur_depth = -2f, cur_coord_x = 0f, lowestDepthDiff = 1f, lowestDepth = 1f, lowestDepthX = 0f;
        bool hitAny = false;
        float shiftMode = view_index > 0f ? -1f : 1f;
        float pixel_width_signed = pixel_width * shiftMode;
        float sep_max_x_signed = sep * shiftMode;
        float offset_f_signed = offset_f * sep_max_x_signed;
        float start_x = vUVx - sep_max_x_signed + offset_f_signed;
        start_x = start_x - Mod(start_x, pixel_width);
        for (int n = 0; n < 100; n++)
        {
            if (n >= loop_cnt) break;
            float uvNextX = start_x + (pixel_width_signed * n);
            float pDepth = GetDepth(uvNextX);
            float dest_x = uvNextX + (pDepth * sep_max_x_signed) - offset_f_signed;
            float diff_x = MathF.Abs(vUVx - dest_x);
            if (diff_x <= pixel_width && cur_depth <= pDepth) { cur_depth = pDepth; cur_coord_x = uvNextX; hitAny = true; }
            if (pDepth <= lowestDepth && diff_x <= lowestDepthDiff + pixel_width) { lowestDepthDiff = diff_x; lowestDepth = pDepth; lowestDepthX = uvNextX; }
        }
        o = hitAny ? Video(cur_coord_x) : Video(lowestDepthX);
        RefDebug = $"{(hitAny ? "hit" : "fill")}@{(hitAny ? cur_coord_x : lowestDepthX) * w:0.###} start={start_x * w:0.###} loop={loop_cnt}";
    }
    return MixF(l, o, profile);
}

static int MixF((float r, float g, float b) l, (float r, float g, float b) r, int profile)
{
    var p = AnaglyphProfiles.Data.AsSpan(profile * ThreeDKernels.ProfileStride, ThreeDKernels.ProfileStride);
    float lr = l.r, lg = l.g, lb = l.b, rr = r.r, rg = r.g, rb = r.b;
    float brightness = p[0], contrast = p[1], gamma = p[2];
    if (gamma > 0.1f) { lr = MathF.Pow(lr, 1 / gamma); lg = MathF.Pow(lg, 1 / gamma); lb = MathF.Pow(lb, 1 / gamma); rr = MathF.Pow(rr, 1 / gamma); rg = MathF.Pow(rg, 1 / gamma); rb = MathF.Pow(rb, 1 / gamma); }
    float red = lr * p[3] + lg * p[4] + lb * p[5] + rr * p[6] + rg * p[7] + rb * p[8];
    float green = lr * p[9] + lg * p[10] + lb * p[11] + rr * p[12] + rg * p[13] + rb * p[14];
    float blue = lr * p[15] + lg * p[16] + lb * p[17] + rr * p[18] + rg * p[19] + rb * p[20];
    red = (red - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    green = (green - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    blue = (blue - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    if (gamma > 0.1f) { red = MathF.Pow(red, gamma); green = MathF.Pow(green, gamma); blue = MathF.Pow(blue, gamma); }
    return Pack(red, green, blue);
}

static int Mix(int l, int r, int profile)
{
    var p = AnaglyphProfiles.Data.AsSpan(profile * ThreeDKernels.ProfileStride, ThreeDKernels.ProfileStride);
    float C(int v, int s) => ((v >> s) & 255) / 255f;
    float lr = C(l, 0), lg = C(l, 8), lb = C(l, 16), rr = C(r, 0), rg = C(r, 8), rb = C(r, 16);
    float brightness = p[0], contrast = p[1], gamma = p[2];
    if (gamma > 0.1f) { lr = MathF.Pow(lr, 1 / gamma); lg = MathF.Pow(lg, 1 / gamma); lb = MathF.Pow(lb, 1 / gamma); rr = MathF.Pow(rr, 1 / gamma); rg = MathF.Pow(rg, 1 / gamma); rb = MathF.Pow(rb, 1 / gamma); }
    float red = lr * p[3] + lg * p[4] + lb * p[5] + rr * p[6] + rg * p[7] + rb * p[8];
    float green = lr * p[9] + lg * p[10] + lb * p[11] + rr * p[12] + rg * p[13] + rb * p[14];
    float blue = lr * p[15] + lg * p[16] + lb * p[17] + rr * p[18] + rg * p[19] + rb * p[20];
    red = (red - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    green = (green - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    blue = (blue - 0.5f) * (contrast + 1f) + 0.5f + brightness;
    if (gamma > 0.1f) { red = MathF.Pow(red, gamma); green = MathF.Pow(green, gamma); blue = MathF.Pow(blue, gamma); }
    return Pack(red, green, blue);
}

static int Pack(float r, float g, float b)
{
    int B(float v) => (int)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
    return B(r) | (B(g) << 8) | (B(b) << 16) | unchecked((int)0xFF000000);
}

static int Ref2DZ(int x, int y, int w, int[] rgba, float[] raw, bool direct, float min, float max)
{
    float u = (x + 0.5f) / w;
    int row = y * w;
    // GLSL LINEAR sample at u' * W - 0.5 texel space: the two texels either side, weight 0.5 each at these u'
    (int, int) Pair(float srcU) { float t = srcU * w - 0.5f; int i0 = (int)MathF.Floor(t); return (Math.Clamp(i0, 0, w - 1), Math.Clamp(i0 + 1, 0, w - 1)); }
    if (u > 0.5f)
    {
        var (i0, i1) = Pair((u - 0.5f) * 2f);
        float z = 0.5f * (RefDisparity(raw[row + i0], direct, min, max) + RefDisparity(raw[row + i1], direct, min, max));
        return Pack(z, z, z);
    }
    else
    {
        var (i0, i1) = Pair(u * 2f);
        int c0 = rgba[row + i0], c1 = rgba[row + i1];
        float C(int v, int s) => ((v >> s) & 255) / 255f;
        return Pack(0.5f * (C(c0, 0) + C(c1, 0)), 0.5f * (C(c0, 8) + C(c1, 8)), 0.5f * (C(c0, 16) + C(c1, 16)));
    }
}

// Screen 2D+Z (fullscreen Dimenco), in UV space like the references above: output u in the left half shows SCREEN u' = 2u,
// in the right half SCREEN u' = 2(u - 0.5) - so output x and x + W/2 always show the same screen column. A screen point
// whose output pixel footprint (2 screen columns x 1 row) overlaps the video's rect reads the frame with a LINEAR,
// clamp-to-edge texture sample at the footprint centre (depth: disparity, then blended); a footprint that misses the rect
// (the bars) is black.
static int RefScreen2DZ(int x, int y, int sw, int fw, int fh, float rx, float ry, float rw, float rh,
    int[] rgba, float[] raw, bool direct, float min, float max)
{
    float u = (x + 0.5f) / sw;
    bool depthHalf = u > 0.5f;
    // rounded to 1e-3: UV round-off (213.99998 for column 214) would flip footprints that END exactly on the rect edge
    float screenX = MathF.Round((depthHalf ? 2f * (u - 0.5f) : 2f * u) * sw, 3), screenY = y + 0.5f;
    bool overlaps = screenX + 1f > rx && screenX - 1f < rx + rw && screenY + 0.5f > ry && screenY - 0.5f < ry + rh;
    if (!overlaps) return Pack(0f, 0f, 0f);
    float tx = Math.Clamp((screenX - rx) / rw * fw - 0.5f, 0f, fw - 1), ty = Math.Clamp((screenY - ry) / rh * fh - 0.5f, 0f, fh - 1);
    int x0 = (int)MathF.Floor(tx), y0 = (int)MathF.Floor(ty), x1 = Math.Min(x0 + 1, fw - 1), y1 = Math.Min(y0 + 1, fh - 1);
    float fx = tx - x0, fy = ty - y0;
    float Lerp2(Func<int, float> at) =>
        (1 - fx) * (1 - fy) * at(y0 * fw + x0) + fx * (1 - fy) * at(y0 * fw + x1) + (1 - fx) * fy * at(y1 * fw + x0) + fx * fy * at(y1 * fw + x1);
    if (depthHalf)
    {
        float z = Lerp2(i => RefDisparity(raw[i], direct, min, max));
        return Pack(z, z, z);
    }
    float C(int i, int s) => ((rgba[i] >> s) & 255) / 255f;
    return Pack(Lerp2(i => C(i, 0)), Lerp2(i => C(i, 8)), Lerp2(i => C(i, 16)));
}

static int MaxChannelDiff(int a, int b)
{
    int m = 0;
    for (int s = 0; s < 32; s += 8) m = Math.Max(m, Math.Abs(((a >> s) & 255) - ((b >> s) & 255)));
    return m;
}

// ---------------------------------------------------------------------------------------------------------------
void RunHeaderChecks()
{
    int cases = 0, mismatches = 0;
    string firstBad = "";
    foreach (var format in Enum.GetValues<HeaderDataFormats>())
        foreach (byte factor in new byte[] { 0, 16, 128, 255 })
            foreach (byte offset in new byte[] { 0, 64, 128, 255 })
                foreach (byte contentType in new byte[] { 0, 3, 5 })
                    foreach (byte dataType in new byte[] { 0, 1, 2 })
                        foreach (bool enables in new[] { true, false })
                            foreach (bool transparent in new[] { true, false })
                            {
                                var mine = new Philips2DZHeader { Format = format, Factor = factor, Offset = offset, ContentType = contentType, DataType = dataType, HeaderFactorEnabled = enables, HeaderOffsetEnabled = !enables, TransparentUnusedPixels = transparent };
                                var oracle = new OracleHeader { Format = (OracleFormats)(int)format, Factor = factor, Offset = offset, ContentType = contentType, DataType = dataType, HeaderFactorEnabled = enables, HeaderOffsetEnabled = !enables, TransparentUnusedPixels = transparent };
                                cases++;
                                if (!mine.HeaderData.AsSpan().SequenceEqual(oracle.HeaderData))
                                {
                                    mismatches++;
                                    if (firstBad == "") firstBad = $"{format} f{factor} o{offset} c{contentType} d{dataType} e{enables} t{transparent}";
                                }
                            }
    Check($"Philips2DZHeader bytes == MultiView.Dimenco oracle ({cases} cases)", mismatches == 0, $"{mismatches} mismatches, first {firstBad}");
    // the default header (what a Dimenco display sees before any setting changes)
    Check("Philips2DZHeader default == oracle default", new Philips2DZHeader().HeaderData.AsSpan().SequenceEqual(new OracleHeader().HeaderData));
}

partial class Program { public static string RefDebug = ""; }
