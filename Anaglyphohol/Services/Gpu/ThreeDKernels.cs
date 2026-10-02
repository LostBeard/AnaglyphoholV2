using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System.Runtime.CompilerServices;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// 2D + depth -> 3D kernels. A port of SpawnDev.BlazorJS.MultiView's GLSL (multiview.renderer.base.fs.glsl
    /// viewColor2DZ, multiview.renderer.anaglyph.fs.glsl anaglyphMix, pseudo2DZ) to ILGPU so the frame, the depth
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
        /// <summary>MultiView's MAX_SEARCH_ITERATIONS: the per-pixel reprojection search never scans past this many pixels.</summary>
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
        /// Column of the source pixel that the RIGHT eye (view 1, source = view 0) sees at (x, y): MultiView's
        /// viewColor2DZ scan, in whole pixels. Scans x .. x + sep*W; a candidate whose reprojection lands within 0.6 px
        /// is a HIT (nearest depth wins = occlusion); otherwise the closest miss fills the hole, preferring BACKGROUND
        /// when two misses are within 0.1 px of each other (disocclusion reveals what is behind).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RightEyeSourceX(int x, int row, int width, ArrayView1D<float, Stride1D.Dense> depth,
            int directDepth, float a, float b, float separationPx, float convergence)
        {
            int bestX = x;
            float bestDepth = -1f;
            int missX = x;
            float missDist = 1000f;
            float missDepth = 1000f;
            int maxI = (int)separationPx;   // GLSL: stop once i/W > separation, i.e. i > separation*W
            if (maxI > MaxSearchIterations - 1) maxI = MaxSearchIterations - 1;
            for (int i = 0; i <= maxI; i++)
            {
                int cx = x + i;
                if (cx >= width) break;   // every later candidate is further right, also out of bounds
                float d = Disparity(depth[row + cx], directDepth, a, b);
                float parallaxPx = (d - convergence) * separationPx;
                float dist = XMath.Abs(i - parallaxPx);
                if (dist < 0.6f)
                {
                    if (d > bestDepth)
                    {
                        bestDepth = d;
                        bestX = cx;
                    }
                }
                else if (dist < missDist)
                {
                    missDist = dist;
                    missX = cx;
                    missDepth = d;
                }
                else if (XMath.Abs(dist - missDist) < 0.1f && d < missDepth)
                {
                    missX = cx;
                    missDepth = d;
                }
            }
            return bestDepth > -1f ? bestX : missX;
        }

        /// <summary>
        /// Anaglyph: left eye = source pixel, right eye = <see cref="RightEyeSourceX"/>, mixed by a Dubois profile
        /// (brightness, contrast, gamma + 3x6 matrix) from <paramref name="profiles"/> at <paramref name="profileOffset"/>.
        /// </summary>
        public static void AnaglyphKernel(Index2D index,
            ArrayView1D<int, Stride1D.Dense> rgba,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<float, Stride1D.Dense> profiles,
            ArrayView1D<int, Stride1D.Dense> output,
            ArrayView1D<float, Stride1D.Dense> minMax,
            int width, int directDepth, float separationPx, float convergence, int profileOffset)
        {
            int x = index.X, y = index.Y;
            ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
            int row = y * width;
            int l = rgba[row + x];
            int r = rgba[row + RightEyeSourceX(x, row, width, depth, directDepth, a, b, separationPx, convergence)];

            float lr = Channel(l, 0), lg = Channel(l, 8), lb = Channel(l, 16);
            float rr = Channel(r, 0), rg = Channel(r, 8), rb = Channel(r, 16);
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

        /// <summary>Slots <see cref="FlickerKernel"/> spreads its atomic sums over (summed on the host).</summary>
        public const int FlickerSlots = 1024;

        /// <summary>
        /// DIAGNOSTIC (the "flicker" sweep): temporal instability of the DISPLAYED disparity. Per pixel, the frame's
        /// disparity d in [0,1] (same mapping the 3D kernels use, so a per-frame min/max renormalization counts too) is
        /// compared with the previous frame's: |d - prev| is added into <paramref name="acc"/>[i % FlickerSlots], and d is
        /// stored as the next frame's prev. Mean |delta d| per pixel per frame = sum(acc) / pixels.
        /// </summary>
        public static void FlickerKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> depth,
            ArrayView1D<float, Stride1D.Dense> minMax,
            ArrayView1D<float, Stride1D.Dense> prev,
            ArrayView1D<float, Stride1D.Dense> acc,
            int directDepth, int hasPrev)
        {
            ScaleBias(minMax[0], minMax[1], directDepth, out float a, out float b);
            float d = Disparity(depth[index], directDepth, a, b);
            if (hasPrev != 0) Atomic.Add(ref acc[index % FlickerSlots], XMath.Abs(d - prev[index]));
            prev[index] = d;
        }
    }
}
