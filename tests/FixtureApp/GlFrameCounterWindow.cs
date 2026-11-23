using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Helmsman.FixtureApp.FixtureContract;
using Box = Helmsman.FixtureApp.GlFrameCounterLayout.Box;
using Rgb = Helmsman.FixtureApp.GlFrameCounterLayout.Rgb;

namespace Helmsman.FixtureApp;

/// <summary>
/// The GL frame counter window (<c>--open-gl</c>): a raw Win32 popup window on a thread of its own, with its own message loop
/// and an OpenGL context on a double-buffered RGBA pixel format, that draws a new frame at every vsync and shows its number as a
/// row of solid cells (<see cref="GlFrameCounterLayout"/>, with glScissor and glClear only: no shaders, no textures). A
/// full-monitor OpenGL window in front is what makes Windows report a full-screen 3D app, and on some drivers it freezes
/// what every capture of its monitor reads (the server's ExclusiveFullscreen and CaptureUnchangedAfterInput warnings); the
/// numbers it reports (<see cref="Events.GlOpened"/>, <see cref="Events.GlFrame"/>) let a test tell a current capture from
/// an older frame.
/// </summary>
/// <remarks>
/// It cannot keep anyone behind it: it closes itself after <see cref="FixtureOptions.EffectiveGlLifetime"/>, on Esc, on a
/// click of its close block (amber, with a dark cross, bottom right), on WM_CLOSE and when the fixture exits
/// (<see cref="Dispose"/>), and its thread is a background thread, so it never keeps the process alive. On every way out the
/// thread releases the GL context, then the device context, then the window and its class, and then ends. All of those run
/// on that thread, so a <see cref="GlWindowWatchdog"/> armed at <see cref="Start"/> ends the whole fixture process (after an
/// <see cref="Events.Exiting"/> event with reason <c>glWatchdog</c>) if the thread is still alive
/// <see cref="GlWindowWatchdog.Grace"/> after the lifetime: a driver stuck in SwapBuffers or in setting up the context.
/// </remarks>
internal sealed class GlFrameCounterWindow : IDisposable
{
    private const string LifetimeReason = "lifetime";
    private const string EscapeReason = "escape";
    private const string CloseBlockReason = "closeBlock";
    private const string ClosedReason = "closed";
    private const string DestroyedReason = "destroyed";
    private const string AppExitReason = "appExit";
    private const string FailedReason = "failed";

    /// <summary>The <see cref="Events.Exiting"/> reason when the watchdog ends the fixture.</summary>
    private const string WatchdogExitReason = "glWatchdog";

    /// <summary>How long the watchdog waits for its exit event to be written before it ends the process anyway.</summary>
    private static readonly TimeSpan WatchdogLogTimeout = TimeSpan.FromSeconds(1);

    private static int _classCount;

    private readonly FixtureOptions _options;
    private readonly GlWindowMode _mode;
    private readonly FixtureEventLog _events;
    private readonly NativeMethods.RECT _monitor;
    private readonly NativeMethods.RECT _workArea;
    private readonly Thread _thread;

    // Kept alive for as long as the window class points at it
    private readonly Gl.WindowProc _windowProc;

    private volatile string? _closeReason;
    private GlWindowWatchdog? _watchdog;
    private bool _destroyed;
    private int _width;
    private int _height;
    private Box _closeBlock;

    /// <summary>A GL window for <paramref name="options"/>, on the monitor of <paramref name="monitorOf"/>; <see cref="Start"/> opens it.</summary>
    public GlFrameCounterWindow(FixtureOptions options, FixtureEventLog events, nint monitorOf)
    {
        _options = options;
        _mode = options.OpenGl ?? throw new ArgumentException("No --open-gl mode.", nameof(options));
        _events = events;
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(Gl.MonitorFromWindow(monitorOf, Gl.MONITOR_DEFAULTTOPRIMARY), ref info))
        {
            throw new Win32Exception("GetMonitorInfo failed for the main window's monitor.");
        }
        _monitor = info.Monitor;
        _workArea = info.WorkArea;
        _windowProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Fixture GL frame counter" };
    }

    /// <summary>
    /// Arms the watchdog, then starts the window's thread, which creates the window, shows it and draws until it closes, and
    /// disarms the watchdog when it ends.
    /// </summary>
    public void Start()
    {
        _watchdog = new GlWindowWatchdog(GlWindowWatchdog.DeadlineFor(_options.EffectiveGlLifetime), () => _thread.IsAlive,
            EndTheFixture);
        _thread.Start();
    }

    /// <summary>
    /// Asks the window to close (the fixture is exiting) and waits a moment for its thread to release everything. The
    /// watchdog stays armed: only the thread's end disarms it, so a stuck thread still takes the process with it.
    /// </summary>
    public void Dispose()
    {
        _closeReason ??= AppExitReason;
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run()
    {
        var className = $"HelmsmanFixtureGlCounter{Environment.ProcessId}x{Interlocked.Increment(ref _classCount)}";
        var module = Gl.GetModuleHandleW(null);
        ushort atom = 0;
        nint hwnd = 0, dc = 0, context = 0;
        long frame = 0;
        try
        {
            // Before ChoosePixelFormat, which hands the pixel format to the OpenGL driver opengl32 loads
            NativeLibrary.Load("opengl32.dll");

            var windowClass = new Gl.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Gl.WNDCLASSEX>(),
                style = Gl.CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                hInstance = module,
                hCursor = Gl.LoadCursorW(0, Gl.IDC_ARROW),
                lpszClassName = className
            };
            atom = Gl.RegisterClassExW(ref windowClass);
            if (atom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
            }

            var bounds = Bounds();
            hwnd = Gl.CreateWindowExW(Gl.WS_EX_APPWINDOW, className, GlTitle(_options.Instance),
                Gl.WS_POPUP | Gl.WS_CLIPSIBLINGS | Gl.WS_CLIPCHILDREN,
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0, 0, module, 0);
            if (hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
            }

            dc = Gl.GetDC(hwnd);
            if (dc == 0)
            {
                throw new Win32Exception("GetDC failed");
            }

            context = CreateContext(dc);
            var vsync = TrySetSwapInterval();
            NativeMethods.GetClientRect(hwnd, out var client);
            _width = client.Right - client.Left;
            _height = client.Bottom - client.Top;
            _closeBlock = GlFrameCounterLayout.CloseBlock(_width, _height);
            var cells = GlFrameCounterLayout.Cells(_width, _height);

            // The first frame is in the buffer before the window shows
            Draw(cells, ++frame);
            Gl.SwapBuffers(dc);
            var activate = _mode.IsFullMonitor && !_options.NoActivate;
            Gl.ShowWindow(hwnd, activate ? Gl.SW_SHOW : Gl.SW_SHOWNOACTIVATE);
            if (activate)
            {
                Gl.SetForegroundWindow(hwnd);
            }

            var lifetime = _options.EffectiveGlLifetime;
            ReportOpened(hwnd, vsync, lifetime);
            var open = Stopwatch.StartNew();
            var sinceReport = Stopwatch.StartNew();
            _events.Write(Events.GlFrame, ("frame", frame));

            while (true)
            {
                while (Gl.PeekMessageW(out var message, 0, 0, 0, Gl.PM_REMOVE))
                {
                    Gl.TranslateMessage(ref message);
                    Gl.DispatchMessageW(ref message);
                }

                if (_closeReason == null && open.Elapsed >= lifetime)
                {
                    _closeReason = LifetimeReason;
                }
                if (_closeReason != null)
                {
                    break;
                }

                Draw(cells, ++frame);

                // Waits for the vsync once wglSwapIntervalEXT(1) is in force
                Gl.SwapBuffers(dc);
                if (sinceReport.ElapsedMilliseconds >= GlFrameReportIntervalMs)
                {
                    sinceReport.Restart();
                    _events.Write(Events.GlFrame, ("frame", frame));
                }
            }
        }
        catch (Exception ex)
        {
            // Logged, not thrown: an exception that leaves a thread ends the process, and the test should see the reason
            _closeReason ??= FailedReason;
            _events.Write(Events.GlFailed, ("message", ex.Message));
        }
        finally
        {
            try
            {
                // The context before the device context, the device context before the window, the window before its class
                if (context != 0)
                {
                    Gl.wglMakeCurrent(0, 0);
                    Gl.wglDeleteContext(context);
                }
                if (dc != 0)
                {
                    Gl.ReleaseDC(hwnd, dc);
                }
                if (hwnd != 0 && !_destroyed)
                {
                    Gl.DestroyWindow(hwnd);
                }
                if (atom != 0)
                {
                    Gl.UnregisterClassW(className, module);
                }

                _events.Write(Events.GlClosed, ("reason", _closeReason ?? DestroyedReason), ("frames", frame));
            }
            finally
            {
                // Armed while the cleanup runs (a driver can hang there too), and disarmed on every way out of it, a throw
                // in the cleanup or in the log included
                _watchdog?.Disarm();
            }
        }
    }

    /// <summary>
    /// The watchdog's action, on a thread-pool thread: the window's thread is stuck, so the window can only go with the
    /// process. Logs <see cref="Events.Exiting"/> if the log takes it within <see cref="WatchdogLogTimeout"/>, then
    /// terminates the process (TerminateProcess on itself, as the fixture's own watchdog does: nothing can block it).
    /// </summary>
    private void EndTheFixture()
    {
        try
        {
            Task.Run(() => _events.Write(Events.Exiting, ("reason", WatchdogExitReason), ("glThreadAlive", _thread.IsAlive)))
                .Wait(WatchdogLogTimeout);
        }
        catch (Exception)
        {
            // The log is a diagnostic channel; the process ends either way
        }

        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    /// <summary>The window's screen rectangle: the monitor's bounds, or the windowed size centred on its work area.</summary>
    private NativeMethods.RECT Bounds()
    {
        if (_mode.IsFullMonitor)
        {
            return _monitor;
        }

        var left = _workArea.Left + (_workArea.Right - _workArea.Left - _mode.Width) / 2;
        var top = _workArea.Top + (_workArea.Bottom - _workArea.Top - _mode.Height) / 2;
        return new NativeMethods.RECT { Left = left, Top = top, Right = left + _mode.Width, Bottom = top + _mode.Height };
    }

    /// <summary>Sets a double-buffered RGBA pixel format on <paramref name="dc"/> and makes a new GL context on it current.</summary>
    private static nint CreateContext(nint dc)
    {
        var format = new Gl.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Gl.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Gl.PFD_DRAW_TO_WINDOW | Gl.PFD_SUPPORT_OPENGL | Gl.PFD_DOUBLEBUFFER,
            iPixelType = Gl.PFD_TYPE_RGBA,
            cColorBits = 32,
            cAlphaBits = 8,
            iLayerType = Gl.PFD_MAIN_PLANE
        };
        var index = Gl.ChoosePixelFormat(dc, ref format);
        if (index == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ChoosePixelFormat found no double-buffered RGBA format");
        }
        if (!Gl.SetPixelFormat(dc, index, ref format))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetPixelFormat failed");
        }

        var context = Gl.wglCreateContext(dc);
        if (context == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglCreateContext failed");
        }
        if (!Gl.wglMakeCurrent(dc, context))
        {
            var error = Marshal.GetLastWin32Error();
            Gl.wglDeleteContext(context);
            throw new Win32Exception(error, "wglMakeCurrent failed");
        }

        return context;
    }

    /// <summary>Turns vsync on with wglSwapIntervalEXT(1) when the driver has it; whether that succeeded.</summary>
    private static bool TrySetSwapInterval()
    {
        var address = Gl.wglGetProcAddress("wglSwapIntervalEXT");

        // Some drivers return small values instead of null for a missing function
        if (address is >= -1 and <= 3)
        {
            return false;
        }

        return Marshal.GetDelegateForFunctionPointer<Gl.SwapIntervalProc>(address)(1) != 0;
    }

    private void ReportOpened(nint hwnd, bool vsync, TimeSpan lifetime)
    {
        NativeMethods.GetWindowRect(hwnd, out var window);
        var origin = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(hwnd, ref origin);
        var neutral = GlFrameCounterLayout.NeutralPoint(_width, _height);
        _events.Write(
            Events.GlOpened,
            ("hwnd", (long)hwnd),
            ("title", GlTitle(_options.Instance)),
            ("mode", _mode.ToString()),
            ("windowRect", FixtureEventLog.Rect(window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top)),
            ("clientRect", FixtureEventLog.Rect(origin.X, origin.Y, _width, _height)),
            ("monitorRect", FixtureEventLog.Rect(_monitor.Left, _monitor.Top, _monitor.Right - _monitor.Left, _monitor.Bottom - _monitor.Top)),
            ("vsync", vsync),
            ("vendor", GlString(Gl.GL_VENDOR)),
            ("renderer", GlString(Gl.GL_RENDERER)),
            ("lifetimeMs", (long)lifetime.TotalMilliseconds),
            ("closeRect", FixtureEventLog.Rect(origin.X + _closeBlock.X, origin.Y + _closeBlock.Y, _closeBlock.Width, _closeBlock.Height)),
            ("neutralPoint", new System.Text.Json.Nodes.JsonObject { ["x"] = origin.X + neutral.X, ["y"] = origin.Y + neutral.Y }));
    }

    private static string GlString(uint name) => Marshal.PtrToStringAnsi(Gl.glGetString(name)) ?? "";

    /// <summary>Draws frame <paramref name="frame"/>: the background, the row of cells and the close block with its cross.</summary>
    private void Draw(IReadOnlyList<Box> cells, long frame)
    {
        Gl.glViewport(0, 0, _width, _height);
        Gl.glDisable(Gl.GL_SCISSOR_TEST);
        Clear(GlFrameCounterLayout.Background);
        Gl.glEnable(Gl.GL_SCISSOR_TEST);

        var colors = GlFrameCounterLayout.CellColors(frame);
        for (var i = 0; i < cells.Count; i++)
        {
            Fill(cells[i], colors[i]);
        }

        Fill(_closeBlock, GlFrameCounterLayout.CloseBlockColor);
        var step = Math.Max(2, _closeBlock.Width / 10);
        for (var offset = step; offset + 2 * step <= _closeBlock.Width; offset += step / 2)
        {
            Fill(new Box(_closeBlock.X + offset, _closeBlock.Y + offset, step, step), GlFrameCounterLayout.CloseMarkColor);
            Fill(new Box(_closeBlock.X + offset, _closeBlock.Y + _closeBlock.Height - offset - step, step, step),
                GlFrameCounterLayout.CloseMarkColor);
        }
    }

    /// <summary>Fills <paramref name="box"/> (client pixels from the top) with <paramref name="color"/>; GL counts from the bottom.</summary>
    private void Fill(Box box, Rgb color)
    {
        Gl.glScissor(box.X, _height - box.Y - box.Height, box.Width, box.Height);
        Clear(color);
    }

    private static void Clear(Rgb color)
    {
        Gl.glClearColor(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
        Gl.glClear(Gl.GL_COLOR_BUFFER_BIT);
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case Gl.WM_KEYDOWN when wParam == Gl.VK_ESCAPE:
                _closeReason ??= EscapeReason;
                return 0;
            case Gl.WM_LBUTTONDOWN:
                // Client coordinates, signed
                var x = (short)((long)lParam & 0xFFFF);
                var y = (short)(((long)lParam >> 16) & 0xFFFF);
                if (_closeBlock.Contains(x, y))
                {
                    _closeReason ??= CloseBlockReason;
                }
                return 0;
            case Gl.WM_CLOSE:
                // Closed by the loop, which releases the GL context before the window goes
                _closeReason ??= ClosedReason;
                return 0;
            case Gl.WM_DESTROY:
                _destroyed = true;
                _closeReason ??= DestroyedReason;
                return 0;
            case Gl.WM_ERASEBKGND:
                // GL draws every pixel
                return 1;
            default:
                return Gl.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>The user32, gdi32 and opengl32 calls of the GL window, with 64-bit-correct signatures.</summary>
    private static class Gl
    {
        public const uint CS_OWNDC = 0x0020;
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_CLIPSIBLINGS = 0x04000000;
        public const uint WS_CLIPCHILDREN = 0x02000000;
        public const uint WS_EX_APPWINDOW = 0x00040000;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint PM_REMOVE = 0x0001;
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_ERASEBKGND = 0x0014;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const nint VK_ESCAPE = 0x1B;
        public const nint IDC_ARROW = 32512;
        public const uint MONITOR_DEFAULTTOPRIMARY = 1;

        public const uint PFD_DOUBLEBUFFER = 0x00000001;
        public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
        public const uint PFD_SUPPORT_OPENGL = 0x00000020;
        public const byte PFD_TYPE_RGBA = 0;
        public const byte PFD_MAIN_PLANE = 0;

        public const uint GL_COLOR_BUFFER_BIT = 0x00004000;
        public const uint GL_SCISSOR_TEST = 0x0C11;
        public const uint GL_VENDOR = 0x1F00;
        public const uint GL_RENDERER = 0x1F01;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

        /// <summary>wglSwapIntervalEXT (WGL_EXT_swap_control): returns BOOL.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int SwapIntervalProc(int interval);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public NativeMethods.POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize;
            public ushort nVersion;
            public uint dwFlags;
            public byte iPixelType;
            public byte cColorBits;
            public byte cRedBits;
            public byte cRedShift;
            public byte cGreenBits;
            public byte cGreenShift;
            public byte cBlueBits;
            public byte cBlueShift;
            public byte cAlphaBits;
            public byte cAlphaShift;
            public byte cAccumBits;
            public byte cAccumRedBits;
            public byte cAccumGreenBits;
            public byte cAccumBlueBits;
            public byte cAccumAlphaBits;
            public byte cDepthBits;
            public byte cStencilBits;
            public byte cAuxBuffers;
            public byte iLayerType;
            public byte bReserved;
            public uint dwLayerMask;
            public uint dwVisibleMask;
            public uint dwDamageMask;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterClassW(string className, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll")]
        public static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint hwnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessageW(out MSG message, nint hwnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessageW(ref MSG message);

        [DllImport("user32.dll")]
        public static extern nint LoadCursorW(nint instance, nint cursorName);

        [DllImport("user32.dll")]
        public static extern nint GetDC(nint hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(nint hwnd, nint dc);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int ChoosePixelFormat(nint dc, ref PIXELFORMATDESCRIPTOR format);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetPixelFormat(nint dc, int format, ref PIXELFORMATDESCRIPTOR descriptor);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SwapBuffers(nint dc);

        [DllImport("opengl32.dll", SetLastError = true)]
        public static extern nint wglCreateContext(nint dc);

        [DllImport("opengl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wglMakeCurrent(nint dc, nint context);

        [DllImport("opengl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wglDeleteContext(nint context);

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern nint wglGetProcAddress(string name);

        [DllImport("opengl32.dll")]
        public static extern void glViewport(int x, int y, int width, int height);

        [DllImport("opengl32.dll")]
        public static extern void glScissor(int x, int y, int width, int height);

        [DllImport("opengl32.dll")]
        public static extern void glEnable(uint capability);

        [DllImport("opengl32.dll")]
        public static extern void glDisable(uint capability);

        [DllImport("opengl32.dll")]
        public static extern void glClearColor(float red, float green, float blue, float alpha);

        [DllImport("opengl32.dll")]
        public static extern void glClear(uint mask);

        [DllImport("opengl32.dll")]
        public static extern nint glGetString(uint name);
    }
}
