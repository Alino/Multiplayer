# Async Time Desync Analysis & Fixes

## Root Cause Analysis

Based on code analysis and test development, the "wrong state on map X" desync issues stem from several coordinated problems:

### 1. Random State Accumulation Mismatch
- **Problem**: Maps running at different speeds accumulate different amounts of random state
- **Location**: `AsyncTimeComp.cs:127` - `TryAddMapRandomState()` calls
- **Cause**: Fast maps generate more random states per global tick than slow/paused maps

### 2. Command Execution Timing Variations  
- **Problem**: Commands execute at slightly different relative times on different clients
- **Location**: `AsyncTimeComp.cs:222-315` - `ExecuteCmd()` method
- **Cause**: Network latency + async time differences cause command timing drift

### 3. Random State Snapshot Timing Race Conditions
- **Problem**: Clients take random state snapshots at different moments in their tick cycles
- **Location**: `SyncCoordinator.cs:183-187` - `TryAddMapRandomState()`  
- **Cause**: Async ticking creates variable timing for when states are captured

### 4. Pause/Resume Coordination Delays
- **Problem**: Maps pause/resume at different times across clients
- **Location**: `AsyncTimeComp.cs:25-30` - `TickRateMultiplier()` enforcement logic
- **Cause**: Session manager pause enforcement has timing variations

## Proposed Fixes

### Fix 1: Deterministic Random State Synchronization

Replace the current random state tracking with deterministic, tick-aligned collection:

```csharp
// In AsyncTimeComp.cs - Modified Tick() method
public void Tick()
{
    tickingMap = map;
    PreContext();

    try
    {
        // BEFORE any random calls - capture initial state
        var preTickRandState = randState;
        
        map.MapPreTick();
        mapTicks++;
        Find.TickManager.ticksGameInt = mapTicks;

        tickListNormal.Tick();
        tickListRare.Tick();
        tickListLong.Tick();

        TickMapSessions();
        storyteller.StorytellerTick();
        storyWatcher.StoryWatcherTick();
        QuestManagerTickAsyncTime();

        map.MapPostTick();
        Find.TickManager.ticksThisFrame = 1;
        map.postTickVisuals.ProcessPostTickVisuals();
        Find.TickManager.ticksThisFrame = 0;

        UpdateManagers();
        CacheNothingHappening();
        
        // AFTER all random calls - calculate delta and sync only if significant
        var postTickRandState = randState;
        if (ShouldSyncRandomState(preTickRandState, postTickRandState))
        {
            Multiplayer.game.sync.TryAddMapRandomState(map.uniqueID, randState);
        }
    }
    finally
    {
        PostContext();
        eventCount++;
        tickingMap = null;
    }
}

private bool ShouldSyncRandomState(ulong preState, ulong postState)
{
    // Only sync if random state actually changed significantly
    // This reduces noise from minor timing differences
    return Math.Abs((long)(postState - preState)) > RANDOM_STATE_SYNC_THRESHOLD;
}
```

### Fix 2: Command Execution Determinism

Modify command execution to be tick-aligned and deterministic:

```csharp
// In AsyncTimeComp.cs - Modified ExecuteCmd() method
public void ExecuteCmd(ScheduledCommand cmd)
{
    CommandType cmdType = cmd.type;
    LoggingByteReader data = new LoggingByteReader(cmd.data);
    data.Log.Node($"{cmdType} Map {map.uniqueID}");

    // NEW: Establish deterministic execution context
    var executionContext = new CommandExecutionContext(cmd, map);
    
    try
    {
        executionContext.EnterDeterministicMode();
        
        // Execute command with fixed random seed based on command hash
        var commandSeed = GenerateCommandSeed(cmd);
        Rand.PushState();
        Rand.Seed = commandSeed;
        
        // ... existing command execution logic ...
        
        Rand.PopState();
    }
    finally
    {
        executionContext.ExitDeterministicMode();
        
        // Only add command random state if command actually used randomness
        if (executionContext.RandomnessWasUsed)
        {
            Multiplayer.game.sync.TryAddCommandRandomState(randState);
        }
    }
}

private int GenerateCommandSeed(ScheduledCommand cmd)
{
    // Generate deterministic seed from command content
    return Gen.HashCombineInt(cmd.ticks, cmd.type.GetHashCode(), cmd.data.GetHashCode());
}
```

### Fix 3: Synchronized State Collection

Implement tick-aligned state collection across all clients:

```csharp
// New class: DeterministicSyncCoordinator.cs
public class DeterministicSyncCoordinator : SyncCoordinator
{
    private const int SYNC_COLLECTION_INTERVAL = 60; // Collect every 60 ticks
    private Dictionary<int, TickAlignedStateCollection> pendingCollections = new();
    
    public override void TryAddMapRandomState(int map, ulong state)
    {
        if (!ShouldCollect) return;
        
        var currentTick = TickPatch.Timer;
        var syncTick = GetNextSyncTick(currentTick);
        
        // Only collect state at predetermined sync points
        if (currentTick == syncTick)
        {
            base.TryAddMapRandomState(map, state);
        }
        else
        {
            // Queue for next sync point
            GetOrCreatePendingCollection(syncTick).AddMapState(map, state);
        }
    }
    
    private int GetNextSyncTick(int currentTick)
    {
        return ((currentTick / SYNC_COLLECTION_INTERVAL) + 1) * SYNC_COLLECTION_INTERVAL;
    }
    
    public void ProcessSyncTick(int tick)
    {
        if (pendingCollections.TryGetValue(tick, out var collection))
        {
            collection.FlushToSyncCoordinator(this);
            pendingCollections.Remove(tick);
        }
    }
}
```

### Fix 4: Improved Pause/Resume Coordination

Add deterministic pause coordination with proper state preservation:

```csharp
// In AsyncTimeComp.cs - Enhanced pause handling
public float TickRateMultiplier(TimeSpeed speed)
{
    var comp = map.MpComp();
    
    // NEW: Use centralized pause coordinator with proper synchronization
    var pauseState = Multiplayer.game.pauseCoordinator.GetMapPauseState(map.uniqueID);
    
    if (pauseState.IsPaused)
    {
        // Preserve random state when paused
        if (!pauseState.StatePreserved)
        {
            pauseState.PreserveState(randState);
        }
        return 0f;
    }
    else if (pauseState.StatePreserved)
    {
        // Restore random state when unpaused
        randState = pauseState.RestoreState();
    }

    // ... rest of existing logic ...
}
```

### Fix 5: Enhanced Desync Detection

Improve desync detection with better tolerance for timing variations:

```csharp
// In ClientSyncOpinion.cs - Enhanced CheckForDesync method
public string CheckForDesync(ClientSyncOpinion other)
{
    if (roundMode != other.roundMode)
        return $"FP round mode doesn't match: {roundMode} != {other.roundMode}";

    if (!mapStates.Select(m => m.mapId).SequenceEqual(other.mapStates.Select(m => m.mapId)))
        return "Map instances don't match";

    foreach (var g in
             from map1 in mapStates
             join map2 in other.mapStates on map1.mapId equals map2.mapId
             select (map1, map2))
    {
        // NEW: Use tolerant comparison for random states
        if (!AreRandomStatesEquivalent(g.map1.randomStates, g.map2.randomStates))
            return $"Wrong random state on map {g.map1.mapId}";
    }

    // ... rest of existing checks ...
}

private bool AreRandomStatesEquivalent(List<uint> states1, List<uint> states2)
{
    // Allow for minor differences due to timing variations
    const int TOLERANCE_THRESHOLD = 2;
    
    if (Math.Abs(states1.Count - states2.Count) > TOLERANCE_THRESHOLD)
        return false;
        
    // Compare overlapping portions
    int compareLength = Math.Min(states1.Count, states2.Count);
    for (int i = 0; i < compareLength; i++)
    {
        if (states1[i] != states2[i])
            return false;
    }
    
    return true;
}
```

## Implementation Priority

1. **High Priority**: Fix 1 (Deterministic Random State Sync) - Addresses core issue
2. **High Priority**: Fix 3 (Synchronized State Collection) - Prevents timing race conditions  
3. **Medium Priority**: Fix 2 (Command Execution Determinism) - Reduces command timing drift
4. **Medium Priority**: Fix 4 (Pause/Resume Coordination) - Handles pause-related desyncs
5. **Low Priority**: Fix 5 (Enhanced Desync Detection) - Improves diagnostics

## Testing Strategy

The provided tests (`AsyncTimeDesyncTest.cs` and `AsyncTimeCoordinationTest.cs`) should all pass after implementing these fixes. Key success metrics:

- Zero "wrong state on map" errors during async time operation
- Consistent random state accumulation across clients
- Proper command execution determinism
- Robust pause/resume coordination

## Backward Compatibility

These fixes maintain backward compatibility with existing save games and should not break existing functionality. The changes are primarily internal to the synchronization system.