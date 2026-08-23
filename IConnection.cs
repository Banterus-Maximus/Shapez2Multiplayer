using Shapez2Multiplayer.Packets;
using System;

namespace Shapez2Multiplayer
{
    public static class PacketDelivery
    {
        // Cursor/preview packets are "latest value wins" presentation data. All
        // gameplay mutations and authoritative snapshots must be reliable and
        // ordered so a lost placement or claim cannot permanently fork the map.
        public static bool IsReliable(Packet packet)
        {
            return packet != Packet.Cursor &&
                packet != Packet.PlacementIndicatorData &&
                packet != Packet.UpdateBuildingMassSelection &&
                packet != Packet.UpdateIslandMassSelection;
        }
    }

    public interface IConnection : IEquatable<IConnection>
    {
        public uint Id { get; }
        public uint UniversalId { get; }
        public int Ping { get; }
        public string Name => $"Player {UniversalId}";

        public void Close();
        public bool Send(byte[] data, Packet packet);
    }
}
