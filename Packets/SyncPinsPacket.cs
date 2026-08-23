using System.Collections.Generic;
using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class SyncPinsPacket : IPacket
    {
        private const int MaxPins = 256;

        public ulong Revision;
        public List<string> SerializedPins = new List<string>();
        public bool HasSnapshot;

        public SyncPinsPacket() { }

        public SyncPinsPacket(ulong revision)
        {
            Revision = revision;
            HasSnapshot = MultiplayerSynchronization.TrySerializePins(out var serializedPins);
            SerializedPins = serializedPins;
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            Revision = reader.ReadUInt64();
            HasSnapshot = reader.ReadBoolean();
            if (!HasSnapshot)
            {
                return;
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > MaxPins)
            {
                HasSnapshot = false;
                Shapez2Multiplayer.logger.Warning?.Log($"Rejected pin snapshot containing {count} entries.");
                return;
            }

            for (var index = 0; index < count; index++)
            {
                SerializedPins.Add(reader.ReadString());
            }
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(Revision);
            writer.Write(HasSnapshot);
            if (!HasSnapshot)
            {
                return false;
            }

            writer.Write(SerializedPins.Count);
            foreach (var serializedPin in SerializedPins)
            {
                writer.Write(serializedPin);
            }
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("A client tried to send an authoritative pin snapshot.");
                return;
            }
            if (!HasSnapshot || !MultiplayerSynchronization.ShouldApplyPinRevision(Revision))
            {
                return;
            }

            if (MultiplayerSynchronization.TryApplyPins(SerializedPins))
            {
                MultiplayerSynchronization.MarkPinRevisionApplied(Revision);
            }
        }
    }
}
