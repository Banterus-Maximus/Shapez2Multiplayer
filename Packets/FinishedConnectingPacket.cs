using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class FinishedConnectingPacket : IPacket
    {
        public int ProtocolVersion;
        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            ProtocolVersion = stream.Position < stream.Length ? reader.ReadInt32() : 1;
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(MultiplayerCore.NetworkProtocolVersion);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null)
            {
                Shapez2Multiplayer.logger.Warning.Log("FinishedConnectingPacket Recieved From Host");
                return;
            }
            if (ProtocolVersion != MultiplayerCore.NetworkProtocolVersion)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Disconnected {connection.Name}: multiplayer protocol {ProtocolVersion} is incompatible with required protocol {MultiplayerCore.NetworkProtocolVersion}.");
                MultiplayerCore.socketManager.Disconnect(connection, MultiplayerCore.DisconnectReason.Lostconnection);
                return;
            }
            MultiplayerCore.socketManager.Connecting.Remove(connection);
            // The save is a point-in-time snapshot. Send the current authoritative
            // state again after the client's load has finished, then periodic
            // snapshots keep it repaired for the rest of the session.
            MultiplayerCore.socketManager.BroadcastResearchState();
            MultiplayerCore.socketManager.BroadcastPinState();
            MultiplayerCore.socketManager.SynchronizePauseState();
            if (MultiplayerCore.socketManager.Connecting.Count == 0)
            {
                PlacementIndicatorDataPacket.SentToAllConnections = false;
                MultiplayerCore.socketManager.ForceUpdateCursor();
                MultiplayerCore.socketManager.PingUpdateTimer = float.MaxValue;
            }
        }
    }
}
