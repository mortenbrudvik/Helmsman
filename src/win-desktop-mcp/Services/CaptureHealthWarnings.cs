using System.Globalization;
using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// The capture health warnings a successful capture carries about how far its image can be trusted (roadmap M2.9, issue #17):
/// a DPI-scaled window, a window that declares a display affinity, a locked session or an unreachable input desktop, and an
/// image of one solid color, whose check also decides the fallback to Windows.Graphics.Capture.
/// </summary>
internal static class CaptureHealthWarnings
{
    /// <summary>
    /// How many pixels the blank check samples along each axis (32 x 32 = 1024 pixels). A sampled pixel counts as the
    /// same color when no channel differs by more than <see cref="BlankTolerance"/>.
    /// </summary>
    private const int BlankSamplesPerAxis = 32;

    private const int BlankTolerance = 2;

    /// <summary>
    /// Warns when the window is not per-monitor DPI aware on its monitor: Windows lays it out at another DPI and
    /// stretches it, and PrintWindow can then draw it at its unscaled size, leaving part of the image empty or the
    /// content offset from the screen.
    /// </summary>
    internal static void AddDpiWarning(INativeWindowWrapper windows, nint windowHandle, MonitorInfo? monitor, List<string> warnings)
    {
        // A monitor whose DPI Windows could not report has no DPI to compare with: its 96 only stands in for the arithmetic
        if (monitor is not { DpiUnknown: false })
        {
            return;
        }

        var windowDpi = (int)windows.GetDpiForWindow(windowHandle);
        var monitorDpi = monitor.Dpi;
        if (windowDpi == 0 || Math.Abs(windowDpi - monitorDpi) <= 1)
        {
            return;
        }

        warnings.Add(CaptureWarnings.Format(CaptureWarnings.DpiScaledWindow,
            $"the window works at {windowDpi} DPI on a {monitorDpi} DPI monitor, so Windows scales it. The image can show " +
            "it at its unscaled size, partly empty or offset from the screen; if it looks wrong, capture_region over " +
            "capturedRegion copies the screen as shown."));
    }

    /// <summary>
    /// Warns when the captured window, or the top-level window it is in, declares a display affinity (issue #17): a copy of
    /// the screen shows what is behind it or black, and what PrintWindow and Windows.Graphics.Capture return for it is not
    /// known, so every way of reading it warns.
    /// </summary>
    internal static void AddDisplayAffinityWarning(INativeWindowWrapper windows, nint rootWindow, List<string> warnings)
    {
        if (DisplayAffinities.Of(windows.GetWindowDisplayAffinity(rootWindow)) is { } affinity)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.DisplayAffinity,
                $"this window declares a display affinity ({DisplayAffinities.Name(affinity)}) to keep itself out of captures, so " +
                "the image may be black, empty, or show what is behind it. Read it with the UI Automation tools instead."));
        }
    }

    /// <summary>
    /// Warns, first, when the session was locked as the pixels were read, or the input desktop could not be reached (issues
    /// #17 and #18): the image may then show the lock screen. Captures are not refused for it, as input is.
    /// </summary>
    internal static void AddSessionWarning(IProcessSecurityProbe security, List<string> warnings)
    {
        switch (CaptureSession.WarningCode(security))
        {
            case CaptureWarnings.SessionLocked:
                warnings.Insert(0, CaptureWarnings.Format(CaptureWarnings.SessionLocked,
                    "the session is locked, so the image may show the lock screen instead of the windows. Captures are not refused " +
                    "while it is locked; capture again once the user has unlocked it."));
                break;
            case CaptureWarnings.DesktopUnavailable:
                warnings.Insert(0, CaptureWarnings.Format(CaptureWarnings.DesktopUnavailable,
                    "the input desktop cannot be reached (a UAC prompt, Ctrl+Alt+Del or a disconnected session), so the image may " +
                    "be black or show the lock screen instead of the windows. Capture again once the user is back."));
                break;
        }
    }

    /// <summary>
    /// Warns when the image is one solid color: about 1024 pixels on a grid all have the same color. That is right for
    /// an empty area, but a black image usually means GPU or protected content this capture cannot read, or a window
    /// that has not painted yet.
    /// </summary>
    internal static void AddBlankWarning(CaptureResult capture, List<string> warnings)
    {
        if (BlankColor(capture) is { } color)
        {
            warnings.Add(CaptureWarnings.Format(CaptureWarnings.ImageBlank,
                $"every sampled pixel is #{color}: GPU or protected content, or not painted yet?"));
        }
    }

    /// <summary>The color as RRGGBB when every sampled pixel has it (within a small tolerance), otherwise null.</summary>
    internal static string? BlankColor(CaptureResult capture)
    {
        if (capture.Width <= 0 || capture.Height <= 0 || capture.Bgra.LongLength < (long)capture.Width * capture.Height * 4)
        {
            return null;
        }

        var pixels = capture.Bgra;
        int b0 = pixels[0], g0 = pixels[1], r0 = pixels[2];
        var columns = Math.Min(BlankSamplesPerAxis, capture.Width);
        var rows = Math.Min(BlankSamplesPerAxis, capture.Height);
        for (var row = 0; row < rows; row++)
        {
            var y = rows == 1 ? 0 : (int)((long)row * (capture.Height - 1) / (rows - 1));
            for (var column = 0; column < columns; column++)
            {
                var x = columns == 1 ? 0 : (int)((long)column * (capture.Width - 1) / (columns - 1));
                var i = ((long)y * capture.Width + x) * 4;
                if (Math.Abs(pixels[i] - b0) > BlankTolerance ||
                    Math.Abs(pixels[i + 1] - g0) > BlankTolerance ||
                    Math.Abs(pixels[i + 2] - r0) > BlankTolerance)
                {
                    return null;
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{r0:X2}{g0:X2}{b0:X2}");
    }
}
