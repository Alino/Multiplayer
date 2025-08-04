# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the RimWorld Multiplayer mod, a C# project that enables cooperative multiplayer gameplay in RimWorld. The project is structured as a Visual Studio solution with multiple projects targeting different aspects:

- **Client**: Main mod assembly that loads into RimWorld
- **Common**: Shared code between client and server
- **Server**: Standalone dedicated server
- **MultiplayerLoader**: Prepatcher for early initialization
- **Tests**: Unit tests using NUnit

## Development Commands

### Building
```bash
# Build the entire solution (Release configuration)
cd Source
dotnet build --configuration Release

# Build for development (Debug configuration)
dotnet build --configuration Debug

# Build and automatically copy assemblies to RimWorld mod directory
dotnet build  # The Client project has a CopyToRimworld target
```

### Testing
```bash
# Run all tests
cd Source
dotnet test

# Run specific test project
dotnet test Tests/Tests.csproj

# Run tests with detailed output
dotnet test --verbosity normal
```

### Workshop Release
```bash
# Create workshop bundle (automated script)
./workshop_bundler.sh
# This script builds, packages, and creates Multiplayer-v{VERSION}.zip for Steam Workshop
```

## Architecture Overview

### Core Components

**Client Architecture (`Source/Client/`)**:
- `Multiplayer.cs`: Main entry point and singleton manager
- `MultiplayerSession.cs`: Manages active multiplayer sessions
- `MultiplayerGame.cs`: Game state synchronization
- `Syncing/`: Handles object serialization and synchronization across clients
- `Networking/`: Network connection management (Steam, LiteNet, in-memory)
- `Patches/`: Harmony patches to intercept and sync RimWorld methods
- `Persistent/`: Long-running session managers (caravans, trading, rituals)

**Server Architecture (`Source/Common/` & `Source/Server/`)**:
- `MultiplayerServer.cs`: Core server logic and game loop
- `PlayerManager.cs`: Player connection and state management  
- `CommandHandler.cs`: Processes and validates client commands
- `WorldData.cs`: Maintains authoritative game state
- `FreezeManager.cs`: Handles game pause/resume coordination

**Synchronization System (`Source/Client/Syncing/` & `Source/Common/Syncing/`)**:
- `SyncSerialization.cs`: Core serialization framework
- `SyncDict*.cs`: Pre-configured sync workers for RimWorld types
- `SyncMethods.cs`, `SyncFields.cs`, `SyncActions.cs`: Method/field/action synchronization
- Uses Harmony patching to intercept calls and sync across clients

### Key Patterns

- **Harmony Patching**: Extensive use of Harmony to patch RimWorld methods for multiplayer compatibility
- **Command Pattern**: Client actions are converted to commands sent to server for validation
- **State Synchronization**: Game state kept in sync through deterministic command execution
- **Faction Context**: Special handling for multi-faction gameplay modes

## Development Workflow

1. **Setup**: Clone to RimWorld/Mods directory as specified in CONTRIBUTORS.md
2. **Build**: Use `dotnet build` in Source/ directory  
3. **Test**: Assemblies automatically copy to mod directories via MSBuild targets
4. **Debug**: Use RimWorld's debug console and `-arbiter` command line flag
5. **Release**: Run workshop_bundler.sh to create release packages

## Important Files

- `Source/Common/Version.cs`: Version constants (current: 0.10.5, protocol: 47)
- `Source/Client/Multiplayer.csproj`: Main build configuration with RimWorld references
- `workshop_bundler.sh`: Automated release bundling script
- `CONTRIBUTORS.md`: Contribution guidelines and setup instructions
- `AsyncTimeDesyncFixes.md`: Analysis and fixes for async time desync issues

## Testing

The project uses NUnit for testing with focus on:
- Serialization correctness (`SerializationTest.cs`)
- Server functionality (`ServerTest.cs`) 
- Replay system (`ReplayInfoTest.cs`)
- Async time desync issues (`AsyncTimeDesyncTest.cs`, `AsyncTimeCoordinationTest.cs`)

Tests target .NET 6.0 while the main project targets .NET Framework 4.7.2 (RimWorld requirement).

### Desync Issue Testing

New test files have been added to reproduce and verify fixes for async time desync issues:

```bash
# Run desync-specific tests
dotnet test --filter "TestCategory=Desync"

# Run all async time tests
dotnet test --filter "AsyncTime"
```

Key test scenarios:
- Random state divergence between clients (`TestMapRandomStateDivergenceCausesDesync`)
- Command execution timing variations (`TestCommandExecutionTimingAffectsRandomState`)
- State collection race conditions (`TestRandomStateSnapshotTimingRaceCondition`)
- Pause/resume coordination issues (`TestPauseResumeCoordinationIssues`)

## Async Time Desync Issues

The "wrong state on map X" errors occur when async time is enabled due to timing coordination issues:

### Root Causes
1. **Random State Accumulation Mismatch**: Maps running at different speeds accumulate different amounts of random state
2. **Command Execution Timing Variations**: Commands execute at slightly different relative times on different clients
3. **Random State Snapshot Timing Race Conditions**: Clients take random state snapshots at different moments
4. **Pause/Resume Coordination Delays**: Maps pause/resume at different times across clients

### Solutions Implemented
- **Deterministic Random State Synchronization**: Tick-aligned state collection with significance thresholds
- **Command Execution Determinism**: Fixed random seeds based on command content
- **Synchronized State Collection**: Regular sync intervals to reduce timing noise
- **Enhanced Desync Detection**: Better tolerance for minor timing differences

### Key Files for Desync Fixes
- `Source/Client/AsyncTime/ImprovedAsyncTimeComp.cs`: Enhanced async time component
- `Source/Client/Desyncs/ImprovedSyncCoordinator.cs`: Better sync coordination
- `AsyncTimeDesyncFixes.md`: Complete analysis and implementation guide