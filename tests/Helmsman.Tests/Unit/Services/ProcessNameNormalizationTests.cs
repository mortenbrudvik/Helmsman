using System.Reflection;

namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The one rule for a process name (ProcessNames), on every form a name is written in. Normalize is how the target policy
/// reads its entries and how the names Windows reports are read: quotes, a folder, ".exe" and surrounding spaces go.
/// FromArgument is how every tool argument that names a process is read: the same, except that a path names no process
/// (null), because a folder is never compared with the process's own. Matches and Contains compare an argument with a
/// process's name by that rule. The server used to drop ".exe" three ways (ProcessHelpers.StripExe, WindowMatching.WithoutExe
/// and TargetPolicySettings.NormalizeProcessName), which disagreed on quotes, paths and spaces; none of them is left.
/// </summary>
public class ProcessNameNormalizationTests
{
    /// <summary>input, Normalize, FromArgument (null: a path), and whether the argument names the process 'notepad'.</summary>
    public static TheoryData<string, string, string?, bool> Cases => new()
    {
        { "notepad", "notepad", "notepad", true },
        { "notepad.exe", "notepad", "notepad", true },
        { "NOTEPAD.EXE", "NOTEPAD", "NOTEPAD", true },
        { "notepad.Exe", "notepad", "notepad", true },
        { " notepad.exe ", "notepad", "notepad", true },
        { "notepad.exe ", "notepad", "notepad", true },
        { "notepad .exe", "notepad", "notepad", true },
        { "\tnotepad\t", "notepad", "notepad", true },
        { "\"notepad.exe\"", "notepad", "notepad", true },
        { "\" notepad \"", "notepad", "notepad", true },
        { @"C:\Windows\System32\notepad.exe", "notepad", null, false },
        { "C:/Windows/System32/notepad.exe", "notepad", null, false },
        { "\"C:\\Program Files\\notepad.exe\"", "notepad", null, false },
        { @"\\server\share\notepad.exe", "notepad", null, false },
        { @".\notepad.exe", "notepad", null, false },
        { "C:notepad.exe", "C:notepad", null, false },
        { "notepad.exe.exe", "notepad.exe", "notepad.exe", false },
        { "notepad.com", "notepad.com", "notepad.com", false },
        { "notepad.ex", "notepad.ex", "notepad.ex", false },
        { "notepad.exe.", "notepad.exe.", "notepad.exe.", false },
        { "notepa", "notepa", "notepa", false },
        { "Microsoft.Photos", "Microsoft.Photos", "Microsoft.Photos", false },
        { "Microsoft.Photos.exe", "Microsoft.Photos", "Microsoft.Photos", false },
        { "python3.12", "python3.12", "python3.12", false },
        { "python3.12.exe", "python3.12", "python3.12", false },
        { "exe", "exe", "exe", false },
        { ".exe", "", "", false },
        { "\"\"", "", "", false },
        { "", "", "", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Normalize_DropsQuotesAFolderExeAndSpaces(string input, string normalized, string? argument, bool namesNotepad)
    {
        _ = argument;
        _ = namesNotepad;

        ProcessNames.Normalize(input).Should().Be(normalized);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FromArgument_IsNormalize_ButAPathNamesNoProcess(string input, string normalized, string? argument, bool namesNotepad)
    {
        _ = normalized;
        _ = namesNotepad;

        ProcessNames.FromArgument(input).Should().Be(argument);
        ProcessNames.IsPath(input).Should().Be(argument == null);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_IsTheWholeNameIgnoringCase_AndContains_IsAPartOfIt(string input, string normalized, string? argument, bool namesNotepad)
    {
        _ = normalized;

        ProcessNames.Matches("notepad", input).Should().Be(namesNotepad);
        ProcessNames.Matches("NOTEPAD", input).Should().Be(namesNotepad, "case never matters");
        ProcessNames.Matches("notepad.exe", input).Should().Be(namesNotepad, "an image name is read the same way");
        ProcessNames.Contains("notepad", input).Should().Be(argument is { Length: > 0 } && "notepad".Contains(argument, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PathRefusal_IsGivenForAPathAlone_AndNamesTheProcessNameToPass(string input, string normalized, string? argument, bool namesNotepad)
    {
        _ = normalized;
        _ = namesNotepad;

        var refusal = ProcessNames.PathRefusal("window.processName", input);
        if (argument != null)
        {
            refusal.Should().BeNull();
            return;
        }

        refusal.Should().StartWith("window.processName takes a process name, not a path: pass 'notepad'")
            .And.Contain($"instead of '{input}'");
    }

    [Fact]
    public void PathRefusal_OfAFolderWithoutAFile_AsksForTheProcesssName()
    {
        ProcessNames.PathRefusal("processName", @"C:\Windows\").Should().Contain("pass the process's name").And.NotContain("pass ''");
    }

    [Fact]
    public void NullOrNoName_MatchesNothing()
    {
        ProcessNames.Matches(null, "notepad").Should().BeFalse();
        ProcessNames.Matches("notepad", null).Should().BeFalse();
        ProcessNames.Contains(null, "note").Should().BeFalse();
        ProcessNames.Contains("notepad", null).Should().BeFalse();
        ProcessNames.Contains("notepad", ".exe").Should().BeFalse("a blank part would be part of every name");
        ProcessNames.PathRefusal("processName", null).Should().BeNull();
        ((Action)(() => ProcessNames.Normalize(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ProcessNames.FromArgument(null!))).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("StripExe", "Helmsman.Services.ProcessHelpers")]
    [InlineData("WithoutExe", "Helmsman.Services.WindowMatching")]
    [InlineData("NormalizeProcessName", "Helmsman.Models.TargetPolicySettings")]
    public void NoOtherWayToReadAProcessName_IsLeft(string method, string type)
    {
        // Two implementations drift apart: the reported inconsistency was the policy and the selectors reading a name differently
        typeof(ProcessNames).Assembly.GetType(type, throwOnError: true)!
            .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Should().BeNull();
    }
}
