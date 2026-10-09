namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// Dubois anaglyph profiles, verbatim from SpawnDev.BlazorJS.MultiView's RenderAnaglyph
    /// (https://github.com/dolphin-emu/dolphin/blob/master/Data/Sys/Shaders/Anaglyph/dubois.glsl).
    /// Layout per profile (<see cref="ThreeDKernels.ProfileStride"/> floats): brightness, contrast, gamma, then
    /// red (l.r l.g l.b r.r r.g r.b), green (same), blue (same).
    /// </summary>
    public static class AnaglyphProfiles
    {
        public const int RedCyan = 0;
        public const int GreenMagenta = 1;

        public static readonly string[] Names = { "Red Cyan", "Green Magenta" };

        /// <summary>All profiles back to back; profile i starts at i * <see cref="ThreeDKernels.ProfileStride"/>.</summary>
        public static readonly float[] Data =
        {
            // Red Cyan
            0.0f, 0.0f, 0.0f,
            0.456f, 0.500f, 0.176f, -0.043f, -0.088f, -0.002f,
            -0.040f, -0.038f, -0.016f, 0.378f, 0.734f, -0.018f,
            -0.015f, -0.021f, -0.005f, -0.072f, -0.113f, 1.226f,
            // Green Magenta
            0.0f, 0.0f, 0.0f,
            -0.062f, -0.158f, -0.039f, 0.529f, 0.705f, 0.024f,
            0.284f, 0.668f, 0.143f, -0.016f, -0.015f, -0.065f,
            -0.015f, -0.027f, 0.021f, 0.009f, 0.075f, 0.937f,
        };

        public static int Count => Data.Length / ThreeDKernels.ProfileStride;
    }
}
