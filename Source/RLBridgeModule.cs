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

        // Frame rate tracking
        private int levelUpdateCallCount = 0;
        private double levelUpdateStartTime = 0.0;
        private bool vsyncDisabled = false;

        // Progress tracking for rewards (you can customize)
        private float maxXProgress = 0f;  // Maximum X position reached (rightward progress)
        private float startX = 0f;        // Starting X position for the episode

        // Room-based progression tracking
        private HashSet<string> visitedRooms = new HashSet<string>();
        private string lastRoom = "";

        // Exit tracking (room adjacency from Level.Bounds)
        private Dictionary<string, List<Vector2>> roomExits = new Dictionary<string, List<Vector2>>();
        private string lastExitScanRoom = "";

        // Cell-based exploration tracking (grid within rooms)
        private HashSet<string> visitedCells = new HashSet<string>();
        private string lastCellKey = "";
        private int framesInCurrentCell = 0;  // Track how long in same cell

        // Episode termination tracking
        private int stepsSinceLastNewCell = 0;
        private int episodeSteps = 0;

        // Death tracking (to apply penalty only once)
        private bool hasDied = false;

        // Transition error handling (for high-speed race conditions)
        private int consecutiveTransitionErrors = 0;
        private const int MAX_TRANSITION_RETRIES = 30;  // ~0.5s at 60fps, less at high speed

        // Unstable state tracking (to prevent Python from hanging during long transitions)
        private int framesWaitingForStability = 0;
        private const int MAX_UNSTABLE_FRAMES = 60;  // ~1s at 60fps, force response after this

        // Warmup period at episode start before stagnation detection applies (in agent steps, not frames)
        // With action_repeat=4, 75 steps = 300 frames
        private const int STAGNATION_WARMUP_STEPS = 100;  // First 300 frames (75 steps @ action_repeat=4)

        // ======== REWARD/PENALTY/THRESHOLD CONFIGURATION ========
        // Edit these values directly instead of using the settings menu

        // Episode termination thresholds
        private const int STAGNATION_THRESHOLD = 750;        // Frames without discovering new cell
        private const int MAX_EPISODE_STEPS = 50000;         // Maximum frames per episode
        private const bool TERMINATE_ON_STAGNATION = true;  // Enable stagnation termination
        private const bool TERMINATE_ON_MAX_STEPS = true;   // Enable max steps termination

        // Rewards
        private const float NEW_ROOM_REWARD = 2000.0f;       // Reward for discovering new room
        private const float NEW_CELL_REWARD = 0.0f;         // Reward for discovering new cell
        private const float EXIT_PROXIMITY_MULTIPLIER = 200.0f; // Multiplier for exit proximity reward
        private const float EXIT_DISTANCE_SCALE = 0.01f;    // Scale factor for exponential decay (1/1000 pixels)

        // Penalties
        private const float DEATH_PENALTY = -500.0f;         // One-time penalty on death
        private const float STAGNATION_PENALTY = -500f;     // One-time penalty on stagnation termination
        private const float REVISIT_ROOM_PENALTY = -1.0f;   // Penalty for backtracking to visited room
        private const float REVISIT_CELL_PENALTY = 0f;  // Penalty for revisiting cell
        private const float STEP_COST = -0.001f;             // Per-step cost to encourage efficiency
        private const float IDLE_PENALTY_RATE = -0.01f;     // Per-frame penalty when idle in same cell

        // Exploration settings
        private const bool USE_CELL_TRACKING = true;        // Enable grid-based cell tracking
        private const float CELL_SIZE = 16.0f;              // Pixels per grid cell
        private const bool PENALIZE_IDLING = true;          // Apply penalty for staying in same cell
        private const int IDLE_THRESHOLD = 10;              // Frames before idle penalty applies

        // Distance-based reward (optional)
        private const bool USE_DISTANCE_REWARD = true;     // Enable rightward progress reward
        private const float PROGRESS_SCALE = 0.01f;         // Scale factor for distance reward

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
                Logger.Log(LogLevel.Warn, "RLBridge",
                    $"Found DebugOverridePressed but incompatible type: {buttonCheck.FieldType.Name}. Using buffer-only mode for inputs.");
                buttonCheck = null;
            }

            if (buttonCheck != null)
            {
                Logger.Log(LogLevel.Info, "RLBridge",
                    $"Found DebugOverridePressed field with type: {buttonCheck.FieldType.Name}");
            }

            // Get the Binding field
            buttonBinding = vbType.GetField("Binding", BindingFlags.Instance | BindingFlags.Public);

            // If we have the Binding field, investigate the Binding type
            if (buttonBinding != null)
            {
                Type bindingType = buttonBinding.FieldType;
                Logger.Log(LogLevel.Info, "RLBridge", $"Found Binding field, investigating {bindingType.Name} type:");

                // List all Binding fields
                foreach (var field in bindingType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Logger.Log(LogLevel.Info, "RLBridge", $"  Binding field: {field.Name} ({field.FieldType.FullName})");
                }

                // Try to find a pressed state field in Binding
                bindingPressed = bindingType.GetField("pressed", BindingFlags.Instance | BindingFlags.NonPublic);
                if (bindingPressed == null)
                {
                    bindingPressed = bindingType.GetField("Pressed", BindingFlags.Instance | BindingFlags.Public);
                }

                if (bindingPressed != null)
                {
                    Logger.Log(LogLevel.Info, "RLBridge", $"Found Binding.pressed field: {bindingPressed.FieldType.FullName}");
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
                    Logger.Log(LogLevel.Info, "RLBridge", $"Found Node type: {nodeType.FullName}");
                    Logger.Log(LogLevel.Info, "RLBridge", "Node type members:");

                    // List all fields in Node
                    foreach (var field in nodeType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        Logger.Log(LogLevel.Info, "RLBridge", $"  Node field: {field.Name} ({field.FieldType.FullName})");
                    }

                    // List all methods in Node
                    foreach (var method in nodeType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (method.Name.Contains("Check") || method.Name.Contains("Press") || method.Name.Contains("Update"))
                        {
                            Logger.Log(LogLevel.Info, "RLBridge", $"  Node method: {method.Name}");
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
                    Logger.Log(LogLevel.Info, "RLBridge",
                        "Successfully found keyboard state fields - will use direct keyboard injection");
                }
                else
                {
                    Logger.Log(LogLevel.Error, "RLBridge",
                        "Failed to find keyboard state fields - input will not work!");
                }
            }

            if (buttonBuffered == null)
            {
                Logger.Log(LogLevel.Error, "RLBridge", "CRITICAL: Could not find bufferCounter field - button input will NOT work!");

                // List all fields for debugging
                Logger.Log(LogLevel.Error, "RLBridge", "Available VirtualButton fields:");
                foreach (var field in vbType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Logger.Log(LogLevel.Error, "RLBridge", $"  - {field.Name} ({field.FieldType.FullName})");
                }
            }
            else if (buttonCheck == null)
            {
                Logger.Log(LogLevel.Info, "RLBridge",
                    "Using buffer-only mode for button inputs (DebugOverridePressed not available). Grab/climb will use repeated buffering.");
            }
            else
            {
                Logger.Log(LogLevel.Info, "RLBridge",
                    "Successfully found VirtualButton fields: bufferCounter and DebugOverridePressed");
            }

            // Start TCP server (localhost only)
            // Port priority: 1) Environment variable, 2) Settings, 3) DefaultPort
            int port = DefaultPort;

            string envPort = Environment.GetEnvironmentVariable("RLBRIDGE_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsedPort))
            {
                port = parsedPort;
                Logger.Log(LogLevel.Info, "RLBridge", $"Using port from RLBRIDGE_PORT environment variable: {port}");
            }
            else if (Settings.TcpPort != DefaultPort)
            {
                port = Settings.TcpPort;
                Logger.Log(LogLevel.Info, "RLBridge", $"Using port from settings: {port}");
            }

            server = new JsonLServer(IPAddress.Loopback, port, HandleMessage);
            server.Start();
            Logger.Log(LogLevel.Info, "RLBridge", $"Started JSONL server at 127.0.0.1:{port}");
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
                // Disable VSync once when we know graphics device is ready
                if (!vsyncDisabled)
                {
                    try
                    {
                        // Disable fixed timestep (allows variable frame rate)
                        Engine.Instance.IsFixedTimeStep = false;

                        // Set target to run as fast as possible (TimeSpan.Zero = unlimited)
                        Engine.Instance.TargetElapsedTime = System.TimeSpan.Zero;

                        // Disable VSync
                        Engine.Instance.GraphicsDevice.PresentationParameters.PresentationInterval = PresentInterval.Immediate;

                        // Don't sleep between frames
                        Engine.Instance.InactiveSleepTime = System.TimeSpan.Zero;

                        Logger.Log(LogLevel.Info, "RLBridge", "Disabled VSync and frame rate limits for unlimited frame rate");
                        vsyncDisabled = true;
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Warn, "RLBridge", $"Failed to disable VSync: {e.Message}");
                        vsyncDisabled = true; // Don't keep trying
                    }
                }

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
                        // Clear input during unstable state
                        ClearInput();
                        // Run game update to let transition complete
                        orig(self);
                        return;
                    }

                    if (resetDelayFrames > 0)
                    {
                        resetDelayFrames--;
                        // Clear input during delay to prevent interference
                        ClearInput();
                        // Still run game update during delay
                        orig(self);
                        return;
                    }

                    // Delay complete and game is stable, execute reset
                    Logger.Log(LogLevel.Debug, "RLBridge", "Executing reset now");
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

                    // Reset room tracking and pre-fill with starting room (don't reward starting location)
                    visitedRooms.Clear();
                    string currentRoomName = self.Session?.Level ?? "";
                    if (!string.IsNullOrEmpty(currentRoomName))
                    {
                        visitedRooms.Add(currentRoomName);  // Pre-fill starting room
                        Logger.Log(LogLevel.Info, "RLBridge", $"Episode starting in room: {currentRoomName} (pre-filled, no reward)");

                        // Scan for exits in starting room
                        ScanForExits(self, currentRoomName);
                    }
                    lastRoom = currentRoomName;

                    // Reset cell tracking and pre-fill with starting cells (don't reward starting location)
                    visitedCells.Clear();
                    var pl = self.Tracker?.GetEntity<Player>();
                    if (pl != null)
                    {
                        int startCellX = (int)Math.Floor(pl.Position.X / CELL_SIZE);
                        int startCellY = (int)Math.Floor(pl.Position.Y / CELL_SIZE);
                        string startCellKey = $"{currentRoomName}:{startCellX},{startCellY}";
                        visitedCells.Add(startCellKey);  // Pre-fill starting cell
                    }
                    lastCellKey = "";
                    framesInCurrentCell = 0;

                    // Reset episode tracking
                    stepsSinceLastNewCell = 0;
                    episodeSteps = 0;
                    hasDied = false;
                    consecutiveTransitionErrors = 0;  // Reset transition error counter
                    framesWaitingForStability = 0;  // Reset stability wait counter

                    Logger.Log(LogLevel.Info, "RLBridge",
                        $"[RESET] Counters reset: episodeSteps=0, stepsSinceLastNewCell=0");
                    Logger.Log(LogLevel.Info, "RLBridge",
                        $"[CONFIG] StagnationThreshold={STAGNATION_THRESHOLD}, MaxEpisodeSteps={MAX_EPISODE_STEPS}, WarmupSteps={STAGNATION_WARMUP_STEPS}");

                    // Mark that we need to send observation after reset
                    responseSent = false;
                }

                // Only apply input and calculate rewards when game is stable
                // During transitions, pause frame counting and let game stabilize
                bool isRunningStep = framesToRun > 0 && isStable;
                if (isRunningStep)
                {
                    ApplyInput(stagedInput);
                    framesToRun--;
                    framesWaitingForStability = 0;  // Reset since we're making progress
                }
                else
                {
                    // Only clear input when there's an active step waiting
                    // This allows manual control when framesToRun == 0
                    if (framesToRun > 0)
                    {
                        ClearInput();
                    }

                    // Track how long we've been waiting for stability
                    if (framesToRun > 0 && !isStable)
                    {
                        framesWaitingForStability++;

                        // If we've been waiting too long, force completion to prevent Python hang
                        if (framesWaitingForStability >= MAX_UNSTABLE_FRAMES)
                        {
                            Logger.Log(LogLevel.Warn, "RLBridge",
                                $"Forcing step completion after {framesWaitingForStability} unstable frames");
                            framesToRun = 0;  // Force completion
                            framesWaitingForStability = 0;
                        }
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
                            Logger.Log(LogLevel.Debug, "RLBridge",
                                $"Transition race condition (retry {consecutiveTransitionErrors}/{MAX_TRANSITION_RETRIES})");
                            transitionError = true;
                        }
                        else
                        {
                            // Too many retries - let outer handler deal with it
                            Logger.Log(LogLevel.Warn, "RLBridge",
                                $"Transition failed after {MAX_TRANSITION_RETRIES} retries, aborting");
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

                // Accumulate rewards during action repeat frames (only when stable)
                if (isRunningStep)
                {
                    try
                    {
                        perfTimer.Restart();
                        var frameReward = CalculateFrameReward(self);
                        timeRewardCalc += perfTimer.Elapsed.TotalMilliseconds;
                        accumulatedReward += frameReward;

                        // Log every 100th frame to track progress
                        if (episodeSteps % 100 == 0)
                        {
                            Logger.Log(LogLevel.Info, "RLBridge",
                                $"[FRAME] episodeSteps={episodeSteps}, framesToRun={framesToRun}, stepsSinceLastNewCell={stepsSinceLastNewCell}");
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Warn, "RLBridge", $"Failed to calculate reward: {e.Message}");
                    }
                }

                // When we finish a request, send an update back to the client
                // Send response even during transitions to prevent Python from hanging
                if (framesToRun == 0 && !responseSent)
                {
                    try
                    {
                        Logger.Log(LogLevel.Debug, "RLBridge", "About to read observation and send response");
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
                        Logger.Log(LogLevel.Debug, "RLBridge", $"Sending observation (done={done})");
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

                            Logger.Log(LogLevel.Info, "RLBridge",
                                $"C# Timing (avg per step): GameUpdate={timeGameUpdate/perfSteps:F2}ms, " +
                                $"RewardCalc={timeRewardCalc/perfSteps:F2}ms, " +
                                $"ObsRead={timeObsRead/perfSteps:F2}ms, " +
                                $"SendResponse={timeSendResponse/perfSteps:F2}ms, " +
                                $"FrameCapture={timeFrameCapture/perfSteps:F2}ms, " +
                                $"TOTAL={totalCSharp/perfSteps:F2}ms");

                            Logger.Log(LogLevel.Info, "RLBridge",
                                $"Level_Update call rate: {updateFPS:F1} FPS (target: {Settings.SpeedMultiplier * 60} FPS), " +
                                $"Total calls: {levelUpdateCallCount}, Elapsed: {elapsedTime:F2}s");
                        }

                        // Auto-reset on episode termination (with delay to let scene stabilize)
                        if (done)
                        {
                            pendingReset = true;
                            resetSlot = 0;  // Reset to default savestate
                            resetDelayFrames = 25;  // Wait 10 frames before resetting (longer delay for stability)
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Error, "RLBridge", $"Failed to send observation: {e.Message}");
                        Logger.Log(LogLevel.Error, "RLBridge", $"Stack trace: {e.StackTrace}");
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
                consecutiveTransitionErrors = 0;
                framesWaitingForStability = 0;
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
                Logger.Log(LogLevel.Info, "RLBridge", "[DEATH DEBUG] GetValidPlayer: Exception getting player from tracker");
                return null;
            }

            if (pl == null)
            {
                Logger.Log(LogLevel.Info, "RLBridge", "[DEATH DEBUG] GetValidPlayer: Player is null");
                return null;
            }

            if (pl.Scene == null)
            {
                Logger.Log(LogLevel.Info, "RLBridge", $"[DEATH DEBUG] GetValidPlayer: Player.Scene is null, pl.Dead={pl.Dead}");
                return null;
            }

            // Check if player is being removed or added to scene
            try
            {
                bool removing = pl.Scene.Entities.removing.Contains(pl);
                bool adding = pl.Scene.Entities.adding.Contains(pl);
                hasDied = hasDied || pl.Dead;

                if (removing || adding)
                {
                    Logger.Log(LogLevel.Info, "RLBridge",
                        $"[DEATH DEBUG] GetValidPlayer: Player being removed/added, pl.Dead={pl.Dead}, removing={removing}, adding={adding}");
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
                        Logger.Log(LogLevel.Info, "RLBridge",
                            $"[STEP] Received step command: action={action}, repeat={repeat}, framesToRun={framesToRun}");
                        // Response will be sent after frames advance in Level_Update
                        break;

                    case "set_render":
                        bool enabled = root.GetProperty("enabled").GetBoolean();
                        renderingEnabled = enabled;
                        Logger.Log(LogLevel.Info, "RLBridge",
                            $"Rendering {(enabled ? "enabled" : "disabled")}");
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
                Logger.Log(LogLevel.Info, "RLBridge", $"current bounds. Top: {currentBounds.Top} Bottom: {currentBounds.Bottom} Left: {currentBounds.Left} Right: {currentBounds.Right}");
                foreach (var room in lvl.Session.MapData.Levels)
                {
                    // Skip the current room
                    if (room.Name == roomName)
                        continue;

                    Rectangle otherBounds = room.Bounds;
                    Logger.Log(LogLevel.Info, "RLBridge", $"other bounds. Top: {otherBounds.Top} Bottom: {otherBounds.Bottom} Left: {otherBounds.Left} Right: {otherBounds.Right}");
                    Logger.Log(LogLevel.Info, "RLBridge",
                            $"other bounds: {otherBounds}");

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
                Logger.Log(LogLevel.Info, "RLBridge", $"Scanned {exits.Count} exits in room {roomName}");
            }
            else
            {
                Logger.Log(LogLevel.Warn, "RLBridge", $"No exits found in room {roomName}");
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

            // Find nearest exit
            float minDistance = float.MaxValue;
            foreach (var exitPos in exits)
            {
                float distance = Vector2.Distance(position, exitPos);
                if (distance < minDistance)
                    minDistance = distance;
            }

            return minDistance;
        }

        // Generate visited cell map for current room
        // Returns a flat array of bytes (0=unvisited, 255=visited) for the cell grid
        private List<byte> GenerateVisitedCellMap(Level lvl, string currentRoom)
        {
            var visitedMap = new List<byte>();

            if (lvl == null || string.IsNullOrEmpty(currentRoom))
                return visitedMap;

            // Get room bounds
            Rectangle roomBounds = lvl.Bounds;
            int roomWidth = roomBounds.Width;
            int roomHeight = roomBounds.Height;

            // Calculate grid dimensions (cells are CELL_SIZE pixels)
            int gridWidth = (int)Math.Ceiling(roomWidth / (float)CELL_SIZE);
            int gridHeight = (int)Math.Ceiling(roomHeight / (float)CELL_SIZE);

            // Generate map: row-major order (top to bottom, left to right)
            for (int gridY = 0; gridY < gridHeight; gridY++)
            {
                for (int gridX = 0; gridX < gridWidth; gridX++)
                {
                    // Calculate cell coordinates relative to room origin
                    int cellX = (int)(roomBounds.Left / CELL_SIZE) + gridX;
                    int cellY = (int)(roomBounds.Top / CELL_SIZE) + gridY;
                    string cellKey = $"{currentRoom}:{cellX},{cellY}";

                    // Check if visited
                    byte value = (byte)(visitedCells.Contains(cellKey) ? 255 : 0);
                    visitedMap.Add(value);
                }
            }

            return visitedMap;
        }

        private float CalculateFrameReward(Level lvl)
        {
            var pl = GetValidPlayer(lvl);
            string currentRoom = lvl.Session?.Level ?? "";
            float reward = 0f;

            // Track player state for death detection (used in ReadObsDone)
            // Don't apply death penalty here - it will be applied at episode end
            hasDied = hasDied || (pl != null && pl.Dead);

            // Only calculate rewards if player is valid AND alive
            // Skip reward calculation during death animation to prevent stagnation false positives
            if (!hasDied)
            {
                // Increment episode step counter (only when alive)
                episodeSteps++;
                stepsSinceLastNewCell++;
                // Calculate cell coordinates (divide position by cell size)
                int cellX = (int)(pl.X / CELL_SIZE);
                int cellY = (int)(pl.Y / CELL_SIZE);
                string cellKey = $"{currentRoom}:{cellX},{cellY}";

                // Check for room changes
                if (!string.IsNullOrEmpty(currentRoom) && currentRoom != lastRoom)
                {
                    // Scan for exits in new room
                    ScanForExits(lvl, currentRoom);

                    if (!visitedRooms.Contains(currentRoom))
                    {
                        // New room discovered!
                        visitedRooms.Add(currentRoom);
                        reward += NEW_ROOM_REWARD;
                        Logger.Log(LogLevel.Info, "RLBridge", $"New room discovered: {currentRoom} (total: {visitedRooms.Count})");
                    }
                    else
                    {
                        // Revisiting a room (backtracking)
                        reward += REVISIT_ROOM_PENALTY;
                    }
                    lastRoom = currentRoom;
                }

                // Check for new cell exploration
                if (USE_CELL_TRACKING)
                {
                    if (cellKey != lastCellKey)
                    {
                        // Changed cells
                        if (!visitedCells.Contains(cellKey))
                        {
                            // New cell discovered!
                            visitedCells.Add(cellKey);
                            stepsSinceLastNewCell = 0;  // Reset stagnation counter

                            // Calculate reward based on cell's distance to nearest exit
                            // Encourages exploring cells closer to the exit
                            // Cell center position
                            float cellCenterX = cellX * CELL_SIZE + CELL_SIZE / 2f;
                            float cellCenterY = cellY * CELL_SIZE + CELL_SIZE / 2f;
                            Vector2 cellCenter = new Vector2(cellCenterX, cellCenterY);

                            // Get distance from cell center to nearest exit
                            float cellToExitDistance = GetDistanceFromPointToNearestExit(lvl, cellCenter, currentRoom);

                            if (cellToExitDistance > 0 && cellToExitDistance < float.MaxValue)
                            {
                                // Step normalization for efficiency (same as before)
                                // Early episode: full reward, late episode: reduced reward
                                float stepNormalization = (float)Math.Exp(-episodeSteps / 20000.0f);

                                // Reward = e^(-distance * scale) * multiplier * efficiency
                                // Closer to exit = higher reward, faster discovery = higher reward
                                float cellExplorationReward = (float)Math.Exp(-cellToExitDistance * EXIT_DISTANCE_SCALE)
                                    * EXIT_PROXIMITY_MULTIPLIER * stepNormalization;
                                reward += cellExplorationReward;

                                Logger.Log(LogLevel.Info, "RLBridge",
                                    $"[NEW CELL] {cellKey} @ step {episodeSteps}: dist={cellToExitDistance:F1}, reward={cellExplorationReward:F2}, norm={stepNormalization:F3}");
                            }
                            else
                            {
                                // No exit found, use flat reward (shouldn't happen often)
                                reward += NEW_CELL_REWARD;
                                Logger.Log(LogLevel.Info, "RLBridge",
                                    $"[NEW CELL] {cellKey} discovered (no exit detected)");
                            }
                        }
                        else
                        {
                            // Revisiting a cell (backtracking within room)
                            reward += REVISIT_CELL_PENALTY;
                        }
                        lastCellKey = cellKey;
                        framesInCurrentCell = 0;  // Reset duration counter
                    }
                    else
                    {
                        // Still in same cell - increment duration
                        framesInCurrentCell++;

                        // Apply penalty if staying too long in same cell
                        // Don't scale by speed - agent needs same number of frames regardless of simulation speed
                        if (PENALIZE_IDLING && framesInCurrentCell > IDLE_THRESHOLD)
                        {
                            // Flat penalty per frame (linear, not quadratic)
                            reward += IDLE_PENALTY_RATE;
                        }
                    }
                }

                // Small time penalty to encourage efficiency
                reward += STEP_COST;

                // Exit proximity reward is now only given on new cell discovery
                // (see cell exploration section above)
                // This prevents reward farming by camping near exits

                // Optional: small distance-based reward for progress within a room
                if (USE_DISTANCE_REWARD)
                {
                    float currentX = pl.X;
                    float newProgress = Math.Max(currentX, maxXProgress);
                    float progressDelta = newProgress - maxXProgress;
                    maxXProgress = newProgress;
                    reward += progressDelta * PROGRESS_SCALE;
                }
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
                    Logger.Log(LogLevel.Error, "RLBridge", $"Failed to capture frame: {e.Message}");
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
            bool stagnated = !dead &&  // Death takes precedence
                            TERMINATE_ON_STAGNATION &&
                            episodeSteps >= STAGNATION_WARMUP_STEPS &&
                            stepsSinceLastNewCell >= STAGNATION_THRESHOLD;

            bool done = dead || reachedExit || maxStepsReached || stagnated;

            // Calculate temporal progress signals (0-1 normalized)
            // Stagnation progress only counts after warmup period
            float stagnationProgress = episodeSteps < STAGNATION_WARMUP_STEPS ? 0.0f :
                Math.Min(stepsSinceLastNewCell / (float)STAGNATION_THRESHOLD, 1.0f);
            float episodeProgress = Math.Min(episodeSteps / (float)MAX_EPISODE_STEPS, 1.0f);

            // Generate visited cell map for current room
            List<byte> visitedMap = GenerateVisitedCellMap(lvl, currentRoom);

            // Calculate grid dimensions for visited map
            Rectangle roomBounds = lvl.Bounds;
            int gridWidth = (int)Math.Ceiling(roomBounds.Width / (float)CELL_SIZE);
            int gridHeight = (int)Math.Ceiling(roomBounds.Height / (float)CELL_SIZE);

            var obs = new {
                px, py, vx, vy, onGround, dashes, stamina,
                frame = frameBase64,
                frameWidth,
                frameHeight,
                stagnationProgress,  // 0-1: How close to stagnation termination
                episodeProgress,     // 0-1: How far through episode
                visitedMap,          // Flat array of bytes (0=unvisited, 255=visited)
                visitedMapWidth = gridWidth,   // Grid width (in cells)
                visitedMapHeight = gridHeight  // Grid height (in cells)
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
                Logger.Log(LogLevel.Info, "RLBridge",
                    $"Native mode: Capturing at {srcWidth}x{srcHeight} resolution");

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
                Logger.Log(LogLevel.Warn, "RLBridge", $"Failed to capture frame: {e.Message}");
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
                    a.Aim = new Vector2(-1, 0);
                    break;

                case 10: // Dash Right
                    a.Dash = true;
                    a.Aim = new Vector2(1, 0);
                    break;

                case 11: // Dash Up
                    a.Dash = true;
                    a.Aim = new Vector2(0, -1);
                    break;

                case 12: // Dash Up-Left
                    a.Dash = true;
                    a.Aim = new Vector2(-1, -1).SafeNormalize();
                    break;

                case 13: // Dash Up-Right
                    a.Dash = true;
                    a.Aim = new Vector2(1, -1).SafeNormalize();
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
                Logger.Log(LogLevel.Error, "RLBridge", $"Failed to release button: {e.Message}");
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
