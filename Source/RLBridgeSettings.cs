using Celeste.Mod;

namespace RLBridge
{
    public class RLBridgeSettings : EverestModuleSettings
    {
        [SettingName("TCP Port (restart to apply)")]
        public int TcpPort { get; set; } = 17653;

        [SettingName("Pause TimeRate (0 = freeze between steps)")]
        public float PauseTimeRate { get; set; } = 1.0f;

        [SettingName("Progress scale (reward multiplier)")]
        public float ProgressScale { get; set; } = 0.01f;

        [SettingName("Per-step cost")]
        public float StepCost { get; set; } = 0.001f;

        [SettingName("Frame capture enabled")]
        public bool CaptureFrames { get; set; } = true;

        [SettingName("Frame width (0 = full resolution)")]
        public int FrameWidth { get; set; } = 160;

        [SettingName("Frame height (0 = full resolution)")]
        public int FrameHeight { get; set; } = 120;

        [SettingName("Frame color mode")]
        public FrameColorMode ColorMode { get; set; } = FrameColorMode.LuminanceHue;
    }

    public enum FrameColorMode
    {
        RGB,           // 3 channels: Red, Green, Blue
        Grayscale,     // 1 channel: Luminance only
        LuminanceHue,  // 2 channels: Brightness + Color info (best balance)
        YUV            // 3 channels: Y (luma) + UV (chroma) - efficient for video
    }
}
