using Helmsman.Infrastructure;

namespace Helmsman.Tests.Unit.Models;

/// <summary>
/// The settings' names (SettingNames): HELMSMAN_* since release 4.0.0, and the WAM_* name each had before, which is still
/// read when the HELMSMAN_ variable is not present or empty. Every setting is checked through its real options class, with a
/// fake environment: the new name is read; the old one is read, and parsed as before, when the new one is not present or is
/// empty or white space; the new one wins when both hold a value; neither is the default.
/// </summary>
public sealed class SettingNamesTests
{
    /// <summary>A fake environment: a name not listed is not present.</summary>
    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    /// <summary>
    /// Each setting: a value that differs from its default, another value that differs from both, and what its options class
    /// makes of an environment, as text to compare.
    /// </summary>
    private static readonly Dictionary<string, (string Value, string Other, Func<Func<string, string?>, string> Effect)> Settings =
        new(StringComparer.Ordinal)
        {
            [SettingNames.Toolsets] = ("windows", "monitors",
                env => Describe(ToolsetSelection.FromEnvironment(env, Toolsets.Server), s => s.Enabled, s => s.Source)),
            [SettingNames.ExcludeTools] = ("kill_process", "close_window",
                env => Describe(ToolsetSelection.FromEnvironment(env, Toolsets.Server), s => s.ExcludedTools, s => s.Source)),
            [SettingNames.ReadOnly] = ("1", "maybe",
                env => Describe(ToolsetSelection.FromEnvironment(env, Toolsets.Server), s => s.Warnings, s => s.ReadOnly)),
            [SettingNames.DenyProcesses] = ("notepad", "calc",
                env => Describe(TargetPolicySettings.FromEnvironment(env), s => s.DeniedProcesses, s => s.DenyListSource)),
            [SettingNames.AllowProcesses] = ("notepad", "calc",
                env => Describe(TargetPolicySettings.FromEnvironment(env), s => s.AllowedProcesses ?? ["(none)"], s => s.DenyListSource)),
            [SettingNames.DestructiveChords] = ("^s", "none",
                env => Describe(InputGuardOptions.FromEnvironment(env), o => o.DestructiveChords.Select(c => c.Spec), o => o.DestructiveChordSource)),
            [SettingNames.AllowedProtocols] = ("vscode", "spotify",
                env => Describe(ProtocolAllowlist.FromEnvironment(env), p => p.ExtraSchemes, p => p.IgnoredEntries.Count)),
            [SettingNames.AllowUserClipboard] = ("1", "perhaps",
                env => Describe(ClipboardOptions.FromEnvironment(env), o => [o.Warning ?? ""], o => o.AllowUserClipboard)),
            [SettingNames.PauseHotkey] = ("none", "Ctrl+Alt+F12",
                env => Describe(PauseHotkeyOptions.FromEnvironment(env), o => [o.Chord?.Text ?? "(off)"], o => o.Source)),
            [SettingNames.TakeoverDetection] = ("stop", "warn",
                env => Describe(TakeoverOptions.FromEnvironment(env), o => [o.Warning ?? ""], o => o.Mode)),
            [SettingNames.AuditPath] = (@"C:\audit-test", @"D:\other-audit",
                env => AuditLogOptions.FromEnvironment(env).Folder),
            [SettingNames.LayoutsPath] = (@"C:\layouts-test", @"D:\other-layouts",
                env => Describe(LayoutStorageOptions.FromEnvironment(env), o => o.MigrationSources, o => o.LayoutsPath)),
            [SettingNames.UiaEngine] = ("uia2", "uia7",
                env => Describe(UiaEngineOptions.FromEnvironment(env), o => [o.Warning ?? ""], o => o.Engine + "/" + o.Source)),
            [SettingNames.UiaProviderContains] = ("1", "0",
                env => ProviderContainsOptions.FromEnvironment(env).Enabled.ToString()),
        };

    private static string Describe<T>(T options, Func<T, IEnumerable<string>> list, Func<T, object> detail) =>
        $"{string.Join(",", list(options))} | {detail(options)}";

    public static TheoryData<string> SettingNameData() => new(SettingNames.All);

    #region The registry

    [Fact]
    public void TheOldNames_AreTheNamesOfRelease3()
    {
        // The variables 3.x read: a user's configuration under these names must keep working
        SettingNames.All.Select(SettingNames.OldNameOf).Should().BeEquivalentTo(
        [
            "WAM_TOOLSETS", "WAM_EXCLUDE_TOOLS", "WAM_READ_ONLY", "WAM_DENY_PROCESSES", "WAM_ALLOW_PROCESSES",
            "WAM_DESTRUCTIVE_CHORDS", "WAM_ALLOWED_PROTOCOLS", "WAM_ALLOW_USER_CLIPBOARD", "WAM_PAUSE_HOTKEY",
            "WAM_TAKEOVER_DETECTION", "WAM_AUDIT_PATH", "WAM_LAYOUTS_PATH", "WAM_UIA_ENGINE", "WAM_UIA_PROVIDER_CONTAINS"
        ]);
        SettingNames.All.Should().OnlyHaveUniqueItems().And.OnlyContain(n => n.StartsWith("HELMSMAN_", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryOptionsClass_NamesItsSettingFromTheRegistry()
    {
        new[]
        {
            ToolsetSelection.ToolsetsVariable, ToolsetSelection.ExcludeToolsVariable, ToolsetSelection.ReadOnlyVariable,
            TargetPolicySettings.DenyVariable, TargetPolicySettings.AllowVariable, InputGuardOptions.DestructiveChordsVariable,
            ProtocolAllowlist.Variable, ClipboardOptions.EnvironmentVariable, PauseHotkeyOptions.EnvironmentVariable,
            TakeoverOptions.EnvironmentVariable, AuditLogOptions.FolderEnvironmentVariable,
            LayoutStorageOptions.LayoutsPathEnvironmentVariable, UiaEngineOptions.EnvironmentVariable,
            ProviderContainsOptions.EnvironmentVariable
        }.Should().Equal(SettingNames.All);
    }

    [Fact]
    public void TheTable_CoversEverySetting()
    {
        Settings.Keys.Should().BeEquivalentTo(SettingNames.All);
    }

    [Fact]
    public void ANameThatIsNoSetting_IsRefused()
    {
        var read = () => SettingNames.Read(Env(), "HELMSMAN_NOT_A_SETTING");
        var oldName = () => SettingNames.OldNameOf("WAM_TOOLSETS");

        read.Should().Throw<ArgumentException>();
        oldName.Should().Throw<ArgumentException>();
    }

    #endregion

    #region The reader

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void Read_UsesTheNewNameWhenItHoldsAValue_OtherwiseTheOldOne(string name)
    {
        var oldName = SettingNames.OldNameOf(name);

        SettingNames.Read(Env((name, "new")), name).Should().Be("new");
        SettingNames.Read(Env((oldName, "old")), name).Should().Be("old");
        SettingNames.Read(Env((name, "new"), (oldName, "old")), name).Should().Be("new");
        SettingNames.Read(Env((name, ""), (oldName, "old")), name).Should().Be("old", "an empty new name counts as not set");
        SettingNames.Read(Env((name, " \t"), (oldName, "old")), name).Should().Be("old", "so does one of white space only");
        SettingNames.Read(Env((name, "new"), (oldName, "")), name).Should().Be("new");
        SettingNames.Read(Env((name, "")), name).Should().Be("");
        SettingNames.Read(Env((oldName, "")), name).Should().Be("");
        SettingNames.Read(Env(), name).Should().BeNull();
    }

    #endregion

    #region Every setting through its options class

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void TheNewName_IsRead(string name)
    {
        var (value, _, effect) = Settings[name];

        effect(Env((name, value))).Should().NotBe(effect(Env()), "the test value must change the setting");
    }

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void TheOldName_IsRead_AsBefore_WhenTheNewOneIsNotPresent(string name)
    {
        var (value, other, effect) = Settings[name];
        var oldName = SettingNames.OldNameOf(name);

        effect(Env((oldName, value))).Should().Be(effect(Env((name, value))));
        effect(Env((oldName, other))).Should().Be(effect(Env((name, other))), "the old name's value is parsed as the new one's");
    }

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void TheNewName_Wins_WhenBothArePresent(string name)
    {
        var (value, other, effect) = Settings[name];
        var oldName = SettingNames.OldNameOf(name);

        effect(Env((name, other), (oldName, value))).Should().Be(effect(Env((name, other))));
        effect(Env((name, other), (oldName, value))).Should().NotBe(effect(Env((name, value))));
    }

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void TheOldName_IsRead_WhenTheNewOneIsEmpty(string name)
    {
        // An empty variable is present: an MCP client's env block that expands an unset variable passes "NAME=", and .NET
        // returns "" for it (null only for a variable that is not there). Every setting reads "" or white space as not set,
        // so the old name's value applies, as when the new name is not there at all.
        var (value, _, effect) = Settings[name];
        var oldName = SettingNames.OldNameOf(name);

        effect(Env((name, ""), (oldName, value))).Should().Be(effect(Env((oldName, value))));
        effect(Env((name, "  "), (oldName, value))).Should().Be(effect(Env((oldName, value))));
        effect(Env((name, ""), (oldName, value))).Should().NotBe(effect(Env()));
    }

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void AnEmptyOrWhiteSpaceValue_IsTheDefault(string name)
    {
        // Why an empty new name can be read as not set: no setting gives an empty or white-space value a meaning
        var (_, _, effect) = Settings[name];

        effect(Env((name, ""))).Should().Be(effect(Env()));
        effect(Env((name, " \t "))).Should().Be(effect(Env()));
    }

    [Theory]
    [MemberData(nameof(SettingNameData))]
    public void Neither_IsTheDefault(string name)
    {
        var (value, _, effect) = Settings[name];

        effect(Env()).Should().NotBe(effect(Env((SettingNames.OldNameOf(name), value))));
    }

    #endregion

    #region The safety settings keep applying under their old names

    [Fact]
    public void AProcessNamedOnlyInTheOldDenyList_IsStillDenied()
    {
        var policy = new TargetPolicy(TargetPolicySettings.FromEnvironment(Env(("WAM_DENY_PROCESSES", "notepad.exe"))), Permissive.Windows);

        policy.Evaluate(["notepad"]).Allowed.Should().BeFalse();
        policy.Evaluate(["calc"]).Allowed.Should().BeTrue();
    }

    [Fact]
    public void TheOldDenyList_None_StillTurnsTheDefaultListOff()
    {
        var policy = new TargetPolicy(TargetPolicySettings.FromEnvironment(Env(("WAM_DENY_PROCESSES", "none"))), Permissive.Windows);

        policy.Evaluate(["KeePass"]).Allowed.Should().BeTrue();
    }

    [Fact]
    public void AnAppMissingFromTheOldAllowList_IsStillRefused()
    {
        var policy = new TargetPolicy(TargetPolicySettings.FromEnvironment(Env(("WAM_ALLOW_PROCESSES", "notepad"))), Permissive.Windows);

        policy.Evaluate(["notepad"]).Allowed.Should().BeTrue();
        policy.Evaluate(["calc"]).Allowed.Should().BeFalse();
    }

    [Fact]
    public void TheOldReadOnly_StillServesOnlyReadOnlyTools()
    {
        var selection = ToolsetSelection.FromEnvironment(Env(("WAM_READ_ONLY", "1")), Toolsets.Server);

        selection.ReadOnly.Should().BeTrue();
        selection.Serves("close_window", readOnlyHint: false).Should().BeFalse();
        selection.Serves("find_windows", readOnlyHint: true).Should().BeTrue();
    }

    [Fact]
    public void TheOldReadOnly_WithAValueItDoesNotKnow_StillFailsClosed()
    {
        ToolsetSelection.FromEnvironment(Env(("WAM_READ_ONLY", "maybe")), Toolsets.Server).ReadOnly.Should().BeTrue();
    }

    [Fact]
    public void TheOldExcludeTools_StillLeavesTheToolOut()
    {
        var selection = ToolsetSelection.FromEnvironment(Env(("WAM_EXCLUDE_TOOLS", "kill_process")), Toolsets.Server);

        selection.Serves("kill_process", readOnlyHint: false).Should().BeFalse();
    }

    [Fact]
    public void TheOldToolsets_StillServeOnlyTheToolsetsNamed()
    {
        var selection = ToolsetSelection.FromEnvironment(Env(("WAM_TOOLSETS", "monitors")), Toolsets.Server);

        selection.Enabled.Should().Contain("monitors").And.NotContain("windows");
        selection.Serves("close_window", readOnlyHint: false).Should().BeFalse();
    }

    [Fact]
    public void TheOldAllowedProtocols_StillAddTheirSchemes_AndBlockedOnesStayBlocked()
    {
        var allowlist = ProtocolAllowlist.FromEnvironment(Env(("WAM_ALLOWED_PROTOCOLS", "vscode,javascript")));

        allowlist.Allows("vscode").Should().BeTrue();
        allowlist.Allows("javascript").Should().BeFalse();
    }

    [Fact]
    public void TheOldDestructiveChords_StillReplaceTheList()
    {
        var options = InputGuardOptions.FromEnvironment(Env(("WAM_DESTRUCTIVE_CHORDS", "^s")));

        options.DestructiveChords.Select(c => c.Spec).Should().Equal("^s");
        options.DestructiveChordSource.Should().Be(SettingNames.DestructiveChords);
    }

    [Fact]
    public void TheOldTakeoverDetection_StillStops()
    {
        TakeoverOptions.FromEnvironment(Env(("WAM_TAKEOVER_DETECTION", "stop"))).Mode.Should().Be(TakeoverMode.Stop);
    }

    [Fact]
    public void TheOldPauseHotkey_StillSetsTheChord()
    {
        PauseHotkeyOptions.FromEnvironment(Env(("WAM_PAUSE_HOTKEY", "Ctrl+Alt+F12"))).Chord!.Text.Should().Be("Ctrl+Alt+F12");
        PauseHotkeyOptions.FromEnvironment(Env(("WAM_PAUSE_HOTKEY", "none"))).Chord.Should().BeNull();
    }

    [Fact]
    public void AnEmptyNewAllowList_KeepsTheOldAllowList()
    {
        var policy = new TargetPolicy(TargetPolicySettings.FromEnvironment(
            Env(("HELMSMAN_ALLOW_PROCESSES", ""), ("WAM_ALLOW_PROCESSES", "notepad"))), Permissive.Windows);

        policy.Evaluate(["notepad"]).Allowed.Should().BeTrue();
        policy.Evaluate(["calc"]).Allowed.Should().BeFalse("an empty new name must not open every app");
    }

    [Fact]
    public void AnEmptyNewReadOnly_KeepsTheOldReadOnly()
    {
        var selection = ToolsetSelection.FromEnvironment(Env(("HELMSMAN_READ_ONLY", ""), ("WAM_READ_ONLY", "1")), Toolsets.Server);

        selection.ReadOnly.Should().BeTrue();
        selection.Serves("close_window", readOnlyHint: false).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyNewDenyList_KeepsTheOldAdditions()
    {
        var policy = new TargetPolicy(TargetPolicySettings.FromEnvironment(
            Env(("HELMSMAN_DENY_PROCESSES", " "), ("WAM_DENY_PROCESSES", "default,notepad.exe"))), Permissive.Windows);

        policy.Evaluate(["notepad"]).Allowed.Should().BeFalse();
        policy.Evaluate(["KeePass"]).Allowed.Should().BeFalse("the default list stays in force too");
    }

    [Fact]
    public void TheOldUserClipboard_StillAllowsReadingIt()
    {
        ClipboardOptions.FromEnvironment(Env(("WAM_ALLOW_USER_CLIPBOARD", "1"))).AllowUserClipboard.Should().BeTrue();
    }

    #endregion
}
