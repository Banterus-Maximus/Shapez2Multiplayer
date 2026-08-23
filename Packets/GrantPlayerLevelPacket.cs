using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class GrantPlayerLevelPacket : IPacket
    {
        public int ExpectedLevel;

        public GrantPlayerLevelPacket() { }

        public GrantPlayerLevelPacket(int expectedLevel)
        {
            ExpectedLevel = expectedLevel;
        }

        public void Decode(Stream stream)
        {
            using BinaryReader reader = new BinaryReader(stream);
            ExpectedLevel = reader.ReadInt32();
        }

        public bool Encode(Stream stream)
        {
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(ExpectedLevel);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("GrantPlayerLevelPacket should only be received on the host.");
                return;
            }

            var research = Shapez2Multiplayer.Research;
            if (research == null)
            {
                return;
            }

            var currentLevel = research.PlayerLevel.Level;
            if (currentLevel != ExpectedLevel)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Ignored stale operator-level claim: client expected level {ExpectedLevel}, host is at {currentLevel}.");
                MultiplayerCore.socketManager.BroadcastResearchState();
                MultiplayerCore.socketManager.BroadcastPinState();
                return;
            }

            // The client-side claim control has already checked that every line is
            // complete. Execute the claim once on the authoritative host; the
            // resulting snapshot invokes GrantPlayerLevel on every other player so
            // they receive the native unlock sequence without duplicating rewards.
            research.PlayerLevel.GrantPlayerLevel();
            MultiplayerCore.socketManager.BroadcastResearchState();
            MultiplayerCore.socketManager.BroadcastVortexState();
            MultiplayerCore.socketManager.BroadcastPinState();
        }
    }
}
