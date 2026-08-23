using Core.Dependency;
using Core.Localization;
using TMPro;
using Unity.Mathematics;
using UnityEngine.Events;

namespace Shapez2Multiplayer
{
    public class HUDPlayerJumpEntry : HUDComponent
    {
        public IConnection Connection { get; set; }
        public bool RepresentsHost { get; set; }
        public TextMeshProUGUI NameText { get; set; }
        public HUDButton JumpButton { get; set; }

        [Construct]
        private void Construct()
        {
            JumpButton.Text = "multiplayer.jump".T();
            JumpButton.Interactable = false;
            JumpButton.OnClick.AddListener(new UnityAction(JumpToPlayer));
            EntryUpdate();
        }

        public void EntryUpdate()
        {
            NameText.text = RepresentsHost ? $"HOST - {Connection.Name}" : Connection.Name;
            JumpButton.Interactable = HUDMultiplayerCursors.Instance != null &&
                HUDMultiplayerCursors.Instance.TryGetPlayerCursor(Connection, RepresentsHost, out var cursor) &&
                cursor.HasWorldPosition;
        }

        private void JumpToPlayer()
        {
            if (Shapez2Multiplayer.GameSessionOrchestrator == null ||
                HUDMultiplayerCursors.Instance == null ||
                !HUDMultiplayerCursors.Instance.TryGetPlayerCursor(Connection, RepresentsHost, out var cursor) ||
                !cursor.HasWorldPosition)
            {
                return;
            }

            var position = cursor.LatestWorldPosition;
            Shapez2Multiplayer.GameSessionOrchestrator.Viewport.Position = new double2(position.x, position.z);
        }

        public override void OnDispose()
        {
        }
    }
}
