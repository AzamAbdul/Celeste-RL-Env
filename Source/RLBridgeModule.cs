// RLBridge/Source/RLBridgeModule.cs
using Celeste.Mod;  // << needed for EverestModule, EverestModuleSettings, Logger, etc.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

using Celeste;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
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
        private bool setSpawnPoint = false;
        private int resetSlot = 0;
        private int resetDelayFrames = 0;  // Delay reset to let scene stabilize
        private bool responseSent = true;  // Track if we've sent response for current step
        private float accumulatedReward = 0f;  // Accumulate reward across action repeat frames

        // Performance timing (debug)
        private System.Diagnostics.Stopwatch perfTimer = new System.Diagnostics.Stopwatch();
        private double timeFrameCapture = 0.0;
        private double timeRewardCalc = 0.0;
        private double timeObsRead = 0.0;
        private double timeGameUpdate = 0.0;
        private double timeSendResponse = 0.0;
        private int perfSteps = 0;

        // Random room selection
        private Random random = new Random();

        // Frame rate tracking
        private int levelUpdateCallCount = 0;
        private double levelUpdateStartTime = 0.0;
        private bool vsyncDisabled = false;

        // Progress tracking for rewards (you can customize)
        private float lastCellDistanceToExit = float.MaxValue;  // Last cell's distance to exit (for potential-based shaping)
        private float startX = 0f;        // Starting X position for the episode

        // Room-based progression tracking
        private HashSet<string> visitedRooms = new HashSet<string>();
        private string lastRoom = "";

        // Exit tracking (room adjacency from Level.Bounds)
        private Dictionary<string, List<Vector2>> roomExits = new Dictionary<string, List<Vector2>>();
        private string lastExitScanRoom = "";
        private Vector2? entryExitPoint = null;  // The exit we entered through (exclude from nearest exit calc)
        private const float ENTRY_EXIT_EXCLUSION_RADIUS = 50f;  // Pixels - exclude exits within this radius of entry point

        // Cell position tracking (for distance-based rewards)
        private string lastCellKey = "";
        private int framesInCurrentCell = 0;  // Track how long in same cell

        // Episode termination tracking (distance-based stagnation)
        private float bestDistanceToExit = float.MaxValue;  // Best (lowest) distance achieved this episode
        private int stepsSinceDistanceImproved = 0;         // Steps since we got closer to exit
        private int episodeSteps = 0;

        // Death tracking (to apply penalty only once)
        private bool hasDied = false;
        private bool hasStagnated = false;

        // Transition error handling (for high-speed race conditions)
        private int consecutiveTransitionErrors = 0;
        private const int MAX_TRANSITION_RETRIES = 30;  // ~0.5s at 60fps, less at high speed

        // Unstable state tracking (to prevent Python from hanging during long transitions)
        private int framesWaitingForStability = 0;
        private const int MAX_UNSTABLE_FRAMES = 60;  // ~1s at 60fps, force response after this

        // Post-reload setup flag (split reset into two phases to avoid stale self reference)
        private bool needsPostReloadSetup = false;

        // Warmup period at episode start before stagnation detection applies (in agent steps, not frames)
        // With action_repeat=4, 75 steps = 300 frames
        private const int STAGNATION_WARMUP_STEPS = 10;  // First 300 frames (75 steps @ action_repeat=4)

        // ======== REWARD/PENALTY/THRESHOLD CONFIGURATION ========
        // Edit these values directly instead of using the settings menu

        // Episode termination thresholds
        private const int STAGNATION_THRESHOLD = 160;        // Frames without getting closer to exit
        private const int MAX_EPISODE_STEPS = 50000;         // Maximum frames per episode
        private const bool TERMINATE_ON_STAGNATION = true;  // Enable distance-based stagnation termination
        private const bool TERMINATE_ON_MAX_STEPS = true;   // Enable max steps termination

        // Room rewards
        private const float NEW_ROOM_REWARD = 2.0f;        // Reward for discovering new room
        private const float REVISIT_ROOM_PENALTY = -.50f;  // Penalty for backtracking to visited room (heavy to prevent bouncing)

        // Penalties
        private const float DEATH_PENALTY = -.50f;         // One-time penalty on death
        private const float STAGNATION_PENALTY = -.25f;    // One-time penalty on stagnation termination
        private const float STEP_COST = -0.001f;          // Per-step cost to encourage efficiency
        private const float IDLE_PENALTY_RATE = 0;         // Per-frame penalty when idle in same cell

        // Cell tracking settings (for distance-based rewards)
        private const float CELL_SIZE = 16.0f;             // Pixels per grid cell
        private const bool PENALIZE_IDLING = true;         // Apply penalty for staying in same cell
        private const int IDLE_THRESHOLD = 30;             // Frames before idle penalty applies

        // Potential-based exit proximity reward (cell-level) with sqrt shaping
        // φ(d) = -sqrt(d), reward = sqrt(d_old) - sqrt(d_new)
        // This gives higher rewards near the exit (steeper gradient)
        // For 300px room: total cumulative reward ≈ sqrt(300) * PROGRESS_SCALE ≈ 3.5
        private const bool USE_DISTANCE_REWARD = true;     // Enable exit proximity reward
        private const float PROGRESS_SCALE = 0.2f;         // Scale for sqrt-shaped distance reward

        // ======== END CONFIGURATION ========

        // Rendering control (for Python-controlled display toggle)
        private bool renderingEnabled = true;  // Default enabled for normal operation

        // Interop helpers (bound at runtime via reflection)
        private SpeedrunToolInterop srt;


        // Reflection fields for VirtualButton manipulation
        private FieldInfo buttonBuffered;
        private FieldInfo buttonCheck;
        private FieldInfo buttonBinding;

        // Fields for Binding manipulation
        private FieldInfo bindingPressed;
        private MethodInfo bindingUpdateMethod;

        // MInput keyboard state manipulation
        private object keyboardDataInstance;
        private FieldInfo kbCurrentState;
        private FieldInfo kbPreviousState;

        // Cached keys for each action
        private Keys grabKey = Keys.Z;
        private Keys jumpKey = Keys.C;
        private Keys dashKey = Keys.X;

        public RLBridgeModule()
        {
            Instance = this;
        }

        public override void Load()
        {
            // Hook the level update
            On.Celeste.Level.Update += Level_Update;

            // Hook Engine rendering to control display (note: frame capture still works)
            On.Monocle.Engine.RenderCore += Engine_RenderCore;

            // Bind optional interops
            srt = new SpeedrunToolInterop().Bind();

            // Get VirtualButton internal fields via reflection
            Type vbType = typeof(VirtualButton);
            buttonBuffered = vbType.GetField("bufferCounter", BindingFlags.Instance | BindingFlags.NonPublic);

            // Try to find the bool override field - name might vary by Celeste version
            buttonCheck = vbType.GetField("DebugOverridePressed", BindingFlags.Instance | BindingFlags.Public);

            // Validate the type - accept both Nullable<bool> and Nullable<Keys>
            if (buttonCheck != null && buttonCheck.FieldType != typeof(bool?) && buttonCheck.FieldType != typeof(Keys?))
            {
                buttonCheck = null;
            }

            if (buttonCheck != null)
            {
            }

            // Get the Binding field
            buttonBinding = vbType.GetField("Binding", BindingFlags.Instance | BindingFlags.Public);

            // If we have the Binding field, investigate the Binding type
            if (buttonBinding != null)
            {
                Type bindingType = buttonBinding.FieldType;

                // List all Binding fields
                foreach (var field in bindingType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                }

                // Try to find a pressed state field in Binding
                bindingPressed = bindingType.GetField("pressed", BindingFlags.Instance | BindingFlags.NonPublic);
                if (bindingPressed == null)
                {
                    bindingPressed = bindingType.GetField("Pressed", BindingFlags.Instance | BindingFlags.Public);
                }

                if (bindingPressed != null)
                {
                }
            }

            // Investigate the Node type
            var nodesField = vbType.GetField("Nodes", BindingFlags.Instance | BindingFlags.Public);
            if (nodesField != null)
            {
                // Get the Node type from the List<Node>
                var listType = nodesField.FieldType;
                if (listType.IsGenericType && listType.GetGenericArguments().Length > 0)
                {
                    Type nodeType = listType.GetGenericArguments()[0];

                    // List all fields in Node
                    foreach (var field in nodeType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                    }

                    // List all methods in Node
                    foreach (var method in nodeType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (method.Name.Contains("Check") || method.Name.Contains("Press") || method.Name.Contains("Update"))
                        {
                        }
                    }
                }
            }

            // Get MInput.Keyboard instance for direct keyboard state manipulation
            Type mInputType = typeof(MInput);
            var kbField = mInputType.GetField("Keyboard", BindingFlags.Static | BindingFlags.Public);
            if (kbField != null)
            {
                keyboardDataInstance = kbField.GetValue(null);
                Type keyboardDataType = kbField.FieldType;

                kbCurrentState = keyboardDataType.GetField("CurrentState", BindingFlags.Instance | BindingFlags.Public);
                kbPreviousState = keyboardDataType.GetField("PreviousState", BindingFlags.Instance | BindingFlags.Public);

                if (kbCurrentState != null && kbPreviousState != null)
                {
                }
                else
                {
                }
            }

            if (buttonBuffered == null)
            {

                // List all fields for debugging
                foreach (var field in vbType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                }
            }
            else if (buttonCheck == null)
            {
            }
            else
            {
            }

            // Start TCP server (localhost only)
            // Port priority: 1) Environment variable, 2) Settings, 3) DefaultPort
            int port = DefaultPort;

            string envPort = Environment.GetEnvironmentVariable("RLBRIDGE_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsedPort))
            {
                port = parsedPort;
            }
            else if (Settings.TcpPort != DefaultPort)
            {
                port = Settings.TcpPort;
            }

            server = new JsonLServer(IPAddress.Loopback, port, HandleMessage);
            server.Start();
        }

        public override void Unload()
        {
            On.Celeste.Level.Update -= Level_Update;
            On.Monocle.Engine.RenderCore -= Engine_RenderCore;
            try { server?.Dispose(); } catch { }
        }

        // Hook to control rendering to display window
        // Frame capture (GetBackBufferData) still works when rendering is disabled
        private void Engine_RenderCore(On.Monocle.Engine.orig_RenderCore orig, Engine self)
        {
            if (renderingEnabled)
            {
                // Normal rendering - draw and present to screen
                orig(self);
            }
            // If disabled, skip rendering - saves GPU overhead
            // Frame capture still works via GetBackBufferData which reads after game logic
        }

        private void Level_Update(On.Celeste.Level.orig_Update orig, Level self)
        {
            try
            {

                // Track Level_Update call rate
                levelUpdateCallCount++;
                if (levelUpdateStartTime == 0.0)
                {
                    levelUpdateStartTime = Engine.Scene?.TimeActive ?? 0.0;
                }

                // Apply speed multiplier - set every frame to override any other mods
                Engine.TimeRate = Settings.SpeedMultiplier;

                // Check if game is in a stable state for RL operations
                bool isStable = IsGameStateStable(self);

                // Handle reset with delay to let scene stabilize
                if (pendingReset)
                {
                    // Don't reset during transitions - wait for them to complete
                    if (!isStable)
                    {
                        ClearInput();
                        orig(self);
                        return;
                    }

                    if (resetDelayFrames > 0)
                    {
                        resetDelayFrames--;
                        ClearInput();
                        orig(self);
                        return;
                    }

                    // Delay complete and game is stable, execute reset
                    pendingReset = false;

                    // Clear any pending input state before reset
                    framesToRun = 0;
                    stagedInput = new InputState();
                    ClearInput();
                    accumulatedReward = 0f;

                    // Set respawn point to the chapter start
                    if (self.Session != null && !setSpawnPoint)
                    {
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
                        pendingReset = true;
                        setSpawnPoint = true;
                        needsPostReloadSetup = true;
                        orig(self);
                        return;
                    }

                    if (needsPostReloadSetup)
                    {
                        self.Reload();
                        needsPostReloadSetup = false;
                    }

                    // Reset progress baseline (safely)
                    var p0 = GetValidPlayer(self);
                    if (p0 != null)
                    {
                        startX = p0.X;
                    }

                    // Reset room tracking and pre-fill with starting room
                    visitedRooms.Clear();
                    string currentRoomName = self.Session?.Level ?? "";
                    if (!string.IsNullOrEmpty(currentRoomName))
                    {
                        visitedRooms.Add(currentRoomName);
                        ScanForExits(self, currentRoomName);
                    }
                    lastRoom = currentRoomName;

                    // Reset cell tracking and initialize distance for potential-based reward
                    var pl = self.Tracker?.GetEntity<Player>();
                    if (pl != null)
                    {
                        int startCellX = (int)Math.Floor(pl.Position.X / CELL_SIZE);
                        int startCellY = (int)Math.Floor(pl.Position.Y / CELL_SIZE);

                        // Initialize distance for potential-based reward
                        float cellCenterX = startCellX * CELL_SIZE + CELL_SIZE / 2f;
                        float cellCenterY = startCellY * CELL_SIZE + CELL_SIZE / 2f;
                        Vector2 cellCenter = new Vector2(cellCenterX, cellCenterY);
                        lastCellDistanceToExit = GetDistanceFromPointToNearestExit(self, cellCenter, currentRoomName);

                        // Set lastCellKey to spawn cell so first step doesn't trigger spurious cell change
                        lastCellKey = $"{currentRoomName}:{startCellX},{startCellX}";
                    }
                    else
                    {
                        lastCellDistanceToExit = float.MaxValue;
                        lastCellKey = "";
                    }
                    framesInCurrentCell = 0;

                    // Reset episode tracking (distance-based stagnation)
                    // Initialize bestDistanceToExit from starting position
                    bestDistanceToExit = lastCellDistanceToExit;
                    stepsSinceDistanceImproved = 0;
                    episodeSteps = 0;
                    hasDied = false;
                    hasStagnated = false;
                    consecutiveTransitionErrors = 0;
                    framesWaitingForStability = 0;
                    setSpawnPoint = false;
                    lastExitScanRoom = "";
                    needsPostReloadSetup = false;
                    entryExitPoint = null;  // No entry exit for spawn room (we spawned here, didn't enter)

                    // Mark that we need to send observation after reset
                    responseSent = false;
                }

                // Check if player is controllable (not in intro animation, not dead)
                bool isControllable = IsPlayerControllable(self);

                // Only apply input and calculate rewards when game is stable AND player is controllable
                bool isRunningStep = framesToRun > 0 && isStable && isControllable;
                if (isRunningStep)
                {
                    ApplyInput(stagedInput);
                    framesToRun--;
                    framesWaitingForStability = 0;
                }
                else if (framesToRun > 0 && !(isStable || isControllable))
                {
                    ClearInput();
                    framesWaitingForStability++;

                    if (framesWaitingForStability >= MAX_UNSTABLE_FRAMES)
                    {
                        framesToRun = 0;
                        framesWaitingForStability = 0;
                    }
                }

                // Run the game update (this advances by one frame; TAS can "gate" deterministically)
                // Catch transition race conditions that occur at high speeds
                perfTimer.Restart();
                bool transitionError = false;
                try
                {
                    orig(self);
                    // Update succeeded - reset error counter
                    consecutiveTransitionErrors = 0;
                }
                catch (NullReferenceException e)
                {
                    // Check if this is a transition-related error (common at high speeds)
                    if (e.StackTrace?.Contains("TransitionRoutine") == true ||
                        e.StackTrace?.Contains("CollideFirst") == true)
                    {
                        consecutiveTransitionErrors++;
                        if (consecutiveTransitionErrors <= MAX_TRANSITION_RETRIES)
                        {
                            // Transition race condition - wait for next frame to retry
                            transitionError = true;
                        }
                        else
                        {
                            // Too many retries - let outer handler deal with it
                            throw;
                        }
                    }
                    else
                    {
                        // Not a transition error - rethrow
                        throw;
                    }
                }
                timeGameUpdate += perfTimer.Elapsed.TotalMilliseconds;

                // If we hit a transition error, skip the rest of this frame and retry next frame
                if (transitionError)
                {
                    // Don't count this frame, don't calculate rewards, don't send response
                    // Just return and let the next frame retry
                    return;
                }

                // Check for death at the start of action loop - set global hasDied flag
                if (!hasDied && IsPlayerDead(self))
                {
                    hasDied = true;
                }

                // Distance-based stagnation: terminate if not getting closer to exit
                hasStagnated = TERMINATE_ON_STAGNATION && episodeSteps >= STAGNATION_WARMUP_STEPS && stepsSinceDistanceImproved >= STAGNATION_THRESHOLD;

                // Accumulate rewards during action repeat frames (only when stable)
                if (isRunningStep && !(hasDied || hasStagnated))
                {
                    try
                    {
                        perfTimer.Restart();
                        var frameReward = CalculateFrameReward(self);
                        timeRewardCalc += perfTimer.Elapsed.TotalMilliseconds;
                        accumulatedReward += frameReward;
                    }
                    catch (Exception e)
                    {
                    }
                }

                // When we finish a request, send an update back to the client
                if ((framesToRun == 0 || hasDied || hasStagnated) && !responseSent)
                {
                    try
                    {
                        perfTimer.Restart();
                        var (obs, done, info) = ReadObsDone(self);
                        timeObsRead += perfTimer.Elapsed.TotalMilliseconds;

                        // Apply termination penalties (death penalty applied first, takes precedence)
                        dynamic infoObj = info;
                        if (done)
                        {
                            // Death penalty (highest priority)
                            if (infoObj.dead && DEATH_PENALTY != 0)
                            {
                                accumulatedReward += DEATH_PENALTY;

                            }
                            // Stagnation penalty (only if not dead)
                            else if (infoObj.stagnated && STAGNATION_PENALTY != 0)
                            {
                                accumulatedReward += STAGNATION_PENALTY;
                            }
                        }

                        perfTimer.Restart();
                        server.TrySend(new { obs, reward = accumulatedReward, done, info });
                        timeSendResponse += perfTimer.Elapsed.TotalMilliseconds;

                        responseSent = true;
                        accumulatedReward = 0f;  // Reset for next step

                        perfSteps++;

                        // Log performance every 1000 steps
                        if (perfSteps % 1000 == 0)
                        {
                            double totalCSharp = timeGameUpdate + timeRewardCalc + timeObsRead + timeSendResponse;
                            double elapsedTime = (Engine.Scene?.TimeActive ?? 0.0) - levelUpdateStartTime;
                            double updateFPS = elapsedTime > 0 ? levelUpdateCallCount / elapsedTime : 0;
                        }
                    }
                    catch (Exception e)
                    {
                        responseSent = true;  // Mark as sent to avoid getting stuck
                    }
                }

                // Sleep when idle to reduce CPU usage (waiting for Python command)
                // Only sleep if we're not processing steps, not resetting, and response is sent
                if (responseSent && framesToRun == 0 && !pendingReset)
                {
                    Thread.Sleep(1);  // 1ms sleep to yield CPU
                }
            }
            catch (Exception e)
            {

                // If Python is waiting for a response, send an error response to unblock it
                if (!responseSent && framesToRun >= 0)
                {
                    try
                    {
                        var errorObs = new {
                            px = 0f, py = 0f, vx = 0f, vy = 0f,
                            onGround = false, dashes = 0, stamina = 0f,
                            frame = "", frameWidth = 0, frameHeight = 0,
                            stagnationProgress = 0f, episodeProgress = 0f
                        };
                        var errorInfo = new {
                            error = true,
                            errorMessage = e.Message,
                            dead = true,
                            reachedExit = false,
                            stagnated = false,
                            maxStepsReached = false,
                            episodeSteps = 0,
                            stepsSinceDistanceImproved = 0,
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
                consecutiveTransitionErrors = 0;
                framesWaitingForStability = 0;
                ClearInput();
            }
        }

        // Check if the game state is stable enough for RL operations
        private bool IsGameStateStable(Level lvl)
        {
            if (lvl == null) return false;
            if (lvl.Transitioning) return false;
            if (lvl.Frozen) return false;
            if (lvl.Paused) return false;
            if (lvl.Tracker == null) return false;
            return true;
        }

        // Check if player is in a controllable state (not in intro/cutscene/dead)
        private bool IsPlayerControllable(Level lvl)
        {
            if (!IsGameStateStable(lvl)) return false;

            try
            {
                var pl = lvl.Tracker.GetEntity<Player>();
                if (pl == null) return false;
                if (IsPlayerDead(lvl)) return false;

                bool adding = pl.Scene.Entities.adding.Contains(pl);
                bool removing = pl.Scene.Entities.removing.Contains(pl);
                if (adding || removing) return false;

                // Check player state machine
                int state = pl.StateMachine.State;
                if (state == Player.StIntroRespawn || state == Player.StDummy)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        // Check if player is dead (separate from controllable check)
        private bool IsPlayerDead(Level lvl)
        {
            if (!IsGameStateStable(lvl)) return false;

            var pl = lvl.Tracker.GetEntity<Player>();
            if (pl == null) return false;
            if (pl.Scene == null) return false;

            try
            {
                return pl.Scene.Entities.removing.Contains(pl) || pl.Dead;
            }
            catch
            {
                return false;
            }
        }

        // Safely get player entity only when valid and fully initialized
        private Player GetValidPlayer(Level lvl)
        {
            if (!IsGameStateStable(lvl)) return null;
            if (!IsPlayerControllable(lvl)) return null;

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

            // Check if player is being removed or added to scene
            try
            {
                bool removing = pl.Scene.Entities.removing.Contains(pl);
                bool adding = pl.Scene.Entities.adding.Contains(pl);

                if (removing || adding)
                {
                    return null;
                }
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
                        pendingReset = true;
                        resetDelayFrames =  5; // Wait 5 frames before resetting
                        break;

                    case "step":
                        int action = root.GetProperty("action").GetInt32();
                        int repeat = root.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
                        stagedInput = DecodeAction(action);
                        framesToRun = Math.Max(1, repeat);
                        responseSent = false;
                        break;

                    case "set_render":
                        bool enabled = root.GetProperty("enabled").GetBoolean();
                        renderingEnabled = enabled;
                        server.TrySend(new { ok = true });
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

        private void ScanForExits(Level lvl, string roomName)
        {
            if (string.IsNullOrEmpty(roomName) || roomName == lastExitScanRoom)
                return;

            lastExitScanRoom = roomName;
            var exits = new List<Vector2>();

            // Get current room bounds
            Rectangle currentBounds = lvl.Bounds;

            // Check all rooms in the map for adjacency
            if (lvl.Session?.MapData?.Levels != null)
            {
                foreach (var room in lvl.Session.MapData.Levels)
                {
                    // Skip the current room
                    if (room.Name == roomName)
                        continue;

                    Rectangle otherBounds = room.Bounds;

                    // Celeste/Monocle: +Y is DOWN, -Y is UP.
                    // Rect convention: Top is the smaller Y value, Bottom is the larger Y value (even if both are negative).
                    const int maxGap = 16;

                    // RIGHT: other is to the right (higher X)
                    bool right =
                        (otherBounds.Left >= currentBounds.Right) &&
                        (otherBounds.Left - currentBounds.Right <= maxGap) &&
                        (otherBounds.Bottom > currentBounds.Top) &&
                        (otherBounds.Top < currentBounds.Bottom);

                    // LEFT: other is to the left (lower X)
                    bool left =
                        (otherBounds.Right <= currentBounds.Left) &&
                        (currentBounds.Left - otherBounds.Right <= maxGap) &&
                        (otherBounds.Bottom > currentBounds.Top) &&
                        (otherBounds.Top < currentBounds.Bottom);

                    // DOWN: other is below (higher Y, because +Y is down)
                    bool down =
                        (otherBounds.Top >= currentBounds.Bottom) &&
                        (otherBounds.Top - currentBounds.Bottom <= maxGap) &&
                        (otherBounds.Right > currentBounds.Left) &&
                        (otherBounds.Left < currentBounds.Right);

                    // UP: other is above (lower Y, more negative is “more up”)
                    bool up =
                        (otherBounds.Bottom <= currentBounds.Top) &&
                        (currentBounds.Top - otherBounds.Bottom <= maxGap) &&
                        (otherBounds.Right > currentBounds.Left) &&
                        (otherBounds.Left < currentBounds.Right);

                    if (right) {
                        int y0 = Math.Max(currentBounds.Top, otherBounds.Top);
                        int y1 = Math.Min(currentBounds.Bottom, otherBounds.Bottom);
                        Vector2 exitPoint = new Vector2(currentBounds.Right, (y0 + y1) * 0.5f);
                        exits.Add(exitPoint);
                    }

                    if (left) {
                        int y0 = Math.Max(currentBounds.Top, otherBounds.Top);
                        int y1 = Math.Min(currentBounds.Bottom, otherBounds.Bottom);
                        Vector2 exitPoint = new Vector2(currentBounds.Left, (y0 + y1) * 0.5f);
                        exits.Add(exitPoint);
                    }

                    if (down) {
                        int x0 = Math.Max(currentBounds.Left, otherBounds.Left);
                        int x1 = Math.Min(currentBounds.Right, otherBounds.Right);
                        Vector2 exitPoint = new Vector2((x0 + x1) * 0.5f, currentBounds.Bottom);
                        exits.Add(exitPoint);
                    }

                    if (up) {
                        int x0 = Math.Max(currentBounds.Left, otherBounds.Left);
                        int x1 = Math.Min(currentBounds.Right, otherBounds.Right);
                        Vector2 exitPoint = new Vector2((x0 + x1) * 0.5f, currentBounds.Top);
                        exits.Add(exitPoint);
                    }
                                    }
            }

            if (exits.Count > 0)
            {
                roomExits[roomName] = exits;
            }
            else
            {
            }
        }

        private float GetDistanceToNearestExit(Level lvl, Player pl, string roomName)
        {
            if (pl == null || string.IsNullOrEmpty(roomName))
                return 0f;

            return GetDistanceFromPointToNearestExit(lvl, pl.Position, roomName);
        }

        private float GetDistanceFromPointToNearestExit(Level lvl, Vector2 position, string roomName)
        {
            if (string.IsNullOrEmpty(roomName))
                return 0f;

            // Get exits for current room
            if (!roomExits.TryGetValue(roomName, out var exits) || exits.Count == 0)
                return 0f;

            // Find nearest exit, excluding the entry exit (the one we came through)
            float minDistance = float.MaxValue;
            int validExitCount = 0;
            foreach (var exitPos in exits)
            {
                // Skip exits near the entry point (the exit we entered through)
                if (entryExitPoint.HasValue &&
                    Vector2.Distance(exitPos, entryExitPoint.Value) < ENTRY_EXIT_EXCLUSION_RADIUS)
                {
                    continue;
                }

                validExitCount++;
                float distance = Vector2.Distance(position, exitPos);
                if (distance < minDistance)
                    minDistance = distance;
            }

            // If all exits were excluded (single-exit room or all exits are entry),
            // fall back to including all exits
            if (validExitCount == 0)
            {
                foreach (var exitPos in exits)
                {
                    float distance = Vector2.Distance(position, exitPos);
                    if (distance < minDistance)
                        minDistance = distance;
                }
            }

            return minDistance;
        }

        private float CalculateFrameReward(Level lvl)
        {
            var pl = GetValidPlayer(lvl);
            string currentRoom = lvl.Session?.Level ?? "";
            float reward = 0f;

            // Note: hasDied is now checked at the start of Level_Update action loop
            // Only calculate rewards if player is valid AND alive
            // Skip reward calculation during death animation to prevent stagnation false positives
            if (!hasDied && !hasStagnated && pl != null)
            {
                // Increment episode step counter (only when alive)
                episodeSteps++;
                // Will be reset if we get closer to exit
                stepsSinceDistanceImproved++;  
                // Calculate cell coordinates (divide position by cell size)
                int cellX = (int)(pl.X / CELL_SIZE);
                int cellY = (int)(pl.Y / CELL_SIZE);
                string cellKey = $"{currentRoom}:{cellX},{cellY}";

                // Check for room changes
                if (!string.IsNullOrEmpty(currentRoom) && currentRoom != lastRoom)
                {
                    // Scan for exits in new room
                    ScanForExits(lvl, currentRoom);

                    // Set entry exit point = nearest exit to player's current position
                    // This is the exit we just came through - we'll exclude it from distance calculations
                    if (roomExits.TryGetValue(currentRoom, out var newRoomExits) && newRoomExits.Count > 0)
                    {
                        float minDist = float.MaxValue;
                        Vector2 nearestExit = newRoomExits[0];
                        foreach (var exit in newRoomExits)
                        {
                            float dist = Vector2.Distance(pl.Position, exit);
                            if (dist < minDist)
                            {
                                minDist = dist;
                                nearestExit = exit;
                            }
                        }
                        entryExitPoint = nearestExit;
                    }

                    if (!visitedRooms.Contains(currentRoom))
                    {
                        // New room discovered!
                        visitedRooms.Add(currentRoom);
                        reward += NEW_ROOM_REWARD;
                    }
                    else
                    {
                        // Revisiting a room (backtracking)
                        reward += REVISIT_ROOM_PENALTY;
                    }
                    lastRoom = currentRoom;

                    // Reset distance tracking for new room
                    // Prevents comparing distances across different rooms (different exit locations)
                    lastCellDistanceToExit = float.MaxValue;
                    bestDistanceToExit = float.MaxValue;
                }

                // Cell change tracking and distance-based rewards
                if (cellKey != lastCellKey)
                {
                    // Changed cells - calculate distance reward
                    if (USE_DISTANCE_REWARD)
                    {
                        float cellCenterX = cellX * CELL_SIZE + CELL_SIZE / 2f;
                        float cellCenterY = cellY * CELL_SIZE + CELL_SIZE / 2f;
                        Vector2 cellCenter = new Vector2(cellCenterX, cellCenterY);
                        float currentCellDistance = GetDistanceFromPointToNearestExit(lvl, cellCenter, currentRoom);

                        if (currentCellDistance > 0 && currentCellDistance < float.MaxValue &&
                            lastCellDistanceToExit < float.MaxValue)
                        {
                            // Sqrt potential-based reward: φ(d) = -sqrt(d)
                            // reward = sqrt(d_old) - sqrt(d_new)
                            // Gives ~4x higher reward near exit vs far away
                            float oldPotential = (float)Math.Sqrt(lastCellDistanceToExit);
                            float newPotential = (float)Math.Sqrt(currentCellDistance);
                            float shapedReward = (oldPotential - newPotential) * PROGRESS_SCALE;
                            reward += shapedReward;

                            // Distance-based stagnation: reset counter if we improved best distance
                            if (currentCellDistance < bestDistanceToExit)
                            {
                                bestDistanceToExit = currentCellDistance;
                                stepsSinceDistanceImproved = 0;
                            }
                        }

                        // Update for next cell change
                        lastCellDistanceToExit = currentCellDistance;
                    }

                    lastCellKey = cellKey;
                    framesInCurrentCell = 0;
                }
                else
                {
                    // Still in same cell - increment duration
                    framesInCurrentCell++;

                    // Apply penalty if staying too long in same cell
                    if (PENALIZE_IDLING && framesInCurrentCell > IDLE_THRESHOLD)
                    {
                        reward += IDLE_PENALTY_RATE;
                    }
                }

                // Small time penalty to encourage efficiency
                reward += STEP_COST;
            }
            else
            {
                // Player doesn't exist during transitions or is dead
                reward = STEP_COST;
            }

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
                    perfTimer.Restart();
                    var frameData = CaptureFrame(out frameWidth, out frameHeight);
                    timeFrameCapture += perfTimer.Elapsed.TotalMilliseconds;
                    if (frameData != null)
                    {
                        frameBase64 = Convert.ToBase64String(frameData);
                    }
                }
                catch (Exception e)
                {
                }
            }

            // Get current room and state
            string currentRoom = lvl.Session?.Level ?? "";
            int cellX = 0;
            int cellY = 0;

            if (pl != null)
            {
                cellX = (int)(pl.X / CELL_SIZE);
                cellY = (int)(pl.Y / CELL_SIZE);
            }

            // ======== EPISODE TERMINATION LOGIC ========
            // Check termination conditions with priority: Death > Exit > MaxSteps > Stagnation

            // 1. Death check (highest priority)
            bool dead = hasDied;

            // 2. Exit check
            bool reachedExit = false; // TODO: set true if touching a custom flag or level transition

            // 3. Max steps check
            bool maxStepsReached = TERMINATE_ON_MAX_STEPS &&
                                   episodeSteps >= MAX_EPISODE_STEPS;

            // 4. Stagnation check (lowest priority - only if not dead)
            // Don't check stagnation if player is dead (prevents "stagnation during death animation" bug)
            bool stagnated = !dead && hasStagnated;

            bool done = dead || reachedExit || maxStepsReached || stagnated;

            // Calculate temporal progress signals (0-1 normalized)
            // Stagnation progress only counts after warmup period
            // Distance-based stagnation progress (0-1)
            float stagnationProgress = Math.Min(stepsSinceDistanceImproved / (float)STAGNATION_THRESHOLD, 1.0f);
            float episodeProgress = Math.Min(episodeSteps / (float)MAX_EPISODE_STEPS, 1.0f);

            var obs = new {
                px, py, vx, vy, onGround, dashes, stamina,
                frame = frameBase64,
                frameWidth,
                frameHeight,
                stagnationProgress,  // 0-1: How close to stagnation termination (distance-based)
                episodeProgress,     // 0-1: How far through episode
                roomsVisitedCount = visitedRooms.Count  // Number of unique rooms visited this episode
            };

            var info = new {
                dead,
                reachedExit,
                stagnated,
                maxStepsReached,
                episodeSteps,
                stepsSinceDistanceImproved,
                bestDistanceToExit,
                room = currentRoom,
                area = lvl.Session?.Area.GetSID() ?? "unknown",
                roomsVisited = visitedRooms.Count,
                cellX,
                cellY
            };

            return (obs, done, info);
        }

        // Capture the current game frame with optional downsampling and color conversion
        private byte[] CaptureFrame(out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                var gd = Engine.Instance?.GraphicsDevice;
                if (gd == null || gd.IsDisposed)
                {
                    return null;
                }

                int srcWidth = gd.PresentationParameters.BackBufferWidth;
                int srcHeight = gd.PresentationParameters.BackBufferHeight;

                if (srcWidth <= 0 || srcHeight <= 0)
                {
                    return null;
                }

            // Determine target resolution
            width = Settings.FrameWidth > 0 ? Settings.FrameWidth : srcWidth;
            height = Settings.FrameHeight > 0 ? Settings.FrameHeight : srcHeight;

            // Read from back buffer
            var srcBuffer = new Color[srcWidth * srcHeight];
            gd.GetBackBufferData(srcBuffer);

            // NATIVE MODE: Return raw backbuffer RGB without interpolation
            if (Settings.ColorMode == FrameColorMode.Native)
            {

                width = srcWidth;
                height = srcHeight;

                var nativeResult = new byte[width * height * 3];  // RGB format

                for (int i = 0; i < srcBuffer.Length; i++)
                {
                    var c = srcBuffer[i];
                    nativeResult[i * 3 + 0] = c.R;
                    nativeResult[i * 3 + 1] = c.G;
                    nativeResult[i * 3 + 2] = c.B;
                }

                return nativeResult;
            }

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
            catch (Exception e)
            {
                return null;
            }
        }

        // ======== Input mapping ========

        private InputState DecodeAction(int id)
        {
            // Action space (17 actions):
            // 0: Idle
            // 1: Left
            // 2: Right
            // 3: Jump
            // 4: Jump + Left
            // 5: Jump + Right
            // 6: Grab (hold wall)
            // 7: Climb Up (Grab + Up)
            // 8: Climb Down (Grab + Down)
            // 9: Dash Left
            // 10: Dash Right
            // 11: Dash Up
            // 12: Dash Up-Left
            // 13: Dash Up-Right
            // 14: Long Jump (held for 40 frames)
            // 15: Long Jump + Left (held for 40 frames)
            // 16: Long Jump + Right (held for 40 frames)
            // 17: Down (crouch/duck)
            // 18: Dash Down
            // 19: Dash Down-Left
            // 20: Dash Down-Right

            var a = new InputState();

            switch (id)
            {
                case 0: // Idle
                    break;

                case 1: // Left
                    a.MoveX = -1;
                    break;

                case 2: // Right
                    a.MoveX = 1;
                    break;

                case 3: // Jump (neutral)
                    a.JumpTap = true;
                    break;

                case 4: // Jump + Left
                    a.JumpTap = true;
                    a.MoveX = -1;
                    break;

                case 5: // Jump + Right
                    a.JumpTap = true;
                    a.MoveX = 1;
                    break;

                case 6: // Grab (for walls)
                    a.Grab = true;
                    break;

                case 7: // Climb Up
                    a.Grab = true;
                    a.MoveY = -1;
                    break;

                case 8: // Climb Down
                    a.Grab = true;
                    a.MoveY = 1;
                    break;

                case 9: // Dash Left
                    a.Dash = true;
                    a.MoveX = -1;
                    break;

                case 10: // Dash Right
                    a.Dash = true;
                    a.MoveX = 1;
                    break;

                case 11: // Dash Up
                    a.Dash = true;
                     a.MoveY = -1;
                    break;

                case 12: // Dash Up-Left
                    a.Dash = true;
                    a.MoveY = -1;
                    a.MoveX = -1;
                    break;

                case 13: // Dash Up-Right
                    a.Dash = true;
                    a.MoveY = -1;
                    a.MoveX = 1;
                    break;

                case 14: // Long Jump (held for 40 frames via action repeat)
                    a.JumpTap = true;
                    break;

                case 15: // Long Jump + Left (held for 40 frames via action repeat)
                    a.JumpTap = true;
                    a.MoveX = -1;
                    break;

                case 16: // Long Jump + Right (held for 40 frames via action repeat)
                    a.JumpTap = true;
                    a.MoveX = 1;
                    break;

                case 17: // Down (crouch/duck)
                    a.MoveY = 1;
                    break;

                case 18: // Dash Down
                    a.Dash = true;
                    a.MoveY = 1;
                    break;

                case 19: // Dash Down-Left
                    a.Dash = true;
                    a.MoveY = 1;
                    a.MoveX = -1;
                    break;

                case 20: // Dash Down-Right
                    a.Dash = true;
                    a.MoveY = 1;
                    a.MoveX = 1;
                    break;

                default:
                    // Invalid action, do nothing
                    break;
            }

            return a;
        }

        private void ApplyInput(InputState a)
        {

            // Build list of keys to press
            var keysToPress = new List<Keys>();

            // Movement keys
            if (a.MoveX < 0) keysToPress.Add(Keys.Left);
            if (a.MoveX > 0) keysToPress.Add(Keys.Right);
            if (a.MoveY < 0) keysToPress.Add(Keys.Up);
            if (a.MoveY > 0) keysToPress.Add(Keys.Down);

            // Action keys
            if (a.JumpTap) keysToPress.Add(jumpKey);  // C
            if (a.Grab) keysToPress.Add(grabKey);      // Z
            if (a.Dash) keysToPress.Add(dashKey);      // X

            // Get current keyboard state
            var currentState = Keyboard.GetState();

            // Merge with existing keys (preserve any keys the user might be pressing)
            var existingKeys = currentState.GetPressedKeys();
            var allKeys = new HashSet<Keys>(keysToPress);
            foreach (var key in existingKeys)
            {
                allKeys.Add(key);
            }

            // Create new keyboard state
            var newState = new KeyboardState(allKeys.ToArray());

            // Update state (following TAS pattern)

            MInput.Keyboard.PreviousState = Keyboard.GetState();;
            MInput.Keyboard.CurrentState = newState;

            // Trigger virtual input update
            MInput.UpdateVirtualInputs();

            // Also set analog movement for non-keyboard inputs
            Input.MoveX.Value = a.MoveX;
            Input.MoveY.Value = a.MoveY;
            if (a.Dash && a.Aim != Vector2.Zero)
            {
                Input.Aim.Value = a.Aim.SafeNormalize();
            }
        }

        // Legacy input method (fallback if keyboard state not available)
        private void ApplyInputLegacy(InputState a)
        {
            // Set movement direction
            Input.MoveX.Value = a.MoveX;
            Input.MoveY.Value = a.MoveY;

            // Handle Jump
            if (a.JumpTap)
            {
                ForceButtonPress(Input.Jump);
            }

            // Handle Grab
            if (a.Grab)
            {
                ForceButtonHold(Input.Grab);
            }
            else
            {
                ForceButtonRelease(Input.Grab);
            }

            // Handle Dash
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
                    // Set DebugOverridePressed - handle both bool? and Keys? types
                    if (buttonCheck.FieldType == typeof(bool?))
                    {
                        buttonCheck.SetValue(button, (bool?)true);
                    }
                    else if (buttonCheck.FieldType == typeof(Keys?))
                    {
                        // Use a dummy key to trigger the press
                        buttonCheck.SetValue(button, (Keys?)Keys.C);
                    }
                }
            }
            catch (Exception e)
            {
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
                    if (buttonCheck.FieldType == typeof(bool?))
                    {
                        buttonCheck.SetValue(button, (bool?)true);
                    }
                    else if (buttonCheck.FieldType == typeof(Keys?))
                    {
                        // Use a dummy key to hold the button
                        buttonCheck.SetValue(button, (Keys?)Keys.V);
                    }
                }
                else if (buttonBuffered != null)
                {
                    // Fallback: Simulate hold by repeatedly refreshing buffer
                    // This keeps the button "pressed" by maintaining buffer counter
                    buttonBuffered.SetValue(button, 0.1f);
                }
            }
            catch (Exception e)
            {
            }
        }

        // Release a held button
        private void ForceButtonRelease(VirtualButton button)
        {
            try
            {
                if (buttonCheck != null)
                {
                    // Release by setting DebugOverridePressed to null (works for both types)
                    if (buttonCheck.FieldType == typeof(bool?))
                    {
                        buttonCheck.SetValue(button, (bool?)null);
                    }
                    else if (buttonCheck.FieldType == typeof(Keys?))
                    {
                        buttonCheck.SetValue(button, (Keys?)null);
                    }
                }
                // In buffer-only mode, no explicit release needed
                // Not calling ForceButtonHold will let bufferCounter decay naturally
            }
            catch (Exception e)
            {
            }
        }

        private void ClearInput()
        {
            var currentState = Keyboard.GetState();
            var emptyState = new KeyboardState();  // No keys pressed

            MInput.Keyboard.PreviousState = currentState;
            MInput.Keyboard.CurrentState = emptyState;
            

            MInput.UpdateVirtualInputs();
            // Reset analog inputs
            Input.MoveX.Value = 0;
            Input.MoveY.Value = 0;
            Input.Aim.Value = Vector2.Zero;
        }

        private void ClearInputLegacy()
        {
            // Clear button buffers to prevent sticking
            Input.Jump.ConsumeBuffer();
            Input.Dash.ConsumeBuffer();
            Input.Grab.ConsumeBuffer();

            // Reset DebugOverridePressed to null to prevent sticking
            if (buttonCheck != null)
            {
                try
                {
                    if (buttonCheck.FieldType == typeof(bool?))
                    {
                        buttonCheck.SetValue(Input.Jump, (bool?)null);
                        buttonCheck.SetValue(Input.Dash, (bool?)null);
                        buttonCheck.SetValue(Input.Grab, (bool?)null);
                    }
                    else if (buttonCheck.FieldType == typeof(Keys?))
                    {
                        buttonCheck.SetValue(Input.Jump, (Keys?)null);
                        buttonCheck.SetValue(Input.Dash, (Keys?)null);
                        buttonCheck.SetValue(Input.Grab, (Keys?)null);
                    }
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
