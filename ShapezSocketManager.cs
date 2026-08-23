using Core.Localization;
using K4os.Compression.LZ4;
using Shapez2Multiplayer.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using static Shapez2Multiplayer.Packets.ChunkedPacket;

namespace Shapez2Multiplayer
{
    public class ShapezSocketManager
    {
        private List<ISocketManager> _socketManagers = new List<ISocketManager>();
        public IReadOnlyCollection<ISocketManager> SocketManagers => _socketManagers;
        public IReadOnlyCollection<IConnection> Connected => _socketManagers.SelectMany(s => s.Connected).ToArray();
        public List<IConnection> Connecting = new List<IConnection>();
        public List<Tuple<IPacket, IConnection>> BufferedRecievePackets = new List<Tuple<IPacket, IConnection>>();
        public List<IPacket> BufferedSendToAllPackets = new List<IPacket>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToAllExceptPackets = new List<Tuple<IPacket, IConnection>>();
        public List<Tuple<IPacket, List<IConnection>>> BufferedSendToAllExceptListPackets = new List<Tuple<IPacket, List<IConnection>>>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToPackets = new List<Tuple<IPacket, IConnection>>();
        public List<Tuple<IPacket, List<IConnection>>> BufferedSendToListPackets = new List<Tuple<IPacket, List<IConnection>>>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToAllFromPackets = new List<Tuple<IPacket, IConnection>>();
        public Dictionary<uint, OtherPlayerEntityPlacementDrawer> PlayersDrawers = new Dictionary<uint, OtherPlayerEntityPlacementDrawer>();
        public Dictionary<uint, OtherPlayerHUDBuildingMassSelection> PlayersBuildingMassSelections = new Dictionary<uint, OtherPlayerHUDBuildingMassSelection>();
        public Dictionary<uint, OtherPlayerHUDIslandMassSelection> PlayersIslandMassSelections = new Dictionary<uint, OtherPlayerHUDIslandMassSelection>();
        public static readonly List<Type> AlwaysAllowedToSend = new List<Type>()
        {
            typeof(SavegamePacket),
            typeof(PausePacket),
            typeof(DisconnectReasonPacket),
            typeof(UpdateConnectionInfoPacket),
            typeof(ChunkedPacket),
            typeof(ChunkReceivedPacket),
            typeof(UniversalIDPacket)
        };
        public static readonly List<Type> AlwaysAllowedToRecieve = new List<Type>()
        {
            typeof(FinishedConnectingPacket),
            typeof(PlayerInfoPacket),
            typeof(ChunkedPacket),
            typeof(ChunkReceivedPacket)
        };
        public ShapezSocketManager(ISocketManager socketManager)
        {
            socketManager.ConnectedEvent += OnConnected;
            socketManager.DisconnectedEvent += OnDisconnected;
            socketManager.MessageEvent += OnMessage;
            lock (_socketManagers)
            {
                _socketManagers.Add(socketManager);
            }
        }
        public void AddSocketManager(ISocketManager socketManager)
        {
            socketManager.ConnectedEvent += OnConnected;
            socketManager.DisconnectedEvent += OnDisconnected;
            socketManager.MessageEvent += OnMessage;
            lock (_socketManagers)
            {
                _socketManagers.Add(socketManager);
            }
        }
        public void RemoveSocketManager(ISocketManager socketManager)
        {
            socketManager.Close();
            lock (_socketManagers)
            {
                _socketManagers.Remove(socketManager);
            }
        }
        public void OnConnected(IConnection connection)
        {
            Shapez2Multiplayer.logger.Info?.Log("Client connected: " + connection.UniversalId);
            HUDMultiplayerPausePanel.instance.AddPlayer(connection);
            HUDMultiplayerJumpPanel.Instance?.AddPlayer(connection);
            ChunkedPacket.ChunkedPacketCache.Add(connection.UniversalId, new Dictionary<uint, ChunkCacheData>());
            PlayersDrawers.Add(connection.UniversalId, Shapez2Multiplayer.CreateOtherPlayerEntityPlacementDrawer());
            PlayersBuildingMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDBuildingMassSelection(connection));
            PlayersIslandMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDIslandMassSelection(connection));
            if (!connection.Send(PacketExtensions.Encode(new UniversalIDPacket(connection.UniversalId)), Packets.Packet.UniversalID)) Shapez2Multiplayer.logger.Warning.Log($"Failed to send UniversalId Packet");
            SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>() { new InfoConnection(connection) }, new List<uint>()));
            Connecting.Add(connection);
            SynchronizePauseState();
            Shapez2Multiplayer.YetToRecieveSavegame.Add(connection);
            if (Shapez2Multiplayer.YetToRecieveSavegame.Count == 1) Shapez2Multiplayer.GameSessionOrchestrator.TrySaveCurrentAsync();
        }

        public void OnDisconnected(IConnection connection)
        {
            Shapez2Multiplayer.logger.Info?.Log("Client disconnected: " + connection.UniversalId);
            HUDMultiplayerPausePanel.instance.RemovePlayer(connection);
            HUDMultiplayerJumpPanel.Instance?.RemovePlayer(connection);
            ChunkedPacket.ChunkedPacketCache.Remove(connection.UniversalId);
            ChunkedPacket.ToSend.RemoveAll(c => c.Item2 == connection);
            if (ChunkedPacket.WaitingFromId.HasValue && ChunkedPacket.WaitingFromId.Value == connection.UniversalId)
            {
                ChunkedPacket.WaitingFromId = null;
                if (ChunkedPacket.ToSend.Count > 0) ChunkedPacket.SendOne();
            }
            PlayersDrawers.Remove(connection.UniversalId);
            PlayersBuildingMassSelections.Remove(connection.UniversalId);
            PlayersIslandMassSelections.Remove(connection.UniversalId);
            HUDMultiplayerCursors.Instance.RemoveCursor(connection);
            SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>(), new List<uint>() { connection.UniversalId }));
            if (Connecting.Remove(connection))
            {
                SynchronizePauseState();
            }
            Shapez2Multiplayer.HUD.Events.ShowNotification.Invoke(new HUDNotificationData(HUDNotificationType.Info, "multiplayer.player-lost-connection".T().Bind("player-name", new RawText(connection.Name))));
        }
        public void Disconnect(IConnection connection, MultiplayerCore.DisconnectReason? reason = null)
        {
            if (reason.HasValue)
            {
                SendTo(new DisconnectReasonPacket(reason.Value), connection);
                lock (_socketManagers)
                {
                    foreach (var socketManager in _socketManagers) socketManager.Update();
                }
            }
            connection.Close();
        }

        public void OnMessage(IConnection connection, byte[] data)
        {
            var compressedLength = data.Length;
            data = LZ4Pickler.Unpickle(data);
#if DEBUG
            Shapez2Multiplayer.logger.Info?.Log($"Recieved Data Of Length: {data.Length}, Compressed {compressedLength}");
#endif
            var packet = PacketExtensions.Decode(data);
            if (Connecting.Count > 0 && !AlwaysAllowedToRecieve.Contains(packet.GetType()))
            {
                BufferedRecievePackets.Add(new Tuple<IPacket, IConnection>(packet, connection));
                return;
            }
            packet.Handle(connection);
        }
        public bool SendToAll(IPacket packet)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                // Authoritative snapshots supersede older buffered snapshots. A
                // slow savegame load must not produce a burst of dozens of stale
                // one-second research packets when the session resumes.
                if (packet is SyncResearchManagerPacket || packet is SyncPinsPacket)
                {
                    BufferedSendToAllPackets.RemoveAll(buffered => buffered.GetType() == packet.GetType());
                }
                BufferedSendToAllPackets.Add(packet);
                return true;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return true;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            bool success = true;
            foreach (var connection in Connected)
            {
                // Do not short-circuit here: one failed connection must not prevent
                // every later connection in the collection from receiving a packet.
                var sent = connection.Send(encoded, type);
                success = sent && success;
                if (!sent) Shapez2Multiplayer.logger.Warning?.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
            return success;
        }
        public void SendToAllExcept(IPacket packet, IConnection excluded)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                BufferedSendToAllExceptPackets.Add(new Tuple<IPacket, IConnection>(packet, excluded));
                return;
            }
            if (!Connected.Any(connection => connection != excluded)) return;
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (connection == excluded) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendToAllFrom(IPacket packet, IConnection from)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                BufferedSendToAllFromPackets.Add(new Tuple<IPacket, IConnection>(packet, from));
                return;
            }
            if (!Connected.Any(connection => connection != from)) return;
            var encoded = PacketExtensions.Encode(packet, from.UniversalId);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (connection == from) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendToAllExcept(IPacket packet, List<IConnection> excluded)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                BufferedSendToAllExceptListPackets.Add(new Tuple<IPacket, List<IConnection>>(packet, excluded));
                return;
            }
            if (!Connected.Any(connection => !excluded.Contains(connection))) return;
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (excluded.Contains(connection)) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendTo(IPacket packet, IConnection connection)
        {
            if (!Connected.Contains(connection) && !Connecting.Contains(connection)) return;
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                BufferedSendToPackets.Add(new Tuple<IPacket, IConnection>(packet, connection));
                return;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
        }
        public void SendTo(IPacket packet, List<IConnection> connections)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                BufferedSendToListPackets.Add(new Tuple<IPacket, List<IConnection>>(packet, connections));
                return;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in connections)
            {
                if (!Connected.Contains(connection) && !Connecting.Contains(connection)) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void ForceUpdateCursor()
        {
            SyncCursorTimer = 0.0f;
            var cursorState = Shapez2Multiplayer.GameCursorManager._State;
            if (ScreenUtils.TryGetWorldCoordinate(Shapez2Multiplayer.GameSessionOrchestrator.Viewport, Shapez2Multiplayer.GameSessionOrchestrator.Viewport.CursorScreenPosition, out var cursorWorldPosition))
            {
                LastCursorState = cursorState;
                LastCursorWorldPosition = (float3)cursorWorldPosition;
                SendToAll(new CursorPacket((float3)cursorWorldPosition, cursorState));
            }
            var viewportIslandLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.IslandLayer;
            var viewportBuildingLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.BuildingLayer;
            var viewportShowAllBuildingLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllBuildingLayers;
            var viewportShowAllIslandLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllIslandLayers;
            LastViewportIslandLayer = viewportIslandLayer;
            LastViewportBuildingLayer = viewportBuildingLayer;
            LastViewportShowAllBuildingLayers = viewportShowAllBuildingLayers;
            LastViewportShowAllIslandLayers = viewportShowAllIslandLayers;
            SendToAll(new ViewportPropertyChangedPacket(viewportIslandLayer, viewportBuildingLayer, viewportShowAllBuildingLayers, viewportShowAllIslandLayers));
            SendToAll(new PlayerInteractionStateChangedPacket(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.State));
        }
        public float PingUpdateTimer = 0.0f;
        public float SyncPauseTimer = 0.0f;
        public float SyncResearchTimer = 0.0f;
        public float SyncPinsTimer = 0.0f;
        private ulong ResearchRevision;
        private ulong PinRevision;
        float MassSelectionsTimer = 0.0f;
        float SyncLobbyDataTimer = 0.0f;
        float SyncCursorTimer = 0.0f;
        const float PING_UPDATE_TIME = 5.0f;
        // Pause packets are small but essential: clients are intentionally not
        // allowed to change simulation speed themselves. Repeating this state
        // repairs a packet lost during the savegame/orchestrator transition.
        const float SYNC_PAUSE_TIME = 1.0f;
        // Vortex delivery totals and research credits change without going through
        // player actions. A short authoritative cadence keeps those simulation
        // products converged while event-driven broadcasts handle purchases/jobs.
        const float SYNC_RESEARCH_TIME = 1.0f;
        const float SYNC_PINS_TIME = 5.0f;
        const float SYNC_MASS_SELECTIONS_TIME = 1.0f;
        const float SYNC_LOBBY_DATA_TIME = 60.0f * 5f;
        const float SYNC_CURSOR_TIME = 0.1f;
        CursorHoverState? LastCursorState;
        float3? LastCursorWorldPosition;
        short? LastViewportIslandLayer;
        short? LastViewportBuildingLayer;
        bool? LastViewportShowAllBuildingLayers;
        bool? LastViewportShowAllIslandLayers;

        public void BroadcastResearchState()
        {
            SyncResearchTimer = 0.0f;
            if (Shapez2Multiplayer.Research == null || Connected.Count == 0) return;
            SendToAll(new SyncResearchManagerPacket(Shapez2Multiplayer.Research, ++ResearchRevision));
        }

        public void BroadcastPinState()
        {
            SyncPinsTimer = 0.0f;
            if (Shapez2Multiplayer.GameSessionOrchestrator == null || Connected.Count == 0) return;
            SendToAll(new SyncPinsPacket(++PinRevision));
        }

        public void SynchronizePauseState()
        {
            SyncPauseTimer = 0.0f;
            PausePacket packet;
            if (Connecting.Count == 0)
            {
                packet = new PausePacket(false);
                SendToAll(packet);
            }
            else
            {
                packet = new PausePacket(true, new CombinedText(
                    "multiplayer.paused-dialog.description-waitingforplayer".T(),
                    new RawText("\n" + string.Join(", ", Connecting.Select(c => c.Name)))));
                // A client still loading the save has no simulation manager yet.
                // Send only to the host and players whose load has completed.
                SendToAllExcept(packet, Connecting);
            }
            packet.Handle(null);
        }

        public void Update()
        {
            lock (_socketManagers)
            {
                foreach (var sm in _socketManagers.ToList()) // the lock will not work for some reason so just use a copy
                {
                    if (sm.Valid) sm.Update();
                }
            }
            if (Connected.Count > 0)
            {
                // Simulation pause can stop scaled delta time, so use unscaled time.
                SyncPauseTimer += Time.unscaledDeltaTime;
                if (SyncPauseTimer >= SYNC_PAUSE_TIME)
                {
                    SynchronizePauseState();
                }
            }
            else
            {
                SyncPauseTimer = 0.0f;
            }
            PingUpdateTimer += Time.deltaTime;
            if (PingUpdateTimer >= PING_UPDATE_TIME)
            {
                PingUpdateTimer = 0.0f;
                SendToAll(new UpdateConnectionInfoPacket(Connected.Select(c => new InfoConnection(c)).ToList(), new List<uint>()));
            }
            SyncResearchTimer += Time.deltaTime;
            if (SyncResearchTimer >= SYNC_RESEARCH_TIME)
            {
                BroadcastResearchState();
            }
            SyncPinsTimer += Time.deltaTime;
            if (SyncPinsTimer >= SYNC_PINS_TIME)
            {
                BroadcastPinState();
            }
            MassSelectionsTimer += Time.deltaTime;
            if (MassSelectionsTimer >= SYNC_MASS_SELECTIONS_TIME)
            {
                MassSelectionsTimer = 0.0f;
                SendToAll(new UpdateBuildingMassSelectionPacket(Shapez2Multiplayer.HUDBuildingMassSelection, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.ToList()));
                SendToAll(new UpdateIslandMassSelectionPacket(Shapez2Multiplayer.HUDIslandMassSelection, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.IslandSelection.ToList()));
            }
            SyncLobbyDataTimer += Time.deltaTime;
            if (SyncLobbyDataTimer >= SYNC_LOBBY_DATA_TIME)
            {
                SyncLobbyDataTimer = 0.0f;
                MultiplayerCore.RefreshLobbyData();
            }
            SyncCursorTimer += Time.deltaTime;
            if (SyncCursorTimer >= SYNC_CURSOR_TIME)
            {
                SyncCursorTimer = 0.0f;
                var cursorState = Shapez2Multiplayer.GameCursorManager._State;
                if (ScreenUtils.TryGetWorldCoordinate(Shapez2Multiplayer.GameSessionOrchestrator.Viewport, Shapez2Multiplayer.GameSessionOrchestrator.Viewport.CursorScreenPosition, out var cursorWorldPosition) && (cursorState != LastCursorState || !((float3)cursorWorldPosition).Equals(LastCursorWorldPosition)))
                {
                    LastCursorState = cursorState;
                    LastCursorWorldPosition = (float3)cursorWorldPosition;
                    SendToAll(new CursorPacket((float3)cursorWorldPosition, cursorState));
                }
                var viewportIslandLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.IslandLayer;
                var viewportBuildingLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.BuildingLayer;
                var viewportShowAllBuildingLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllBuildingLayers;
                var viewportShowAllIslandLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllIslandLayers;
                if (viewportIslandLayer != LastViewportIslandLayer || viewportBuildingLayer != LastViewportBuildingLayer || viewportShowAllBuildingLayers != LastViewportShowAllBuildingLayers || viewportShowAllIslandLayers != LastViewportShowAllIslandLayers)
                {
                    LastViewportIslandLayer = viewportIslandLayer;
                    LastViewportBuildingLayer = viewportBuildingLayer;
                    LastViewportShowAllBuildingLayers = viewportShowAllBuildingLayers;
                    LastViewportShowAllIslandLayers = viewportShowAllIslandLayers;
                    SendToAll(new ViewportPropertyChangedPacket(viewportIslandLayer, viewportBuildingLayer, viewportShowAllBuildingLayers, viewportShowAllIslandLayers));
                }
            }
            if (Connecting.Count > 0) return;
            foreach (var packet in BufferedRecievePackets)
            {
                packet.Item1.Handle(packet.Item2);
            }
            BufferedRecievePackets.Clear();
            foreach (var packet in BufferedSendToAllPackets)
            {
                SendToAll(packet);
            }
            BufferedSendToAllPackets.Clear();
            foreach (var packet in BufferedSendToAllExceptPackets)
            {
                SendToAllExcept(packet.Item1, packet.Item2);
            }
            BufferedSendToAllExceptPackets.Clear();
            foreach (var packet in BufferedSendToAllExceptListPackets)
            {
                SendToAllExcept(packet.Item1, packet.Item2);
            }
            BufferedSendToAllExceptListPackets.Clear();
            foreach (var packet in BufferedSendToPackets)
            {
                SendTo(packet.Item1, packet.Item2);
            }
            BufferedSendToPackets.Clear();
            foreach (var packet in BufferedSendToListPackets)
            {
                SendTo(packet.Item1, packet.Item2);
            }
            BufferedSendToListPackets.Clear();
            foreach (var packet in BufferedSendToAllFromPackets)
            {
                SendToAllFrom(packet.Item1, packet.Item2);
            }
            BufferedSendToAllFromPackets.Clear();
        }
    }
}
