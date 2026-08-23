using System.IO;

namespace Shapez2Multiplayer.Packets
{
    /// <summary>
    /// Cheap topology fingerprint used to detect world divergence which cannot be
    /// repaired safely without reloading a savegame.
    /// </summary>
    public class SyncWorldDigestPacket : IPacket
    {
        public ulong Revision;
        public int BuildingCount;
        public ulong Digest;

        public SyncWorldDigestPacket() { }

        public SyncWorldDigestPacket(ulong revision)
        {
            Revision = revision;
            MultiplayerSynchronization.ComputeWorldDigest(out BuildingCount, out Digest);
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(Revision);
            writer.Write(BuildingCount);
            writer.Write(Digest);
            return true;
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            Revision = reader.ReadUInt64();
            BuildingCount = reader.ReadInt32();
            Digest = reader.ReadUInt64();
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("A client tried to send an authoritative world digest.");
                return;
            }
            MultiplayerSynchronization.ObserveWorldDigest(Revision, BuildingCount, Digest);
        }
    }
}
