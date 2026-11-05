//====================[ Imports ]====================
using UnityEngine;
using EFT.Communications;

namespace KMABodyDrag.Presentation
{
    //====================[ VFX_UI ]====================
    // Notification toasts. The look hint lives in DragHud.
    internal static class VFX_UI
    {
        public static void Text(Color color, string message) =>
            NotificationManager.DisplayMessageNotification(
                message, ENotificationDurationType.Long, ENotificationIconType.Default, color);
    }
}
