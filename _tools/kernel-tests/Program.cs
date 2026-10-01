using Anaglyphohol.Services.Gpu;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using OracleHeader = SpawnDev.BlazorJS.MultiView.Dimenco.Philips2DZHeader;
using OracleFormats = SpawnDev.BlazorJS.MultiView.Dimenco.HeaderDataFormats;

// Anaglyphohol 3D kernel + Philips header harness. Exit code = number of failed checks.
// The kernel reference below is a LITERAL port of MultiView's GLSL (uv space, float math, texel lookups) - deliberately
// NOT the pixel-space formulation ThreeDKernels uses, so an indexing / unit mistake in the port shows up as a mismatch.
int failed = 0, passed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"PASS  {name}"); }
    else { failed++; Console.WriteLine($"FAIL  {name}  {detail}"); }
}

bool useGpu = args.Contains("-gpu");
using var context = Context.Create().AllAccelerators().EnableAlgorithms().ToContext();
var devices = context.Devices.Where(d => d.AcceleratorType == AcceleratorType.CPU || (useGpu && d.AcceleratorType is AcceleratorType.Cuda or AcceleratorType.OpenCL)).ToList();
foreach (var device in devices)
{
    using var acc = device.CreateAccelerator(context);
    Console.WriteLine($"== {acc.AcceleratorType}: {acc.Name}");
    RunKernelChecks(acc);
}
RunHeaderChecks();
Console.WriteLine($"RESULTS: passed {passed}, failed {failed}");
return failed;

void RunKernelChecks(Accelerator acc)
{
    var anaglyph = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>(ThreeDKernels.AnaglyphKernel);
    var twoDZ = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ThreeDKernels.TwoDZKernel);
    using var profiles = acc.Allocate1D(AnaglyphProfiles.Data);
    const float SepMax = 0.025f;   // ThreeDRenderer.SepMax

    foreach (var (w, h) in new[] { (97, 23), (640, 9), (33, 5) })
    {
        var rng = new Random(w * 31 + h);
        var rgba = new int[w * h];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = rng.Next(0, 0x1000000) | unchecked((int)0xFF000000);
        foreach (bool direct in new[] { false, true })
        {
            // smooth ramp + a near "object" block, positive (DAv3 depth must be > 0)
            var raw = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = 1f + 3f * x / w + 0.5f * MathF.Sin(y * 0.7f);
                    if (x > w / 3 && x < w / 2) v = direct ? 0.6f : 5f;   // near block: small depth / large disparity
                    raw[y * w + x] = v;
                }
            float min = raw.Min(), max = raw.Max();
            // min/max reach the kernels as a 2-float DEVICE view (the depth pipeline's GPU reduction writes it in the app)
            using var minMaxBuf = acc.Allocate1D(new[] { min, max });
            using var rgbaBuf = acc.Allocate1D(rgba);
            using var rawBuf = acc.Allocate1D(raw);
            using var outBuf = acc.Allocate1D<int>(w * h);

            foreach (var (level, conv, profile) in new[] { (1f, 0.5f, 0), (0.8f, 0.2f, 1), (0.35f, 0.9f, 0) })
            {
                float sep = SepMax * level;
                anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, sep * w, conv, profile * ThreeDKernels.ProfileStride);
                acc.Synchronize();
                var got = outBuf.GetAsArray1D();
                int bad = 0, worst = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int exp = RefAnaglyph(x, y, w, rgba, raw, direct, min, max, sep, conv, profile);
                        int diff = MaxChannelDiff(exp, got[y * w + x]);
                        worst = Math.Max(worst, diff);
                        if (diff > 1) bad++;
                    }
                // uv-space vs pixel-space can round a hit test that sits exactly on the 0.6 px boundary differently;
                // anything beyond a handful of pixels is a real port error.
                double frac = (double)bad / (w * h);
                Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct} level={level} conv={conv} profile={profile}", frac <= 0.002,
                    $"{bad}/{w * h} pixels off by >1 (worst {worst})");
            }

            // NEGATIVE CONTROL: the 3D effect is real - level 1 must differ from level 0 (no parallax)
            anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, SepMax * w, 0.5f, 0);
            acc.Synchronize();
            var with3D = outBuf.GetAsArray1D();
            anaglyph(new Index2D(w, h), rgbaBuf.View, rawBuf.View, profiles.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0, 0f, 0.5f, 0);
            acc.Synchronize();
            var flat = outBuf.GetAsArray1D();
            int changed = with3D.Zip(flat).Count(p => p.First != p.Second);
            Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct} parallax changes the image", w < 64 || changed > w * h / 50, $"only {changed} pixels changed");
            // zero separation = both eyes see the source: Dubois(src, src) exactly
            int flatBad = 0;
            for (int i = 0; i < w * h; i++) if (MaxChannelDiff(Mix(rgba[i], rgba[i], 0), flat[i]) > 1) flatBad++;
            Check($"{acc.AcceleratorType} anaglyph {w}x{h} direct={direct} zero separation = Dubois(src, src)", flatBad == 0, $"{flatBad} pixels differ");

            twoDZ(new Index2D(w, h), rgbaBuf.View, rawBuf.View, outBuf.View, minMaxBuf.View, w, direct ? 1 : 0);
            acc.Synchronize();
            var dz = outBuf.GetAsArray1D();
            int dzBad = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (MaxChannelDiff(Ref2DZ(x, y, w, rgba, raw, direct, min, max), dz[y * w + x]) > 1) dzBad++;
            Check($"{acc.AcceleratorType} 2D+Z {w}x{h} direct={direct}", dzBad == 0, $"{dzBad} pixels differ");

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
            Check($"{acc.AcceleratorType} 2D+Z {w}x{h} direct={direct} reads min/max from the device view", wideDiff == 0 && changedDz > w * h / 8,
                $"{wideDiff} pixels off the widened-range reference, {changedDz} changed");
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------
// GLSL-literal reference (multiview.renderer.base.fs.glsl viewColor2DZ + anaglyph.fs.glsl + pseudo2DZ).
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

static int Texel(float u, int w) => Math.Clamp((int)MathF.Floor(u * w), 0, w - 1);

static int RefAnaglyph(int x, int y, int w, int[] rgba, float[] raw, bool direct, float min, float max, float uSeparation, float uConvergence, int profile)
{
    float viewU = (x + 0.5f) / w;
    int row = y * w;
    int left = rgba[row + Texel(viewU, w)];   // view 0 = the source view (uSourceViewIndex = 0)
    // view 1, not inverted: viewOffset = 1
    float viewOffset = 1f;
    float searchDir = MathF.Sign(viewOffset);
    float maxViewSeparation = MathF.Abs(viewOffset) * uSeparation;
    float pixelSizeX = 1f / w;
    float bestDepth = -1f; float bestU = viewU;
    float closestMissDist = 1000f; float closestMissU = viewU; float closestMissDepth = 1000f;
    for (int i = 0; i < ThreeDKernels.MaxSearchIterations; i++)
    {
        float offset = i * pixelSizeX;
        if (offset > maxViewSeparation) break;
        float candU = viewU + offset * searchDir;
        if (candU < 0f || candU > 1f) continue;
        float d = RefDisparity(raw[row + Texel(candU, w)], direct, min, max);
        float parallax = (d - uConvergence) * uSeparation * viewOffset;
        float projectedX = candU - parallax;
        float dist = MathF.Abs(projectedX - viewU);
        if (dist < pixelSizeX * 0.6f)
        {
            if (d > bestDepth) { bestDepth = d; bestU = candU; }
        }
        else if (dist < closestMissDist) { closestMissDist = dist; closestMissU = candU; closestMissDepth = d; }
        else if (MathF.Abs(dist - closestMissDist) < pixelSizeX * 0.1f && d < closestMissDepth) { closestMissU = candU; closestMissDepth = d; }
    }
    float finalU = bestDepth > -1f ? bestU : closestMissU;
    int right = rgba[row + Texel(finalU, w)];
    return Mix(left, right, profile);
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
