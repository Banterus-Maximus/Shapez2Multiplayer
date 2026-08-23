using Game.HUD.QuestArea.PinnedShapes;
using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class PinChangePacket : IPacket
    {
        public IPin Pin;
        public bool Remove;
        public PinChangePacket() { }
        public PinChangePacket(IPin pin, bool remove)
        {
            Pin = pin;
            Remove = remove;
        }

        public void Decode(Stream stream)
        {
            using BinaryReader reader = new BinaryReader(stream);
            Remove = reader.ReadBoolean();
            Pin = PinFactory.Deserialize(reader.ReadString());
        }

        public bool Encode(Stream stream)
        {
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(Remove);
            writer.Write(Pin.Serialize());
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null)
            {
                Shapez2Multiplayer.logger.Warning?.Log("PinChangePacket is a client request and should only be received by the host.");
                return;
            }

            var previousIgnorePinEvents = Shapez2Multiplayer.IgnorePinEvents;
            Shapez2Multiplayer.IgnorePinEvents = true;
            try
            {
                if (Remove)
                {
                    if (!Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.HUDData.Pins.TryUnpin(Pin)) Shapez2Multiplayer.logger.Warning?.Log("Client requested removal of a pin which was not present on the host.");
                }
                else
                {
                    if (!Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.HUDData.Pins.TryPin(Pin)) Shapez2Multiplayer.logger.Warning?.Log("Client requested a pin which the host could not add.");
                }
            }
            finally
            {
                Shapez2Multiplayer.IgnorePinEvents = previousIgnorePinEvents;
            }

            // Always acknowledge with the complete host state. This also rolls a
            // client's optimistic local UI change back if the request was invalid.
            MultiplayerCore.socketManager.BroadcastPinState();
        }
    }
}
