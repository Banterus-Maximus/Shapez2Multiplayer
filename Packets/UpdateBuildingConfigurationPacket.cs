using Game.Core.Coordinates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Shapez2Multiplayer.Packets
{
    public class UpdateBuildingConfigurationPacket : IPacket
    {
        private static readonly List<PendingConfiguration> PendingConfigurations = new List<PendingConfiguration>();
        private const float ConfigurationRetrySeconds = 10.0f;
        public GlobalTileCoordinate TileCoordinate { get; set; }
        public IBuildingConfiguration BuildingConfiguration { get; set; }
        public byte[] RemainingData { get; set; }
        public UpdateBuildingConfigurationPacket() { }
        public UpdateBuildingConfigurationPacket(GlobalTileCoordinate tileCoordinate, IBuildingConfiguration buildingConfiguration)
        {
            TileCoordinate = tileCoordinate;
            BuildingConfiguration = buildingConfiguration;
        }
        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            TileCoordinate = Encoding.DecodeGlobalTileCoordinate(stream);
            RemainingData = reader.ReadBytes((int)(stream.Length - stream.Position));
        }

        public bool Encode(Stream stream)
        {
            var serializationVisitor = new BinarySerializationVisitor(true, false, Savegame.CurrentVersion, stream, Shapez2Multiplayer.GameSessionOrchestrator.DataSerializers, Shapez2Multiplayer.logger);
            Encoding.Encode(TileCoordinate, stream);
            BuildingConfiguration.Sync(serializationVisitor);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (TryApply())
            {
                return;
            }

            // Placement actions are scheduled, not necessarily executed before
            // the next ordered packet is handled. Keep only the newest config for
            // a tile and retry it after the building enters the map model.
            PendingConfigurations.RemoveAll(pending => pending.Packet.TileCoordinate.Equals(TileCoordinate));
            PendingConfigurations.Add(new PendingConfiguration(CloneForRetry(), connection, Time.realtimeSinceStartup + ConfigurationRetrySeconds));
        }

        public static void ProcessPendingConfigurations()
        {
            if (PendingConfigurations.Count == 0 || Shapez2Multiplayer.GameSessionOrchestrator == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            foreach (var pending in PendingConfigurations.ToList())
            {
                if (pending.Packet.TryApply())
                {
                    PendingConfigurations.Remove(pending);
                    continue;
                }
                if (now < pending.ExpiresAt)
                {
                    continue;
                }

                PendingConfigurations.Remove(pending);
                Shapez2Multiplayer.logger.Warning?.Log($"Building configuration at {pending.Packet.TileCoordinate} still had no target after {ConfigurationRetrySeconds} seconds.");
                if (pending.Source == null)
                {
                    MultiplayerSynchronization.RequestRepair(SyncSubsystem.All, "building configuration target was missing after retry");
                }
                else
                {
                    MultiplayerCore.socketManager?.SendAuthoritativeState(pending.Source, SyncSubsystem.All);
                }
            }
        }

        public static void ClearPendingConfigurations()
        {
            PendingConfigurations.Clear();
        }

        private bool TryApply()
        {
            if (RemainingData == null || Shapez2Multiplayer.MapModel == null || !Shapez2Multiplayer.MapModel.TryGetBuilding(TileCoordinate, out var building))
            {
                return false;
            }

            using var memoryStream = new MemoryStream(RemainingData);
            var serializationVisitor = new BinarySerializationVisitor(false, false, Savegame.CurrentVersion, memoryStream, Shapez2Multiplayer.GameSessionOrchestrator.DataSerializers, Shapez2Multiplayer.logger);
            building.Configuration.Sync(serializationVisitor);
            return true;
        }

        private UpdateBuildingConfigurationPacket CloneForRetry()
        {
            return new UpdateBuildingConfigurationPacket
            {
                TileCoordinate = TileCoordinate,
                RemainingData = RemainingData?.ToArray() ?? System.Array.Empty<byte>()
            };
        }

        private class PendingConfiguration
        {
            public UpdateBuildingConfigurationPacket Packet;
            public IConnection? Source;
            public float ExpiresAt;

            public PendingConfiguration(UpdateBuildingConfigurationPacket packet, IConnection? source, float expiresAt)
            {
                Packet = packet;
                Source = source;
                ExpiresAt = expiresAt;
            }
        }
    }
}
