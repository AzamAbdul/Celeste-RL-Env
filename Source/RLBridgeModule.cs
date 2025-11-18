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
        private bool responseSent = true;  // Track if we've sent response for current step

        // Progress tracking for rewards (you can customize)
        private float maxXProgress = 0f;  // Maximum X position reached (rightward progress)
        private float startX = 0f;        // Starting X position for the episode

        // Interop helpers (bound at runtime via reflection)
        private SpeedrunToolInterop srt;

        // Input state tracking for proper button handling
        private bool lastJump = false;
        private bool lastDash = false;
        private bool lastGrab = false;

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
            buttonCheck = vbType.GetField("DebugOverridePressed", BindingFlags.Instance | BindingFlags.Public);

            if (buttonBuffered == null || buttonCheck == null)
            {
                Logger.Log(LogLevel.Error, "RLBridge", "Could not find VirtualButton internal fields");

                // List all fields for debugging
                Logger.Log(LogLevel.Error, "RLBridge", "Available VirtualButton fields:");
                foreach (var field in vbType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Logger.Log(LogLevel.Error, "RLBridge", $"  - {field.Name} ({field.FieldType.Name})");
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
            // Handle reset if requested
            if (pendingReset)
            {
                pendingReset = false;
                // Try Speedrun Tool savestate load; otherwise fall back to reloading the current session
                if (srt.IsAvailable && srt.LoadSlot(resetSlot))
                    Logger.Log(LogLevel.Error, "RLBridge", $"Loaded savestate slot {resetSlot}");
                else
                {
                    Logger.Log(LogLevel.Error, "RLBridge", $"Speedrun Tool unavailable -> session restart");
                    self.Reload();
                }

                // Reset progress baseline
                var p0 = self.Tracker.GetEntity<Player>();
                if (p0 != null)
                {
                    startX = p0.X;
                    maxXProgress = p0.X;
                }

                // Mark that we need to send observation after reset
                responseSent = false;
            }

            // Apply staged input during step frames
            if (framesToRun > 0)
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

            // When we finish a request, send an update back to the client (only once!)
            if (framesToRun == 0 && !responseSent)
            {
                var (obs, reward, done, info) = ReadObsRewardDone(self);
                server.TrySend(new { obs, reward, done, info });
                responseSent = true;
            }
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

        // ======== Observations / rewards ========

        private (object obs, float reward, bool done, object info) ReadObsRewardDone(Level lvl)
        {
            var pl = lvl.Tracker.GetEntity<Player>();
            float px = 0, py = 0, vx = 0, vy = 0;
            bool onGround = false;
            int dashes = 0;
            float stamina = 0;

            if (pl != null)
            {
                px = pl.X;
                py = pl.Y;
                vx = pl.Speed.X;
                vy = pl.Speed.Y;
                onGround = pl.OnGround();
                dashes = pl.Dashes;
                stamina = pl.Stamina;
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

            // Reward: encourage rightward progress, penalize time
            float reward = 0f;
            if (pl != null)
            {
                // Check if player reached new maximum X position (rightward progress)
                float currentX = pl.X;
                float newProgress = Math.Max(currentX, maxXProgress);
                float progressDelta = newProgress - maxXProgress;

                // Update max progress
                maxXProgress = newProgress;

                // Reward = new ground covered - time penalty
                // Scale: 1 unit of distance = 0.01 reward (adjust as needed)
                reward = progressDelta * 0.01f - 0.01f; // -0.01 time penalty per step
            }
            else
            {
                // Player doesn't exist (shouldn't happen unless dead)
                reward = -0.01f;
            }
            bool dead = pl == null || (pl != null && pl.Dead);
            bool reachedExit = false; // TODO: set true if touching a custom flag or level transition

            bool done = dead || reachedExit;

            var obs = new {
                px, py, vx, vy, onGround, dashes, stamina,
                frame = frameBase64,
                frameWidth,
                frameHeight
            };

            var info = new {
                dead,
                reachedExit
            };

            return (obs, reward, done, info);
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
            // Tune this table to your liking.
            // 0: idle
            // 1: left, 2: right, 3: grab, 4: jumpTap,
            // 5..12: dash in 8 dirs (clockwise from up)
            var a = new InputState();

            switch (id)
            {
                case 0: break;
                case 1: a.MoveX = -1; break;
                case 2: a.MoveX = 1; break;
                case 3: a.Grab = true; break;
                case 4: a.JumpTap = true; break;

                case 5:  a.Dash = true; a.Aim = new Vector2( 0, -1); break; // up
                case 6:  a.Dash = true; a.Aim = new Vector2( 1, -1); break;
                case 7:  a.Dash = true; a.Aim = new Vector2( 1,  0); break;
                case 8:  a.Dash = true; a.Aim = new Vector2( 1,  1); break;
                case 9:  a.Dash = true; a.Aim = new Vector2( 0,  1); break;
                case 10: a.Dash = true; a.Aim = new Vector2(-1,  1); break;
                case 11: a.Dash = true; a.Aim = new Vector2(-1,  0); break;
                case 12: a.Dash = true; a.Aim = new Vector2(-1, -1); break;

                default: break;
            }
            return a;
        }

        private void ApplyInput(InputState a)
        {
            // Set movement direction
            Input.MoveX.Value = a.MoveX; // -1, 0, or 1

            // Handle Jump with proper button press injection (tap action)
            if (a.JumpTap && !lastJump)
            {
                ForceButtonPress(Input.Jump);
            }
            lastJump = a.JumpTap;

            // Handle Grab - MUST be held for wall climbing, not just pressed
            if (a.Grab)
            {
                // Hold grab button down (needed for wall climbing)
                ForceButtonHold(Input.Grab);
            }
            else if (lastGrab && !a.Grab)
            {
                // Release grab button
                ForceButtonRelease(Input.Grab);
            }
            lastGrab = a.Grab;

            // Handle Dash with aim direction (tap action)
            if (a.Dash && !lastDash)
            {
                if (a.Aim != Vector2.Zero)
                {
                    Input.Aim.Value = a.Aim.SafeNormalize();
                }
                ForceButtonPress(Input.Dash);
            }
            lastDash = a.Dash;
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
            Input.Aim.Value = Vector2.Zero;

            // Reset button state tracking
            lastJump = false;
            lastDash = false;
            lastGrab = false;

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
        public bool Grab;
        public bool JumpTap;
        public bool Dash;
        public Vector2 Aim;         // normalized 8-dir for dash
    }
}
