// RLBridge/Source/RLBridgeModule.cs
using Celeste.Mod;  // << needed for EverestModule, EverestModuleSettings, Logger, etc.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Celeste;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace RLBridge
{
    public class RLBridgeModule : EverestModule
    {
        public static RLBridgeModule Instance;

        public override Type SettingsType => typeof(RLBridgeSettings);
        public static RLBridgeSettings Settings => (RLBridgeSettings)Instance._Settings;

        // Networking
        private JsonLServer server;
        private const int DefaultPort = 17653; // localhost only
        private readonly object stateLock = new object();

        // Step/input scheduling
        private int framesToRun = 0;
        private InputState stagedInput = new InputState();
        private bool pendingReset = false;
        private int resetSlot = 0;
        private int resetDelayFrames = 0;  // Delay reset to let scene stabilize
        private bool responseSent = true;  // Track if we've sent response for current step
        private float accumulatedReward = 0f;  // Accumulate reward across action repeat frames

        // Progress tracking for rewards (you can customize)
        private float maxXProgress = 0f;  // Maximum X position reached (rightward progress)
        private float startX = 0f;        // Starting X position for the episode

        // Room-based progression tracking
        private HashSet<string> visitedRooms = new HashSet<string>();
        private string lastRoom = "";

        // Cell-based exploration tracking (grid within rooms)
        private HashSet<string> visitedCells = new HashSet<string>();
        private string lastCellKey = "";
        private int framesInCurrentCell = 0;  // Track how long in same cell

        // Episode termination tracking
        private int stepsSinceLastNewCell = 0;
        private int episodeSteps = 0;

        // Death tracking (to apply penalty only once)
        private bool wasDeadLastFrame = false;

        // Interop helpers (bound at runtime via reflection)
        private SpeedrunToolInterop srt;


        // Reflection fields for VirtualButton manipulation
        private FieldInfo buttonBuffered;
        private FieldInfo buttonCheck;

        public RLBridgeModule()
        {
            Instance = this;
        }

        public override void Load()
        {
            // Hook the level update
            On.Celeste.Level.Update += Level_Update;

            // Bind optional interops
            srt = new SpeedrunToolInterop().Bind();

            // Get VirtualButton internal fields via reflection
            Type vbType = typeof(VirtualButton);
            buttonBuffered = vbType.GetField("bufferCounter", BindingFlags.Instance | BindingFlags.NonPublic);

            // Try to find the bool override field - name might vary by Celeste version
            buttonCheck = vbType.GetField("DebugOverridePressed", BindingFlags.Instance | BindingFlags.Public);

            // Validate the type is Nullable<bool>
            if (buttonCheck != null && buttonCheck.FieldType != typeof(bool?))
            {
                Logger.Log(LogLevel.Warn, "RLBridge", $"Found DebugOverridePressed but wrong type: {buttonCheck.FieldType.Name}, expected Nullable<Boolean>");
                buttonCheck = null;
            }

            if (buttonBuffered == null || buttonCheck == null)
            {
                Logger.Log(LogLevel.Error, "RLBridge", "Could not find VirtualButton internal fields");

                // List all fields for debugging
                Logger.Log(LogLevel.Error, "RLBridge", "Available VirtualButton fields:");
                foreach (var field in vbType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Logger.Log(LogLevel.Error, "RLBridge", $"  - {field.Name} ({field.FieldType.FullName})");
                }

                Logger.Log(LogLevel.Error, "RLBridge", $"Field lookup results: bufferCounter={buttonBuffered != null}, DebugOverridePressed={buttonCheck != null}");
            }
            else
            {
                Logger.Log(LogLevel.Info, "RLBridge", "Successfully found VirtualButton fields: bufferCounter and DebugOverridePressed");
            }

            // Start TCP server (localhost only)
            server = new JsonLServer(IPAddress.Loopback, DefaultPort, HandleMessage);
            server.Start();
            Logger.Log(LogLevel.Error, "RLBridge", $"Started JSONL server at 127.0.0.1:{DefaultPort}");
        }

        public override void Unload()
        {
            On.Celeste.Level.Update -= Level_Update;
            try { server?.Dispose(); } catch { }
        }

        private void Level_Update(On.Celeste.Level.orig_Update orig, Level self)
        {
            try
            {
                // Apply speed multiplier for faster training
                Engine.TimeRate = Settings.SpeedMultiplier;

                // Check if game is in a stable state for RL operations
                bool isStable = IsGameStateStable(self);

                // Handle reset with delay to let scene stabilize
                if (pendingReset)
                {
                    if (resetDelayFrames > 0)
                    {
                        resetDelayFrames--;
                        // Clear input during delay to prevent interference
                        ClearInput();
                        // Still run game update during delay
                        orig(self);
                        return;
                    }

                    // Delay complete, execute reset
                    pendingReset = false;

                    // Clear any pending input state before reset
                    framesToRun = 0;
                    stagedInput = new InputState();
                    ClearInput();
                    accumulatedReward = 0f;

                    // Try Speedrun Tool savestate load; otherwise restart the chapter
                    if (srt.IsAvailable && resetSlot >= 0 && srt.LoadSlot(resetSlot))
                    {
                        Logger.Log(LogLevel.Info, "RLBridge", $"Loaded savestate slot {resetSlot}");
                    }
                    else
                    {
                        // Restart from the beginning of the chapter
                        Logger.Log(LogLevel.Info, "RLBridge", $"Restarting chapter from beginning");

                        // Set respawn point to the chapter start
                        if (self.Session != null)
                        {
                            // Get the first room in the chapter
                            var area = self.Session.Area;
                            var mapData = AreaData.Areas[area.ID].Mode[(int)area.Mode];

                            if (mapData != null && mapData.MapData != null)
                            {
                                var startRoom = mapData.MapData.StartLevel();
                                if (startRoom != null)
                                {
                                    self.Session.Level = startRoom.Name;
                                    self.Session.RespawnPoint = startRoom.Spawns[0];
                                    self.Session.FirstLevel = true;
                                }
                            }
                        }

                        // Reload with the updated session
                        self.Reload();
                    }

                    // Reset progress baseline (safely)
                    var p0 = GetValidPlayer(self);
                    if (p0 != null)
                    {
                        startX = p0.X;
                        maxXProgress = p0.X;
                    }

                    // Reset room tracking
                    visitedRooms.Clear();
                    lastRoom = self.Session?.Level ?? "";

                    // Reset cell tracking
                    visitedCells.Clear();
                    lastCellKey = "";
                    framesInCurrentCell = 0;

                    // Reset episode tracking
                    stepsSinceLastNewCell = 0;
                    episodeSteps = 0;
                    wasDeadLastFrame = false;

                    // Mark that we need to send observation after reset
                    responseSent = false;
                }

                // Only apply input and calculate rewards when game is stable
                bool isRunningStep = framesToRun > 0 && isStable;
                if (isRunningStep)
                {
                    ApplyInput(stagedInput);
                    framesToRun--;
                }
                else
                {
                    ClearInput();
                }

                // Run the game update (this advances by one frame; TAS can "gate" deterministically)
                orig(self);

                // Accumulate rewards during action repeat frames (only when stable)
                if (isRunningStep)
                {
                    try
                    {
                        var frameReward = CalculateFrameReward(self);
                        accumulatedReward += frameReward;
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Warn, "RLBridge", $"Failed to calculate reward: {e.Message}");
                    }
                }

                // When we finish a request, send an update back to the client (only when stable!)
                if (framesToRun == 0 && !responseSent && isStable)
                {
                    try
                    {
                        var (obs, done, info) = ReadObsDone(self);
                        server.TrySend(new { obs, reward = accumulatedReward, done, info });
                        responseSent = true;
                        accumulatedReward = 0f;  // Reset for next step

                        // Auto-reset on episode termination (with delay to let scene stabilize)
                        if (done)
                        {
                            pendingReset = true;
                            resetSlot = 0;  // Reset to default savestate
                            resetDelayFrames = 5;  // Wait 5 frames before resetting
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Error, "RLBridge", $"Failed to send observation: {e.Message}");
                        responseSent = true;  // Mark as sent to avoid getting stuck
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "RLBridge", $"Critical error in Level_Update: {e.Message}");
                Logger.Log(LogLevel.Error, "RLBridge", $"Stack trace: {e.StackTrace}");

                // If Python is waiting for a response, send an error response to unblock it
                if (!responseSent && framesToRun >= 0)
                {
                    try
                    {
                        var errorObs = new {
                            px = 0f, py = 0f, vx = 0f, vy = 0f,
                            onGround = false, dashes = 0, stamina = 0f,
                            frame = "", frameWidth = 0, frameHeight = 0
                        };
                        var errorInfo = new {
                            error = true,
                            errorMessage = e.Message,
                            dead = true,
                            reachedExit = false,
                            stagnated = false,
                            maxStepsReached = false,
                            episodeSteps = 0,
                            stepsSinceLastNewCell = 0,
                            room = "",
                            area = "",
                            roomsVisited = 0,
                            cellsVisited = 0,
                            cellX = 0,
                            cellY = 0
                        };
                        server.TrySend(new { obs = errorObs, reward = 0f, done = true, info = errorInfo });
                        responseSent = true;
                    }
                    catch
                    {
                        // Failed to send error response, but at least we tried
                    }
                }

                // Clear state to prevent infinite errors
                framesToRun = 0;
                pendingReset = false;
                resetDelayFrames = 0;
                accumulatedReward = 0f;
                ClearInput();
            }
        }

        // Check if the game state is stable enough for RL operations
        private bool IsGameStateStable(Level lvl)
        {
            if (lvl == null) return false;
            if (lvl.Transitioning) return false;  // Room transition in progress
            if (lvl.Frozen) return false;  // Game is frozen (cutscene, etc.)
            if (lvl.Paused) return false;  // Game is paused

            // Check if tracker exists and is valid
            if (lvl.Tracker == null) return false;

            return true;
        }

        // Safely get player entity only when valid and fully initialized
        private Player GetValidPlayer(Level lvl)
        {
            if (!IsGameStateStable(lvl)) return null;

            Player pl;
            try
            {
                pl = lvl.Tracker.GetEntity<Player>();
            }
            catch
            {
                return null;
            }

            if (pl == null) return null;
            if (pl.Scene == null) return null;

            // Check if player is being removed from scene
            try
            {
                if (pl.Scene.Entities.removing.Contains(pl)) return null;
            }
            catch
            {
                return null;
            }

            // Check if player's collision data is initialized (prevents ClimbBoundsCheck crashes)
            try
            {
                // Try to access Collider - if it throws or is null, player isn't ready
                if (pl.Collider == null) return null;

                // Quick check that we can read position without crashing
                var _ = pl.Position;
            }
            catch
            {
                // Player collision system not ready
                return null;
            }

            return pl;
        }

        // ======== Command handler (JSON-RPC-ish) ========

        private void HandleMessage(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string op = root.GetProperty("op").GetString();

                switch (op)
                {
                    case "ping":
                        server.TrySend(new { ok = true });
                        break;

                    case "reset":
                        resetSlot = root.TryGetProperty("slot", out var s) ? s.GetInt32() : 0;
                        pendingReset = true;
                        resetDelayFrames = 5;  // Wait 5 frames before resetting
                        // Immediate ack; the next Level.Update will send a full obs packet
                        server.TrySend(new { ok = true });
                        break;

                    case "step":
                        int action = root.GetProperty("action").GetInt32();
                        int repeat = root.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
                        stagedInput = DecodeAction(action);
                        framesToRun = Math.Max(1, repeat);
                        responseSent = false;  // Mark that we need to send response
                        // Response will be sent after frames advance in Level_Update
                        break;

                    default:
                        server.TrySend(new { error = $"unknown op: {op}" });
                        break;
                }
            }
            catch (Exception e)
            {
                server.TrySend(new { error = e.Message });
            }
        }

        // ======== Reward calculation (per frame) ========

        private float CalculateFrameReward(Level lvl)
        {
            var pl = GetValidPlayer(lvl);
            string currentRoom = lvl.Session?.Level ?? "";
            float reward = 0f;

            // Increment episode step counter
            episodeSteps++;
            stepsSinceLastNewCell++;

            // Only calculate rewards if player is valid
            if (pl != null)
            {
                // Calculate cell coordinates (divide position by cell size)
                int cellX = (int)(pl.X / Settings.CellSize);
                int cellY = (int)(pl.Y / Settings.CellSize);
                string cellKey = $"{currentRoom}:{cellX},{cellY}";

                // Check for room changes
                if (!string.IsNullOrEmpty(currentRoom) && currentRoom != lastRoom)
                {
                    if (!visitedRooms.Contains(currentRoom))
                    {
                        // New room discovered!
                        visitedRooms.Add(currentRoom);
                        reward += Settings.NewRoomReward;
                        Logger.Log(LogLevel.Info, "RLBridge", $"New room discovered: {currentRoom} (total: {visitedRooms.Count})");
                    }
                    else
                    {
                        // Revisiting a room (backtracking)
                        reward += Settings.RevisitRoomPenalty;
                    }
                    lastRoom = currentRoom;
                }

                // Check for new cell exploration
                if (Settings.UseCellTracking)
                {
                    if (cellKey != lastCellKey)
                    {
                        // Changed cells
                        if (!visitedCells.Contains(cellKey))
                        {
                            // New cell discovered!
                            visitedCells.Add(cellKey);
                            reward += Settings.NewCellReward;
                            stepsSinceLastNewCell = 0;  // Reset stagnation counter
                        }
                        else
                        {
                            // Revisiting a cell (backtracking within room)
                            reward += Settings.RevisitCellPenalty;
                        }
                        lastCellKey = cellKey;
                        framesInCurrentCell = 0;  // Reset duration counter
                    }
                    else
                    {
                        // Still in same cell - increment duration
                        framesInCurrentCell++;

                        // Apply penalty if staying too long in same cell (inversely scaled by speed)
                        int scaledIdleThreshold = Math.Max(1, Settings.IdleThreshold / Settings.SpeedMultiplier);
                        if (Settings.PenalizeIdling && framesInCurrentCell > scaledIdleThreshold)
                        {
                            // Flat penalty per frame (linear, not quadratic)
                            reward += Settings.IdlePenaltyRate;
                        }
                    }
                }

                // Small time penalty to encourage efficiency
                reward += Settings.StepCost;

                // Optional: small distance-based reward for progress within a room
                if (Settings.UseDistanceReward)
                {
                    float currentX = pl.X;
                    float newProgress = Math.Max(currentX, maxXProgress);
                    float progressDelta = newProgress - maxXProgress;
                    maxXProgress = newProgress;
                    reward += progressDelta * Settings.ProgressScale;
                }
            }
            else
            {
                // Player doesn't exist (dead)
                reward = Settings.StepCost;
            }

            // Apply death penalty ONLY ONCE when transitioning to dead state
            bool dead = pl == null || (pl != null && pl.Dead);
            if (dead && !wasDeadLastFrame && Settings.DeathPenalty != 0)
            {
                reward += Settings.DeathPenalty;  // Only applied once!
            }
            wasDeadLastFrame = dead;

            return reward;
        }

        // ======== Observations ========

        private (object obs, bool done, object info) ReadObsDone(Level lvl)
        {
            var pl = GetValidPlayer(lvl);
            float px = 0, py = 0, vx = 0, vy = 0;
            bool onGround = false;
            int dashes = 0;
            float stamina = 0;

            // Safely read player state
            if (pl != null)
            {
                try
                {
                    px = pl.X;
                    py = pl.Y;
                    vx = pl.Speed.X;
                    vy = pl.Speed.Y;
                    onGround = pl.OnGround();
                    dashes = pl.Dashes;
                    stamina = pl.Stamina;
                }
                catch (Exception e)
                {
                    // Player state is invalid (probably during transition/removal)
                    Logger.Log(LogLevel.Warn, "RLBridge", $"Failed to read player state: {e.Message}");
                }
            }

            // Capture current frame (if enabled in settings)
            string frameBase64 = "";
            int frameWidth = 0;
            int frameHeight = 0;
            if (Settings.CaptureFrames)
            {
                try
                {
                    var frameData = CaptureFrame(out frameWidth, out frameHeight);
                    if (frameData != null)
                    {
                        frameBase64 = Convert.ToBase64String(frameData);
                    }
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Error, "RLBridge", $"Failed to capture frame: {e.Message}");
                }
            }

            // Get current room and state
            string currentRoom = lvl.Session?.Level ?? "";
            int cellX = 0;
            int cellY = 0;

            if (pl != null)
            {
                cellX = (int)(pl.X / Settings.CellSize);
                cellY = (int)(pl.Y / Settings.CellSize);
            }

            bool dead = pl == null || (pl != null && pl.Dead);
            bool reachedExit = false; // TODO: set true if touching a custom flag or level transition

            // Additional termination conditions (inversely scaled by speed for faster iteration)
            // At higher speeds, use LOWER thresholds for faster episode turnover
            int scaledStagnationThreshold = Math.Max(1, Settings.StagnationThreshold / Settings.SpeedMultiplier);
            int scaledMaxSteps = Math.Max(1, Settings.MaxEpisodeSteps / Settings.SpeedMultiplier);

            bool stagnated = Settings.TerminateOnStagnation &&
                            stepsSinceLastNewCell >= scaledStagnationThreshold;
            bool maxStepsReached = Settings.TerminateOnMaxSteps &&
                                   episodeSteps >= scaledMaxSteps;

            bool done = dead || reachedExit || stagnated || maxStepsReached;

            var obs = new {
                px, py, vx, vy, onGround, dashes, stamina,
                frame = frameBase64,
                frameWidth,
                frameHeight
            };

            var info = new {
                dead,
                reachedExit,
                stagnated,
                maxStepsReached,
                episodeSteps,
                stepsSinceLastNewCell,
                room = currentRoom,
                area = lvl.Session?.Area.GetSID() ?? "unknown",
                roomsVisited = visitedRooms.Count,
                cellsVisited = visitedCells.Count,
                cellX,
                cellY
            };

            return (obs, done, info);
        }

        // Capture the current game frame with optional downsampling and color conversion
        private byte[] CaptureFrame(out int width, out int height)
        {
            var gd = Engine.Instance.GraphicsDevice;
            int srcWidth = gd.PresentationParameters.BackBufferWidth;
            int srcHeight = gd.PresentationParameters.BackBufferHeight;

            // Determine target resolution
            width = Settings.FrameWidth > 0 ? Settings.FrameWidth : srcWidth;
            height = Settings.FrameHeight > 0 ? Settings.FrameHeight : srcHeight;

            // Read from back buffer
            var srcBuffer = new Color[srcWidth * srcHeight];
            gd.GetBackBufferData(srcBuffer);

            // Determine bytes per pixel based on color mode
            int bytesPerPixel = Settings.ColorMode switch
            {
                FrameColorMode.RGB => 3,
                FrameColorMode.Grayscale => 1,
                FrameColorMode.LuminanceHue => 2,
                FrameColorMode.YUV => 3,
                _ => 1
            };

            var result = new byte[width * height * bytesPerPixel];

            // Bilinear downsampling with color conversion
            float xRatio = (float)srcWidth / width;
            float yRatio = (float)srcHeight / height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Map to source coordinates
                    float srcX = x * xRatio;
                    float srcY = y * yRatio;

                    // Get 4 nearest pixels for bilinear interpolation
                    int x1 = (int)srcX;
                    int y1 = (int)srcY;
                    int x2 = Math.Min(x1 + 1, srcWidth - 1);
                    int y2 = Math.Min(y1 + 1, srcHeight - 1);

                    float xWeight = srcX - x1;
                    float yWeight = srcY - y1;

                    // Sample 4 corners
                    var c11 = srcBuffer[y1 * srcWidth + x1];
                    var c21 = srcBuffer[y1 * srcWidth + x2];
                    var c12 = srcBuffer[y2 * srcWidth + x1];
                    var c22 = srcBuffer[y2 * srcWidth + x2];

                    // Bilinear interpolation
                    byte r = (byte)(
                        c11.R * (1 - xWeight) * (1 - yWeight) +
                        c21.R * xWeight * (1 - yWeight) +
                        c12.R * (1 - xWeight) * yWeight +
                        c22.R * xWeight * yWeight
                    );
                    byte g = (byte)(
                        c11.G * (1 - xWeight) * (1 - yWeight) +
                        c21.G * xWeight * (1 - yWeight) +
                        c12.G * (1 - xWeight) * yWeight +
                        c22.G * xWeight * yWeight
                    );
                    byte b = (byte)(
                        c11.B * (1 - xWeight) * (1 - yWeight) +
                        c21.B * xWeight * (1 - yWeight) +
                        c12.B * (1 - xWeight) * yWeight +
                        c22.B * xWeight * yWeight
                    );

                    int idx = y * width + x;

                    switch (Settings.ColorMode)
                    {
                        case FrameColorMode.RGB:
                            result[idx * 3 + 0] = r;
                            result[idx * 3 + 1] = g;
                            result[idx * 3 + 2] = b;
                            break;

                        case FrameColorMode.Grayscale:
                            // Standard luminance formula
                            result[idx] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
                            break;

                        case FrameColorMode.LuminanceHue:
                            // Channel 0: Luminance (same as grayscale)
                            float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                            result[idx * 2 + 0] = (byte)lum;

                            // Channel 1: Hue (0-255 representing 0-360 degrees)
                            float rNorm = r / 255f;
                            float gNorm = g / 255f;
                            float bNorm = b / 255f;
                            float max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
                            float min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
                            float delta = max - min;

                            float hue = 0f;
                            if (delta > 0.001f)
                            {
                                if (max == rNorm)
                                    hue = 60f * (((gNorm - bNorm) / delta) % 6);
                                else if (max == gNorm)
                                    hue = 60f * (((bNorm - rNorm) / delta) + 2);
                                else
                                    hue = 60f * (((rNorm - gNorm) / delta) + 4);

                                if (hue < 0) hue += 360f;
                            }

                            result[idx * 2 + 1] = (byte)(hue * 255f / 360f);
                            break;

                        case FrameColorMode.YUV:
                            // YUV color space (BT.601 standard)
                            float Y = 0.299f * r + 0.587f * g + 0.114f * b;
                            float U = (b - Y) * 0.492f + 128f;
                            float V = (r - Y) * 0.877f + 128f;

                            result[idx * 3 + 0] = (byte)Math.Clamp(Y, 0, 255);
                            result[idx * 3 + 1] = (byte)Math.Clamp(U, 0, 255);
                            result[idx * 3 + 2] = (byte)Math.Clamp(V, 0, 255);
                            break;
                    }
                }
            }

            return result;
        }

        // ======== Input mapping ========

        private InputState DecodeAction(int id)
        {
            // Systematic action space:
            // 0: Idle
            // 1-9: Jump + 9 directions
            // 10-18: Grab + 9 directions
            // 19-27: Dash + 9 directions
            // 28-36: Movement only (9 directions, no action)
            // 37-45: Grab+Jump + 9 directions (CLIMBING!)

            var a = new InputState();

            if (id == 0)
            {
                // Idle - do nothing
                return a;
            }

            // Decode which action group (jump, grab, dash, movement, grab+jump)
            int actionGroup = (id - 1) / 9;  // 0=jump, 1=grab, 2=dash, 3=movement, 4=grab+jump
            int dirIndex = (id - 1) % 9;     // 0-8 for direction

            // Set the action
            switch (actionGroup)
            {
                case 0: a.JumpTap = true; break;  // Jump actions (1-9)
                case 1: a.Grab = true; break;     // Grab actions (10-18)
                case 2: a.Dash = true; break;     // Dash actions (19-27)
                case 3: break;                     // Movement only (28-36)
                case 4: a.Grab = true; a.JumpTap = true; break;  // Grab+Jump (37-45) - CLIMBING!
            }

            // Set the direction based on dirIndex
            // 0: neutral, 1: left, 2: right, 3: up, 4: down,
            // 5: up-left, 6: up-right, 7: down-left, 8: down-right
            switch (dirIndex)
            {
                case 0: // Neutral (no direction)
                    break;
                case 1: // Left
                    a.MoveX = -1;
                    break;
                case 2: // Right
                    a.MoveX = 1;
                    break;
                case 3: // Up
                    a.MoveY = -1;
                    break;
                case 4: // Down
                    a.MoveY = 1;
                    break;
                case 5: // Up-left
                    a.MoveX = -1;
                    a.MoveY = -1;
                    break;
                case 6: // Up-right
                    a.MoveX = 1;
                    a.MoveY = -1;
                    break;
                case 7: // Down-left
                    a.MoveX = -1;
                    a.MoveY = 1;
                    break;
                case 8: // Down-right
                    a.MoveX = 1;
                    a.MoveY = 1;
                    break;
            }

            // For dash, set aim direction based on movement direction
            if (a.Dash)
            {
                if (a.MoveX != 0 || a.MoveY != 0)
                {
                    a.Aim = new Vector2(a.MoveX, a.MoveY).SafeNormalize();
                }
                else
                {
                    // Neutral dash - default to up (or could be forward based on player facing)
                    a.Aim = new Vector2(0, -1);
                }
            }

            return a;
        }

        private void ApplyInput(InputState a)
        {
            // Set movement direction
            Input.MoveX.Value = a.MoveX; // -1, 0, or 1
            Input.MoveY.Value = a.MoveY; // -1, 0, or 1 (for down/crouch)

            // Handle Jump with proper button press injection (tap action)
            // IMPORTANT: For climbing, we need jump to trigger even when grab is held
            if (a.JumpTap)
            {
                // Always force jump when requested, don't track state
                // This ensures climb jumps and wall kicks work properly
                ForceButtonPress(Input.Jump);
            }

            // Handle Grab - MUST be held for wall climbing, not just pressed
            if (a.Grab)
            {
                // Hold grab button down (needed for wall climbing)
                ForceButtonHold(Input.Grab);
            }
            else
            {
                // Release grab button when not in action
                ForceButtonRelease(Input.Grab);
            }

            // Handle Dash with aim direction (tap action)
            if (a.Dash)
            {
                if (a.Aim != Vector2.Zero)
                {
                    Input.Aim.Value = a.Aim.SafeNormalize();
                }
                ForceButtonPress(Input.Dash);
            }
        }

        // Force a button press (for tap actions like jump/dash)
        private void ForceButtonPress(VirtualButton button)
        {
            try
            {
                if (buttonBuffered != null)
                {
                    // Set the buffer counter to a positive value so the press is registered
                    buttonBuffered.SetValue(button, 0.1f);
                }
                if (buttonCheck != null)
                {
                    // Set DebugOverridePressed to true (it's a Nullable<bool>)
                    buttonCheck.SetValue(button, (bool?)true);
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "RLBridge", $"Failed to force button press: {e.Message}");
            }
        }

        // Force a button to be held down (for hold actions like grab)
        private void ForceButtonHold(VirtualButton button)
        {
            try
            {
                if (buttonCheck != null)
                {
                    // Keep button held by continuously setting DebugOverridePressed
                    buttonCheck.SetValue(button, (bool?)true);
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "RLBridge", $"Failed to force button hold: {e.Message}");
            }
        }

        // Release a held button
        private void ForceButtonRelease(VirtualButton button)
        {
            try
            {
                if (buttonCheck != null)
                {
                    // Release by setting DebugOverridePressed to null
                    buttonCheck.SetValue(button, (bool?)null);
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "RLBridge", $"Failed to release button: {e.Message}");
            }
        }

        private void ClearInput()
        {
            // Reset all input states
            Input.MoveX.Value = 0;
            Input.MoveY.Value = 0;
            Input.Aim.Value = Vector2.Zero;

            // Clear button buffers to prevent sticking
            Input.Jump.ConsumeBuffer();
            Input.Dash.ConsumeBuffer();
            Input.Grab.ConsumeBuffer();

            // Reset DebugOverridePressed to null to prevent sticking
            if (buttonCheck != null)
            {
                try
                {
                    buttonCheck.SetValue(Input.Jump, (bool?)null);
                    buttonCheck.SetValue(Input.Dash, (bool?)null);
                    buttonCheck.SetValue(Input.Grab, (bool?)null);
                }
                catch { }
            }
        }
    }

    // Compact action state for one frame
    public struct InputState
    {
        public int MoveX;           // -1,0,1
        public int MoveY;           // -1,0,1 (for down/crouch)
        public bool Grab;
        public bool JumpTap;
        public bool Dash;
        public Vector2 Aim;         // normalized 8-dir for dash
    }
}
