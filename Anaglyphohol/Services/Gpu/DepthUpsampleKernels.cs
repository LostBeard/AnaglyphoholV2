using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System.Runtime.CompilerServices;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// Model-resolution depth -> frame resolution, EDGE-AWARE: joint bilateral upsampling (Kopf, Cohen, Lischinski,
    /// Uyttendaele, "Joint Bilateral Upsampling", SIGGRAPH 2007), guided by the frame's own colours.
    /// </summary>
    /// <remarks>
    /// The depth models run at 168..672 px on the long side, so every depth sample covers 3..11 frame pixels per axis.
    /// Plain bilinear upsampling (<see cref="TemporalDepthKernels.UpsampleKernel"/>) blends a near object's depth into
    /// the background for several pixels past its outline, and viewColor2DZ then shifts that halo with the object: the
    /// soft, smeared silhouettes TJ wanted gone (2026-10-05, "edge-aware depth upscaling ... Please do").
    /// Here every output pixel averages the low-res depth samples around it weighted by distance AND by how close the
    /// colour that sample stands for (<see cref="GuideKernel"/>: the box average of the frame pixels it covers - what
    /// the model saw) is to the output pixel's own colour, so a depth edge lands on the colour edge.
    /// Input and output follow <see cref="TemporalDepthKernels.UpsampleKernel"/>: source in u units (1 + 100 * disparity),
    /// output disparity in [0, 1].
    /// </remarks>
    public static class DepthUpsampleKernels
    {
        /// <summary>Low-res taps per axis: a 5x5 window centred on the nearest low-res sample.</summary>
        public const int Radius = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Channel(int packed, int shift) => ((packed >> shift) & 0xFF) * (1f / 255f);

        /// <summary>
        /// One thread per LOW-RES pixel: the mean RGB of the frame pixels it covers, into guide[3 * i + c].
        /// </summary>
        public static void GuideKernel(Index1D index,
            ArrayView1D<int, Stride1D.Dense> rgba, int frameW, int frameH,
            ArrayView1D<float, Stride1D.Dense> guide, int lowW, int lowH)
        {
            int lx = index % lowW, ly = index / lowW;
            // int is enough (<= 672 x 3840) and WGSL has no native 64-bit integers
            int x0 = lx * frameW / lowW, x1 = (lx + 1) * frameW / lowW;
            int y0 = ly * frameH / lowH, y1 = (ly + 1) * frameH / lowH;
            if (x1 <= x0) x1 = x0 + 1;
            if (y1 <= y0) y1 = y0 + 1;
            if (x1 > frameW) x1 = frameW;
            if (y1 > frameH) y1 = frameH;
            float r = 0f, g = 0f, b = 0f;
            for (int y = y0; y < y1; y++)
            {
                int row = y * frameW;
                for (int x = x0; x < x1; x++)
                {
                    int p = rgba[row + x];
                    r += Channel(p, 0); g += Channel(p, 8); b += Channel(p, 16);
                }
            }
            float n = 1f / ((x1 - x0) * (y1 - y0));
            guide[3 * index] = r * n;
            guide[3 * index + 1] = g * n;
            guide[3 * index + 2] = b * n;
        }

        /// <summary>
        /// One thread per FRAME pixel: joint bilateral upsample of <paramref name="src"/> (u units, lowW x lowH) into
        /// <paramref name="dst"/> (disparity [0,1], frameW x frameH). Weight of low-res sample q for frame pixel p:
        /// exp(-|p' - q|^2 / 2 sigmaSpatial^2) * exp(-|I(p) - G(q)|^2 / 2 sigmaRange^2), p' = p in low-res coordinates,
        /// I = frame colour, G = <see cref="GuideKernel"/> colour (RGB in [0,1]). If no colour in the window is close
        /// enough to carry weight (a thin feature the model never resolved), the pixel falls back to bilinear.
        /// </summary>
        public static void JointBilateralUpsampleKernel(Index2D index,
            ArrayView1D<float, Stride1D.Dense> src, int lowW, int lowH,
            ArrayView1D<float, Stride1D.Dense> guide,
            ArrayView1D<int, Stride1D.Dense> rgba,
            ArrayView1D<float, Stride1D.Dense> dst, int frameW, int frameH,
            float sigmaSpatial, float sigmaRange)
        {
            int x = index.X, y = index.Y;
            float fx = (x + 0.5f) * lowW / frameW - 0.5f;
            float fy = (y + 0.5f) * lowH / frameH - 0.5f;
            int cx = (int)XMath.Floor(fx + 0.5f), cy = (int)XMath.Floor(fy + 0.5f);
            int p = rgba[y * frameW + x];
            float pr = Channel(p, 0), pg = Channel(p, 8), pb = Channel(p, 16);
            float invS = 1f / (2f * sigmaSpatial * sigmaSpatial), invR = 1f / (2f * sigmaRange * sigmaRange);
            float sum = 0f, wsum = 0f;
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                int qy = cy + dy;
                if (qy < 0 || qy >= lowH) continue;
                float ddy = qy - fy;
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int qx = cx + dx;
                    if (qx < 0 || qx >= lowW) continue;
                    float ddx = qx - fx;
                    int q = qy * lowW + qx;
                    float er = pr - guide[3 * q], eg = pg - guide[3 * q + 1], eb = pb - guide[3 * q + 2];
                    float w = XMath.Exp(-(ddx * ddx + ddy * ddy) * invS - (er * er + eg * eg + eb * eb) * invR);
                    sum += w * src[q];
                    wsum += w;
                }
            }
            float u;
            if (wsum > 1e-6f) u = sum / wsum;
            else
            {
                // bilinear, exactly as TemporalDepthKernels.UpsampleKernel
                float bx = fx < 0f ? 0f : fx, by = fy < 0f ? 0f : fy;
                int x0 = (int)bx, y0 = (int)by;
                if (x0 > lowW - 1) x0 = lowW - 1;
                if (y0 > lowH - 1) y0 = lowH - 1;
                int x1 = x0 + 1 < lowW ? x0 + 1 : x0, y1 = y0 + 1 < lowH ? y0 + 1 : y0;
                float tx = bx - x0, ty = by - y0;
                float a = src[y0 * lowW + x0], b = src[y0 * lowW + x1], c = src[y1 * lowW + x0], d = src[y1 * lowW + x1];
                u = (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
            }
            float disp = (u - 1f) * 0.01f;
            dst[y * frameW + x] = disp < 0f ? 0f : (disp > 1f ? 1f : disp);
        }
    }
}
