using ModelContextProtocol.Server;
using Helmsman.Models;
using Helmsman.Services.Interfaces;

namespace Helmsman.Infrastructure;

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

/// <summary>What a call would send to the agent's host window, for the user's question.</summary>
/// <param name="Tool">The tool.</param>
/// <param name="Action">
/// What the call does when it sends no characters or keys (a click: its button and point), or what its characters are for
/// ("set_value on the Button 'Allow'"); null for none.
/// </param>
/// <param name="Content">
/// The characters and keys the call sends, built from the value the call sends (InputPreview), only when the user is asked;
/// null when it sends none. The function returns null when the call is refused anyway before anything is sent (keys that do
/// not parse): the user is not asked then, and without an approval nothing reaches the host.
/// </param>
/// <param name="Pastes">
/// True when the call pastes or drops content by what it is (a right or middle click, a drag, a paste mode): what it puts
/// in cannot be shown, so it is refused for the agent's host without a question (PasteNotAllowed).
/// </param>
/// <param name="NotAllowed">
/// What of the call is outside the allowlist for the agent's host before any content is read (a modifier key held around a
/// click or scroll); null when nothing. Refused without a question (InvalidArguments).
/// </param>
public sealed record HostInput(string Tool, string? Action = null, Func<InputPreview?>? Content = null, bool Pastes = false,
    string? NotAllowed = null);

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
    /// <param name="input">What the call sends, which the question shows in full.</param>
    /// <param name="cancellationToken">The call's cancellation.</param>
    /// <exception cref="ToolErrorException">
    /// TextTooLong (the input does not fit a question whole), ConfirmationRequired or ConfirmationDeclined: nothing may be sent.
    /// </exception>
    Task<HostConfirmation> ConfirmAsync(McpServer? server, IReadOnlyList<InputTarget> targets, HostInput input, CancellationToken cancellationToken);

}

/// <summary>The server's <see cref="IHostTargetConfirmation"/>.</summary>
public sealed class HostTargetConfirmation : IHostTargetConfirmation
{
    /// <summary>
    /// The most characters the input may take as the question shows it (<see cref="ShownInput"/>: an escaped character or a
    /// key counts as its marker's length). The user approves only input they see whole, so longer input to the host is
    /// refused (TextTooLong) rather than shown cut: a harmless start could hide a command after it. About four lines of a
    /// client's question, as long as the longest question of the close tools; it also keeps type_text below its auto-paste
    /// threshold for plain text.
    /// </summary>
    public const int MaxShownLength = 300;

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
    public async Task<HostConfirmation> ConfirmAsync(McpServer? server, IReadOnlyList<InputTarget> targets, HostInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(input);

        var hosts = HostWindows(targets);
        if (hosts.Count == 0 || _session.IsPaused)
        {
            return HostConfirmation.None;
        }

        var windows = hosts.Values.DistinctBy(w => w.Window.Handle).ToList();
        var names = string.Join(" and ", windows.Select(w => Name(w.Window)));

        // Never a paste or a drop into the host: what it puts in cannot be shown, and the clipboard can change after the yes
        if (input.Pastes)
        {
            throw new ToolErrorException(nameof(InputErrorCode.PasteNotAllowed), HostPasteRefusal(names));
        }

        if (input.NotAllowed is { } notAllowedAction)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                ShownInput.HostAllowlistRefusal(notAllowedAction, names) + " Nothing was sent, and the user was not asked.");
        }

        // The question shows every character and key that would be sent, or the call is refused: never a cut preview
        InputPreview? content = null;
        if (input.Content != null)
        {
            content = input.Content();
            if (content == null)
            {
                return HostConfirmation.None;
            }

            if (content.Pastes)
            {
                throw new ToolErrorException(nameof(InputErrorCode.PasteNotAllowed), HostPasteRefusal(names));
            }

            // Only input whose effect the question can state: text, Enter, Tab, Backspace, navigation keys, a few Ctrl letters
            if (content.NotAllowed is { } notAllowed)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    ShownInput.HostAllowlistRefusal(notAllowed, names) + " Nothing was sent, and the user was not asked.");
            }

            if (content.Shown.Length > MaxShownLength)
            {
                throw new ToolErrorException(nameof(InputErrorCode.TextTooLong),
                    $"Input to {names}, the agent's own host, is shown to the user in full before it is sent, and this input shows " +
                    $"as {content.Shown.Length} characters, more than the {MaxShownLength} a question shows whole. Nothing was sent, " +
                    "and the user was not asked. Send less per call: split it into several calls, each of which the user confirms.");
            }
        }
        if (!UserConfirmation.CanAsk(server))
        {
            throw new ToolErrorException(nameof(WindowErrorCode.ConfirmationRequired),
                $"{names} {(windows.Count == 1 ? "is" : "are")} the agent's own host: the terminal, editor or chat app this server was " +
                "started from. allowHostTarget sends input there only after the user confirms it, and this MCP client cannot ask the " +
                "user (it does not support elicitation). Nothing was sent. No argument replaces the user's answer: ask the user to do " +
                "this step themselves.");
        }

        var answer = await UserConfirmation.AskAsync(server!, Question(windows, input, content), Title, _answerTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!answer.Confirmed)
        {
            throw new ToolErrorException(nameof(WindowErrorCode.ConfirmationDeclined),
                $"{answer.Reason} allowHostTarget sends input to {names}, the agent's own host, only with the user's yes. Nothing was " +
                "sent. Ask the user how to continue.");
        }

        return new HostConfirmation(hosts.ToDictionary(p => p.Key, p => p.Value.Approval with { KeyboardLayout = content?.Layout, CapsLock = content?.CapsLock ?? false }));
    }


    /// <summary>The host windows among <paramref name="targets"/> named with allowHostTarget, by the handle each target names.</summary>
    private Dictionary<nint, HostWindowToConfirm> HostWindows(IReadOnlyList<InputTarget> targets)
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

        return hosts;
    }

    private static string HostPasteRefusal(string names) =>
        $"The input pastes or drops content into {names}, the agent's own host (Ctrl+V, Shift+Insert, Win+V, the SYN character, a " +
        "right or middle click, a drag, or a paste method). That is never allowed: the user is shown every character sent there, " +
        "and what a paste or drop puts in cannot be shown and can change after their yes. Nothing was sent, and the user was not " +
        "asked. Type the text with type_text (method unicode) instead.";

    /// <summary>
    /// The question: which window (its title quoted on one line, <see cref="UserConfirmation.Quote(string?)"/>, and its
    /// process), that it is the agent's own, and what the call sends there: the action, and every character and key it sends
    /// between ⟦ and ⟧ (<see cref="ShownInput"/>), saying first when it presses Enter or types a line break, which can run a
    /// command in a terminal.
    /// </summary>
    private static string Question(IReadOnlyList<HostWindowToConfirm> windows, HostInput input, InputPreview? content)
    {
        var question = new System.Text.StringBuilder(
            "The AI agent asks to send input to the terminal, editor or chat app it runs in (this window, or a dialog or menu it " +
            "opens). Input there can answer the agent's own permission prompts or run commands.\n");
        question.Append(windows.Count == 1 ? "Window: " : "Windows: ")
            .Append(string.Join("; ", windows.Select(w => Name(w.Window) + (w.OnlyWhileAncestorsUnknown ? ", possibly the agent's own window" : string.Empty))))
            .Append('\n');
        var tool = input.Action == null ? input.Tool : $"{input.Tool} ({input.Action})";
        if (content == null)
        {
            question.Append($"It will send: {tool}.\n");
        }
        else
        {
            var then = content.PressesEnter
                ? ", and press Enter (the Enter key, or a line break, Ctrl+M or Ctrl+J, which a terminal takes as Enter), which in a " +
                  "terminal runs the command"
                : string.Empty;
            question.Append($"{tool} will {content.How} in this window{then}:\n")
                .Append(ShownInput.Open).Append(content.Shown).Append(ShownInput.Close).Append('\n')
                .Append(ShownInput.Legend).Append('\n');
        }

        return question.Append("Allow it, for this call only?").ToString();
    }

    private static string Name(WindowSummary window) =>
        $"{(string.IsNullOrEmpty(window.Title) ? "an untitled window" : UserConfirmation.Quote(window.Title))} ({window.ProcessName}, PID {window.ProcessId})";
}
