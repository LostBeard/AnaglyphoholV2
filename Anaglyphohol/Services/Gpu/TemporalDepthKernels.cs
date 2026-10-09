using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System.Runtime.CompilerServices;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// Video depth: temporal filtering of the disparity map, at MODEL resolution, on the accelerator.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED 2026-10-02 (DAv3 Small, 168x98, live-action clip): where the picture does not change, the displayed
    /// disparity still moves ~1.5% of its range per frame (median) and 6-9% one frame in ten - the model's own per-frame
    /// noise. Smoothing the normalization range (ThreeDKernels.SmoothRangeKernel) removes part of the big jumps but not
    /// the per-pixel jitter; this filter is for that.</para>
    /// <para><see cref="TemporalFilterKernel"/> is TJ's GMZPlayer DepthRollingWindow.FilterDepthRollingWindow (the code
    /// SpawnDev.ILGPU grew out of) on a single RING buffer of the last <see cref="RingFrames"/> maps instead of 20
    /// separate views - 20 storage bindings exceed WebGPU's per-shader limit, and ILGPU would coalesce them by copying
    /// every frame. Values are in units u = 1 + 100 * disparity, so 0 still means "no sample" (an unfilled slot) and the
    /// thresholds read as percent of the depth range.</para>
    /// </remarks>
    public static class TemporalDepthKernels
    {
        /// <summary>Frames in the ring (DepthRollingWindow kept 20).</summary>
        public const int RingFrames = 20;

        /// <summary>Raw model depth -> u = 1 + 100 * disparity, with disparity in [0,1] (1 = near) from the given range.</summary>
        public static void DisparityKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> rawDepth,
            ArrayView1D<float, Stride1D.Dense> range,
            ArrayView1D<float, Stride1D.Dense> u,
            int directDepth)
        {
            ThreeDKernels.ScaleBias(range[0], range[1], directDepth, out float a, out float b);
            u[index] = 1f + 100f * ThreeDKernels.Disparity(rawDepth[index], directDepth, a, b);
        }

        /// <summary>One Euro low-pass smoothing factor for a cutoff in cycles per FRAME (dt = 1 frame).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float OneEuroAlpha(float cutoff)
        {
            float tau = 1f / (2f * XMath.PI * cutoff);
            return 1f / (1f + tau);
        }

        /// <summary>
        /// Per-pixel One Euro filter (Casiez et al., CHI 2012) on the disparity map, in u units: an adaptive low-pass
        /// whose cutoff rises with the (filtered) rate of change - still regions are smoothed hard (no jitter), moving
        /// ones follow with little lag. State: the filtered value and the filtered derivative, two floats per pixel.
        /// Cutoffs are in cycles per frame. <paramref name="reset"/> != 0 starts over from this frame.
        /// </summary>
        public static void OneEuroKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> u,
            ArrayView1D<float, Stride1D.Dense> xHat,
            ArrayView1D<float, Stride1D.Dense> dxHat,
            ArrayView1D<float, Stride1D.Dense> output,
            int reset, float minCutoff, float beta, float dCutoff)
        {
            float x = u[index];
            if (reset != 0)
            {
                xHat[index] = x;
                dxHat[index] = 0f;
                output[index] = x;
                return;
            }
            float prev = xHat[index];
            float dx = x - prev;
            float aD = OneEuroAlpha(dCutoff);
            float edx = dxHat[index] + aD * (dx - dxHat[index]);
            float cutoff = minCutoff + beta * XMath.Abs(edx);
            float a = OneEuroAlpha(cutoff);
            float filtered = prev + a * (x - prev);
            xHat[index] = filtered;
            dxHat[index] = edx;
            output[index] = filtered;
        }

        /// <summary>Copies the current map into ring slot <paramref name="slot"/>.</summary>
        public static void RingWriteKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> u,
            ArrayView1D<float, Stride1D.Dense> ring,
            int slot, int plane)
        {
            ring[slot * plane + index] = u[index];
        }

        /// <summary>
        /// Bilinear upsample of a model-resolution map in u units to the frame, as DISPARITY in [0,1]
        /// (what the 3D kernels read with a [0,1] range and directDepth = 0).
        /// </summary>
        public static void UpsampleKernel(Index2D index,
            ArrayView1D<float, Stride1D.Dense> src, int srcW, int srcH,
            ArrayView1D<float, Stride1D.Dense> dst, int dstW, int dstH)
        {
            float fx = (index.X + 0.5f) * srcW / dstW - 0.5f;
            float fy = (index.Y + 0.5f) * srcH / dstH - 0.5f;
            if (fx < 0f) fx = 0f;
            if (fy < 0f) fy = 0f;
            int x0 = (int)fx, y0 = (int)fy;
            if (x0 > srcW - 1) x0 = srcW - 1;
            if (y0 > srcH - 1) y0 = srcH - 1;
            int x1 = x0 + 1 < srcW ? x0 + 1 : x0;
            int y1 = y0 + 1 < srcH ? y0 + 1 : y0;
            float tx = fx - x0, ty = fy - y0;
            float a = src[y0 * srcW + x0], b = src[y0 * srcW + x1];
            float c = src[y1 * srcW + x0], d = src[y1 * srcW + x1];
            float u = (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
            float disp = (u - 1f) * 0.01f;
            dst[index.Y * dstW + index.X] = disp < 0f ? 0f : (disp > 1f ? 1f : disp);
        }

        // ── DepthRollingWindow port ──────────────────────────────────────────────────────────────────────────
        // frameIdx 0 = most recent; the slot of frame t is (head - 1 - t) mod RingFrames. Frames not yet written read 0.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float SampleDepth(int pixelX, int pixelY, int frameIdx, ArrayView1D<float, Stride1D.Dense> ring,
            int head, int filled, int width, int height)
        {
            if (pixelX < 0 || pixelX >= width || pixelY < 0 || pixelY >= height || frameIdx < 0 || frameIdx >= filled)
                return 0f;
            int slot = (head - 1 - frameIdx + 2 * RingFrames) % RingFrames;
            return ring[slot * (width * height) + pixelY * width + pixelX];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ComputeTemporalGradient(int x, int y, int frameIdx, ArrayView1D<float, Stride1D.Dense> ring,
            int head, int filled, int width, int height)
        {
            if (frameIdx >= RingFrames - 1) return 0f;
            float currentDepth = SampleDepth(x, y, frameIdx, ring, head, filled, width, height);
            float nextFrameDepth = SampleDepth(x, y, frameIdx + 1, ring, head, filled, width, height);
            if (currentDepth <= 0f || nextFrameDepth <= 0f) return 0f;
            return nextFrameDepth - currentDepth;
        }

        static float DetectEdges(int x, int y, float currentDepth, ArrayView1D<float, Stride1D.Dense> ring,
            int head, int filled, int width, int height, float edgeThreshold, float temporalDecay)
        {
            if (currentDepth == 0f) return 0f;
            float maxWeightedDiff = 0f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    float neighborDepth = SampleDepth(x + dx, y + dy, 0, ring, head, filled, width, height);
                    if (neighborDepth > 0f)
                        maxWeightedDiff = XMath.Max(maxWeightedDiff, XMath.Abs(currentDepth - neighborDepth) * 1.5f);
                }
            }
            for (int t = 1; t < 10; t++)
            {
                float timeWeight = XMath.Exp(-((float)t) / temporalDecay);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float neighborDepth = SampleDepth(x + dx, y + dy, t, ring, head, filled, width, height);
                        if (neighborDepth > 0f)
                            maxWeightedDiff = XMath.Max(maxWeightedDiff, timeWeight * XMath.Abs(currentDepth - neighborDepth));
                    }
                }
            }
            return XMath.Min(maxWeightedDiff / edgeThreshold, 1f);
        }

        static float DetectMotion(int x, int y, float currentDepth, ArrayView1D<float, Stride1D.Dense> ring,
            int head, int filled, int width, int height, float motionThreshold, float temporalDecay)
        {
            if (currentDepth == 0f) return 0f;
            float maxWeightedMotion = 0f;
            int validSamples = 0;
            float currentGradient = ComputeTemporalGradient(x, y, 0, ring, head, filled, width, height);
            for (int t = 1; t < 10; t++)
            {
                float timeWeight = XMath.Exp(-((float)t) / temporalDecay);
                float temporalDepth = SampleDepth(x, y, t, ring, head, filled, width, height);
                if (temporalDepth > 0f)
                {
                    float depthDiff = XMath.Abs(temporalDepth - currentDepth);
                    float historicalGradient = ComputeTemporalGradient(x, y, t, ring, head, filled, width, height);
                    float gradientDiff = 0f;
                    if (historicalGradient != 0f && currentGradient != 0f)
                    {
                        gradientDiff = XMath.Abs(historicalGradient - currentGradient);
                        float minGradient = XMath.Min(XMath.Abs(historicalGradient), XMath.Abs(currentGradient));
                        if (minGradient > 0.001f)
                        {
                            gradientDiff /= minGradient;
                            gradientDiff = XMath.Min(gradientDiff, 5f);
                        }
                    }
                    float combinedDiff = depthDiff * (1f + gradientDiff * 0.5f);
                    maxWeightedMotion = XMath.Max(maxWeightedMotion, timeWeight * combinedDiff);
                    validSamples++;
                }
                if (t < 3)
                {
                    for (int dy = -1; dy <= 1; dy += 2)
                    {
                        for (int dx = -1; dx <= 1; dx += 2)
                        {
                            float neighborDepth = SampleDepth(x + dx, y + dy, t, ring, head, filled, width, height);
                            if (neighborDepth > 0f)
                            {
                                maxWeightedMotion = XMath.Max(maxWeightedMotion, timeWeight * XMath.Abs(neighborDepth - currentDepth) * 0.8f);
                                validSamples++;
                            }
                        }
                    }
                }
            }
            if (validSamples == 0) return 0f;
            float normalizedMotion = maxWeightedMotion / motionThreshold;
            return XMath.Min(normalizedMotion * normalizedMotion, 1f);
        }

        static int FindKeyFrameIndex(int x, int y, float currentDepth, ArrayView1D<float, Stride1D.Dense> ring,
            int head, int filled, int width, int height)
        {
            if (currentDepth <= 0f) return 0;
            int bestFrameIdx = 0;
            float lowestInstability = float.MaxValue;
            for (int t = 0; t < 10; t++)
            {
                float frameDepth = SampleDepth(x, y, t, ring, head, filled, width, height);
                if (frameDepth <= 0f) continue;
                float instability = 0f;
                int validNeighbors = 0;
                for (int offset = -1; offset <= 1; offset += 2)
                {
                    int neighborT = t + offset;
                    if (neighborT >= 0 && neighborT < 10)
                    {
                        float neighborDepth = SampleDepth(x, y, neighborT, ring, head, filled, width, height);
                        if (neighborDepth > 0f)
                        {
                            instability += XMath.Abs(frameDepth - neighborDepth);
                            validNeighbors++;
                        }
                    }
                }
                if (validNeighbors > 0)
                {
                    instability /= validNeighbors;
                    instability *= 1f + 0.1f * t;
                    if (instability < lowestInstability)
                    {
                        lowestInstability = instability;
                        bestFrameIdx = t;
                    }
                }
            }
            return bestFrameIdx;
        }

        /// <summary>
        /// GMZPlayer's FilterDepthRollingWindow on the ring (see the class remarks): per pixel, edge and motion detection
        /// decide how much to trust the current frame; stable pixels blend a temporal-stability keyframe and up to 15
        /// earlier frames, weighted by temporal decay, gradient consistency and depth similarity, with an anti-ghosting
        /// clamp toward the current frame. Output in u units (see <see cref="UpsampleKernel"/>).
        /// </summary>
        public static void TemporalFilterKernel(Index1D index,
            ArrayView1D<float, Stride1D.Dense> ring,
            ArrayView1D<float, Stride1D.Dense> output,
            int head, int filled, int width, int height,
            float edgeThreshold, float motionThreshold, float temporalDecay,
            float similarityDelta, float similaritySigma, float spatialTemporalRadius, float maxDeviationBase)
        {
            int x = index % width;
            int y = index / width;
            float currentDepth = SampleDepth(x, y, 0, ring, head, filled, width, height);
            if (currentDepth == 0f)
            {
                output[index] = 0f;
                return;
            }

            float edgeWeight = DetectEdges(x, y, currentDepth, ring, head, filled, width, height, edgeThreshold, temporalDecay);
            float motionWeight = DetectMotion(x, y, currentDepth, ring, head, filled, width, height, motionThreshold, temporalDecay);
            int keyframeIdx = FindKeyFrameIndex(x, y, currentDepth, ring, head, filled, width, height);
            float keyframeDepth = SampleDepth(x, y, keyframeIdx, ring, head, filled, width, height);
            float currentFrameConfidence = XMath.Max(edgeWeight, motionWeight);

            float gradientConsistency = 1f;
            if (currentFrameConfidence < 0.5f)
            {
                float gradientSum = 0f, gradientSqSum = 0f;
                int gradientSamples = 0;
                for (int t = 0; t < 4; t++)
                {
                    float gradient = ComputeTemporalGradient(x, y, t, ring, head, filled, width, height);
                    if (gradient != 0f)
                    {
                        gradientSum += gradient;
                        gradientSqSum += gradient * gradient;
                        gradientSamples++;
                    }
                }
                if (gradientSamples >= 2)
                {
                    float meanGradient = gradientSum / gradientSamples;
                    float gradientVariance = (gradientSqSum / gradientSamples) - (meanGradient * meanGradient);
                    float normalizedVariance = gradientVariance / (0.01f + XMath.Abs(meanGradient));
                    gradientConsistency = XMath.Exp(-normalizedVariance / 0.5f);
                }
            }

            float filteredDepth;
            if (currentFrameConfidence > 0.8f)
            {
                filteredDepth = currentDepth;
            }
            else
            {
                float adaptiveDelta = similarityDelta * (1f + edgeWeight * 0.5f);
                float adaptiveSigma = similaritySigma * (1f + motionWeight * 0.5f);
                float keyframeWeight = 3f * (1f - currentFrameConfidence) * gradientConsistency;
                float weightedSum = keyframeDepth * keyframeWeight;
                float totalWeight = keyframeWeight;
                float currentFrameWeight = 1f + 0.5f * currentFrameConfidence;
                weightedSum += currentDepth * currentFrameWeight;
                totalWeight += currentFrameWeight;

                int maxHistoryFrames = (int)(5 + (1f - currentFrameConfidence) * 10f * gradientConsistency);
                maxHistoryFrames = XMath.Min(maxHistoryFrames, 15);
                float currentGradient = ComputeTemporalGradient(x, y, 0, ring, head, filled, width, height);
                for (int t = 1; t < maxHistoryFrames; t++)
                {
                    if (t == keyframeIdx) continue;
                    float frameDepth = SampleDepth(x, y, t, ring, head, filled, width, height);
                    if (frameDepth <= 0f) continue;

                    float temporalWeight = XMath.Exp(-(float)t / temporalDecay);
                    float historicalGradient = ComputeTemporalGradient(x, y, t, ring, head, filled, width, height);
                    float gradientWeight = 1f;
                    if (currentGradient != 0f && historicalGradient != 0f)
                    {
                        float gradientDiff = XMath.Abs(historicalGradient - currentGradient);
                        float minGradient = XMath.Min(XMath.Abs(historicalGradient), XMath.Abs(currentGradient));
                        if (minGradient > 0.001f)
                        {
                            float relativeGradientDiff = XMath.Min(gradientDiff / minGradient, 5f);
                            gradientWeight = XMath.Exp(-relativeGradientDiff / 2f);
                        }
                    }
                    temporalWeight *= gradientWeight;

                    float depthDiff = XMath.Abs(frameDepth - currentDepth);
                    float similarityWeight = depthDiff < adaptiveDelta
                        ? 1f - (depthDiff / adaptiveDelta) * 0.2f
                        : 0.8f * XMath.Exp(-(depthDiff - adaptiveDelta) / adaptiveSigma);

                    float frameWeight = temporalWeight * similarityWeight;
                    if (XMath.Abs(t - keyframeIdx) <= 2) frameWeight *= 1.5f;

                    if (t <= 2 && currentFrameConfidence < 0.5f)
                    {
                        int radius = (int)spatialTemporalRadius;
                        if (radius < 1) radius = 1;
                        float spatialBoost = 0f;
                        int validSamples = 0;
                        for (int dy = -radius; dy <= radius; dy += radius)
                        {
                            for (int dx = -radius; dx <= radius; dx += radius)
                            {
                                if (dx == 0 && dy == 0) continue;
                                float neighborDepth = SampleDepth(x + dx, y + dy, t, ring, head, filled, width, height);
                                if (neighborDepth > 0f)
                                {
                                    float neighborToCurrentDiff = XMath.Abs(neighborDepth - currentDepth);
                                    if (neighborToCurrentDiff < depthDiff)
                                    {
                                        spatialBoost += 1f - (neighborToCurrentDiff / (depthDiff + 0.001f));
                                        validSamples++;
                                    }
                                }
                            }
                        }
                        if (validSamples > 0)
                        {
                            spatialBoost /= validSamples;
                            frameWeight *= 1f + spatialBoost * (1f - edgeWeight);
                        }
                    }
                    weightedSum += frameWeight * frameDepth;
                    totalWeight += frameWeight;
                }

                filteredDepth = weightedSum / totalWeight;

                // Anti-ghosting: do not stray too far from the current frame (GMZPlayer: 0.02 * (1 - confidence)).
                float maxDeviation = maxDeviationBase * (1f - currentFrameConfidence);
                float deviation = XMath.Abs(filteredDepth - currentDepth);
                if (deviation > maxDeviation)
                {
                    float blendFactor = maxDeviation / deviation;
                    filteredDepth = currentDepth * (1f - blendFactor) + filteredDepth * blendFactor;
                }
                if (gradientConsistency < 0.3f)
                {
                    float blendRatio = gradientConsistency / 0.3f;
                    filteredDepth = currentDepth * (1f - blendRatio) + filteredDepth * blendRatio;
                }
            }
            output[index] = filteredDepth;
        }
    }
}
