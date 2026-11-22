using Helmsman.Models;

namespace Helmsman.Services;

/// <summary>
/// What a launch opens, read from its target and arguments only (the file system is not read): the files and folders a
/// window's title would show (<see cref="DocumentHints"/>, and for reuseExisting <see cref="ReuseTitleNames"/>) and the
/// web address it opens.
/// </summary>
public static class LaunchDocuments
{
    /// <summary>
    /// The files and folders an open window's title must show for reuseExisting to count it as showing what the launch
    /// opens: those (as <see cref="DocumentHints"/> takes them) of the arguments that are not options and of a document
    /// launched directly, with their full paths (a relative path taken in <paramref name="workingDirectory"/>). Empty when
    /// the launch opens nothing: no such argument, and a target that only starts an app (shell:AppsFolder\&lt;AUMID&gt;, or
    /// a scheme alone such as ms-settings:). Null when it opens something no title can be trusted to show: a URI
    /// (ms-settings:display, mailto:...), a web address (a page sets its own title), or arguments none of which names a
    /// file or folder.
    /// </summary>
    public static IReadOnlyList<DocumentHint>? ReuseTitleNames(
        ResolvedLaunchTarget target, IEnumerable<string>? arguments, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var values = (arguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')).ToList();
        if (target.Kind == LaunchTargetKind.Other && !StartsAppOnly(target.FileName))
        {
            if (IsUri(target.FileName, out var scheme) && !IsWebScheme(scheme))
                return null;

            values.Add(target.FileName);
        }

        if (values.Count == 0)
            return [];

        if (values.Any(IsWebAddress))
            return null;

        var hints = FileHints(values, workingDirectory);
        return hints.Count == 0 ? null : hints;
    }

    /// <summary>The first web address (http or https) the launch opens, as an argument or launched directly; null when none.</summary>
    internal static string? WebAddressOf(ResolvedLaunchTarget target, IEnumerable<string>? arguments) =>
        (arguments ?? []).Append(target.Kind == LaunchTargetKind.Other ? target.FileName : string.Empty).FirstOrDefault(IsWebAddress);

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A target that starts an app and opens nothing in it: shell:AppsFolder\&lt;AUMID&gt;, or a scheme alone (ms-settings:).</summary>
    private static bool StartsAppOnly(string fileName) =>
        fileName.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ||
        (IsUri(fileName, out var scheme) && !IsWebScheme(scheme) && fileName.AsSpan(scheme.Length + 1).TrimStart('/').IsWhiteSpace());

    /// <summary>A URI: a scheme of two or more characters (not a drive letter) before a colon, other than file:.</summary>
    private static bool IsUri(string value, out string scheme)
    {
        var colon = value.IndexOf(':');
        scheme = colon > 1 ? value[..colon] : string.Empty;
        return colon > 1 && !scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
               scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static bool IsWebScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files and folders that show a launched document in an existing window's title: those among the arguments (and
    /// a document launched directly), with their full paths (a relative path taken in <paramref name="workingDirectory"/>,
    /// where the app takes it). Options (-x, --x) are skipped, and so are web addresses: a web page sets its own title, so
    /// a host name in it is no sign of the launch.
    /// </summary>
    public static IReadOnlyList<DocumentHint> DocumentHints(
        ResolvedLaunchTarget target, IEnumerable<string>? arguments, string? workingDirectory = null)
    {
        var values = (arguments ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')).ToList();
        if (target.Kind == LaunchTargetKind.Other)
            values.Add(target.FileName);

        return FileHints(values, workingDirectory);
    }

    /// <summary>
    /// The file or folder of each path among <paramref name="values"/> that is not a web address: its name, of 3
    /// characters or more and listed once, and its full path when it can be told.
    /// </summary>
    private static List<DocumentHint> FileHints(IEnumerable<string> values, string? workingDirectory)
    {
        var hints = new List<DocumentHint>();
        foreach (var value in values)
        {
            if (IsWebAddress(value) || (value.IndexOfAny(['\\', '/']) < 0 && !Path.HasExtension(value)))
                continue;

            string name;
            try
            {
                name = Path.GetFileName(value.TrimEnd('\\', '/'));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (name.Length >= 3 && !hints.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                hints.Add(new DocumentHint(name, FullPathOf(value, workingDirectory)));
        }

        return hints;
    }

    /// <summary>
    /// The full path of a file or folder argument, without a trailing separator: a file: URI's local path, a fully
    /// qualified path, or a relative one in <paramref name="workingDirectory"/>; null when it cannot be told. Only the text
    /// is read; the file system is not.
    /// </summary>
    private static string? FullPathOf(string value, string? workingDirectory)
    {
        try
        {
            var path = value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : value;
            var full = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
                : !string.IsNullOrEmpty(workingDirectory) && Path.IsPathFullyQualified(workingDirectory) ? Path.GetFullPath(path, workingDirectory)
                : null;
            return full == null ? null : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
