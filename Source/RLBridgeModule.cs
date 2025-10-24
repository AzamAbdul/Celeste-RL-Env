// RLBridge/Source/RLBridgeModule.cs
using Celeste.Mod;  // << needed for EverestModule, EverestModuleSettings, Logger, etc.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Celeste;
using Microsoft.Xna.Framework;
using Monocle;

namespace RLBridge
{
    public class RLBridgeModule : EverestModule
    {
        public static RLBridgeModule Instance;

        // Networking
        private JsonLServer server;
        private const int DefaultPort = 17653; // localhost only
        private readonly object stateLock = new object();

        // Step/input scheduling
        private int framesToRun = 0;
        private InputState stagedInput = new InputState();
        private bool pendingReset = false;
        private int resetSlot = 0;

        // Progress tracking for rewards (you can customize)
        private float lastProgress = 0f;
        private Vector2 lastPlayerPos = Vector2.Zero;

        // Interop helpers (bound at runtime via reflection)
        private TasInterop tas;
        private SpeedrunToolInterop srt;

        public RLBridgeModule()
        {
            Instance = this;
        }

        public override void Load()
        {
            // Hook the level update
            On.Celeste.Level.Update += Level_Update;

            // Bind optional interops
            tas = new TasInterop().Bind();
            srt = new SpeedrunToolInterop().Bind();

            // Start TCP server (localhost only)
            server = new JsonLServer(IPAddress.Loopback, DefaultPort, HandleMessage);
            server.Start();
            Logger.Log("RLBridge", $"Started JSONL server at 127.0.0.1:{DefaultPort}");
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
                    Logger.Log("RLBridge", $"Loaded savestate slot {resetSlot}");
                else
                {
                    Logger.Log("RLBridge", $"Speedrun Tool unavailable -> session restart");
                    self.Reload();
                }

                // Reset progress baseline
                lastProgress = 0f;
                var p0 = self.Tracker.GetEntity<Player>();
                if (p0 != null) lastPlayerPos = p0.Position;
            }

            // Apply staged input during step frames
            if (framesToRun > 0)
            {
                ApplyInput(stagedInput);
                framesToRun--;
                // If CelesteTAS is available, ask it to advance exactly 1 frame deterministically
                if (tas.IsAvailable) tas.AdvanceOneFrame();
            }
            else
            {
                ClearInput();
            }

            // Run the game update (this advances by one frame; TAS can “gate” deterministically)
            orig(self);

            // When we finish a request, send an update back to the client
            if (framesToRun == 0)
            {
                var (obs, reward, done, info) = ReadObsRewardDone(self);
                server.TrySend(new { obs, reward, done, info });
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

                    case "seed":
                        int seed = root.GetProperty("value").GetInt32();
                        bool seeded = tas.IsAvailable && tas.SetUniversalSeed(seed);
                        server.TrySend(new { ok = seeded, seeded, seed });
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

            // Example “progress” = negative distance to a target, or simply delta-X to the right.
            float progress = 0f;
            if (pl != null)
            {
                // Simple: reward moving right / up; replace with your navmesh/proxy
                progress = (pl.Position - lastPlayerPos).Length();
                lastPlayerPos = pl.Position;
            }

            float reward = progress - 0.001f; // tiny living cost
            bool dead = pl == null || (pl != null && pl.Dead);
            bool reachedExit = false; // TODO: set true if touching a custom flag or level transition

            bool done = dead || reachedExit;

            var obs = new {
                px, py, vx, vy, onGround, dashes, stamina
            };

            var info = new {
                dead,
                reachedExit
            };

            return (obs, reward, done, info);
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
            // Preferred path: feed inputs through TAS (button edge semantics, coyote time, dash angles, etc.)
            if (tas.IsAvailable && tas.SetInputs(a))
                return;

            // Fallback: minimal direct injection (crude; not perfect)
            Input.MoveX.Value = a.MoveX; // -1,0,1
            if (a.Grab) Input.Grab.ConsumePress(); // keep held via TAS if you need perfect behavior
            if (a.JumpTap) Input.Jump.ConsumePress();
            if (a.Dash && a.Aim != Vector2.Zero)
            {
                Input.Aim.Value = a.Aim;
                Input.Dash.ConsumePress();
            }
        }

        private void ClearInput()
        {
            Input.MoveX.Value = 0;
            // Clear one-frame buttons so they don't “stick”
            Input.Jump.ConsumeBuffer();
            Input.Dash.ConsumeBuffer();
            Input.Grab.ConsumeBuffer();
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
