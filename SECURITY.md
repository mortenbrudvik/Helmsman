# Security policy

Helmsman lets an AI agent click, type, start programs and read the screen on your real Windows desktop. Its checks are
what stand between the agent, or text the agent reads, and that desktop, so a way around one of them is a security
problem. Please report it privately, as described below.

## Supported versions

| Version | Security fixes |
|---------|----------------|
| 4.x, the latest release line | Yes |
| 3.x and earlier | No |

Fixes are made for the latest 4.x release only. Release 4.0.0 renamed the product, and 3.x and earlier get no fixes:
upgrade as [Upgrading from 3.x](README.md#upgrading-from-3x) describes. The Claude Code plugin runs the
`bin/helmsman.exe` committed for each release, so a fix reaches plugin users with the next release.

## Reporting a vulnerability

Do not describe a vulnerability in a public issue, pull request or discussion. Report it privately through GitHub: on
the repository's **Security** tab choose **Report a vulnerability**, or go to
<https://github.com/mortenbrudvik/Helmsman/security/advisories/new>. If the Security tab shows no **Report a
vulnerability** button, open an issue that only asks for a private channel, with no details of the problem.

Please include:

- The Helmsman version (`serverInfo.version` in the MCP handshake, or `server.version` from `get_server_info`), the
  Windows version, how the server runs (the Claude Code plugin, Claude Desktop, another MCP client) and the `HELMSMAN_*`
  settings in force.
- Which check you got past, and where the documentation says it is enforced.
- The steps or tool calls, with their arguments, that reproduce it, and what happened compared with what the
  documentation says should happen.
- What an attack needs: an agent told to misbehave, text on a web page or in a window the agent reads, or another
  program already running on the computer.
- The relevant lines of the audit log (`%LOCALAPPDATA%\Helmsman\audit`), with anything private removed.

Helmsman has a single maintainer, and reports are handled on a best-effort basis, without a promised response or fix
time. Once a fix is released, the report can be published as a GitHub security advisory; say in your report whether and
how you want to be credited.

## What counts as a vulnerability

The documentation says which checks the server enforces and which it does not: README's [Security](README.md#security)
and [What the process allow/deny list covers](README.md#what-the-process-allowdeny-list-covers), and docs/TOOLS.md's
[Safety and Coordinates](docs/TOOLS.md#safety-and-coordinates). In scope is a way for the agent, or untrusted text it
reads (a web page, a window title, a document), to get past a check those pages describe as enforced. For example:

- Input reaching the agent's own terminal, editor or chat app without the user's yes to that call, or other input than
  the confirmation question showed ([Input guard](docs/TOOLS.md#input-guard)).
- Input, an element action, a dialog answer, a window change, a `capture_window` capture or a UI Automation read
  reaching an app the deny list denies or the allow list leaves out, where the allow/deny list section lists it as
  enforced ([Target policy](docs/TOOLS.md#target-policy)).
- A launch that [Commands and scripts](docs/TOOLS.md#commands-and-scripts) says is refused starting anyway: a listed
  interpreter or system tool with arguments, a script, installer or shortcut file, or an unsafe Chromium or Electron
  switch.
- Input, a window change or a launch going through while input is paused, or the agent resuming the pause without the
  user ([Pause hotkey](docs/TOOLS.md#pause-hotkey)).
- The agent reading the user's clipboard, or pasting it, without the user's consent, by a route the
  [Clipboard](docs/TOOLS.md#clipboard) section covers.
- A protected process being ended, or a protected window closed or a process killed without the confirmation the
  README's Security section says the server asks for.
- Typed text, clipboard content or another value the [audit log](docs/TOOLS.md#audit-log) says it never records
  appearing in it.
- A tool that read-only mode or the toolset settings leave out being callable
  ([Toolsets](docs/TOOLS.md#toolsets)).

## Known limits

These are documented as not covered, so they are not vulnerabilities by themselves. Ideas for narrowing them are
welcome as ordinary issues.

- The launch guard is a list of names, not a sandbox: a renamed copy of a listed program, a program missing from the
  list, or a command typed into a console is not caught ([Commands and scripts](docs/TOOLS.md#commands-and-scripts)).
  Nor is the input guard: Start search, Task Manager's Run new task and File Explorer's address bar stay open
  ([Input safety](docs/TOOLS.md#input-safety)).
- What the allow/deny list does not filter. The full list is under "Not enforced" in
  [What the process allow/deny list covers](README.md#what-the-process-allowdeny-list-covers) and in the Target Policy
  entry of README's [Security](README.md#security). It includes, among others: `take_screenshot` and `capture_region`;
  a window capture whose `captureMethod` is `ScreenBitBlt` or `BitBltFallback`, which can show a denied window that
  covers the captured one; `get_browser_content` and `get_browser_url` with an explicit `cdpPort`; the launch tools;
  a denied app's window titles in the window lists and in the audit log; and a denied app's content inside an allowed
  app's window (an embedded WebView2, a preview), which input, window captures and OCR still reach.
- With `allowHostTarget`, the question shows the user the keys and text that will be sent, not what the host
  application does with them: in a terminal, a confirmed Enter runs the line.
- The debug browser's DevTools port has no authentication: any program on the computer can control that browser while
  it runs (Browser Debug Port in [Security](README.md#security)).
- The pause hotkey cannot tell an injected key press from the user's, and a program the agent started before the pause
  can press the chord or resume the shared pause ([Pause hotkey](docs/TOOLS.md#pause-hotkey)).
- Pastes the [Clipboard](docs/TOOLS.md#clipboard) section lists as not covered, such as an application's own Paste menu
  item or drag and drop.
- Prompt injection as such: text the agent reads can try to steer it, and the skill tells agents to treat such text as
  data, but that is guidance ([Untrusted content](docs/TOOLS.md#untrusted-content)). It is a vulnerability when it gets
  past an enforced check, as above.
- A computer that is already compromised: a program running as the user can do whatever Helmsman can.
- The exe is not code-signed (see below).

## Release files

Each GitHub release from 4.1.0 on carries `SHA256SUMS.txt`, with the SHA-256 of the zip and of the files in it, and a
build provenance attestation of the zip and `helmsman.exe`; [Verify a download](README.md#verify-a-download) shows how
to check them and what they do and do not prove. The exe is not code-signed, so Windows SmartScreen may warn about it.
