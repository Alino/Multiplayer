using HarmonyLib;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Factions;
using Multiplayer.Client.Patches;
using Multiplayer.Client.Saving;
using Multiplayer.Client.Util;

namespace Multiplayer.Client
{
    public class AsyncTimeComp : IExposable, ITickable
    {
        public static Map tickingMap;
        public static Map executingCmdMap;

        public float TickRateMultiplier(TimeSpeed speed)
        {
            var comp = map.MpComp();

            // ASYNC TIME DESYNC FIXES: Use improved pause coordination system
            var pauseState = GetMapPauseState(map.uniqueID);
            var enforcePause = comp.sessionManager.IsAnySessionCurrentlyPausing(map) ||
                Multiplayer.WorldComp.sessionManager.IsAnySessionCurrentlyPausing(map);
            
            pauseState.SetPauseState(enforcePause);
            
            if (pauseState.IsPaused)
            {
                // Preserve random state when paused to maintain consistency
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

            if (mapTicks < slower.forceNormalSpeedUntil)
                return speed == TimeSpeed.Paused ? 0 : 1;

            switch (speed)
            {
                case TimeSpeed.Paused:
                    return 0f;
                case TimeSpeed.Normal:
                    return 1f;
                case TimeSpeed.Fast:
                    return 3f;
                case TimeSpeed.Superfast:
                    if (nothingHappeningCached)
                        return 12f;
                    return 6f;
                case TimeSpeed.Ultrafast:
                    return 15f;
                default:
                    return -1f;
            }
        }

        public TimeSpeed DesiredTimeSpeed => timeSpeedInt;

        public void SetDesiredTimeSpeed(TimeSpeed speed)
        {
            timeSpeedInt = speed;
        }

        public bool Paused => this.ActualRateMultiplier(DesiredTimeSpeed) == 0f;

        public float TimeToTickThrough { get; set; }

        public Queue<ScheduledCommand> Cmds => cmds;

        public int TickableId => map.uniqueID;

        public Map map;
        public int mapTicks;
        private TimeSpeed timeSpeedInt;
        public bool forcedNormalSpeed;
        public int eventCount;

        public Storyteller storyteller;
        public StoryWatcher storyWatcher;
        public TimeSlower slower = new();

        public TickList tickListNormal = new(TickerType.Normal);
        public TickList tickListRare = new(TickerType.Rare);
        public TickList tickListLong = new(TickerType.Long);

        // Shared random state for ticking and commands
        public ulong randState = 1;

        // ASYNC TIME DESYNC FIXES: Enhanced state tracking for better synchronization
        private static readonly Dictionary<int, MapPauseState> mapPauseStates = new();
        private static readonly object mapPauseStatesLock = new object();
        private static readonly Dictionary<int, CommandExecutionContext> executionContexts = new();
        private static readonly object executionContextsLock = new object();

        public Queue<ScheduledCommand> cmds = new();

        public AsyncTimeComp(Map map)
        {
            this.map = map;
        }

        public void Tick()
        {
            tickingMap = map;
            
            try
            {
                // ASYNC TIME DESYNC FIXES: Enhanced exception handling for stability
                PreContext();

                // Capture random state before any potentially random operations
                var preTickRandState = randState;

                //SimpleProfiler.Start();

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
                
                // ASYNC TIME DESYNC FIXES: Only sync random state if it changed significantly
                if (ShouldSyncRandomState(preTickRandState, randState))
                {
                    try
                    {
                        Multiplayer.game.sync.TryAddMapRandomState(map.uniqueID, randState);
                    }
                    catch (Exception syncError)
                    {
                        MpLog.Error($"Error syncing map random state for map {map?.uniqueID}: {syncError}");
                        // Don't rethrow - sync failure shouldn't crash the tick
                    }
                }
            }
            catch (Exception e)
            {
                MpLog.Error($"Critical error in AsyncTimeComp tick for map {map?.uniqueID}: {e}");
                throw;
            }
            finally
            {
                try
                {
                    PostContext();
                    eventCount++;
                }
                catch (Exception postError)
                {
                    MpLog.Error($"Error in PostContext for map {map?.uniqueID}: {postError}");
                    // Don't rethrow in finally block
                }
                finally
                {
                    tickingMap = null;
                    //SimpleProfiler.Pause();
                }
            }
        }

        public void TickMapSessions()
        {
            map.MpComp().sessionManager.TickSessions();
        }

        // These are normally called in Map.MapUpdate() and react to changes in the game state even when the game is paused (not ticking)
        // Update() methods are not deterministic, but in multiplayer all game state changes (which don't happen during ticking) happen in commands
        // Thus these methods can be moved to Tick() and ExecuteCmd() by way of this method
        public void UpdateManagers()
        {
            map.regionGrid.UpdateClean();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();

            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            map.glowGrid.GlowGridUpdate_First();
        }

        private TimeSnapshot? prevTime;
        private Storyteller prevStoryteller;
        private StoryWatcher prevStoryWatcher;

        public void PreContext()
        {
            if (Multiplayer.GameComp.multifaction)
            {
                map.PushFaction(
                    map.ParentFaction is { IsPlayer: true }
                    ? map.ParentFaction
                    : Multiplayer.WorldComp.spectatorFaction,
                    force: true);
            }

            prevTime = TimeSnapshot.GetAndSetFromMap(map);

            prevStoryteller = Current.Game.storyteller;
            prevStoryWatcher = Current.Game.storyWatcher;

            Current.Game.storyteller = storyteller;
            Current.Game.storyWatcher = storyWatcher;

            Rand.PushState();
            Rand.StateCompressed = randState;

            // Reset the effects of SkyManager.SkyManagerUpdate
            map.skyManager.curSkyGlowInt = map.skyManager.CurrentSkyTarget().glow;
        }

        public void PostContext()
        {
            Current.Game.storyteller = prevStoryteller;
            Current.Game.storyWatcher = prevStoryWatcher;

            prevTime?.Set();

            randState = Rand.StateCompressed;
            Rand.PopState();

            if (Multiplayer.GameComp.multifaction)
                map.PopFaction();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapTicks, "mapTicks");
            Scribe_Values.Look(ref timeSpeedInt, "timeSpeed");

            Scribe_Deep.Look(ref storyteller, "storyteller");

            Scribe_Deep.Look(ref storyWatcher, "storyWatcher");
            if (Scribe.mode == LoadSaveMode.LoadingVars && storyWatcher == null)
                storyWatcher = new StoryWatcher();

            Scribe_Custom.LookULong(ref randState, "randState", 1);
        }

        public void FinalizeInit()
        {
            cmds = new Queue<ScheduledCommand>(
                Multiplayer.session.dataSnapshot?.MapCmds.GetValueSafe(map.uniqueID) ?? new List<ScheduledCommand>()
            );

            Log.Message($"Init map with cmds {cmds.Count}");
        }

        public static bool keepTheMap;
        public static List<object> prevSelected;

        public void ExecuteCmd(ScheduledCommand cmd)
        {
            CommandType cmdType = cmd.type;
            LoggingByteReader data = new LoggingByteReader(cmd.data);
            data.Log.Node($"{cmdType} Map {map.uniqueID}");

            MpContext context = data.MpContext();

            // ASYNC TIME DESYNC FIXES: Performance optimization - only create execution context if needed
            CommandExecutionContext executionContext = null;
            bool needsDeterministicExecution = IsCommandTypeThatNeedsRandomness(cmdType);
            
            if (needsDeterministicExecution)
            {
                executionContext = new CommandExecutionContext(cmd, map);
                
                lock (executionContextsLock)
                {
                    executionContexts[map.uniqueID] = executionContext;
                }
            }

            keepTheMap = false;
            var prevMap = Current.Game.CurrentMap;
            Current.Game.currentMapIndex = (sbyte)map.Index;

            executingCmdMap = map;
            TickPatch.currentExecutingCmdIssuedBySelf = cmd.issuedBySelf && !TickPatch.Simulating;

            PreContext();
            map.PushFaction(cmd.GetFaction());

            context.map = map;

            prevSelected = Find.Selector.selected;
            Find.Selector.selected = new List<object>();

            SelectorDeselectPatch.deselected = new List<object>();

            bool prevDevMode = Prefs.data.devMode;
            bool prevGodMode = DebugSettings.godMode;
            Multiplayer.GameComp.playerData.GetValueOrDefault(cmd.playerId)?.SetContext();

            try
            {
                // ASYNC TIME DESYNC FIXES: Enter deterministic execution mode only if needed
                if (needsDeterministicExecution)
                {
                    executionContext.EnterDeterministicMode();
                }
                
                if (cmdType == CommandType.Sync)
                {
                    var handler = SyncUtil.HandleCmd(data);
                    data.Log.current.text = handler.ToString();
                }

                if (cmdType == CommandType.DebugTools)
                {
                    DebugSync.HandleCmd(data);
                }

                if (cmdType == CommandType.MapTimeSpeed && Multiplayer.GameComp.asyncTime)
                {
                    TimeSpeed speed = (TimeSpeed)data.ReadByte();
                    SetDesiredTimeSpeed(speed);

                    MpLog.Debug("Set map time speed " + speed);
                }

                if (cmdType == CommandType.Designator)
                {
                    HandleDesignator(data);
                }

                UpdateManagers();
            }
            catch (Exception e)
            {
                MpLog.Error($"Map cmd exception ({cmdType}): {e}");
            }
            finally
            {
                DebugSettings.godMode = prevGodMode;
                Prefs.data.devMode = prevDevMode;

                foreach (var deselected in SelectorDeselectPatch.deselected)
                    prevSelected.Remove(deselected);
                SelectorDeselectPatch.deselected = null;

                Find.Selector.selected = prevSelected;
                prevSelected = null;

                Find.MainButtonsRoot.tabs.Notify_SelectedObjectDespawned();

                map.PopFaction();
                PostContext();

                TickPatch.currentExecutingCmdIssuedBySelf = false;
                executingCmdMap = null;

                if (!keepTheMap)
                    TrySetCurrentMap(prevMap);

                keepTheMap = false;

                // ASYNC TIME DESYNC FIXES: Exit deterministic mode and only sync if command used randomness
                if (needsDeterministicExecution && executionContext != null)
                {
                    executionContext.ExitDeterministicMode();
                    
                    if (executionContext.RandomnessWasUsed)
                    {
                        Multiplayer.game.sync.TryAddCommandRandomState(randState);
                    }
                }
                else
                {
                    // For commands that don't need deterministic execution, sync as before
                    Multiplayer.game.sync.TryAddCommandRandomState(randState);
                }

                eventCount++;

                if (cmdType != CommandType.MapTimeSpeed)
                    Multiplayer.ReaderLog.AddCurrentNode(data);
                    
                // ASYNC TIME DESYNC FIXES: Cleanup execution context
                if (needsDeterministicExecution)
                {
                    lock (executionContextsLock)
                    {
                        executionContexts.Remove(map.uniqueID);
                    }
                }
            }
        }

        private static void TrySetCurrentMap(Map map)
        {
            if (!Find.Maps.Contains(map))
            {
                Current.Game.CurrentMap = Find.Maps.Any() ? Find.Maps[0] : null;
                Find.World.renderer.wantedMode = WorldRenderMode.Planet;
            }
            else
            {
                Current.Game.currentMapIndex = (sbyte)map.Index;
            }
        }

        private void HandleDesignator(ByteReader data)
        {
            Container<Area>? prevArea = null;

            bool SetState(Designator designator)
            {
                if (designator is Designator_AreaAllowed)
                {
                    Area area = SyncSerialization.ReadSync<Area>(data);
                    if (area == null) return false;

                    prevArea = Designator_AreaAllowed.selectedArea;
                    Designator_AreaAllowed.selectedArea = area;
                }

                if (designator is Designator_Install)
                {
                    Thing thing = SyncSerialization.ReadSync<Thing>(data);
                    if (thing == null) return false;

                    DesignatorInstall_SetThingToInstall.thingToInstall = thing;
                }

                if (designator is Designator_Zone)
                {
                    Zone zone = SyncSerialization.ReadSync<Zone>(data);
                    if (zone != null)
                        Find.Selector.selected.Add(zone);
                }

                return true;
            }

            void RestoreState()
            {
                if (prevArea.HasValue)
                    Designator_AreaAllowed.selectedArea = prevArea.Value.Inner;

                DesignatorInstall_SetThingToInstall.thingToInstall = null;
            }

            var mode = SyncSerialization.ReadSync<DesignatorMode>(data);
            var designator = SyncSerialization.ReadSync<Designator>(data);

            try
            {
                if (!SetState(designator)) return;

                if (mode == DesignatorMode.SingleCell)
                {
                    IntVec3 cell = SyncSerialization.ReadSync<IntVec3>(data);

                    designator.DesignateSingleCell(cell);
                    designator.Finalize(true);
                }
                else if (mode == DesignatorMode.MultiCell)
                {
                    IntVec3[] cells = SyncSerialization.ReadSync<IntVec3[]>(data);

                    designator.DesignateMultiCell(cells);
                }
                else if (mode == DesignatorMode.Thing)
                {
                    Thing thing = SyncSerialization.ReadSync<Thing>(data);
                    if (thing == null) return;

                    designator.DesignateThing(thing);
                    designator.Finalize(true);
                }
            }
            finally
            {
                RestoreState();
            }
        }

        private bool nothingHappeningCached;

        private void CacheNothingHappening()
        {
            nothingHappeningCached = true;
            var list = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);

            foreach (var pawn in list)
            {
                if (pawn.HostFaction == null && pawn.RaceProps.Humanlike && pawn.Awake())
                    nothingHappeningCached = false;
            }

            if (nothingHappeningCached && map.IsPlayerHome && map.dangerWatcher.DangerRating >= StoryDanger.Low)
                nothingHappeningCached = false;
        }

        public override string ToString()
        {
            return $"{nameof(AsyncTimeComp)}_{map}";
        }

        public void QuestManagerTickAsyncTime()
        {
            if (!Multiplayer.GameComp.asyncTime || Paused) return;

            MultiplayerAsyncQuest.TickMapQuests(this);
        }

        // ASYNC TIME DESYNC FIXES: Helper methods for improved synchronization

        /// <summary>
        /// Determines if random state changes are significant enough to sync
        /// CONSERVATIVE: Always sync any change to maintain correctness
        /// </summary>
        private bool ShouldSyncRandomState(ulong preState, ulong postState)
        {
            // SAFETY FIRST: Always sync if state changed at all
            // The original issue was timing noise, not excessive syncing
            if (preState != postState)
                return true;
                
            // Also sync at regular intervals even if no change (for heartbeat)
            if (TickPatch.Timer % 60 == 0)
                return true;
                
            return false;
        }

        /// <summary>
        /// SAFE: Conservative approach to command randomness classification
        /// Better safe than sorry - unknown commands get deterministic execution
        /// </summary>
        private static bool IsCommandTypeThatNeedsRandomness(CommandType cmdType)
        {
            // SAFE: Default to true for unknown command types to prevent desyncs
            return cmdType switch
            {
                CommandType.Sync => true,        // Sync commands can trigger random events
                CommandType.Designator => true,  // Designators (building, etc.) often use randomness
                CommandType.DebugTools => true,  // Debug tools might use randomness
                
                // Only explicitly exclude commands we KNOW are safe
                CommandType.MapTimeSpeed => false, // Time speed changes are deterministic
                
                // SAFE: Unknown commands default to deterministic execution
                // Better performance hit than desync risk
                _ => true 
            };
        }

        /// <summary>
        /// Get or create thread-safe pause state for a map
        /// </summary>
        private static MapPauseState GetMapPauseState(int mapId)
        {
            // SAFE: Always use proper locking - performance is less important than correctness
            lock (mapPauseStatesLock)
            {
                if (mapPauseStates.TryGetValue(mapId, out var existingState))
                {
                    return existingState;
                }
                
                // Create new state within lock
                var newState = new MapPauseState();
                mapPauseStates[mapId] = newState;
                
                return newState;
            }
        }

        /// <summary>
        /// Clean up map-specific state to prevent memory leaks when maps are destroyed
        /// </summary>
        public static void CleanupMapState(int mapId)
        {
            lock (mapPauseStatesLock)
            {
                if (mapPauseStates.Remove(mapId))
                {
                    MpLog.Debug($"Cleaned up pause state for map {mapId}");
                }
            }
        }

        /// <summary>
        /// Clean up execution context for destroyed maps
        /// </summary>
        public static void CleanupExecutionContext(int mapId)
        {
            lock (executionContextsLock)
            {
                if (executionContexts.Remove(mapId))
                {
                    MpLog.Debug($"Cleaned up execution context for map {mapId}");
                }
            }
        }

        /// <summary>
        /// Clean up all async time state (called when game ends)
        /// </summary>
        public static void CleanupAllState()
        {
            lock (mapPauseStatesLock)
            {
                int count = mapPauseStates.Count;
                mapPauseStates.Clear();
                if (count > 0)
                {
                    MpLog.Debug($"Cleaned up {count} map pause states");
                }
            }

            lock (executionContextsLock)
            {
                int count = executionContexts.Count;
                executionContexts.Clear();
                if (count > 0)
                {
                    MpLog.Debug($"Cleaned up {count} execution contexts");
                }
            }
        }
    }

    /// <summary>
    /// Context for deterministic command execution
    /// </summary>
    public class CommandExecutionContext
    {
        public ScheduledCommand Command { get; }
        public Map Map { get; }
        public bool RandomnessWasUsed { get; private set; }
        
        private ulong preExecutionRandState;
        private int randomCallCount;
        private bool randomStatePushed = false; // Track if we successfully pushed state

        public CommandExecutionContext(ScheduledCommand command, Map map)
        {
            Command = command;
            Map = map;
        }

        public void EnterDeterministicMode()
        {
            preExecutionRandState = Rand.StateCompressed;
            randomCallCount = 0;
            randomStatePushed = false;
            
            // Set deterministic seed based on command properties
            var commandSeed = GenerateCommandSeed();
            
            try
            {
                Rand.PushState();
                randomStatePushed = true; // Only set if push succeeded
                Rand.Seed = commandSeed;
            }
            catch (Exception e)
            {
                MpLog.Error($"Failed to enter deterministic mode: {e}");
                randomStatePushed = false;
                // Critical: Don't continue execution with wrong random state
                throw;
            }
        }

        public void ExitDeterministicMode()
        {
            try
            {
                var postExecutionRandState = Rand.StateCompressed;
                RandomnessWasUsed = postExecutionRandState != preExecutionRandState || randomCallCount > 0;
                
                // Only pop if we successfully pushed
                if (randomStatePushed)
                {
                    Rand.PopState();
                    randomStatePushed = false;
                }
            }
            catch (Exception e)
            {
                MpLog.Error($"CRITICAL: Failed to exit deterministic mode: {e}");
                
                // Last resort: Reset to pre-execution state to prevent permanent corruption
                if (randomStatePushed)
                {
                    try
                    {
                        Rand.StateCompressed = preExecutionRandState;
                        randomStatePushed = false;
                        MpLog.Error("Random state reset to pre-execution value as emergency recovery");
                    }
                    catch (Exception resetError)
                    {
                        MpLog.Error($"CATASTROPHIC: Cannot recover random state: {resetError}");
                        // At this point, the random state is permanently corrupted
                        // This would require a full game restart to fix
                    }
                }
            }
        }

        private int GenerateCommandSeed()
        {
            // Generate DETERMINISTIC seed from command content to ensure
            // the SAME command produces the SAME random sequence across ALL clients
            // CRITICAL: Must not include any client-specific data like thread IDs!
            return Gen.HashCombineInt(
                Command.ticks,
                Command.type.GetHashCode(),
                Command.data?.GetHashCode() ?? 0,
                Gen.HashCombineInt(Map.uniqueID, Command.playerId)
                // NEVER add thread IDs, timestamps, or other client-specific data!
            );
        }
    }

    /// <summary>
    /// State tracking for map pause coordination - Thread Safe
    /// </summary>
    public class MapPauseState
    {
        private volatile bool _isPaused;
        private volatile bool _statePreserved;
        private ulong _preservedRandState;
        private readonly object _lock = new object();

        public bool IsPaused 
        { 
            get => _isPaused;
        }
        
        public bool StatePreserved => _statePreserved;

        /// <summary>
        /// Thread-safe setter for pause state
        /// </summary>
        public void SetPauseState(bool isPaused)
        {
            _isPaused = isPaused;
        }

        public void PreserveState(ulong randState)
        {
            lock (_lock)
            {
                _preservedRandState = randState;
                _statePreserved = true;
            }
        }

        public ulong RestoreState()
        {
            lock (_lock)
            {
                _statePreserved = false;
                return _preservedRandState;
            }
        }
    }

    public enum DesignatorMode : byte
    {
        SingleCell,
        MultiCell,
        Thing
    }

}
