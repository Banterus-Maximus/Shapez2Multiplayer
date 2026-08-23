using Game.Core.Research;
using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class LevelUpPlayerLevelGoalPacket : IPacket
    {
        public PlayerLevelGoalId PlayerLevelGoalId;
        public int ExpectedLevel;
        public LevelUpPlayerLevelGoalPacket() { }
        public LevelUpPlayerLevelGoalPacket(PlayerLevelGoalId playerLevelGoalId, int expectedLevel)
        {
            PlayerLevelGoalId = playerLevelGoalId;
            ExpectedLevel = expectedLevel;
        }

        public void Decode(Stream stream)
        {
            using BinaryReader reader = new BinaryReader(stream);
            PlayerLevelGoalId = new PlayerLevelGoalId(reader.ReadString());
            ExpectedLevel = reader.ReadInt32();
        }

        public bool Encode(Stream stream)
        {
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(PlayerLevelGoalId.Id);
            writer.Write(ExpectedLevel);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("LevelUpPlayerLevelGoalPacket should only be received on the host.");
                return;
            }

            var currentLevel = Shapez2Multiplayer.Research.PlayerLevelGoals.GetLevel(PlayerLevelGoalId);
            if (currentLevel != ExpectedLevel)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Ignored stale job completion request for {PlayerLevelGoalId.Id}: client expected level {ExpectedLevel}, host is at {currentLevel}.");
                MultiplayerCore.socketManager.BroadcastResearchState();
                MultiplayerCore.socketManager.BroadcastPinState();
                return;
            }

            if (!Shapez2Multiplayer.Research.PlayerLevelGoals.TryLevelUp(PlayerLevelGoalId))
            {
                Shapez2Multiplayer.logger.Warning.Log("Client tried to level up player level goal when not able to, likely desync, research manager will be resynced now.");
            }
            MultiplayerCore.socketManager.BroadcastResearchState();
            MultiplayerCore.socketManager.BroadcastPinState();
        }
    }
}
