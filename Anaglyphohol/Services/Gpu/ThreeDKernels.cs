using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System.Runtime.CompilerServices;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// 2D + depth -> 3D kernels. A port of TJ's GLSL from the Anaglyphohol 3.x store build (vjs/anglyphoholv3
    /// multiview.renderer.base.fs.glsl viewColor2DZ, multiview.renderer.anaglyph.fs.glsl anaglyphMix, pseudo2DZ) to ILGPU so the frame, the depth
    /// map and the 3D output all live on the ONE accelerator the depth model runs on - no WebGL context, no readback.
    /// </summary>
    /// <remarks>
    /// Pixel buffers are packed RGBA8 ints (R in the low byte), row-major, top row first - the layout
    /// SpawnDev.ILGPU's IExternalImageCopier writes and ICanvasRenderer presents.
    /// <para>
    /// Depth arrives RAW from the model at frame resolution. <see cref="Disparity"/> maps it to [0,1] with
    /// 1 = NEAR, the convention the GLSL was written for (Transformers.js DAv2 output in the alpha channel):
    /// <c>t = directDepth ? 1/raw : raw; d = clamp(t * a + b)</c>. DAv2 emits disparity (high = near); DAv3 emits
    /// depth (high = far), whose reciprocal is disparity. a/b come from the frame's raw min/max, which the kernels read
    /// from a 2-float DEVICE view (min, max) the depth pipeline's GPU reduction wrote - no per-frame readback
    /// (<see cref="ScaleBias"/>).
    /// </para>
    /// </remarks>
    public static class ThreeDKernels
    {
        /// <summary>Floats per anaglyph profile: brightness, contrast, gamma, then the 18-entry Dubois matrix.</summary>
        public const int ProfileStride = 21;
        /// <summary>The GLSL's loop bound (for n &lt; 100): the per-pixel reprojection search never scans more edges than this.</summary>
        public const int MaxSearchIterations = 100;

        /// <summary>
        /// Scale/bias that map a raw depth sample to disparity in [0,1] (1 = near), from the frame's raw min/max.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ScaleBias(float rawMin, float rawMax, int directDepth, out float a, out float b)
        {
            if (directDepth != 0)
            {
                // depth z > 0: disparity 1/z runs from 1/max (far) to 1/min (near)
                float zMin = XMath.Max(rawMin, 1e-6f), zMax = XMath.Max(rawMax, zMin * 1.000001f);
                float lo = 1f / zMax, hi = 1f / zMin;
                a = 1f / (hi - lo);
                b = -lo * a;
            }
            else
            {
                float range = rawMax - rawMin;
                a = range > 1e-12f ? 1f / range : 0f;
                b = -rawMin * a;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Disparity(float raw, int directDepth, float a, float b)
        {
            float t = raw;
            if (directDepth != 0) t = raw > 1e-6f ? 1f / raw : 1e6f;
            float d = t * a + b;
            return d < 0f ? 0f : (d > 1f ? 1f : d);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Channel(int packed, int shift) => ((packed >> shift) & 0xFF) * (1f / 255f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ToByte(float v)
        {
            float c = v < 0f ? 0f : (v > 1f ? 1f : v);
            return (int)(c * 255f + 0.5f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Pack(float r, float g, float b) => ToByte(r) | (ToByte(g) << 8) | (ToByte(b) << 16) | unchecked((int)0xFF000000);

        /// <summary>
        /// The RIGHT eye's source position at output pixel x: TJ's viewColor2DZ from the Anaglyphohol 3.x store build
        /// (vjs/anglyphoholv3 public/shaders/multiview.renderer.base.fs.glsl), in pixel units. Returned as a pixel EDGE e
        /// (the boundary between source columns e-1 and e): the GLSL scans uv = k/W, which a LINEAR sampler reads as the
        /// 50/50 blend of the two columns either side - that half-pixel blend is part of the look (clean, anti-aliased
        /// shifted edges), so callers sample with <see cref="EdgeChannel"/>.
        /// <para>
        /// Scan: from floor(x + 0.5 + S(1 - f)) LEFTWARD, ceil(S) + 2 edges (S = separation px, f = focus), so BOTH sides
        /// of the focus plane are reachable. A candidate at depth d lands at e - d*S + f*S. HIT = lands within 1 px of the
        /// pixel centre; the nearest (highest d) hit wins, ties to the later (more left) candidate. No hit = disocclusion:
        /// fill from the BACKGROUND - the lowest-depth candidate whose landing distance is within 1 px of the best so far.
        /// </para>
        /// </summary>
        /// <remarks>
        /// 2026-10-05 (TJ: "v3 3d looks better than v4 3d ... the edges on 3d objects look cleaner in v3"): v4 had ported
        /// the NEWER SpawnDev.BlazorJS.MultiView shader instead, which differs in every point above - one-sided scan from x
        /// (nothing behind the focus plane could ever HIT, so the background fell back to zero shift), 0.6 px hits,
        /// nearest-miss fill (foreground smeared into holes), whole-pixel NEAREST samples, and 2.5% of the frame width as
        /// separation (38 px at 1080p vs 3.x's fixed 9 px at its default level).
        /// </remarks>
        /// <param name="separationPx">must be &gt;= 1: below one pixel the GLSL shows the source pixel itself
        /// (<see cref="AnaglyphKernel"/> checks). Edges may lie OUTSIDE 0..width - a pixel shifted in from beyond the
        /// frame border - and sample the border column (clamp to edge), so no edge value can serve as a sentinel.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RightEyeSourceEdge(int x, int row, int width, ArrayView1D<float, Stride1D.Dense> depth,
            int directDepth, float a, float b, float separationPx, float focus)
        {
            float centre = x + 0.5f;
            int start = (int)XMath.Floor(centre + separationPx * (1f - focus));
            int count = (int)XMath.Ceiling(separationPx) + 2;
            if (count > MaxSearchIterations) count = MaxSearchIterations;
            float curDepth = -2f;
            int curEdge = 0;
            bool hit = false;
            float lowestDiff = width;   // GLSL: 1.0 in uv = the whole width
            float lowestDepth = 1f;
            int lowestEdge = 0;
            for (int n = 0; n < count; n++)
            {
                int e = start - n;
                float d = EdgeDisparity(e, row, width, depth, directDepth, a, b);
                float diff = XMath.Abs(centre - (e - d * separationPx + focus * separationPx));
                if (diff <= 1f && curDepth <= d)
                {
                    curDepth = d;
                    curEdge = e;
                    hit = true;
                }
                if (d <= lowestDepth && diff <= lowestDiff + 1f)
                {
                    lowestDiff = diff;
                    lowestDepth = d;
                    lowestEdge = e;
                }
            }
            return hit ? curEdge : lowestEdge;
        }

        /// <summary>Disparity a LINEAR, clamp-to-edge sampler returns at pixel edge e: the mean of columns e-1 and e.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float EdgeDisparity(int e, int row, int width, ArrayView1D<float, Stride1D.Dense> depth, int directDepth, float a, float b)
        {
            int c0 = e - 1 < 0 ? 0 : (e - 1 > width - 1 ? width - 1 : e - 1);
            int c1 = e < 0 ? 0 : (e > width - 1 ? width - 1 : e);
            return 0.5f * (Disparity(depth[row + c0], directDepth, a, b) + Disparity(depth[row + c1], directDepth, a, b));
        }

        /// <summary>One colour channel a LINEAR, clamp-to-edge sampler returns at pixel edge e: the mean of columns e-1 and e.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float EdgeChannel(ArrayView1D<int, Stride1D.Dense> rgba, int row, int width, int e, int shift)
        {
            int c0 = e - 1 < 0 ? 0 : (e - 1 > width - 1 ? width - 1 : e - 1);
            int c1 = e < 0 ? 0 : (e > width - 1 ? width - 1 : e);
            return 0.5f * (Channel(rgba[row + c0], shift) + Channel(rgba[row + c1], shift));
        }

        /// <summary>
        /// Anaglyph: left eye = source pixel, right eye = <see cref="RightEyeSourceEdge"/>, mixed by a Dubois profile
        /// (brightness, contrast, gamma + 3x6 matrix) from <paramref name="profiles"/> at <paramref name="profileOffset"/>.
        /// </summary>
        public static void AnaglyphKernel(Index2D index,
            ArrayView1D<int, Stride1D.Dense> rgba,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<float, Stride1D.Dense> profiles,
            ArrayView1D<int, Stride1D.Dense> output,
            ArrayView1D<float, Stride1D.Dense> minMax,
            int width, int directDepth, float separationPx, float focus, int profileOffset)
        {
            int x = index.X, y = index.Y;
            ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
            int row = y * width;
            int l = rgba[row + x];

            float lr = Channel(l, 0), lg = Channel(l, 8), lb = Channel(l, 16);
            float rr, rg, rb;
            // GLSL: sep_max_x < pixel_width -> the right eye shows the source pixel itself
            if (separationPx < 1f) { rr = lr; rg = lg; rb = lb; }
            else
            {
                int edge = RightEyeSourceEdge(x, row, width, depth, directDepth, a, b, separationPx, focus);
                rr = EdgeChannel(rgba, row, width, edge, 0);
                rg = EdgeChannel(rgba, row, width, edge, 8);
                rb = EdgeChannel(rgba, row, width, edge, 16);
            }
            int p = profileOffset;
            float brightness = profiles[p + 0], contrast = profiles[p + 1], gamma = profiles[p + 2];
            if (gamma > 0.1f)
            {
                float ig = 1f / gamma;
                lr = XMath.Pow(lr, ig); lg = XMath.Pow(lg, ig); lb = XMath.Pow(lb, ig);
                rr = XMath.Pow(rr, ig); rg = XMath.Pow(rg, ig); rb = XMath.Pow(rb, ig);
            }
            float red = lr * profiles[p + 3] + lg * profiles[p + 4] + lb * profiles[p + 5] + rr * profiles[p + 6] + rg * profiles[p + 7] + rb * profiles[p + 8];
            float green = lr * profiles[p + 9] + lg * profiles[p + 10] + lb * profiles[p + 11] + rr * profiles[p + 12] + rg * profiles[p + 13] + rb * profiles[p + 14];
            float blue = lr * profiles[p + 15] + lg * profiles[p + 16] + lb * profiles[p + 17] + rr * profiles[p + 18] + rg * profiles[p + 19] + rb * profiles[p + 20];
            red = (red - 0.5f) * (contrast + 1f) + 0.5f + brightness;
            green = (green - 0.5f) * (contrast + 1f) + 0.5f + brightness;
            blue = (blue - 0.5f) * (contrast + 1f) + 0.5f + brightness;
            if (gamma > 0.1f)
            {
                // pow of a negative is NaN in every shading language; the GLSL relied on it never happening
                red = XMath.Pow(XMath.Max(red, 0f), gamma);
                green = XMath.Pow(XMath.Max(green, 0f), gamma);
                blue = XMath.Pow(XMath.Max(blue, 0f), gamma);
            }
            output[row + x] = Pack(red, green, blue);
        }

        /// <summary>
        /// Dimenco / Philips 2D+Z: left half = the source squeezed 2:1, right half = disparity as grey (white = near).
        /// Each output pixel averages the two source pixels it covers, which is exactly what the GLSL's LINEAR sample at
        /// u = (2x+1)/W returns.
        /// </summary>
        public static void TwoDZKernel(Index2D index,
            ArrayView1D<int, Stride1D.Dense> rgba,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<int, Stride1D.Dense> output,
            ArrayView1D<float, Stride1D.Dense> minMax,
            int width, int directDepth)
        {
            int x = index.X, y = index.Y;
            ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
            int row = y * width;
            bool depthHalf = 2 * x + 1 > width;   // (x + 0.5) / W > 0.5
            int s = depthHalf ? 2 * x - width : 2 * x;
            if (s < 0) s = 0;
            int s0 = s < width ? s : width - 1;
            int s1 = s + 1 < width ? s + 1 : width - 1;
            if (depthHalf)
            {
                float d = 0.5f * (Disparity(depth[row + s0], directDepth, a, b) + Disparity(depth[row + s1], directDepth, a, b));
                output[row + x] = Pack(d, d, d);
            }
            else
            {
                int c0 = rgba[row + s0], c1 = rgba[row + s1];
                output[row + x] = Pack(
                    0.5f * (Channel(c0, 0) + Channel(c1, 0)),
                    0.5f * (Channel(c0, 8) + Channel(c1, 8)),
                    0.5f * (Channel(c0, 16) + Channel(c1, 16)));
            }
        }

        /// <summary>
        /// Dimenco / Philips 2D+Z of the WHOLE SCREEN (fullscreen video). A Dimenco display splits the SCREEN in halves, not
        /// the video, so <see cref="TwoDZKernel"/> (the frame's own halves) only lines up when the video fills the screen
        /// width. This kernel draws the screen as the viewer sees it: output pixel (x, y) is screen column 2x+1 (left half,
        /// colour) or 2(x - W/2)+1 (right half, disparity as grey), mapped into the frame through the video's displayed
        /// content rect (<paramref name="rectX"/>.. in output pixels, object-fit already applied). Outside the rect - the
        /// letterbox / pillarbox bars - is black, at depth 0 (far). Frame and depth are sampled bilinearly.
        /// </summary>
        public static void TwoDZScreenKernel(Index2D index,
            ArrayView1D<int, Stride1D.Dense> rgba,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<int, Stride1D.Dense> output,
            ArrayView1D<float, Stride1D.Dense> minMax,
            int frameW, int frameH, int outW, int directDepth,
            float rectX, float rectY, float rectW, float rectH)
        {
            int x = index.X, y = index.Y;
            bool depthHalf = 2 * x + 1 > outW;   // the same split as TwoDZKernel
            float sx = depthHalf ? 2f * x - outW + 1f : 2f * x + 1f;   // centre of the 2 screen columns this pixel covers
            float sy = y + 0.5f;
            int o = y * outW + x;
            // a bar only where the pixel's whole footprint (2 screen columns x 1 row) misses the video; a footprint that
            // straddles the video's edge samples the edge (clamped below), as TwoDZKernel does at an odd width
            if (sx + 1f <= rectX || sx - 1f >= rectX + rectW || sy + 0.5f <= rectY || sy - 0.5f >= rectY + rectH)
            {
                output[o] = Pack(0f, 0f, 0f);
                return;
            }
            float u = (sx - rectX) / rectW * frameW - 0.5f;
            float v = (sy - rectY) / rectH * frameH - 0.5f;
            u = u < 0f ? 0f : (u > frameW - 1 ? frameW - 1 : u);
            v = v < 0f ? 0f : (v > frameH - 1 ? frameH - 1 : v);
            int x0 = (int)u, y0 = (int)v;
            int x1 = x0 + 1 < frameW ? x0 + 1 : x0, y1 = y0 + 1 < frameH ? y0 + 1 : y0;
            float fx = u - x0, fy = v - y0;
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            int i00 = y0 * frameW + x0, i10 = y0 * frameW + x1, i01 = y1 * frameW + x0, i11 = y1 * frameW + x1;
            if (depthHalf)
            {
                ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
                float d = w00 * Disparity(depth[i00], directDepth, a, b) + w10 * Disparity(depth[i10], directDepth, a, b)
                        + w01 * Disparity(depth[i01], directDepth, a, b) + w11 * Disparity(depth[i11], directDepth, a, b);
                output[o] = Pack(d, d, d);
            }
            else
            {
                int c00 = rgba[i00], c10 = rgba[i10], c01 = rgba[i01], c11 = rgba[i11];
                output[o] = Pack(
                    w00 * Channel(c00, 0) + w10 * Channel(c10, 0) + w01 * Channel(c01, 0) + w11 * Channel(c11, 0),
                    w00 * Channel(c00, 8) + w10 * Channel(c10, 8) + w01 * Channel(c01, 8) + w11 * Channel(c11, 8),
                    w00 * Channel(c00, 16) + w10 * Channel(c10, 16) + w01 * Channel(c01, 16) + w11 * Channel(c11, 16));
            }
        }

        /// <summary>
        /// Video: the depth range the 3D kernels normalize by, smoothed over time (ONE thread). Each frame used to be
        /// normalized by its own raw min/max, so anything that moved either extreme shifted EVERY pixel's disparity - a
        /// source of flicker even where the picture is still. The smoothed range WIDENS quickly (<paramref name="grow"/>
        /// of the way per frame, so a new near/far object is not clipped for long) and NARROWS slowly
        /// (<paramref name="shrink"/>), so per-frame jitter in the extremes stops moving the whole map.
        /// </summary>
        public static void SmoothRangeKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> raw,
            ArrayView1D<float, Stride1D.Dense> smooth,
            int reset, float grow, float shrink)
        {
            float rMin = raw[0], rMax = raw[1];
            if (reset != 0)
            {
                smooth[0] = rMin;
                smooth[1] = rMax;
                return;
            }
            float sMin = smooth[0], sMax = smooth[1];
            sMin += (rMin < sMin ? grow : shrink) * (rMin - sMin);
            sMax += (rMax > sMax ? grow : shrink) * (rMax - sMax);
            smooth[0] = sMin;
            smooth[1] = sMax;
        }

        /// <summary>Slots <see cref="FlickerKernel"/> spreads its atomic sums over (summed on the host). The accumulator holds
        /// FIVE such banks: all / STATIC pixels' |delta d| for the DISPLAYED range, the static pixel count, and all / STATIC
        /// pixels' |delta d| for each frame's RAW range - both measured on the same frames.</summary>
        public const int FlickerSlots = 1024;
        /// <summary>A pixel is STATIC when no RGB channel moved more than this many levels since the previous frame.</summary>
        public const int FlickerStaticLevels = 2;

        /// <summary>
        /// DIAGNOSTIC (the "flicker" sweep): temporal instability of the DISPLAYED disparity. Per pixel, the frame's
        /// disparity d in [0,1] (same mapping the 3D kernels use, so a per-frame min/max renormalization counts too) is
        /// compared with the previous frame's: |d - prev| is added into <paramref name="acc"/>[i % FlickerSlots], and d is
        /// stored as the next frame's prev. Mean |delta d| per pixel per frame = sum(acc) / pixels. Pixels whose COLOR did not
        /// change (<see cref="FlickerStaticLevels"/>) are also summed separately: any depth change there is the model's own
        /// jitter, not motion.
        /// </summary>
        public static void FlickerKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<float, Stride1D.Dense> otherDepth,
            ArrayView1D<float, Stride1D.Dense> minMax,
            ArrayView1D<float, Stride1D.Dense> rawMinMax,
            ArrayView1D<float, Stride1D.Dense> prev,
            ArrayView1D<float, Stride1D.Dense> prevRaw,
            ArrayView1D<int, Stride1D.Dense> frame,
            ArrayView1D<int, Stride1D.Dense> prevFrame,
            ArrayView1D<float, Stride1D.Dense> acc,
            int directDepth, int hasPrev, int useOtherDepth, int useOtherRange)
        {
            // The second arm may share the first arm's depth or range; WebGPU forbids binding one buffer to two storage
            // slots, so the caller binds a placeholder and clears the flag instead.
            ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
            float ar = a, br = b;
            if (useOtherRange != 0) ScaleBias(rawMinMax[0], rawMinMax[1], directDepth, out ar, out br);
            float raw = depth[index];
            float d = Disparity(raw, directDepth, a, b);
            float dRaw = Disparity(useOtherDepth != 0 ? otherDepth[index] : raw, directDepth, ar, br);
            int c = frame[index];
            if (hasPrev != 0)
            {
                float delta = XMath.Abs(d - prev[index]);
                float deltaRaw = XMath.Abs(dRaw - prevRaw[index]);
                int slot = index % FlickerSlots;
                Atomic.Add(ref acc[slot], delta);
                Atomic.Add(ref acc[3 * FlickerSlots + slot], deltaRaw);
                int p = prevFrame[index];
                int dr = XMath.Abs((c & 0xFF) - (p & 0xFF));
                int dg = XMath.Abs(((c >> 8) & 0xFF) - ((p >> 8) & 0xFF));
                int db = XMath.Abs(((c >> 16) & 0xFF) - ((p >> 16) & 0xFF));
                if (dr <= FlickerStaticLevels && dg <= FlickerStaticLevels && db <= FlickerStaticLevels)
                {
                    Atomic.Add(ref acc[FlickerSlots + slot], delta);
                    Atomic.Add(ref acc[2 * FlickerSlots + slot], 1f);
                    Atomic.Add(ref acc[4 * FlickerSlots + slot], deltaRaw);
                }
            }
            prev[index] = d;
            prevRaw[index] = dRaw;
            prevFrame[index] = c;
        }
    }
}
