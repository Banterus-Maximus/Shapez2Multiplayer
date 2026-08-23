using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class UniversalIDPacket : IPacket
    {
        public uint UniversalId;
        public int ProtocolVersion;
        public UniversalIDPacket() { }
        public UniversalIDPacket(uint universalId)
        {
            UniversalId = universalId;
        }

        public void Decode(Stream stream)
        {
            using BinaryReader reader = new BinaryReader(stream);
            UniversalId = reader.ReadUInt32();
            ProtocolVersion = stream.Position < stream.Length ? reader.ReadInt32() : 1;
        }

        public bool Encode(Stream stream)
        {
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(UniversalId);
            writer.Write(MultiplayerCore.NetworkProtocolVersion);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (MultiplayerCore.connectionManager == null) return;
            if (ProtocolVersion != MultiplayerCore.NetworkProtocolVersion)
            {
                Shapez2Multiplayer.logger.Error?.Log($"Host uses multiplayer protocol {ProtocolVersion}, but this build requires {MultiplayerCore.NetworkProtocolVersion}. Both players must install the same local mod build.");
                MultiplayerCore.Disconnect(MultiplayerCore.DisconnectReason.Lostconnection);
                return;
            }
            MultiplayerCore.connectionManager.UniversalId = UniversalId;
        }
    }
}
