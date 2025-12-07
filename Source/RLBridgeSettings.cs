using Celeste.Mod;

namespace RLBridge
{
    public class RLBridgeSettings : EverestModuleSettings
    {
        [SettingName("TCP Port (restart to apply)")]
        public int TcpPort { get; set; } = 17653;

        [SettingName("Pause TimeRate (0 = freeze between steps)")]
        public float PauseTimeRate { get; set; } = 1.0f;

        [SettingName("New room reward")]
        public float NewRoomReward { get; set; } = 10.0f;

        [SettingName("Revisit room penalty")]
        public float RevisitRoomPenalty { get; set; } = -1.0f;

        [SettingName("Use cell tracking (grid exploration)")]
        public bool UseCellTracking { get; set; } = true;

        [SettingName("Cell size (pixels per grid cell)")]
        public float CellSize { get; set; } = 64.0f;

        [SettingName("New cell reward")]
        public float NewCellReward { get; set; } = 0.1f;

        [SettingName("Revisit cell penalty")]
        public float RevisitCellPenalty { get; set; } = -0.05f;

        [SettingName("Per-step cost")]
        public float StepCost { get; set; } = -0.01f;

        [SettingName("Use distance reward")]
        public bool UseDistanceReward { get; set; } = false;

        [SettingName("Progress scale (if distance enabled)")]
        public float ProgressScale { get; set; } = 0.01f;

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
