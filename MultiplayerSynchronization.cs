using Game.Core.Research;
using Game.HUD.QuestArea.PinnedShapes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
        public static bool ApplyingAuthoritativeResearchState { get; set; }
        private static readonly HashSet<string> PendingJobRequests = new HashSet<string>();
        private static readonly HashSet<int> PendingPlayerLevelRequests = new HashSet<int>();
        private static bool HasAuthoritativePlayerLevel;
        private static int AuthoritativePlayerLevel;

        public static void ResetClientState()
        {
            LastResearchRevision = 0;
            LastPinRevision = 0;
            LastVortexRevision = 0;
            ApplyingAuthoritativeResearchState = false;
            PendingJobRequests.Clear();
            PendingPlayerLevelRequests.Clear();
            HasAuthoritativePlayerLevel = false;
            AuthoritativePlayerLevel = 0;
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
        }

        public static void MarkResearchRevisionApplied(ulong revision)
        {
            LastResearchRevision = revision;
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

                return true;
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
