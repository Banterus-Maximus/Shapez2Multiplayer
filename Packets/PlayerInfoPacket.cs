using System.Collections.Generic;
using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class PlayerInfoPacket : IPacket
    {
        public string Name { get; set; }
        public PlayerInfoPacket() { }
        public PlayerInfoPacket(string name) {
            Name = name;
        }
        public void Decode(Stream stream)
        {
            using BinaryReader reader = new BinaryReader(stream);
            Name = reader.ReadString();
        }

        public bool Encode(Stream stream)
        {
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(Name);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection is ENetConnection eNetConnection)
            {
                ENetConnection.NameCache[eNetConnection.Id] = Name;
                if (MultiplayerCore.socketManager != null)
                {
                    MultiplayerCore.socketManager.SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>() { new InfoConnection(eNetConnection) }, new List<uint>()));
                    if (MultiplayerCore.socketManager.Connecting.Contains(eNetConnection))
                    {
                        MultiplayerCore.socketManager.SynchronizePauseState();
                    }
                }
            }
        }
    }
}
