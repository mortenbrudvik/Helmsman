using System.Runtime.InteropServices;

namespace Helmsman.Native;

internal static partial class NativeMethods
{
    // Whether a full-screen 3D app is in front, for the capture warning ExclusiveFullscreen (ScreenCaptureWrapper). It only reads.
    internal static partial class Shell32
    {
        /// <summary>
        /// The shell's notification state (a QUERY_USER_NOTIFICATION_STATE value, such as 3, QUNS_RUNNING_D3D_FULL_SCREEN);
        /// an HRESULT, negative on failure.
        /// </summary>
        [LibraryImport("shell32.dll")]
        public static partial int SHQueryUserNotificationState(out int pquns);
    }
}
