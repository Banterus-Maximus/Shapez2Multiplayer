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
            // HUDPauseMenu.Hide and the camera controller both write to the
            // viewport during the close transition. Keep the requested position
            // authoritative until that transition and its easing have finished.
            FramesAfterMenuClosed = 120;
            MaximumPendingFrames = 600;
        }

        public static bool TryGetPendingViewportJump(out double2 position)
        {
            if (PendingViewportJump.HasValue)
            {
                position = PendingViewportJump.Value;
                return true;
            }

            position = default;
            return false;
        }

        public void Update()
        {
            MultiplayerCore.Update();
            Packets.UpdateBuildingConfigurationPacket.ProcessPendingConfigurations();
        }

        public void LateUpdate()
        {
            if (!PendingViewportJump.HasValue || Shapez2Multiplayer.GameSessionOrchestrator == null)
            {
                return;
            }

            // This also covers frames where no competing Position setter ran and
            // makes the resulting camera transform update immediately.
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
