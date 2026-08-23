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
        public static bool ApplyingAuthoritativeResearchState { get; set; }
        private static readonly HashSet<string> PendingJobRequests = new HashSet<string>();

        public static void ResetClientState()
        {
            LastResearchRevision = 0;
            LastPinRevision = 0;
            ApplyingAuthoritativeResearchState = false;
            PendingJobRequests.Clear();
            Shapez2Multiplayer.IgnorePinEvents = false;
        }

        public static bool ShouldApplyResearchRevision(ulong revision)
        {
            return revision > LastResearchRevision;
        }

        public static void MarkResearchRevisionApplied(ulong revision)
        {
            LastResearchRevision = revision;
            PendingJobRequests.Clear();
        }

        public static bool TryBeginJobRequest(string goalId, int expectedLevel)
        {
            return PendingJobRequests.Add(goalId + "\n" + expectedLevel);
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
                    if (item is not IPin pin)
                    {
                        pins.Clear();
                        return false;
                    }
                    pins.Add(pin);
                }
                return true;
            }

            if (value is not IEnumerable enumerable)
            {
                return false;
            }

            foreach (var item in enumerable)
            {
                if (item is not IPin pin)
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
