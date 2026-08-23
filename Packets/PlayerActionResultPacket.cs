using System.IO;

namespace Shapez2Multiplayer.Packets
{
    /// <summary>Host acknowledgement for a client-originated player action.</summary>
    public class PlayerActionResultPacket : IPacket
    {
        public ulong CommandId;
        public bool Accepted;

        public PlayerActionResultPacket() { }

        public PlayerActionResultPacket(ulong commandId, bool accepted)
        {
            CommandId = commandId;
            Accepted = accepted;
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(CommandId);
            writer.Write(Accepted);
            return true;
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            CommandId = reader.ReadUInt64();
            Accepted = reader.ReadBoolean();
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("A client tried to acknowledge a player action.");
                return;
            }

            MultiplayerSynchronization.CompleteOutgoingAction(CommandId, Accepted);
        }
    }
}
