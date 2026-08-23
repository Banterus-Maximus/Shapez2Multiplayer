using Game.Core.Research;
using Game.HUD.QuestArea.PinnedShapes;
using Game.Placement.Data;
using Shapez2Multiplayer.Packets;
using System;
using System.Linq;

namespace Shapez2Multiplayer
{
    public static class MultiplayerEvents
    {
        public static void OnPinAdded(IPin pin)
        {
            if (Shapez2Multiplayer.IgnorePinEvents) return;
            if (MultiplayerCore.Hosting)
            {
                MultiplayerCore.socketManager.BroadcastPinState();
            }
            else if (MultiplayerCore.Client)
            {
                MultiplayerCore.connectionManager.Send(new PinChangePacket(pin, false));
            }
        }
        public static void OnPinRemoved(IPin pin)
        {
            if (Shapez2Multiplayer.IgnorePinEvents) return;
            if (MultiplayerCore.Hosting)
            {
                MultiplayerCore.socketManager.BroadcastPinState();
            }
            else if (MultiplayerCore.Client)
            {
                MultiplayerCore.connectionManager.Send(new PinChangePacket(pin, true));
            }
        }
        public static void OnPlacementDataChanged(IPlacementData placementData, PlacementInputHolder placementInput)
        {
            var packet = new PlacementIndicatorDataPacket(placementData, placementInput);
            var previousChunkedPackets = ChunkedPacket.ToSend.Where(c => c.Item3 == Packet.PlacementIndicatorData).Select(c => c.Item1.Id).Distinct().ToList();
            MultiplayerCore.SendToAll(packet);
            if (packet.Result)
            {
                foreach (var chunkId in previousChunkedPackets)
                {
                    ChunkedPacket.Cancel(chunkId);
                }
            }
        }
        public static void OnResearchLinearUpgradeManagerChanged(ResearchLinearUpgradeId researchLinearUpgradeId, int level)
        {
            if (!MultiplayerCore.Hosting) throw new Exception("OnResearchLinearUpgradeManagerChanged Should Only Be Called On Host");
            MultiplayerCore.socketManager.BroadcastResearchState();
        }
        public static void OnResearchPlayerLevelManagerChanged()
        {
            if (!MultiplayerCore.Hosting) throw new Exception("OnResearchPlayerLevelManagerChanged Should Only Be Called On Host");
            MultiplayerCore.socketManager.BroadcastResearchState();
        }
        public static void OnResearchPlayerLevelGoalManagerChanged()
        {
            if (!MultiplayerCore.Hosting) throw new Exception("OnResearchPlayerLevelGoalManagerChanged Should Only Be Called On Host");
            MultiplayerCore.socketManager.BroadcastResearchState();
            // TryLevelUp consumes the completed goal's shapes. Since vortex
            // storage has its own packet, publish that changed balance alongside
            // the new goal level or clients can keep an invalid Claim button.
            MultiplayerCore.socketManager.BroadcastVortexState();
            MultiplayerCore.socketManager.BroadcastPinState();
        }
        public static void OnResearchUnlockProgressManagerChanged()
        {
            if (!MultiplayerCore.Hosting) throw new Exception("OnResearchUnlockProgressManagerChanged Should Only Be Called On Host");
            MultiplayerCore.socketManager.BroadcastResearchState();
        }
        public static void OnResearchUnlockManagerResearchManuallyUnlockedByPlayer(IResearchUpgrade upgrade)
        {
            if (!MultiplayerCore.Hosting) throw new Exception("OnResearchUnlockManagerResearchManuallyUnlockedByPlayer Should Only Be Called On Host");
            MultiplayerCore.socketManager.BroadcastResearchState();
        }
        public static void OnPlayerInteractionStateChanged()
        {
            MultiplayerCore.SendToAll(new PlayerInteractionStateChangedPacket(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.State));
        }
        public static void OnWaypointAdded(IPlayerWaypoint waypoint)
        {
            if (Shapez2Multiplayer.IgnoreWaypointEvents) return;
            MultiplayerCore.SendToAll(new UpdateWaypointPacket(waypoint));
            if (MultiplayerCore.Hosting) MultiplayerCore.socketManager.BroadcastWaypointState();
        }
        public static void OnWaypointChanged(IPlayerWaypoint waypoint)
        {
            if (Shapez2Multiplayer.IgnoreWaypointEvents) return;
            MultiplayerCore.SendToAll(new UpdateWaypointPacket(waypoint));
            if (MultiplayerCore.Hosting) MultiplayerCore.socketManager.BroadcastWaypointState();
        }
        public static void OnWaypointRemoved(IPlayerWaypoint waypoint)
        {
            if (Shapez2Multiplayer.IgnoreWaypointEvents) return;
            MultiplayerCore.SendToAll(new DeleteWaypointPacket(waypoint));
            if (MultiplayerCore.Hosting) MultiplayerCore.socketManager.BroadcastWaypointState();
        }
    }
}
