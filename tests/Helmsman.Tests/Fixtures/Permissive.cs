using ModelContextProtocol.Server;
using Helmsman.Infrastructure;

namespace Helmsman.Tests.Fixtures;

/// <summary>
/// Stand-ins for the server's safety collaborators that refuse nothing, for tests about something else. The services take
/// these collaborators as required constructor parameters (a missing registration must fail, never silently switch a check
/// off), so a test that is not about the check passes one of these instead of leaving it out. A test about a check passes
/// the real one, or a mock that refuses.
/// </summary>
public static class Permissive
{
    /// <summary>Target policy settings that deny no process (HELMSMAN_DENY_PROCESSES=none, no allow list).</summary>
    public static TargetPolicySettings NoDenials { get; } = new([], null, "test");

    /// <summary>A target policy that denies nothing: <see cref="TargetPolicyExtensions.DeniesAnything"/> is false.</summary>
    public static ITargetPolicy TargetPolicy { get; } = new TargetPolicy(NoDenials, new Mock<INativeWindowWrapper>().Object);

    /// <summary>A capture check that allows every window, without reading it (the verdict of <see cref="TargetPolicy"/>).</summary>
    public static ICaptureTargetPolicy CapturePolicy { get; } = new AnyCapture();

    /// <summary>
    /// A window wrapper that knows no window: a loose mock, so IsWindow is false and every other read returns its default. For
    /// a service that needs the wrapper only for the target policy's reads, in a test that is about something else.
    /// </summary>
    public static INativeWindowWrapper Windows => new Mock<INativeWindowWrapper>().Object;

    /// <summary>
    /// A window wrapper on which <paramref name="window"/> is in front, a top-level window with no owner, and every window is
    /// open and visible: for the clipboard origin's check that the window the agent's input went to is in front when the
    /// copy happens, in a test about its other rules.
    /// </summary>
    public static INativeWindowWrapper WindowInFront(nint window)
    {
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetForegroundWindow()).Returns(window);
        windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), It.IsAny<WindowAncestor>())).Returns((nint h, WindowAncestor _) => h);
        windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        return windows.Object;
    }

    /// <summary>An agent host with no process and no window: nothing is the host's.</summary>
    public static IHostGuard HostGuard { get; } = new NoHost();

    /// <summary>
    /// A security probe that knows nothing: every integrity Unknown, the session's lock state Unknown, the input desktop
    /// available, and whether Windows asks before it elevates Unknown.
    /// </summary>
    public static IProcessSecurityProbe SecurityProbe { get; } = new UnknownSecurity();

    /// <summary>An input session over a <see cref="FakeInputWrapper"/> that the user never pauses.</summary>
    public static IInputSession Session => new InputSession(new FakeInputWrapper());

    /// <summary>Takeover detection off (HELMSMAN_TAKEOVER_DETECTION unset): nothing is watched.</summary>
    public static ITakeoverMonitor Takeover => new StubTakeoverMonitor(TakeoverMode.Off);

    /// <summary>A paste guard that lets every batch paste.</summary>
    public static IClipboardPasteGuard PasteGuard { get; } = new AnyPaste();

    /// <summary>
    /// A host confirmation that never asks and approves nothing: allowHostTarget passes through to the service as the call
    /// gave it, and the input guard (or its mock) decides alone.
    /// </summary>
    public static IHostTargetConfirmation HostConfirmation { get; } = new NeverAsks();

    private sealed class NeverAsks : IHostTargetConfirmation
    {
        public Task<HostConfirmation> ConfirmAsync(McpServer? server, IReadOnlyList<InputTarget> targets, HostInput input,
            CancellationToken cancellationToken) => Task.FromResult(Helmsman.Infrastructure.HostConfirmation.None);
    }

    private sealed class NoHost : IHostGuard
    {
        public HostSnapshot Host { get; } = new(Environment.ProcessId, [], nint.Zero, null, null, null);

        public bool IsHostProcess(int processId) => false;

        public bool IsHostWindow(nint hWnd) => false;

        public bool IsHostWindowOnlyWhileAncestorsUnknown(nint hWnd) => false;
    }

    private sealed class UnknownSecurity : IProcessSecurityProbe
    {
        public ProcessIntegrity ServerIntegrity { get; } = Unknown(Environment.ProcessId);

        public ProcessIntegrity GetIntegrity(int processId) => Unknown(processId);

        public bool IsInputDesktopAvailable() => true;

        public bool IsSessionLocked() => false;

        public SessionLockState GetSessionLock() => SessionLockState.Unknown;

        public ElevationPrompt GetElevationPrompt() => ElevationPrompt.Unknown;

        private static ProcessIntegrity Unknown(int processId) =>
            new(processId, IntegrityLevel.Unknown, null, null, "not read in tests");
    }

    private sealed class AnyCapture : ICaptureTargetPolicy
    {
        public CaptureRefusal? Check(nint windowHandle) => null;

        public TargetPolicyVerdict Verdict(WindowInfo? window) => TargetPolicyVerdict.Allow;
    }

    private sealed class AnyPaste : IClipboardPasteGuard
    {
        public string? Check(IReadOnlyList<InputEvent> batch, nint receivingWindow) => null;
    }
}
