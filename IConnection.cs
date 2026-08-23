using Shapez2Multiplayer.Packets;
using System;

namespace Shapez2Multiplayer
{
    public static class PacketDelivery
    {
        // Cursor/preview packets and complete repeating snapshots are "latest
        // value wins" data. Keeping them off the reliable gameplay queue avoids
        // head-of-line stalls; a later full snapshot repairs a dropped one.
        // Mutations, requests, acknowledgements and pause state remain reliable.
        public static bool IsReliable(Packet packet)
        {
            return packet != Packet.Cursor &&
                packet != Packet.PlacementIndicatorData &&
                packet != Packet.UpdateBuildingMassSelection &&
                packet != Packet.UpdateIslandMassSelection &&
                packet != Packet.SyncResearchManager &&
                packet != Packet.SyncVortexStorage &&
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
