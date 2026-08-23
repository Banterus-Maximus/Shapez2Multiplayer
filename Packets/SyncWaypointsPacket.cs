using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Shapez2Multiplayer.Packets
{
    /// <summary>Complete host-authoritative waypoint collection.</summary>
    public class SyncWaypointsPacket : IPacket
    {
        private const int MaxWaypoints = 2048;
        private const int MaxTextLength = 4096;

        public ulong Revision;
        public List<PlayerWaypoint> Waypoints = new List<PlayerWaypoint>();
        public bool HasSnapshot = true;

        public SyncWaypointsPacket() { }

        public SyncWaypointsPacket(ulong revision)
        {
            Revision = revision;
            if (Shapez2Multiplayer.PlayerWaypoints == null)
            {
                HasSnapshot = false;
                return;
            }

            Waypoints = Shapez2Multiplayer.PlayerWaypoints.Waypoints
                .Cast<PlayerWaypoint>()
                .Select(Clone)
                .ToList();
        }

        public bool Encode(Stream stream)
        {
            if (!HasSnapshot || Waypoints.Count > MaxWaypoints)
            {
                return false;
            }

            using var writer = new BinaryWriter(stream);
            writer.Write(Revision);
            writer.Write(Waypoints.Count);
            foreach (var waypoint in Waypoints)
            {
                WriteWaypoint(writer, waypoint);
            }
            return true;
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            Revision = reader.ReadUInt64();
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxWaypoints)
            {
                HasSnapshot = false;
                Shapez2Multiplayer.logger.Warning?.Log($"Rejected waypoint snapshot containing {count} entries.");
                return;
            }

            for (var index = 0; index < count; index++)
            {
                Waypoints.Add(ReadWaypoint(reader));
            }
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("A client tried to send an authoritative waypoint snapshot.");
                return;
            }
            if (!HasSnapshot || !MultiplayerSynchronization.ShouldApplyWaypointRevision(Revision))
            {
                return;
            }

            if (MultiplayerSynchronization.TryApplyWaypoints(Waypoints))
            {
                MultiplayerSynchronization.MarkWaypointRevisionApplied(Revision);
            }
            else
            {
                MultiplayerSynchronization.RequestRepair(SyncSubsystem.Waypoints, $"waypoint snapshot {Revision} did not converge");
            }
        }

        private static PlayerWaypoint Clone(PlayerWaypoint waypoint)
        {
            return new PlayerWaypoint
            {
                Name = waypoint.Name,
                ShapeIconKey = waypoint.ShapeIconKey,
                UID = waypoint.UID,
                PositionX = waypoint.PositionX,
                PositionY = waypoint.PositionY,
                Zoom = waypoint.Zoom,
                Angle = waypoint.Angle,
                BuildingLayer = waypoint.BuildingLayer,
                IslandLayer = waypoint.IslandLayer,
                RotationDegrees = waypoint.RotationDegrees,
            };
        }

        private static void WriteWaypoint(BinaryWriter writer, PlayerWaypoint waypoint)
        {
            writer.Write(waypoint.Name ?? string.Empty);
            writer.Write(waypoint.ShapeIconKey ?? string.Empty);
            writer.Write(waypoint.UID ?? string.Empty);
            writer.Write(waypoint.PositionX);
            writer.Write(waypoint.PositionY);
            writer.Write(waypoint.Zoom);
            writer.Write(waypoint.Angle);
            writer.Write(waypoint.BuildingLayer);
            writer.Write(waypoint.IslandLayer);
            writer.Write(waypoint.RotationDegrees);
        }

        private static PlayerWaypoint ReadWaypoint(BinaryReader reader)
        {
            var name = ReadLimitedString(reader);
            var shapeIconKey = ReadLimitedString(reader);
            var uid = ReadLimitedString(reader);
            return new PlayerWaypoint
            {
                Name = name,
                ShapeIconKey = shapeIconKey,
                UID = uid,
                PositionX = reader.ReadDouble(),
                PositionY = reader.ReadDouble(),
                Zoom = reader.ReadSingle(),
                Angle = reader.ReadSingle(),
                BuildingLayer = reader.ReadInt16(),
                IslandLayer = reader.ReadInt16(),
                RotationDegrees = reader.ReadSingle(),
            };
        }

        private static string ReadLimitedString(BinaryReader reader)
        {
            var value = reader.ReadString();
            if (value.Length > MaxTextLength)
            {
                throw new InvalidDataException("Waypoint text exceeded the protocol limit.");
            }
            return value;
        }
    }
}
