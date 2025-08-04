using NUnit.Framework;
using Multiplayer.Common;
using System.Collections.Generic;
using System.Linq;

namespace Tests
{
    /// <summary>
    /// Tests specifically focused on reproducing async time coordination issues
    /// that lead to desync problems when maps run at different speeds
    /// </summary>
    [TestFixture]
    public class AsyncTimeCoordinationTest
    {
        /// <summary>
        /// Test reproducing the issue where maps running at different speeds
        /// accumulate different amounts of random state, causing desync
        /// </summary>
        [Test]
        public void TestAsyncSpeedDivergenceCreatesDesync()
        {
            // Simulate 10 ticks where fast map ticks 3x as often as slow map
            var fastMapStates = new List<uint>();
            var slowMapStates = new List<uint>();

            uint fastRandState = 1000;
            uint slowRandState = 2000;

            for (int tick = 0; tick < 10; tick++)
            {
                // Fast map ticks 3 times per global tick (speed = 3.0)
                for (int subTick = 0; subTick < 3; subTick++)
                {
                    fastRandState = MockRandom(fastRandState);
                    fastMapStates.Add(fastRandState);
                }

                // Slow map ticks once per global tick (speed = 1.0)
                slowRandState = MockRandom(slowRandState);
                slowMapStates.Add(slowRandState);
            }

            // Fast map should have accumulated 30 random states (10 ticks * 3 speed)
            // Slow map should have accumulated 10 random states (10 ticks * 1 speed)
            Assert.That(fastMapStates.Count, Is.EqualTo(30));
            Assert.That(slowMapStates.Count, Is.EqualTo(10));

            // This difference in state accumulation is a major source of desync
            Console.WriteLine($"Fast map states: {fastMapStates.Count}, Slow map states: {slowMapStates.Count}");
            
            // If these were sent as sync opinions, they would desync due to count mismatch
            Assert.That(fastMapStates.Count, Is.Not.EqualTo(slowMapStates.Count), 
                "Different tick rates should cause different state accumulation");
        }

        /// <summary>
        /// Test that command execution timing affects random state synchronization
        /// </summary>
        [Test]
        public void TestCommandExecutionTimingAffectsRandomState()
        {
            var client1States = new List<uint>();
            var client2States = new List<uint>();

            uint baseState = 5000;

            // Simulate the same command executed at slightly different times
            // due to network latency or async timing differences
            
            // Client 1: Command executes at tick 5
            for (int tick = 0; tick < 10; tick++)
            {
                if (tick == 5)
                {
                    // Command execution causes random number generation
                    baseState = MockRandom(baseState);
                    client1States.Add(baseState);
                }
                // Regular ticking
                baseState = MockRandom(baseState);
                client1States.Add(baseState);
            }

            // Reset for client 2
            baseState = 5000;

            // Client 2: Same command executes at tick 6 (one tick later)
            for (int tick = 0; tick < 10; tick++)
            {
                if (tick == 6) // One tick later!
                {
                    baseState = MockRandom(baseState);
                    client2States.Add(baseState);
                }
                baseState = MockRandom(baseState);
                client2States.Add(baseState);
            }

            // The random state sequences should differ due to timing
            Assert.That(client1States, Is.Not.EqualTo(client2States));
            
            // This demonstrates how command timing can cause desync
            Console.WriteLine($"Client 1 final state: {client1States.Last()}");
            Console.WriteLine($"Client 2 final state: {client2States.Last()}");
        }

        /// <summary>
        /// Test the race condition where random state snapshot timing varies
        /// </summary>
        [Test]
        public void TestRandomStateSnapshotTimingRaceCondition()
        {
            uint mapState = 1000;
            
            // Client 1: Takes snapshot after 5 random calls
            var state1 = mapState;
            for (int i = 0; i < 5; i++)
            {
                state1 = MockRandom(state1);
            }

            // Client 2: Takes snapshot after 6 random calls (due to timing difference)
            var state2 = mapState;
            for (int i = 0; i < 6; i++)
            {
                state2 = MockRandom(state2);
            }

            // Different snapshot timing leads to different captured states
            Assert.That(state1, Is.Not.EqualTo(state2));
            
            // This would cause a "wrong state on map" desync
            Console.WriteLine($"Client 1 snapshot: {state1}, Client 2 snapshot: {state2}");
        }

        /// <summary>
        /// Test pause/resume coordination issues with async time
        /// </summary>
        [Test]
        public void TestPauseResumeCoordinationIssues()
        {
            var mapStates = new List<uint>();
            uint randState = 3000;

            // Simulate a map that gets paused and resumed at different times
            // than other clients due to async coordination delays
            
            bool isPaused = false;
            
            for (int tick = 0; tick < 20; tick++)
            {
                // Simulate pause/resume at tick 10
                if (tick == 10)
                    isPaused = !isPaused;
                    
                if (tick == 15)
                    isPaused = !isPaused;

                // Only tick if not paused
                if (!isPaused)
                {
                    randState = MockRandom(randState);
                    mapStates.Add(randState);
                }
            }

            // Now simulate another client with slightly different pause timing
            var otherMapStates = new List<uint>();
            randState = 3000;
            isPaused = false;

            for (int tick = 0; tick < 20; tick++)
            {
                // Pause/resume one tick later due to coordination delay
                if (tick == 11) // One tick later!
                    isPaused = !isPaused;
                    
                if (tick == 16) // One tick later!
                    isPaused = !isPaused;

                if (!isPaused)
                {
                    randState = MockRandom(randState);
                    otherMapStates.Add(randState);
                }
            }

            // Different pause timing leads to different state accumulation
            Assert.That(mapStates.Count, Is.Not.EqualTo(otherMapStates.Count));
            Console.WriteLine($"Client 1 ticks: {mapStates.Count}, Client 2 ticks: {otherMapStates.Count}");
        }

        /// <summary>
        /// Test that demonstrates how sync collection intervals can help reduce timing noise
        /// </summary>
        [Test]
        public void TestSyncCollectionIntervalReducesNoise()
        {
            const int SYNC_INTERVAL = 5; // Collect every 5 ticks
            var client1SyncStates = new List<uint>();
            var client2SyncStates = new List<uint>();

            uint state1 = 1000;
            uint state2 = 1000;

            for (int tick = 0; tick < 20; tick++)
            {
                // Both clients tick normally
                state1 = MockRandom(state1);
                state2 = MockRandom(state2);

                // Client 2 has occasional extra random calls (timing noise)
                if (tick % 7 == 0) // Every 7 ticks, client 2 has extra call
                {
                    state2 = MockRandom(state2);
                }

                // Only collect at sync intervals
                if (tick % SYNC_INTERVAL == 0)
                {
                    client1SyncStates.Add(state1);
                    client2SyncStates.Add(state2);
                }
            }

            // With sync intervals, we collect fewer samples but they should be more stable
            Assert.That(client1SyncStates.Count, Is.EqualTo(client2SyncStates.Count));
            Assert.That(client1SyncStates.Count, Is.EqualTo(4)); // 20 ticks / 5 interval = 4 collections

            // The states will still differ due to timing noise, but we have fewer comparison points
            Console.WriteLine($"Sync collections: {client1SyncStates.Count}");
            Console.WriteLine($"States match: {client1SyncStates.SequenceEqual(client2SyncStates)}");
        }

        /// <summary>
        /// Test that simulates the actual desync detection logic with timing variations
        /// </summary>
        [Test]
        public void TestDesyncDetectionWithTimingVariations()
        {
            // Create sync opinions for two clients with slightly different timing
            var opinion1 = CreateMockOpinion(100);
            var opinion2 = CreateMockOpinion(100);

            // Simulate map 1 on both clients with slight timing differences
            const int mapId = 1;
            uint state = 5000;

            // Client 1: Normal execution
            for (int i = 0; i < 10; i++)
            {
                state = MockRandom(state);
                opinion1.GetRandomStatesForMap(mapId).Add(state);
            }

            // Client 2: Extra random call in the middle (timing variation)
            state = 5000;
            for (int i = 0; i < 10; i++)
            {
                state = MockRandom(state);
                opinion2.GetRandomStatesForMap(mapId).Add(state);
                
                // Extra call at iteration 5 due to timing difference
                if (i == 5)
                {
                    state = MockRandom(state);
                    opinion2.GetRandomStatesForMap(mapId).Add(state);
                }
            }

            // This should cause a desync due to different sequence lengths
            var desyncMessage = opinion1.CheckForDesync(opinion2);
            Assert.That(desyncMessage, Is.Not.Null);
            Assert.That(desyncMessage, Does.Contain("Wrong random state on map 1"));
            
            Console.WriteLine($"Desync detected: {desyncMessage}");
            Console.WriteLine($"Client 1 states: {opinion1.GetRandomStatesForMap(mapId).Count}");
            Console.WriteLine($"Client 2 states: {opinion2.GetRandomStatesForMap(mapId).Count}");
        }

        /// <summary>
        /// Mock random number generator for deterministic testing
        /// </summary>
        private uint MockRandom(uint state)
        {
            // Simple LCG for deterministic testing
            return (uint)((state * 1664525 + 1013904223) & 0xFFFFFFFF);
        }

        /// <summary>
        /// Helper to create mock sync opinion
        /// </summary>
        private MockClientSyncOpinion CreateMockOpinion(int startTick)
        {
            return new MockClientSyncOpinion(startTick);
        }

        /// <summary>
        /// Mock implementation matching the main test file
        /// </summary>
        public class MockClientSyncOpinion
        {
            public bool isLocalClientsOpinion;
            public int startTick;
            public List<uint> commandRandomStates = new();
            public List<uint> worldRandomStates = new();
            public List<MockMapRandomStateData> mapStates = new();
            public List<int> desyncStackTraceHashes = new();
            public bool simulating;
            public int roundMode;

            public MockClientSyncOpinion(int startTick)
            {
                this.startTick = startTick;
            }

            public string? CheckForDesync(MockClientSyncOpinion other)
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
                    if (!g.map1.randomStates.SequenceEqual(g.map2.randomStates))
                        return $"Wrong random state on map {g.map1.mapId}";
                }

                if (!worldRandomStates.SequenceEqual(other.worldRandomStates))
                    return "Wrong random state for the world";

                if (!commandRandomStates.SequenceEqual(other.commandRandomStates))
                    return "Random state from commands doesn't match";

                if (!simulating && !other.simulating && desyncStackTraceHashes.Any() && other.desyncStackTraceHashes.Any() && !desyncStackTraceHashes.SequenceEqual(other.desyncStackTraceHashes))
                    return "Trace hashes don't match";

                return null;
            }

            public List<uint> GetRandomStatesForMap(int mapId)
            {
                var result = mapStates.Find(m => m.mapId == mapId);
                if (result != null) return result.randomStates;
                mapStates.Add(result = new MockMapRandomStateData(mapId));
                return result.randomStates;
            }
        }

        public class MockMapRandomStateData
        {
            public int mapId;
            public List<uint> randomStates = new List<uint>();

            public MockMapRandomStateData(int mapId)
            {
                this.mapId = mapId;
            }
        }
    }
}