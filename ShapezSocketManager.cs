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
        private readonly Dictionary<uint, ulong> LastAcceptedActionCommand = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, bool> LastActionCommandResult = new Dictionary<uint, bool>();
        private readonly Dictionary<uint, float> LastSyncRequestTime = new Dictionary<uint, float>();
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
            typeof(ChunkReceivedPacket),
            typeof(RequestSyncPacket)
        };
        private static bool IsAuthoritativeSnapshot(IPacket packet)
        {
            return packet is SyncResearchManagerPacket ||
                packet is SyncVortexStoragePacket ||
                packet is SyncPinsPacket ||
                packet is SyncWaypointsPacket ||
                packet is SyncWorldDigestPacket;
        }
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
            ChunkedPacket.ChunkedPacketCache.Add(connection.UniversalId, new Dictionary<uint, ChunkCacheData>());
            PlayersDrawers.Add(connection.UniversalId, Shapez2Multiplayer.CreateOtherPlayerEntityPlacementDrawer());
            PlayersBuildingMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDBuildingMassSelection(connection));
            PlayersIslandMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDIslandMassSelection(connection));
            if (!connection.Send(PacketExtensions.Encode(new UniversalIDPacket(connection.UniversalId)), Packets.Packet.UniversalID)) Shapez2Multiplayer.logger.Warning.Log($"Failed to send UniversalId Packet");
            SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>() { new InfoConnection(connection) }, new List<uint>()));
            Connecting.Add(connection);
            SynchronizePauseState();
            Shapez2Multiplayer.YetToRecieveSavegame.Add(connection);
            LastAcceptedActionCommand[connection.UniversalId] = 0;
            LastActionCommandResult[connection.UniversalId] = false;
            LastSyncRequestTime[connection.UniversalId] = float.NegativeInfinity;
            if (Shapez2Multiplayer.YetToRecieveSavegame.Count == 1) Shapez2Multiplayer.GameSessionOrchestrator.TrySaveCurrentAsync();
        }

        public void OnDisconnected(IConnection connection)
        {
            Shapez2Multiplayer.logger.Info?.Log("Client disconnected: " + connection.UniversalId);
            HUDMultiplayerPausePanel.instance.RemovePlayer(connection);
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
            LastAcceptedActionCommand.Remove(connection.UniversalId);
            LastActionCommandResult.Remove(connection.UniversalId);
            LastSyncRequestTime.Remove(connection.UniversalId);
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

        public bool TryAcceptActionCommand(IConnection connection, ulong commandId, out bool previousResult)
        {
            LastAcceptedActionCommand.TryGetValue(connection.UniversalId, out var lastCommandId);
            LastActionCommandResult.TryGetValue(connection.UniversalId, out previousResult);
            if (commandId == 0 || commandId <= lastCommandId)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Ignored duplicate player action {commandId} from {connection.Name} (last accepted {lastCommandId}).");
                return false;
            }
            if (lastCommandId != 0 && commandId != lastCommandId + 1)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Player action sequence gap from {connection.Name}: received {commandId} after {lastCommandId}.");
            }
            LastAcceptedActionCommand[connection.UniversalId] = commandId;
            LastActionCommandResult[connection.UniversalId] = false;
            return true;
        }

        public void RecordActionCommandResult(IConnection connection, bool accepted)
        {
            LastActionCommandResult[connection.UniversalId] = accepted;
        }

        public void HandleSyncRequest(IConnection connection, SyncSubsystem subsystems, string reason)
        {
            var now = Time.realtimeSinceStartup;
            LastSyncRequestTime.TryGetValue(connection.UniversalId, out var lastRequestTime);
            if (now - lastRequestTime < 1.0f)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Rate-limited repeated sync request from {connection.Name}.");
                return;
            }
            LastSyncRequestTime[connection.UniversalId] = now;
            Shapez2Multiplayer.logger.Info?.Log($"Sending {subsystems} repair snapshot to {connection.Name}. Reason: {reason}");
            SendAuthoritativeState(connection, subsystems);
        }

        public void OnMessage(IConnection connection, byte[] data)
        {
            var compressedLength = data.Length;
            try
            {
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
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Rejected malformed or incompatible packet from {connection.Name} ({compressedLength} compressed bytes).");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
            }
        }
        public bool SendToAll(IPacket packet)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                // Authoritative snapshots supersede older buffered snapshots. A
                // slow savegame load must not produce a burst of dozens of stale
                // one-second research packets when the session resumes.
                if (IsAuthoritativeSnapshot(packet))
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
                if (IsAuthoritativeSnapshot(packet))
                {
                    BufferedSendToPackets.RemoveAll(buffered => buffered.Item2 == connection && buffered.Item1.GetType() == packet.GetType());
                }
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
                if (IsAuthoritativeSnapshot(packet))
                {
                    BufferedSendToListPackets.RemoveAll(buffered => buffered.Item1.GetType() == packet.GetType() && buffered.Item2.SequenceEqual(connections));
                }
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
        public float SyncVortexTimer = 0.0f;
        public float SyncPinsTimer = 0.0f;
        public float SyncWaypointsTimer = 0.0f;
        public float SyncWorldDigestTimer = 0.0f;
        private ulong ResearchRevision;
        private ulong VortexRevision;
        private ulong PinRevision;
        private ulong WaypointRevision;
        private ulong WorldDigestRevision;
        float MassSelectionsTimer = 0.0f;
        float SyncLobbyDataTimer = 0.0f;
        float SyncCursorTimer = 0.0f;
        const float PING_UPDATE_TIME = 5.0f;
        // Pause packets are small but essential: clients are intentionally not
        // allowed to change simulation speed themselves. Repeating this state
        // repairs a packet lost during the savegame/orchestrator transition.
        const float SYNC_PAUSE_TIME = 1.0f;
        // Research credits change without always going through player actions.
        const float SYNC_RESEARCH_TIME = 1.0f;
        // Vortex totals have a separate cadence so unrelated research handling
        // cannot delay or invalidate delivered-shape reconciliation.
        const float SYNC_VORTEX_TIME = 0.5f;
        const float SYNC_PINS_TIME = 5.0f;
        const float SYNC_WAYPOINTS_TIME = 5.0f;
        const float SYNC_WORLD_DIGEST_TIME = 3.0f;
        const float SYNC_MASS_SELECTIONS_TIME = 1.0f;
        const float SYNC_LOBBY_DATA_TIME = 60.0f * 5f;
        const float SYNC_CURSOR_TIME = 0.1f;
        CursorHoverState? LastCursorState;
        float3? LastCursorWorldPosition;
        short? LastViewportIslandLayer;
        short? LastViewportBuildingLayer;
        bool? LastViewportShowAllBuildingLayers;
        bool? LastViewportShowAllIslandLayers;

        public void BroadcastResearchState(IConnection? target = null)
        {
            if (target == null) SyncResearchTimer = 0.0f;
            if (Shapez2Multiplayer.Research == null || Connected.Count == 0) return;
            var packet = new SyncResearchManagerPacket(Shapez2Multiplayer.Research, ++ResearchRevision);
            if (target == null) SendToAll(packet); else SendTo(packet, target);
        }

        public void BroadcastVortexState(IConnection? target = null)
        {
            if (target == null) SyncVortexTimer = 0.0f;
            if (Shapez2Multiplayer.Research == null || Connected.Count == 0) return;
            var packet = new SyncVortexStoragePacket(Shapez2Multiplayer.Research.ShapeStorage, ++VortexRevision);
            if (target == null) SendToAll(packet); else SendTo(packet, target);
        }

        public void BroadcastPinState(IConnection? target = null)
        {
            if (target == null) SyncPinsTimer = 0.0f;
            if (Shapez2Multiplayer.GameSessionOrchestrator == null || Connected.Count == 0) return;
            var packet = new SyncPinsPacket(++PinRevision);
            if (target == null) SendToAll(packet); else SendTo(packet, target);
        }

        public void BroadcastWaypointState(IConnection? target = null)
        {
            if (target == null) SyncWaypointsTimer = 0.0f;
            if (Shapez2Multiplayer.PlayerWaypoints == null || Connected.Count == 0) return;
            var packet = new SyncWaypointsPacket(++WaypointRevision);
            if (target == null) SendToAll(packet); else SendTo(packet, target);
        }

        public void BroadcastWorldDigest(IConnection? target = null)
        {
            if (target == null) SyncWorldDigestTimer = 0.0f;
            if (Shapez2Multiplayer.MapModel == null || Connected.Count == 0) return;
            var packet = new SyncWorldDigestPacket(++WorldDigestRevision);
            if (target == null) SendToAll(packet); else SendTo(packet, target);
        }

        public void SendAuthoritativeState(IConnection connection, SyncSubsystem subsystems)
        {
            if ((subsystems & SyncSubsystem.Research) != 0) BroadcastResearchState(connection);
            if ((subsystems & SyncSubsystem.Vortex) != 0) BroadcastVortexState(connection);
            if ((subsystems & SyncSubsystem.Pins) != 0) BroadcastPinState(connection);
            if ((subsystems & SyncSubsystem.Waypoints) != 0) BroadcastWaypointState(connection);
            if ((subsystems & SyncSubsystem.WorldDigest) != 0) BroadcastWorldDigest(connection);
        }

        public void SendAuthoritativeStateToAll(SyncSubsystem subsystems)
        {
            if ((subsystems & SyncSubsystem.Research) != 0) BroadcastResearchState();
            if ((subsystems & SyncSubsystem.Vortex) != 0) BroadcastVortexState();
            if ((subsystems & SyncSubsystem.Pins) != 0) BroadcastPinState();
            if ((subsystems & SyncSubsystem.Waypoints) != 0) BroadcastWaypointState();
            if ((subsystems & SyncSubsystem.WorldDigest) != 0) BroadcastWorldDigest();
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
            PingUpdateTimer += Time.unscaledDeltaTime;
            if (PingUpdateTimer >= PING_UPDATE_TIME)
            {
                PingUpdateTimer = 0.0f;
                SendToAll(new UpdateConnectionInfoPacket(Connected.Select(c => new InfoConnection(c)).ToList(), new List<uint>()));
            }
            SyncResearchTimer += Time.unscaledDeltaTime;
            if (SyncResearchTimer >= SYNC_RESEARCH_TIME)
            {
                BroadcastResearchState();
            }
            SyncVortexTimer += Time.unscaledDeltaTime;
            if (SyncVortexTimer >= SYNC_VORTEX_TIME)
            {
                BroadcastVortexState();
            }
            SyncPinsTimer += Time.unscaledDeltaTime;
            if (SyncPinsTimer >= SYNC_PINS_TIME)
            {
                BroadcastPinState();
            }
            SyncWaypointsTimer += Time.unscaledDeltaTime;
            if (SyncWaypointsTimer >= SYNC_WAYPOINTS_TIME)
            {
                BroadcastWaypointState();
            }
            SyncWorldDigestTimer += Time.unscaledDeltaTime;
            if (SyncWorldDigestTimer >= SYNC_WORLD_DIGEST_TIME)
            {
                BroadcastWorldDigest();
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
                try
                {
                    packet.Item1.Handle(packet.Item2);
                }
                catch (Exception ex)
                {
                    Shapez2Multiplayer.logger.Warning?.Log($"Failed to apply buffered {packet.Item1.GetType().Name} from {packet.Item2.Name}.");
                    Shapez2Multiplayer.logger.Warning?.LogException(ex);
                }
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
