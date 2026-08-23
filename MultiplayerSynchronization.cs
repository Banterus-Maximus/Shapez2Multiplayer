using Core.Localization;
using Game.Core.Research;
using Game.HUD.QuestArea.PinnedShapes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shapez2Multiplayer.Packets;
using UnityEngine;

namespace Shapez2Multiplayer
{
    /// <summary>
    /// Tracks client-side authoritative snapshot revisions and contains the pin
    /// reconciliation code. Pins are deliberately read through reflection: the
    /// game has changed the concrete IPlayerPins implementation between releases,
    /// while its public TryPin/TryUnpin API and IPin serialization stayed stable.
    /// </summary>
    public static class MultiplayerSynchronization
    {
        public static ulong LastResearchRevision { get; private set; }
        public static ulong LastPinRevision { get; private set; }
        public static ulong LastVortexRevision { get; private set; }
        public static ulong LastWaypointRevision { get; private set; }
        public static ulong LastWorldDigestRevision { get; private set; }
        public static bool ApplyingAuthoritativeResearchState { get; set; }
        private static readonly HashSet<string> PendingJobRequests = new HashSet<string>();
        private static readonly HashSet<int> PendingPlayerLevelRequests = new HashSet<int>();
        private static bool HasAuthoritativePlayerLevel;
        private static int AuthoritativePlayerLevel;
        private static float LastResearchSnapshotTime;
        private static float LastVortexSnapshotTime;
        private static float LastPinSnapshotTime;
        private static float LastWaypointSnapshotTime;
        private static float LastWorldDigestTime;
        private static float LastRepairRequestTime = float.NegativeInfinity;
        private static ulong NextActionCommandId = 1;
        private static readonly Dictionary<ulong, float> PendingActionCommands = new Dictionary<ulong, float>();
        private const float RepairRequestCooldown = 2.0f;
        private static int ConsecutiveWorldDigestMismatches;
        private static bool WorldMismatchNotificationShown;
        private static float NextWatchdogCheckTime;

        public static void ResetClientState()
        {
            LastResearchRevision = 0;
            LastPinRevision = 0;
            LastVortexRevision = 0;
            LastWaypointRevision = 0;
            LastWorldDigestRevision = 0;
            ApplyingAuthoritativeResearchState = false;
            PendingJobRequests.Clear();
            PendingPlayerLevelRequests.Clear();
            HasAuthoritativePlayerLevel = false;
            AuthoritativePlayerLevel = 0;
            var now = Time.realtimeSinceStartup;
            LastResearchSnapshotTime = now;
            LastVortexSnapshotTime = now;
            LastPinSnapshotTime = now;
            LastWaypointSnapshotTime = now;
            LastWorldDigestTime = now;
            LastRepairRequestTime = float.NegativeInfinity;
            NextActionCommandId = 1;
            PendingActionCommands.Clear();
            ConsecutiveWorldDigestMismatches = 0;
            WorldMismatchNotificationShown = false;
            NextWatchdogCheckTime = now;
            Shapez2Multiplayer.IgnorePinEvents = false;
        }

        public static bool ShouldApplyResearchRevision(ulong revision)
        {
            return revision > LastResearchRevision;
        }

        public static bool ShouldApplyVortexRevision(ulong revision)
        {
            return revision > LastVortexRevision;
        }

        public static void MarkVortexRevisionApplied(ulong revision)
        {
            LastVortexRevision = revision;
            LastVortexSnapshotTime = Time.realtimeSinceStartup;
        }

        public static void MarkResearchRevisionApplied(ulong revision)
        {
            LastResearchRevision = revision;
            LastResearchSnapshotTime = Time.realtimeSinceStartup;
            PendingJobRequests.Clear();
            PendingPlayerLevelRequests.Clear();
        }

        public static bool TryBeginJobRequest(string goalId, int expectedLevel)
        {
            return PendingJobRequests.Add(goalId + "\n" + expectedLevel);
        }

        public static bool TryBeginPlayerLevelRequest(int expectedLevel)
        {
            return PendingPlayerLevelRequests.Add(expectedLevel);
        }

        public static void SetAuthoritativePlayerLevel(int level)
        {
            AuthoritativePlayerLevel = level;
            HasAuthoritativePlayerLevel = true;
        }

        public static void MonitorClientPlayerLevel()
        {
            if (!MultiplayerCore.Client ||
                MultiplayerCore.connectionManager == null ||
                !MultiplayerCore.connectionManager.FinishedConnecting ||
                ApplyingAuthoritativeResearchState ||
                !HasAuthoritativePlayerLevel ||
                Shapez2Multiplayer.Research == null)
            {
                return;
            }

            var localLevel = Shapez2Multiplayer.Research.PlayerLevel.Level;
            if (localLevel <= AuthoritativePlayerLevel || !TryBeginPlayerLevelRequest(AuthoritativePlayerLevel))
            {
                return;
            }

            // Some game revisions perform the certification claim outside
            // GrantPlayerLevel. Detect that mutation as a fallback and ask the
            // host to perform the same one-level claim authoritatively.
            Shapez2Multiplayer.logger.Info?.Log($"Detected local operator-level claim {AuthoritativePlayerLevel} -> {localLevel}; requesting host authorization.");
            MultiplayerCore.connectionManager.Send(new Packets.GrantPlayerLevelPacket(AuthoritativePlayerLevel));
        }

        public static bool TryForcePlayerLevel(ResearchPlayerLevelManager manager, int targetLevel)
        {
            if (targetLevel < 0)
            {
                return false;
            }
            if (manager.Level == targetLevel)
            {
                return true;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var managerType = manager.GetType();
                var levelField = managerType.GetField("<Level>k__BackingField", flags) ??
                    managerType.GetField("_Level", flags) ??
                    managerType.GetFields(flags).FirstOrDefault(field =>
                        field.FieldType == typeof(int) &&
                        string.Equals(field.Name.Trim('_'), "Level", StringComparison.OrdinalIgnoreCase));
                if (levelField != null)
                {
                    levelField.SetValue(manager, targetLevel);
                }
                else
                {
                    var levelProperty = managerType.GetProperty(nameof(ResearchPlayerLevelManager.Level), flags);
                    var levelSetter = levelProperty?.GetSetMethod(true);
                    if (levelSetter == null)
                    {
                        return false;
                    }
                    levelSetter.Invoke(manager, new object[] { targetLevel });
                }

                if (manager.Level != targetLevel)
                {
                    return false;
                }

                var changedEvent = managerType.GetField("_OnLevelChanged", flags)?.GetValue(manager);
                changedEvent?.GetType().GetMethod("Invoke", flags, null, Type.EmptyTypes, null)?.Invoke(changedEvent, null);
                return true;
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Failed to force the authoritative operator level to {targetLevel}.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                return false;
            }
        }

        public static bool ShouldApplyPinRevision(ulong revision)
        {
            return revision > LastPinRevision;
        }

        public static void MarkPinRevisionApplied(ulong revision)
        {
            LastPinRevision = revision;
            LastPinSnapshotTime = Time.realtimeSinceStartup;
        }

        public static bool ShouldApplyWaypointRevision(ulong revision)
        {
            return revision > LastWaypointRevision;
        }

        public static void MarkWaypointRevisionApplied(ulong revision)
        {
            LastWaypointRevision = revision;
            LastWaypointSnapshotTime = Time.realtimeSinceStartup;
        }

        public static void MonitorClientSnapshots()
        {
            if (!MultiplayerCore.Client ||
                MultiplayerCore.connectionManager == null ||
                !MultiplayerCore.connectionManager.FinishedConnecting)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now < NextWatchdogCheckTime)
            {
                return;
            }
            NextWatchdogCheckTime = now + 1.0f;
            var stale = SyncSubsystem.None;
            if (now - LastResearchSnapshotTime > 8.0f) stale |= SyncSubsystem.Research;
            if (now - LastVortexSnapshotTime > 6.0f) stale |= SyncSubsystem.Vortex;
            if (now - LastPinSnapshotTime > 45.0f) stale |= SyncSubsystem.Pins;
            if (now - LastWaypointSnapshotTime > 45.0f) stale |= SyncSubsystem.Waypoints;
            if (now - LastWorldDigestTime > 45.0f) stale |= SyncSubsystem.WorldDigest;
            if (stale != SyncSubsystem.None)
            {
                RequestRepair(stale, "snapshot watchdog timeout");
            }

            if (PendingActionCommands.Any(command => now - command.Value > 10.0f))
            {
                PendingActionCommands.Clear();
                RequestRepair(SyncSubsystem.All, "player action acknowledgement timeout");
            }
        }

        public static ulong NextOutgoingActionId()
        {
            return NextActionCommandId++;
        }

        public static void TrackOutgoingAction(ulong commandId)
        {
            if (MultiplayerCore.Client && commandId != 0)
            {
                PendingActionCommands[commandId] = Time.realtimeSinceStartup;
            }
        }

        public static void CompleteOutgoingAction(ulong commandId, bool accepted)
        {
            PendingActionCommands.Remove(commandId);
            if (!accepted)
            {
                // The rejection path on the host sends repair snapshots before
                // this acknowledgement, so do not create a duplicate request.
                Shapez2Multiplayer.logger.Warning?.Log($"Host rejected player action {commandId}; applying the accompanying authoritative repair state.");
                Shapez2Multiplayer.HUD?.Events.ShowNotification.Invoke(new HUDNotificationData(
                    HUDNotificationType.Info,
                    new RawText("The host rejected a multiplayer action. State was refreshed; reconnect if the world now looks different.")));
            }
        }

        public static bool RequestRepair(SyncSubsystem subsystems, string reason, bool bypassCooldown = false)
        {
            var manager = MultiplayerCore.connectionManager;
            if (!MultiplayerCore.Client || manager == null || !manager.FinishedConnecting)
            {
                return false;
            }

            var now = Time.realtimeSinceStartup;
            if (!bypassCooldown && now - LastRepairRequestTime < RepairRequestCooldown)
            {
                return false;
            }

            LastRepairRequestTime = now;
            Shapez2Multiplayer.logger.Info?.Log($"Requesting {subsystems} authoritative repair. Reason: {reason}");
            return manager.Send(new RequestSyncPacket(subsystems, reason));
        }

        public static void ComputeWorldDigest(out int buildingCount, out ulong digest)
        {
            var map = Shapez2Multiplayer.MapModel;
            // BuildingCount is maintained by the game and is O(1). The previous
            // implementation walked and stringified every building on every peer
            // every three seconds, causing visible GC and frame-time spikes on
            // large factories. Reliable ordered actions provide the primary
            // topology guarantee; this heartbeat cheaply detects missing/additional
            // buildings without scanning the entire world.
            buildingCount = map?.BuildingCount ?? 0;
            digest = ((ulong)(uint)buildingCount * 0x9E3779B185EBCA87UL) ^ 0xD6E8FEB86659FD93UL;
        }

        public static void ObserveWorldDigest(ulong revision, int hostBuildingCount, ulong hostDigest)
        {
            if (revision <= LastWorldDigestRevision)
            {
                return;
            }
            LastWorldDigestRevision = revision;
            LastWorldDigestTime = Time.realtimeSinceStartup;
            ComputeWorldDigest(out var localBuildingCount, out var localDigest);
            if (localBuildingCount == hostBuildingCount && localDigest == hostDigest)
            {
                ConsecutiveWorldDigestMismatches = 0;
                WorldMismatchNotificationShown = false;
                return;
            }

            ConsecutiveWorldDigestMismatches++;
            Shapez2Multiplayer.logger.Warning?.Log($"World digest mismatch {ConsecutiveWorldDigestMismatches}/3: host {hostBuildingCount}/{hostDigest:X16}, client {localBuildingCount}/{localDigest:X16}.");
            if (ConsecutiveWorldDigestMismatches < 3 || WorldMismatchNotificationShown)
            {
                return;
            }

            WorldMismatchNotificationShown = true;
            Shapez2Multiplayer.HUD?.Events.ShowNotification.Invoke(new HUDNotificationData(
                HUDNotificationType.Info,
                new RawText("Multiplayer building count differs from the host. Reconnect to reload the host's world safely.")));
        }

        public static bool TryApplyWaypoints(IReadOnlyList<PlayerWaypoint> targetWaypoints)
        {
            var manager = Shapez2Multiplayer.PlayerWaypoints;
            if (manager == null)
            {
                return false;
            }

            var previousIgnoreWaypointEvents = Shapez2Multiplayer.IgnoreWaypointEvents;
            Shapez2Multiplayer.IgnoreWaypointEvents = true;
            try
            {
                var targetsById = targetWaypoints.ToDictionary(waypoint => waypoint.UID);
                var current = manager.Waypoints.Cast<IPlayerWaypoint>().ToList();
                foreach (var waypoint in current)
                {
                    if (!targetsById.ContainsKey(waypoint.UID))
                    {
                        manager.DeleteWaypoint(waypoint);
                    }
                }

                foreach (var target in targetWaypoints)
                {
                    var existing = manager.Waypoints.Cast<IPlayerWaypoint>()
                        .FirstOrDefault(waypoint => waypoint.UID == target.UID);
                    if (existing == null)
                    {
                        manager.Add(target);
                    }
                    else if (!WaypointEquals(existing, target))
                    {
                        manager.ChangeWaypoint(existing, target.Name, target.ShapeIconKey, target);
                    }
                }

                var result = manager.Waypoints.Cast<IPlayerWaypoint>()
                    .OrderBy(waypoint => waypoint.UID)
                    .ToList();
                var expected = targetWaypoints.OrderBy(waypoint => waypoint.UID).ToList();
                return result.Count == expected.Count && result.Zip(expected, WaypointEquals).All(equal => equal);
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Failed to apply the authoritative waypoint snapshot.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                return false;
            }
            finally
            {
                Shapez2Multiplayer.IgnoreWaypointEvents = previousIgnoreWaypointEvents;
            }
        }

        private static bool WaypointEquals(IPlayerWaypoint left, IPlayerWaypoint right)
        {
            var leftWaypoint = (PlayerWaypoint)left;
            var rightWaypoint = (PlayerWaypoint)right;
            return leftWaypoint.UID == rightWaypoint.UID &&
                leftWaypoint.Name == rightWaypoint.Name &&
                leftWaypoint.ShapeIconKey == rightWaypoint.ShapeIconKey &&
                leftWaypoint.PositionX == rightWaypoint.PositionX &&
                leftWaypoint.PositionY == rightWaypoint.PositionY &&
                leftWaypoint.Zoom == rightWaypoint.Zoom &&
                leftWaypoint.Angle == rightWaypoint.Angle &&
                leftWaypoint.BuildingLayer == rightWaypoint.BuildingLayer &&
                leftWaypoint.IslandLayer == rightWaypoint.IslandLayer &&
                leftWaypoint.RotationDegrees == rightWaypoint.RotationDegrees;
        }

        public static bool TrySerializePins(out List<string> serializedPins)
        {
            serializedPins = new List<string>();
            if (!TryGetCurrentPins(out var pins))
            {
                Shapez2Multiplayer.logger.Warning?.Log("Could not locate the game's current pin collection; skipping pin snapshot.");
                return false;
            }

            try
            {
                serializedPins.AddRange(pins.Select(pin => pin.Serialize()));
                return true;
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Failed to serialize the current pinned-shape state.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                serializedPins.Clear();
                return false;
            }
        }

        public static bool TryApplyPins(IReadOnlyList<string> serializedPins)
        {
            if (!TryGetCurrentPins(out var currentPins))
            {
                Shapez2Multiplayer.logger.Warning?.Log("Could not locate the game's current pin collection; authoritative pin snapshot was not applied.");
                return false;
            }

            List<string> currentSerializedPins;
            try
            {
                currentSerializedPins = currentPins.Select(pin => pin.Serialize()).ToList();
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Failed to read the client's current pinned-shape state.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                return false;
            }
            if (currentSerializedPins.SequenceEqual(serializedPins))
            {
                return true;
            }

            var targetPins = new List<IPin>(serializedPins.Count);
            try
            {
                foreach (var serializedPin in serializedPins)
                {
                    targetPins.Add(PinFactory.Deserialize(serializedPin));
                }
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Rejected an invalid authoritative pin snapshot.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                return false;
            }

            var pinsManager = Shapez2Multiplayer.GameSessionOrchestrator?.LocalPlayer.HUDData.Pins;
            if (pinsManager == null)
            {
                return false;
            }

            var previousIgnorePinEvents = Shapez2Multiplayer.IgnorePinEvents;
            Shapez2Multiplayer.IgnorePinEvents = true;
            try
            {
                // Rebuild when either contents or ordering differs. This keeps the
                // left-hand pinned UI in the exact host order and also repairs pins
                // whose serialized job level changed in place.
                foreach (var pin in currentPins)
                {
                    if (!pinsManager.TryUnpin(pin))
                    {
                        Shapez2Multiplayer.logger.Warning?.Log("Failed to remove a stale pin while applying the host snapshot.");
                    }
                }

                foreach (var pin in targetPins)
                {
                    if (!pinsManager.TryPin(pin))
                    {
                        Shapez2Multiplayer.logger.Warning?.Log("Failed to add a pin while applying the host snapshot.");
                    }
                }

                if (!TryGetCurrentPins(out var appliedPins))
                {
                    return false;
                }
                return appliedPins.Select(pin => pin.Serialize()).SequenceEqual(serializedPins);
            }
            finally
            {
                Shapez2Multiplayer.IgnorePinEvents = previousIgnorePinEvents;
            }
        }

        private static bool TryGetCurrentPins(out List<IPin> pins)
        {
            pins = new List<IPin>();
            var pinsManager = Shapez2Multiplayer.GameSessionOrchestrator?.LocalPlayer.HUDData.Pins;
            if (pinsManager == null)
            {
                return false;
            }

            if (TryExtractPins(pinsManager, out pins))
            {
                return true;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = pinsManager.GetType();
            while (type != null)
            {
                foreach (var property in type.GetProperties(flags)
                    .Where(property => property.GetIndexParameters().Length == 0 &&
                        property.Name.IndexOf("pin", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    try
                    {
                        if (TryExtractPins(property.GetValue(pinsManager), out pins))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Some computed game properties are not safe before HUD initialization.
                    }
                }

                foreach (var field in type.GetFields(flags)
                    .Where(field => field.Name.IndexOf("pin", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (TryExtractPins(field.GetValue(pinsManager), out pins))
                    {
                        return true;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryExtractPins(object? value, out List<IPin> pins)
        {
            pins = new List<IPin>();
            if (value is null || value is string)
            {
                return false;
            }

            if (value is IEnumerable<IPin> typedPins)
            {
                pins.AddRange(typedPins);
                return true;
            }

            if (value is IDictionary dictionary)
            {
                foreach (var item in dictionary.Values)
                {
                    var pin = item as IPin;
                    if (pin is null)
                    {
                        pins.Clear();
                        return false;
                    }
                    pins.Add(pin);
                }
                return true;
            }

            var enumerable = value as IEnumerable;
            if (enumerable is null)
            {
                return false;
            }

            foreach (var item in enumerable)
            {
                var pin = item as IPin;
                if (pin is null)
                {
                    pins.Clear();
                    return false;
                }
                pins.Add(pin);
            }
            return true;
        }
    }
}
