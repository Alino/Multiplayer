using NUnit.Framework;
using Multiplayer.Common;
using System.Collections.Generic;
using System.Linq;

namespace Tests
{
    [TestFixture]
    public class AsyncTimeDesyncTest
    {
        /// <summary>
        /// Mock implementation of ClientSyncOpinion for testing desync detection
        /// without requiring full client dependencies
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

        /// <summary>
        /// Test that reproduces the "wrong state on map" desync when random states diverge
        /// </summary>
        [Test]
        public void TestMapRandomStateDivergenceCausesDesync()
        {
            // Arrange: Create two client opinions with different map random states
            var clientOpinion1 = new MockClientSyncOpinion(100);
            var clientOpinion2 = new MockClientSyncOpinion(100);

            const int mapId = 1;
            
            // Client 1 has random states: [1000, 2000, 3000]
            var mapStates1 = clientOpinion1.GetRandomStatesForMap(mapId);
            mapStates1.AddRange(new uint[] { 1000, 2000, 3000 });

            // Client 2 has different random states: [1000, 2000, 3001] (last state differs)
            var mapStates2 = clientOpinion2.GetRandomStatesForMap(mapId);
            mapStates2.AddRange(new uint[] { 1000, 2000, 3001 });

            // Act: Check for desync
            var desyncMessage = clientOpinion1.CheckForDesync(clientOpinion2);

            // Assert: Should detect desync on map 1
            Assert.That(desyncMessage, Is.Not.Null);
            Assert.That(desyncMessage, Does.Contain("Wrong random state on map 1"));
        }

        /// <summary>
        /// Test random state accumulation during async ticking
        /// </summary>
        [Test]
        public void TestAsyncTimeRandomStateAccumulation()
        {
            // This test simulates what happens when maps tick at different rates
            var opinion = new MockClientSyncOpinion(50);
            
            // Simulate map 1 ticking 3 times with different random states
            const int mapId1 = 1;
            var mapStates1 = opinion.GetRandomStatesForMap(mapId1);
            
            // Simulate random state changes as map ticks
            mapStates1.Add(1000); // Tick 1
            mapStates1.Add(1001); // Tick 2  
            mapStates1.Add(1002); // Tick 3

            // Simulate map 2 ticking only once (slower/paused)
            const int mapId2 = 2;
            var mapStates2 = opinion.GetRandomStatesForMap(mapId2);
            mapStates2.Add(2000); // Only one tick

            Assert.That(mapStates1.Count, Is.EqualTo(3));
            Assert.That(mapStates2.Count, Is.EqualTo(1));
            Assert.That(opinion.mapStates.Count, Is.EqualTo(2));
        }

        /// <summary>
        /// Test command random state tracking
        /// </summary>
        [Test]
        public void TestCommandRandomStateTracking()
        {
            var opinion = new MockClientSyncOpinion(25);
            
            // Simulate command execution with random state changes
            opinion.commandRandomStates.Add(5000);
            opinion.commandRandomStates.Add(5001);
            opinion.commandRandomStates.Add(5002);

            // World random states should also be tracked
            opinion.worldRandomStates.Add(9000);
            opinion.worldRandomStates.Add(9001);

            Assert.That(opinion.commandRandomStates.Count, Is.EqualTo(3));
            Assert.That(opinion.worldRandomStates.Count, Is.EqualTo(2));
        }

        /// <summary>
        /// Test that map instances must match between clients
        /// </summary>
        [Test]
        public void TestMapInstancesMustMatch()
        {
            var clientOpinion1 = new MockClientSyncOpinion(75);
            var clientOpinion2 = new MockClientSyncOpinion(75);

            // Client 1 has maps 1 and 2
            clientOpinion1.GetRandomStatesForMap(1).Add(1000);
            clientOpinion1.GetRandomStatesForMap(2).Add(2000);

            // Client 2 has maps 1 and 3 (different set of maps)
            clientOpinion2.GetRandomStatesForMap(1).Add(1000);
            clientOpinion2.GetRandomStatesForMap(3).Add(3000);

            var desyncMessage = clientOpinion1.CheckForDesync(clientOpinion2);

            Assert.That(desyncMessage, Is.Not.Null);
            Assert.That(desyncMessage, Does.Contain("Map instances don't match"));
        }

        /// <summary>
        /// Test world random state desync detection
        /// </summary>
        [Test]
        public void TestWorldRandomStateDesync()
        {
            var clientOpinion1 = new MockClientSyncOpinion(40);
            var clientOpinion2 = new MockClientSyncOpinion(40);

            // Same map states
            clientOpinion1.GetRandomStatesForMap(1).Add(1000);
            clientOpinion2.GetRandomStatesForMap(1).Add(1000);

            // Different world states
            clientOpinion1.worldRandomStates.AddRange(new uint[] { 5000, 5001 });
            clientOpinion2.worldRandomStates.AddRange(new uint[] { 5000, 5002 }); // Last differs

            var desyncMessage = clientOpinion1.CheckForDesync(clientOpinion2);

            Assert.That(desyncMessage, Is.Not.Null);
            Assert.That(desyncMessage, Does.Contain("Wrong random state for the world"));
        }

        /// <summary>
        /// Test that matching states produce no desync
        /// </summary>
        [Test]
        public void TestMatchingStatesProduceNoDesync()
        {
            var clientOpinion1 = new MockClientSyncOpinion(60);
            var clientOpinion2 = new MockClientSyncOpinion(60);

            // Identical map states
            clientOpinion1.GetRandomStatesForMap(1).AddRange(new uint[] { 1000, 1001, 1002 });
            clientOpinion2.GetRandomStatesForMap(1).AddRange(new uint[] { 1000, 1001, 1002 });

            // Identical world states  
            clientOpinion1.worldRandomStates.AddRange(new uint[] { 5000, 5001 });
            clientOpinion2.worldRandomStates.AddRange(new uint[] { 5000, 5001 });

            // Identical command states
            clientOpinion1.commandRandomStates.AddRange(new uint[] { 3000, 3001 });
            clientOpinion2.commandRandomStates.AddRange(new uint[] { 3000, 3001 });

            var desyncMessage = clientOpinion1.CheckForDesync(clientOpinion2);

            Assert.That(desyncMessage, Is.Null, "No desync should be detected when all states match");
        }

        /// <summary>
        /// Test byte serialization/deserialization of sync opinions (like the actual networking code)
        /// </summary>
        [Test]
        public void TestSyncOpinionSerialization()
        {
            var originalOpinion = new MockClientSyncOpinion(123);
            originalOpinion.commandRandomStates.AddRange(new uint[] { 1000, 1001 });
            originalOpinion.worldRandomStates.AddRange(new uint[] { 2000, 2001 });
            originalOpinion.GetRandomStatesForMap(1).AddRange(new uint[] { 3000, 3001 });
            originalOpinion.GetRandomStatesForMap(2).AddRange(new uint[] { 4000, 4001 });

            // Simulate serialization (similar to what happens over network)
            var writer = new ByteWriter();
            
            writer.WriteInt32(originalOpinion.startTick);
            writer.WritePrefixedUInts(originalOpinion.commandRandomStates);
            writer.WritePrefixedUInts(originalOpinion.worldRandomStates);
            
            writer.WriteInt32(originalOpinion.mapStates.Count);
            foreach (var map in originalOpinion.mapStates)
            {
                writer.WriteInt32(map.mapId);
                writer.WritePrefixedUInts(map.randomStates);
            }

            // Simulate deserialization
            var data = writer.ToArray();
            var reader = new ByteReader(data);
            
            var deserializedOpinion = new MockClientSyncOpinion(reader.ReadInt32());
            deserializedOpinion.commandRandomStates.AddRange(reader.ReadPrefixedUInts());
            deserializedOpinion.worldRandomStates.AddRange(reader.ReadPrefixedUInts());
            
            int mapCount = reader.ReadInt32();
            for (int i = 0; i < mapCount; i++)
            {
                int mapId = reader.ReadInt32();
                var mapData = reader.ReadPrefixedUInts();
                deserializedOpinion.GetRandomStatesForMap(mapId).AddRange(mapData);
            }

            // Verify serialization round-trip works correctly
            Assert.That(deserializedOpinion.startTick, Is.EqualTo(originalOpinion.startTick));
            Assert.That(deserializedOpinion.commandRandomStates, Is.EqualTo(originalOpinion.commandRandomStates));
            Assert.That(deserializedOpinion.worldRandomStates, Is.EqualTo(originalOpinion.worldRandomStates));
            Assert.That(deserializedOpinion.mapStates.Count, Is.EqualTo(originalOpinion.mapStates.Count));
            
            // Deserialized and original should be equivalent (no desync)
            var desyncMessage = originalOpinion.CheckForDesync(deserializedOpinion);
            Assert.That(desyncMessage, Is.Null, "Serialization round-trip should not cause desync");
        }
    }
}