using Shapez2Multiplayer.Packets;
using System;

namespace Shapez2Multiplayer
{
    public static class PacketDelivery
    {
        public static bool IsReplaceableSnapshot(Packet packet)
        {
            return packet == Packet.SyncResearchManager ||
                packet == Packet.SyncVortexStorage ||
                packet == Packet.SyncPins ||
                packet == Packet.SyncWaypoints ||
                packet == Packet.SyncWorldDigest;
        }

        // Cursor/preview packets and most repeating snapshots are "latest value
        // wins" data. Keeping them off the reliable gameplay queue avoids
        // head-of-line stalls. Vortex totals remain reliable because missing that
        // packet leaves milestone UI at zero; ENet isolates it on snapshot channel 1.
        public static bool IsReliable(Packet packet)
        {
            return packet != Packet.Cursor &&
                packet != Packet.PlacementIndicatorData &&
                packet != Packet.UpdateBuildingMassSelection &&
                packet != Packet.UpdateIslandMassSelection &&
                packet != Packet.SyncResearchManager &&
                packet != Packet.SyncPins &&
                packet != Packet.SyncWaypoints &&
                packet != Packet.SyncWorldDigest;
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
