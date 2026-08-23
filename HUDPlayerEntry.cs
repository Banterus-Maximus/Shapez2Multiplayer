using Core.Dependency;
using Core.Localization;
using TMPro;
using Unity.Mathematics;
using UnityEngine.Events;

namespace Shapez2Multiplayer
{
    public class HUDPlayerEntry : HUDComponent
    {
        public IConnection Connection { get; set; }
        public TextMeshProUGUI NameText { get; set; }
        public TextMeshProUGUI PingText { get; set; }
        public HUDButton JumpButton { get; set; }
        public HUDButton KickButton { get; set; }
        public bool RepresentsHost { get; set; }
        [Construct]
        private void Construct()
        {
            KickButton.Interactable = MultiplayerCore.Hosting && !RepresentsHost && !(Connection is InfoConnection);
            KickButton.Text = "multiplayer.kick".T();
            KickButton.OnClick.AddListener(new UnityAction(() =>
            {
                MultiplayerCore.socketManager?.Disconnect(Connection, MultiplayerCore.DisconnectReason.Kicked);
                Connection.Close();
            }));
            JumpButton.Text = "multiplayer.jump".T();
            JumpButton.Interactable = false;
            JumpButton.OnClick.AddListener(new UnityAction(JumpToPlayer));
        }
        private void OnEnable()
        {
            InvokeRepeating(nameof(EntryUpdate), 0f, 1f);
        }
        private void OnDisable()
        {
            CancelInvoke();
        }
        public void EntryUpdate()
        {
            NameText.text = RepresentsHost ? $"HOST - {Connection.Name}" : Connection.Name;
            PingText.text = Connection.Ping.ToString();
            JumpButton.Interactable = HUDMultiplayerCursors.Instance != null &&
                HUDMultiplayerCursors.Instance.TryGetPlayerCursor(Connection, RepresentsHost, out var cursor) &&
                cursor.HasWorldPosition;
        }
        private void JumpToPlayer()
        {
            if (HUDMultiplayerCursors.Instance == null ||
                !HUDMultiplayerCursors.Instance.TryGetPlayerCursor(Connection, RepresentsHost, out var cursor) ||
                !cursor.HasWorldPosition)
            {
                return;
            }

            var position = cursor.LatestWorldPosition;
            MultiplayerDontDestroyObject.RequestViewportJump(new double2(position.x, position.z));
        }

        public override void OnDispose()
        {
            
        }
    }
}
