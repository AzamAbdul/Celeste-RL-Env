using Celeste.Mod;

namespace RLBridge
{
    public class RLBridgeSettings : EverestModuleSettings
    {
        [SettingName("TCP Port (restart to apply)")]
        public int TcpPort { get; set; } = 17653;

        [SettingName("Speed multiplier (1=normal, 10=10x speed)")]
        [SettingRange(1, 100)]
        public int SpeedMultiplier { get; set; } = 10;

        [SettingName("Pause TimeRate (0 = freeze between steps)")]
        public float PauseTimeRate { get; set; } = 1.0f;

        [SettingName("New room reward")]
        public float NewRoomReward { get; set; } = 100.0f;

        [SettingName("Revisit room penalty")]
        public float RevisitRoomPenalty { get; set; } = -1.0f;

        [SettingName("Use cell tracking (grid exploration)")]
        public bool UseCellTracking { get; set; } = true;

        [SettingName("Cell size (pixels per grid cell)")]
        public float CellSize { get; set; } = 64.0f;

        [SettingName("New cell reward")]
        public float NewCellReward { get; set; } = 0.0f;

        [SettingName("Revisit cell penalty")]
        public float RevisitCellPenalty { get; set; } = -0.05f;

        [SettingName("Penalize idling in same cell")]
        public bool PenalizeIdling { get; set; } = true;

        [SettingName("Idle threshold (frames in same cell)")]
        public int IdleThreshold { get; set; } = 10;

        [SettingName("Idle penalty rate (per frame over threshold)")]
        public float IdlePenaltyRate { get; set; } = -0.01f;

        [SettingName("Per-step cost")]
        public float StepCost { get; set; } = -0.01f;

        [SettingName("Death penalty")]
        public float DeathPenalty { get; set; } = -10.0f;

        [SettingName("Stagnation penalty")]
        public float StagnationPenalty { get; set; } = -5.0f;

        [SettingName("Use distance reward")]
        public bool UseDistanceReward { get; set; } = false;

        [SettingName("Progress scale (if distance enabled)")]
        public float ProgressScale { get; set; } = 0.01f;

        [SettingName("Frame capture enabled")]
        public bool CaptureFrames { get; set; } = true;

        [SettingName("Frame width (0 = full resolution)")]
        public int FrameWidth { get; set; } = 640;

        [SettingName("Frame height (0 = full resolution)")]
        public int FrameHeight { get; set; } = 360;

        [SettingName("Frame color mode")]
        public FrameColorMode ColorMode { get; set; } = FrameColorMode.Native;

        [SettingName("Terminate on stagnation")]
        public bool TerminateOnStagnation { get; set; } = true;

        [SettingName("Stagnation threshold (steps without new cell)")]
        public int StagnationThreshold { get; set; } = 10;

        [SettingName("Terminate on max steps")]
        public bool TerminateOnMaxSteps { get; set; } = true;

        [SettingName("Max episode steps")]
        public int MaxEpisodeSteps { get; set; } = 5000;
    }

    public enum FrameColorMode
    {
        RGB,           // 3 channels: Red, Green, Blue
        Grayscale,     // 1 channel: Luminance only
        LuminanceHue,  // 2 channels: Brightness + Color info (best balance)
        YUV,           // 3 channels: Y (luma) + UV (chroma) - efficient for video
        Native         // 3 channels: Raw RGB at native resolution (no downsampling)
    }
}
