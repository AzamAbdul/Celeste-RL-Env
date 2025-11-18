# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RLBridge is an Everest mod for Celeste that exposes a reinforcement learning interface via a JSONL TCP server. It allows external RL agents to control the player character, receive observations, and manage game state through a simple JSON-RPC-style protocol.

## Building and Development

### Build Commands

```bash
# Build the mod (Debug configuration)
dotnet build Source/RLBridge.csproj

# Build with Release configuration (creates zip package)
dotnet build Source/RLBridge.csproj -c Release

# Clean build artifacts
dotnet clean Source/RLBridge.csproj
```

### Project Structure

- **Source/RLBridge.csproj** - Main project file in the `Source/` directory
- **Output** - DLL and PDB are automatically copied to `bin/` after build
- **Packaging** - Release builds create `RLBridge.zip` containing the mod bundle

### Celeste Mod Dependencies

The mod requires:
- Everest (version 1.5935.0+)

Runtime dependencies are referenced from `CelestePrefix` (defaults to `../../..` relative to the Celeste.dll location).

## Architecture

### Core Components

**RLBridgeModule** (`Source/RLBridgeModule.cs`)
- Main Everest module that orchestrates the RL bridge
- Hooks into `Level.Update` to intercept game loop and inject inputs
- Manages frame stepping, reset logic, and observation/reward computation
- TCP server runs on localhost port 17653

**JsonLServer** (`Source/JsonLServer.cs`)
- Lightweight JSONL (JSON Lines) TCP server for bidirectional communication
- Single client connection at a time
- Thread-safe message queuing with `TrySend(object)` serialization

**SpeedrunToolInterop** (`Source/SpeedrunToolInterop.cs`)
- Reflection-based bridge to Speedrun Tool savestate system
- Used for fast episode resets via savestate loading
- Falls back to `Level.Reload()` if unavailable

### RL Interface Protocol

The server accepts JSONL messages with an `"op"` field:

- `{"op": "ping"}` - Health check, responds with `{"ok": true}`
- `{"op": "reset", "slot": 0}` - Reset episode using savestate slot (optional, requires Speedrun Tool)
- `{"op": "step", "action": 5, "repeat": 1}` - Execute action for N frames

Step responses include: `{obs, reward, done, info}` with player state.

### Action Space

Actions are encoded as integers (see `DecodeAction` in RLBridgeModule.cs:208):
- 0: No-op
- 1: Move left
- 2: Move right
- 3: Grab
- 4: Jump (tap)
- 5-12: Dash in 8 directions (up, up-right, right, down-right, down, down-left, left, up-left)

### Observation Space

Observations include:
- `px`, `py`: Player X/Y position
- `vx`, `vy`: Player X/Y velocity
- `onGround`: Boolean ground contact
- `dashes`: Remaining dash count
- `stamina`: Current stamina value

### Reward Function

Current implementation (RLBridgeModule.cs:160):
- Reward = distance traveled since last step - 0.001 (living cost)
- Episode termination on player death or custom exit condition
- Customize `ReadObsRewardDone` for specific tasks

## Key Implementation Details

### Frame Stepping Synchronization

The mod uses a staged input system:
1. Client sends `step` command with action
2. `stagedInput` and `framesToRun` are set
3. Each `Level.Update` applies input if `framesToRun > 0`
4. After frames complete, observation is sent back

This ensures the TCP thread never directly mutates game state.

### Input Handling

Input is applied directly through Celeste's `Input` API with proper button edge detection:
- **Movement**: Continuous analog input via `Input.MoveX`
- **Buttons**: State tracking prevents spurious presses (only triggers on rising edge)
- **Dash**: Normalized aim vector via `Input.Aim` + `ConsumePress()`
- Input state is cleared when not stepping to prevent control sticking

### Interop Pattern

Speedrun Tool interop uses reflection to avoid hard dependencies:
- Checked at runtime via `Bind()`
- Graceful fallback if mods not installed
- `IsAvailable` flag guards all method calls

## Development Notes

- The server binds to `127.0.0.1:17653` only (localhost for security)
- Input uses direct Celeste Input API - not deterministic across different runs (RNG/physics may vary slightly)
- For deterministic execution, consider setting fixed seeds at the Celeste engine level or using custom physics hooks
- Progress tracking baseline resets on episode reset to avoid reward discontinuities
- Customize reward shaping and observation space for specific RL tasks (platforming, speedrunning, etc.)
- Button edge detection ensures clean press/release semantics without spurious inputs
