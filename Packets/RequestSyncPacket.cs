using System;
using System.IO;

namespace Shapez2Multiplayer.Packets
{
    [Flags]
    public enum SyncSubsystem : byte
    {
        None = 0,
        Research = 1 << 0,
        Vortex = 1 << 1,
        Pins = 1 << 2,
        Waypoints = 1 << 3,
        WorldDigest = 1 << 4,
        All = Research | Vortex | Pins | Waypoints | WorldDigest
    }

    /// <summary>
    /// A client request for fresh authoritative state. Requests are deliberately
    /// targetable so one stale UI subsystem does not require reloading the world.
    /// </summary>
    public class RequestSyncPacket : IPacket
    {
        public SyncSubsystem Subsystems;
        public string Reason = string.Empty;

        public RequestSyncPacket() { }

        public RequestSyncPacket(SyncSubsystem subsystems, string reason)
        {
            Subsystems = subsystems & SyncSubsystem.All;
            Reason = reason ?? string.Empty;
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Subsystems);
            writer.Write(Reason.Length > 256 ? Reason.Substring(0, 256) : Reason);
            return Subsystems != SyncSubsystem.None;
        }

        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            Subsystems = (SyncSubsystem)(reader.ReadByte() & (byte)SyncSubsystem.All);
            Reason = reader.ReadString();
            if (Reason.Length > 256)
            {
                Reason = Reason.Substring(0, 256);
            }
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null || Subsystems == SyncSubsystem.None)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Ignored an invalid authoritative sync request.");
                return;
            }

            MultiplayerCore.socketManager?.HandleSyncRequest(connection, Subsystems, Reason);
        }
    }
}
