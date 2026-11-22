using System.Reflection;

namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// The three ways the server drops ".exe" from a process name, on the same inputs. ProcessHelpers.StripExe (launch,
/// reuse, kill and the launch window owner) only removes one trailing ".exe" of any case; WindowMatching.WithoutExe
/// (window selectors, layouts) trims first; TargetPolicySettings.NormalizeProcessName (the target policy and the guards)
/// also drops quotes and a folder and trims again after the extension. They are kept apart on purpose: each row shows
/// where they differ.
/// </summary>
public class ProcessNameNormalizationTests
{
    public static TheoryData<string, string, string, string> Cases => new()
    {
        // input,                 StripExe,               WithoutExe,           NormalizeProcessName
        { "notepad.exe",          "notepad",              "notepad",            "notepad" },
        { "notepad",              "notepad",              "notepad",            "notepad" },
        { "NOTEPAD.EXE",          "NOTEPAD",              "NOTEPAD",            "NOTEPAD" },
        { "notepad.Exe",          "notepad",              "notepad",            "notepad" },
        { "notepad.exe.exe",      "notepad.exe",          "notepad.exe",        "notepad.exe" },
        { "",                     "",                     "",                   "" },
        { ".exe",                 "",                     "",                   "" },
        { "exe",                  "exe",                  "exe",                "exe" },
        { "notepad.ex",           "notepad.ex",           "notepad.ex",         "notepad.ex" },
        { "notepad.exe.",         "notepad.exe.",         "notepad.exe.",       "notepad.exe." },
        { " notepad.exe ",        " notepad.exe ",        "notepad",            "notepad" },
        { "notepad.exe ",         "notepad.exe ",         "notepad",            "notepad" },
        { "notepad .exe",         "notepad ",             "notepad ",           "notepad" },
        { "\"notepad.exe\"",      "\"notepad.exe\"",      "\"notepad.exe\"",    "notepad" },
        { @"C:\Windows\notepad.exe", @"C:\Windows\notepad", @"C:\Windows\notepad", "notepad" },
        { "C:/Windows/notepad.exe", "C:/Windows/notepad", "C:/Windows/notepad", "notepad" },
        { "notepad.com",          "notepad.com",          "notepad.com",        "notepad.com" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void StripExe_RemovesOneTrailingExeOfAnyCaseAndNothingElse(string input, string stripExe, string withoutExe,
        string normalized)
    {
        _ = withoutExe;
        _ = normalized;

        StripExe(input).Should().Be(stripExe);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WithoutExe_AlsoTrimsSpacesFirst(string input, string stripExe, string withoutExe, string normalized)
    {
        _ = stripExe;
        _ = normalized;

        WindowMatching.WithoutExe(input).Should().Be(withoutExe);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NormalizeProcessName_AlsoDropsQuotesAndFolder(string input, string stripExe, string withoutExe,
        string normalized)
    {
        _ = stripExe;
        _ = withoutExe;

        TargetPolicySettings.NormalizeProcessName(input).Should().Be(normalized);
    }

    [Fact]
    public void Null_IsRefused_ByEachOfThem()
    {
        var stripExe = () => StripExe(null!);
        var withoutExe = () => WindowMatching.WithoutExe(null!);
        var normalize = () => TargetPolicySettings.NormalizeProcessName(null!);

        stripExe.Should().Throw<NullReferenceException>();
        withoutExe.Should().Throw<ArgumentNullException>();
        normalize.Should().Throw<ArgumentNullException>();
    }

    /// <summary>ProcessHelpers is internal to the server; it is reached the way the architecture tests reach internals.</summary>
    private static string StripExe(string input) =>
        Invoke(typeof(WindowMatching).Assembly.GetType("WinDesktopMcp.Services.ProcessHelpers", throwOnError: true)!
            .GetMethod("StripExe", BindingFlags.Public | BindingFlags.Static)!, input);

    private static string Invoke(MethodInfo method, string input)
    {
        try
        {
            return (string)method.Invoke(null, [input])!;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
