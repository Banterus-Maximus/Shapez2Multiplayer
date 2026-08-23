using System.IO;
using System.Linq;

namespace Shapez2Multiplayer.Packets
{
    public class PlayerActionPacket : IPacket
    {
        public ulong CommandId;
        public IPlayerAction PlayerAction { get; set; }
        public PlayerActionPacket() { }
        public PlayerActionPacket(IPlayerAction playerAction)
        {
            CommandId = MultiplayerSynchronization.NextOutgoingActionId();
            PlayerAction = playerAction;
        }
        public void Decode(Stream stream)
        {
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                CommandId = reader.ReadUInt64();
            }
            Encoding.serializationVisitor = new BinarySerializationVisitor(false, false, Savegame.CurrentVersion, stream, Shapez2Multiplayer.GameSessionOrchestrator.DataSerializers, Shapez2Multiplayer.logger);
            if (stream.Position >= stream.Length)
            {
                Shapez2Multiplayer.logger.Error.Log("Recieved empty player action packet");
                return;
            }
            PlayerAction = Encoding.DecodePlayerAction(stream);
        }

        public bool Encode(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(CommandId);
            }
            Encoding.serializationVisitor = new BinarySerializationVisitor(true, false, Savegame.CurrentVersion, stream, Shapez2Multiplayer.GameSessionOrchestrator.DataSerializers, Shapez2Multiplayer.logger);
            Encoding.Encode(PlayerAction, stream);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (PlayerAction == null) return;
#if DEBUG
            Shapez2Multiplayer.DebugLastAction = PlayerAction;
#endif
            Shapez2Multiplayer.WaitingActions.Add(PlayerAction);
            if (connection != null && !MultiplayerCore.socketManager.TryAcceptActionCommand(connection, CommandId, out var previousResult))
            {
                Shapez2Multiplayer.WaitingActions.Remove(PlayerAction);
                MultiplayerCore.socketManager.SendTo(new PlayerActionResultPacket(CommandId, previousResult), connection);
                return;
            }

            var isAuthoritativeResearchAction = PlayerAction is ResearchUpgradePlayerAction || PlayerAction is LevelUpLinearUpgradePlayerAction;
            bool accepted;
            if (isAuthoritativeResearchAction && connection != null)
            {
                accepted = Shapez2Multiplayer.PlayerActions.TryScheduleAction(PlayerAction);
                if (!accepted)
                {
                    Shapez2Multiplayer.logger.Warning.Log("Action Failed, Likely Desync");
                    Shapez2Multiplayer.WaitingActions.Remove(PlayerAction);
                    MultiplayerCore.socketManager.SendAuthoritativeState(connection, SyncSubsystem.All);
                }
                // Research requests use the validated scheduler exactly once. The
                // previous fall-through scheduled the same action a second time via
                // TryScheduleActionNoDetection, which could double-spend credits.
                MultiplayerCore.socketManager.RecordActionCommandResult(connection, accepted);
                MultiplayerCore.socketManager.SendTo(new PlayerActionResultPacket(CommandId, accepted), connection);
                return;
            }
            if (PlayerAction is ActionModifyBuildings actionModifyBuildings)
            {
                foreach (var delete in actionModifyBuildings.Data.Delete)
                {
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.Remove(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.Where(b => b.Id == delete.BuildingId));
                }
            } else if (PlayerAction is ActionModifyIsland actionModifyIsland)
            {
                foreach (var delete in actionModifyIsland.Data.Delete)
                {
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.Remove(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.Where(b => b.Island.Id == delete.IslandId));
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.IslandSelection.Remove(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.IslandSelection.Where(i => i.Id == delete.IslandId));
                }
            }
            accepted = Shapez2Multiplayer.PlayerActions.TryScheduleActionNoDetection(PlayerAction);
            if (!accepted)
            {
                Shapez2Multiplayer.logger.Warning.Log("Action Failed, Likely Desync");
                Shapez2Multiplayer.WaitingActions.Remove(PlayerAction);
                if (connection == null)
                {
                    MultiplayerSynchronization.RequestRepair(SyncSubsystem.All, $"could not apply player action {CommandId}");
                }
            }
            if (connection != null)
            {
                // A client action is relayed only after the host accepted it. This
                // prevents peers applying a placement the authoritative map rejected.
                if (accepted)
                {
                    MultiplayerCore.socketManager.SendToAllFrom(this, connection);
                }
                else
                {
                    MultiplayerCore.socketManager.SendAuthoritativeState(connection, SyncSubsystem.All);
                }
                MultiplayerCore.socketManager.RecordActionCommandResult(connection, accepted);
                MultiplayerCore.socketManager.SendTo(new PlayerActionResultPacket(CommandId, accepted), connection);
            }
        }
    }
}
