namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// Pins the target policy's verdicts for every form a process name can be written in: as an entry of
/// HELMSMAN_DENY_PROCESSES or HELMSMAN_ALLOW_PROCESSES, and as the name the policy is asked about (notepad, notepad.exe,
/// any case, a full path with either slash, quotes, surrounding spaces, another extension, a name with dots). The policy
/// is reached only through its settings and Evaluate, never through the helper that reads the names, so these verdicts
/// stay the evidence that the policy reads every form as it did, whatever reads the names for it.
/// </summary>
public class TargetPolicyProcessNameFormTests
{
    private static TargetPolicy Policy(string? deny = null, string? allow = null) =>
        new(TargetPolicySettings.FromEnvironment(name => name switch
        {
            TargetPolicySettings.DenyVariable => deny,
            TargetPolicySettings.AllowVariable => allow,
            _ => null
        }), Permissive.Windows);

    /// <summary>A deny entry in each form, and whether it denies the process Windows names 'notepad'.</summary>
    public static TheoryData<string, bool> DenyEntries => new()
    {
        { "notepad", true },
        { "notepad.exe", true },
        { "NOTEPAD.EXE", true },
        { "Notepad.Exe", true },
        { @"C:\Windows\System32\notepad.exe", true },
        { "C:/Windows/System32/notepad.exe", true },
        { @"C:\Evil\notepad.exe", true }, // the folder is dropped: the entry denies every notepad, wherever it runs from
        { "\"C:\\Program Files\\notepad.exe\"", true },
        { "\"notepad.exe\"", true },
        { "  notepad  ", true },
        { "notepad .exe", true },
        { "notepad.com", false },
        { "notepad.exe.exe", true }, // read once from the variable and once more by the policy: both extensions go
        { "notepa", false },
        { "note*", false },
        { "C:notepad.exe", false },
    };

    [Theory]
    [MemberData(nameof(DenyEntries))]
    public void ADenyEntry_DeniesNotepad_InTheFormsItAlwaysDid(string entry, bool denies)
    {
        var verdict = Policy(deny: entry).Evaluate(["notepad"]);

        verdict.Allowed.Should().Be(!denies);
        if (denies)
            verdict.ProcessName.Should().Be("notepad");
    }

    /// <summary>The name the policy is asked about in each form, and whether a deny list of 'notepad' denies it.</summary>
    public static TheoryData<string, bool> EvaluatedNames => new()
    {
        { "notepad", true },
        { "notepad.exe", true },
        { "NOTEPAD.EXE", true },
        { @"C:\Windows\System32\notepad.exe", true },
        { "C:/Windows/System32/notepad.exe", true },
        { "\"notepad.exe\"", true },
        { " notepad ", true },
        { "notepad .exe", true },
        { "notepad.com", false },
        { "notepad.exe.exe", false }, // read once: only the last extension goes
        { "notepad2", false },
    };

    [Theory]
    [MemberData(nameof(EvaluatedNames))]
    public void AnEvaluatedName_IsDenied_InTheFormsItAlwaysWas(string name, bool denied)
    {
        var verdict = Policy(deny: "notepad").Evaluate([name]);

        verdict.Allowed.Should().Be(!denied);
        if (denied)
            verdict.ProcessName.Should().BeEquivalentTo("notepad");
    }

    [Theory]
    [InlineData("Microsoft.Photos", "Microsoft.Photos", true)]
    [InlineData("Microsoft.Photos", "Microsoft.Photos.exe", true)]
    [InlineData("Microsoft.Photos.exe", "microsoft.photos", true)]
    [InlineData("Microsoft.Photos", "Microsoft", false)]
    [InlineData("Microsoft.Photos", "Photos", false)]
    [InlineData("python3.12", "python3.12", true)]
    [InlineData("python3.12", "PYTHON3.12.EXE", true)]
    [InlineData("python3.12", "python3", false)]
    [InlineData("python3.12", "python", false)]
    [InlineData("python", "python3.12", false)]
    public void ANameWithDots_IsWholeUpToItsExe(string entry, string name, bool denied)
    {
        Policy(deny: entry).Evaluate([name]).Allowed.Should().Be(!denied);
    }

    [Theory]
    [InlineData("notepad", true)]
    [InlineData("notepad.exe", true)]
    [InlineData(@"C:\Tools\notepad.exe", true)] // the documented meaning: a name may include a path, which is dropped
    [InlineData("\"notepad.exe\"", true)]
    [InlineData(" NOTEPAD ", true)]
    [InlineData("notepad.com", false)]
    [InlineData("notepa", false)]
    public void AnAllowEntry_AllowsNotepad_InTheFormsItAlwaysDid(string entry, bool allows)
    {
        var policy = Policy(allow: entry);

        policy.Evaluate(["notepad"]).Allowed.Should().Be(allows);
        policy.Evaluate(["wordpad"]).Allowed.Should().BeFalse("an allow list allows only what it names");
    }

    [Fact]
    public void TheDenyList_WinsOverTheAllowList_InEveryForm()
    {
        Policy(deny: @"C:\Windows\notepad.exe", allow: "NOTEPAD").Evaluate(["notepad"]).Allowed.Should().BeFalse();
        Policy(deny: "notepad", allow: @"C:\Windows\notepad.exe").Evaluate(["notepad.exe"]).Allowed.Should().BeFalse();
    }

    [Fact]
    public void EveryDefaultEntry_IsDenied_ByItsNameInEveryForm()
    {
        var policy = Policy();
        foreach (var entry in TargetPolicySettings.DefaultDeniedProcesses)
        {
            foreach (var form in new[]
                     {
                         entry, entry + ".exe", entry.ToUpperInvariant(), entry.ToLowerInvariant() + ".EXE", $@"C:\Apps\{entry}.exe",
                         $"C:/Apps/{entry}.exe", $"\"{entry}.exe\"", $" {entry} "
                     })
            {
                var verdict = policy.Evaluate([form]);
                verdict.Allowed.Should().BeFalse($"'{form}' names the default list's '{entry}'");
                verdict.ProcessName.Should().BeEquivalentTo(entry);
            }
        }
    }
}
