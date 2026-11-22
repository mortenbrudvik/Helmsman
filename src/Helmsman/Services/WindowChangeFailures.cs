using Helmsman.Models;
using Helmsman.Native;

namespace Helmsman.Services;

/// <summary>
/// The failures the window services share: a handle that is no window, a window that closed during the call, and a
/// SetWindowPos or z-order change that Windows refused.
/// </summary>
internal static class WindowChangeFailures
{
    /// <summary>The error of a window handle that is not a window.</summary>
    public const string InvalidHandleMessage = "Invalid window handle";

    public static WindowChangeResult WindowGone(nint handle) =>
        WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
            "The window no longer exists: it closed during the call. Call find_windows for a current handle.");

    public static WindowChangeResult NotPlaced(nint handle, int win32Error, WindowErrorCode failure) => win32Error switch
    {
        NativeEnums.ERROR_ACCESS_DENIED => WindowChangeResult.Failed(handle, WindowErrorCode.AccessDenied,
            $"Windows blocked the change (Win32 error {win32Error}, access denied). The window most likely belongs to an app " +
            "running with higher privileges, for example as administrator: User Interface Privilege Isolation blocks this " +
            "server from moving or resizing it. Nothing was changed; ask the user to do this step."),
        NativeEnums.ERROR_INVALID_WINDOW_HANDLE => WindowGone(handle),
        // SetWindowPos can fail without setting the last error, and error 0's text is "The operation completed successfully"
        0 => WindowChangeResult.Failed(handle, failure,
            $"Windows refused to {(failure == WindowErrorCode.ResizeFailed ? "resize" : "move")} the window without giving a reason " +
            "(the application may have rejected the change). Nothing was changed."),
        _ => WindowChangeResult.Failed(handle, failure,
            $"Windows did not {(failure == WindowErrorCode.ResizeFailed ? "resize" : "move")} the window (Win32 error {win32Error}: " +
            $"{new System.ComponentModel.Win32Exception(win32Error).Message}).")
    };
}
