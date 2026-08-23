using Game.Core.Research;
using System.IO;
using System.Linq;

namespace Shapez2Multiplayer.Packets
{
    /// <summary>
    /// An authoritative host snapshot of vortex shape totals. This is deliberately
    /// independent from the broader research packet so an unlock, currency, job,
    /// or presentation failure cannot prevent delivered shapes from converging.
    /// </summary>
    public class SyncVortexStoragePacket : IPacket
    {
        private const int MaxShapeEntries = 100000;

        public ulong Revision;
        public ResearchShapeStorage.SerializedData Shapes = new ResearchShapeStorage.SerializedData();
        public bool HasSnapshot = true;

        public SyncVortexStoragePacket() { }

        public SyncVortexStoragePacket(ResearchShapeStorage shapeStorage, ulong revision)
        {
            Revision = revision;
            Shapes = shapeStorage.Serialize();
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            Revision = reader.ReadUInt64();
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxShapeEntries)
            {
                HasSnapshot = false;
                Shapez2Multiplayer.logger.Warning?.Log($"Rejected vortex snapshot containing {count} shape entries.");
                return;
            }

            for (var index = 0; index < count; index++)
            {
                var shapeKey = reader.ReadString();
                // StoredShapes changed from int to long in the current game.
                // Encode already writes the runtime long value; reading only four
                // bytes here misaligns every following key and rejects the packet.
                var amount = reader.ReadInt64();
                Shapes.StoredShapes[shapeKey] = amount;
            }
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(Revision);
            writer.Write(Shapes.StoredShapes.Count);
            foreach (var pair in Shapes.StoredShapes)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("A client tried to send an authoritative vortex snapshot.");
                return;
            }
            if (!HasSnapshot || !MultiplayerSynchronization.ShouldApplyVortexRevision(Revision))
            {
                return;
            }

            var research = Shapez2Multiplayer.Research;
            if (research == null)
            {
                return;
            }

            ResearchShapeStorage.SerializedData localShapes;
            try
            {
                localShapes = research.ShapeStorage.Serialize();
            }
            catch (System.Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Failed to read local vortex totals for snapshot revision {Revision}.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
                return;
            }

            var allShapeKeys = localShapes.StoredShapes.Keys
                .Concat(Shapes.StoredShapes.Keys)
                .Distinct()
                .ToList();
            var previousApplyState = MultiplayerSynchronization.ApplyingAuthoritativeResearchState;
            var converged = true;
            MultiplayerSynchronization.ApplyingAuthoritativeResearchState = true;
            try
            {
                foreach (var shapeKey in allShapeKeys)
                {
                    try
                    {
                        Shapes.StoredShapes.TryGetValue(shapeKey, out var serializedTarget);
                        if (serializedTarget < 0)
                        {
                            Shapez2Multiplayer.logger.Warning?.Log($"Ignored invalid negative vortex total for shape {shapeKey}.");
                            converged = false;
                            continue;
                        }

                        var shapeId = research.ShapeStorage.ShapeIdManager.Resolve(shapeKey);
                        var current = research.ShapeStorage.GetAmount(shapeId);
                        var target = (ulong)serializedTarget;
                        if (current < target)
                        {
                            research.ShapeStorage.Add(shapeId, target - current);
                        }
                        else if (current > target && !research.ShapeStorage.TryTake(shapeId, current - target))
                        {
                            Shapez2Multiplayer.logger.Warning?.Log($"Failed to reduce vortex total for shape {shapeKey} from {current} to {target}.");
                            converged = false;
                            continue;
                        }
                        var reconciled = research.ShapeStorage.GetAmount(shapeId);
                        if (reconciled != target)
                        {
                            Shapez2Multiplayer.logger.Warning?.Log($"Vortex total for shape {shapeKey} remained {reconciled} after applying host total {target}.");
                            converged = false;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // A newly introduced or malformed shape must not prevent
                        // all other vortex totals in this snapshot from applying.
                        Shapez2Multiplayer.logger.Warning?.Log($"Failed to reconcile vortex shape {shapeKey} in revision {Revision}; continuing with the remaining shapes.");
                        Shapez2Multiplayer.logger.Warning?.LogException(ex);
                        converged = false;
                    }
                }

                // Goal rows cache their displayed progress and eligibility. A
                // train batch can leave the client's numeric storage equal to the
                // host before this packet arrives, so refresh even when no local
                // Add/TryTake was necessary during reconciliation.
                research.PlayerLevelGoals._OnChanged.Invoke();

                if (converged)
                {
                    MultiplayerSynchronization.MarkVortexRevisionApplied(Revision);
                }
                else
                {
                    MultiplayerSynchronization.RequestRepair(SyncSubsystem.Vortex, $"vortex snapshot {Revision} did not converge");
                }
            }
            finally
            {
                MultiplayerSynchronization.ApplyingAuthoritativeResearchState = previousApplyState;
            }
        }
    }
}
