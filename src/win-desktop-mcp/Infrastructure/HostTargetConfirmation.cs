using ModelContextProtocol.Server;
using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Infrastructure;

/// <summary>The approvals of one call's host windows (<see cref="IHostTargetConfirmation.ConfirmAsync"/>).</summary>
public sealed class HostConfirmation
{
    private readonly IReadOnlyDictionary<nint, HostTargetApproval> _approved;

    internal HostConfirmation(IReadOnlyDictionary<nint, HostTargetApproval> approved) => _approved = approved;

    /// <summary>Nothing was asked: no target names a host window with allowHostTarget, or input is paused.</summary>
    public static HostConfirmation None { get; } = new(new Dictionary<nint, HostTargetApproval>());

    /// <summary><paramref name="target"/> with the user's approval of its window, when they gave one; else as it is.</summary>
    public InputTarget Apply(InputTarget target) =>
        target.WindowHandle is { } handle && _approved.TryGetValue(handle, out var approval) ? target with { ConfirmedHost = approval } : target;

    /// <summary>The user's approval of the window <paramref name="window"/> names, or null.</summary>
    public HostTargetApproval? For(nint window) => _approved.TryGetValue(window, out var approval) ? approval : null;
}

/// <summary>
/// Asks the user, through the client, before a call's allowHostTarget sends input to the agent's own host window.
/// </summary>
public interface IHostTargetConfirmation
{
    /// <summary>
    /// Asks the user once whether the call may send <paramref name="input"/> to the host windows among
    /// <paramref name="targets"/>, and returns the approvals to put on the targets (<see cref="HostConfirmation.Apply"/>).
    /// </summary>
    /// <param name="server">The call's server, through which the client is asked; null when the call has none.</param>
    /// <param name="targets">The call's targets, as the tool resolved them.</param>
    /// <param name="input">What the call sends, as the question shows it (the tool, and a bounded description).</param>
    /// <param name="cancellationToken">The call's cancellation.</param>
    /// <exception cref="ToolErrorException">ConfirmationRequired or ConfirmationDeclined: nothing may be sent.</exception>
    Task<HostConfirmation> ConfirmAsync(McpServer? server, IReadOnlyList<InputTarget> targets, string input, CancellationToken cancellationToken);
}

/// <summary>The server's <see cref="IHostTargetConfirmation"/>.</summary>
public sealed class HostTargetConfirmation : IHostTargetConfirmation
{
    /// <summary>The most characters of a typed text the question shows.</summary>
    public const int MaxTextPreviewLength = 40;

    /// <summary>The most characters of a key spec the question shows.</summary>
    public const int MaxKeysPreviewLength = 60;

    /// <summary>A text to type, for the question: its length and its start on one line, cut at <see cref="MaxTextPreviewLength"/>.</summary>
    public static string TextPreview(string? text) =>
        $"{text?.Length ?? 0} characters: {UserConfirmation.Quote(text, MaxTextPreviewLength)}";

    /// <summary>Keys to send, for the question: on one line, cut at <see cref="MaxKeysPreviewLength"/>.</summary>
    public static string KeysPreview(string? keys) => UserConfirmation.Quote(keys, MaxKeysPreviewLength);

    private readonly IInputTargetGuard _guard;
    private readonly IInputSession _session;
    private readonly TimeSpan _answerTimeout;

    /// <param name="guard">Tells which targets are host windows.</param>
    /// <param name="session">Whether input is paused: nothing is asked then.</param>
    /// <param name="answerTimeout">How long the user has to answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    public HostTargetConfirmation(IInputTargetGuard guard, IInputSession session, TimeSpan? answerTimeout = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
    }

    /// <summary>The label of the question's checkbox.</summary>
    public const string Title = "Allow input to the agent's own window";

    /// <summary>
    /// Asks only when a target names a host window with allowHostTarget (<see cref="IInputTargetGuard.HostWindowToConfirm"/>),
    /// and not while input is paused (the input service then refuses the call with PausedByUser, and the user is not asked
    /// about input that would not be sent). Called before the input gate, as the other questions are, so a user who takes
    /// minutes to answer holds up no other call, and the pause hotkey and resume_input work meanwhile. Fails closed: a client
    /// that cannot ask is ConfirmationRequired (no argument stands in for the user), and anything but a ticked yes in time,
    /// including a client that fails to ask, is ConfirmationDeclined. Nothing is remembered: every call asks again.
    /// </summary>
    public async Task<HostConfirmation> ConfirmAsync(McpServer? server, IReadOnlyList<InputTarget> targets, string input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var hosts = new Dictionary<nint, HostWindowToConfirm>();
        foreach (var target in targets)
        {
            if (target.AllowHostTarget && target.WindowHandle is { } handle && !hosts.ContainsKey(handle) &&
                _guard.HostWindowToConfirm(target) is { } window)
            {
                hosts[handle] = window;
            }
        }

        if (hosts.Count == 0 || _session.IsPaused)
        {
            return HostConfirmation.None;
        }

        var windows = hosts.Values.DistinctBy(w => w.Window.Handle).ToList();
        var names = string.Join(" and ", windows.Select(w => Name(w.Window)));
        if (!UserConfirmation.CanAsk(server))
        {
            throw new ToolErrorException(nameof(WindowErrorCode.ConfirmationRequired),
                $"{names} {(windows.Count == 1 ? "is" : "are")} the agent's own host: the terminal, editor or chat app this server was " +
                "started from. allowHostTarget sends input there only after the user confirms it, and this MCP client cannot ask the " +
                "user (it does not support elicitation). Nothing was sent. No argument replaces the user's answer: ask the user to do " +
                "this step themselves.");
        }

        var answer = await UserConfirmation.AskAsync(server!, Question(windows, input), Title, _answerTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!answer.Confirmed)
        {
            throw new ToolErrorException(nameof(WindowErrorCode.ConfirmationDeclined),
                $"{answer.Reason} allowHostTarget sends input to {names}, the agent's own host, only with the user's yes. Nothing was " +
                "sent. Ask the user how to continue.");
        }

        return new HostConfirmation(hosts.ToDictionary(p => p.Key, p => p.Value.Approval));
    }

    /// <summary>
    /// The question: which window (title and process), that it is the agent's own, and what the call sends there, every part
    /// from another app or from the agent quoted on one line and cut short (<see cref="UserConfirmation.Quote(string?)"/>).
    /// </summary>
    private static string Question(IReadOnlyList<HostWindowToConfirm> windows, string input) =>
        "The AI agent asks to send input to the terminal, editor or chat app it runs in (this window, or a dialog or menu it " +
        "opens). Input there can answer the agent's own permission prompts or run commands.\n" +
        $"{(windows.Count == 1 ? "Window" : "Windows")}: {string.Join("; ", windows.Select(w => Name(w.Window) + (w.OnlyWhileAncestorsUnknown ? ", possibly the agent's own window" : string.Empty)))}\n" +
        $"Input: {input}\n" +
        "Allow it, for this call only?";

    private static string Name(WindowSummary window) =>
        $"{(string.IsNullOrEmpty(window.Title) ? "an untitled window" : UserConfirmation.Quote(window.Title))} ({window.ProcessName}, PID {window.ProcessId})";
}
