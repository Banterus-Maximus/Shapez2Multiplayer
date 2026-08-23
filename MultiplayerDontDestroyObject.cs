using Unity.Mathematics;
using UnityEngine;

namespace Shapez2Multiplayer
{
    public class MultiplayerDontDestroyObject : MonoBehaviour
    {
        private static double2? PendingViewportJump;
        private static int FramesAfterMenuClosed;
        private static int MaximumPendingFrames;

        public static void RequestViewportJump(double2 position)
        {
            PendingViewportJump = position;
            FramesAfterMenuClosed = 8;
            MaximumPendingFrames = 600;
        }

        public void Update()
        {
            MultiplayerCore.Update();
        }

        public void LateUpdate()
        {
            if (!PendingViewportJump.HasValue || Shapez2Multiplayer.GameSessionOrchestrator == null)
            {
                return;
            }

            // The pause menu and camera input controller can both restore the
            // position they cached before the click. Reapply after all ordinary
            // camera updates and for a few frames after the menu has closed.
            Shapez2Multiplayer.GameSessionOrchestrator.Viewport.Position = PendingViewportJump.Value;
            MaximumPendingFrames--;
            if (MaximumPendingFrames <= 0)
            {
                PendingViewportJump = null;
                return;
            }
            if (HUDMultiplayerPausePanel.instance != null && HUDMultiplayerPausePanel.instance.isActiveAndEnabled)
            {
                return;
            }

            FramesAfterMenuClosed--;
            if (FramesAfterMenuClosed <= 0)
            {
                PendingViewportJump = null;
            }
        }
    }
}
