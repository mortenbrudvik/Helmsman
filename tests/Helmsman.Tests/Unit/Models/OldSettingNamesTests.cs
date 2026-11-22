namespace Helmsman.Tests.Unit.Models;

/// <summary>
/// The warning about settings still given under their WAM_ names from before release 4.0.0 (SettingNames.FindOld,
/// OldSettingNames): the old names read in place of a HELMSMAN_ name that is not set, and separately the old names ignored
/// because their HELMSMAN_ name is set too. It names variables, never their values.
/// </summary>
public sealed class OldSettingNamesTests
{
    private const string SecretPath = @"C:\Users\someone\private-audit";

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void OnlyNewNames_GiveNoWarning()
    {
        var old = SettingNames.FindOld(Env(("HELMSMAN_DENY_PROCESSES", "notepad"), ("HELMSMAN_READ_ONLY", "1")));

        old.ReadInstead.Should().BeEmpty();
        old.Ignored.Should().BeEmpty();
        old.Warnings.Should().BeEmpty();
        OldSettingNames.None.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void NoSettingAtAll_GivesNoWarning()
    {
        SettingNames.FindOld(Env(("PATH", @"C:\Windows"), ("WAM_SOMETHING_ELSE", "1"))).Warnings.Should().BeEmpty();
    }

    [Fact]
    public void OldNamesReadInstead_AreListedInOneWarning_ThatSaysToRenameThem()
    {
        // Arrange
        var environment = Env(("WAM_DENY_PROCESSES", "keepass"), ("WAM_AUDIT_PATH", SecretPath), ("HELMSMAN_TOOLSETS", "all"));

        // Act
        var old = SettingNames.FindOld(environment);

        // Assert
        old.ReadInstead.Should().Equal("WAM_DENY_PROCESSES", "WAM_AUDIT_PATH");
        old.Ignored.Should().BeEmpty();
        var warning = old.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("WAM_DENY_PROCESSES and WAM_AUDIT_PATH")
            .And.Contain("HELMSMAN_DENY_PROCESSES and HELMSMAN_AUDIT_PATH")
            .And.Contain("not set")
            .And.Contain("Rename them")
            .And.NotContain("TOOLSETS");
    }

    [Fact]
    public void OldNamesIgnored_AreListedInAWarningOfTheirOwn()
    {
        // Arrange - one old name read instead, two ignored (one beside an empty new name, which is present too)
        var environment = Env(
            ("WAM_PAUSE_HOTKEY", "none"),
            ("WAM_DENY_PROCESSES", "keepass"), ("HELMSMAN_DENY_PROCESSES", "notepad"),
            ("WAM_LAYOUTS_PATH", SecretPath), ("HELMSMAN_LAYOUTS_PATH", ""));

        // Act
        var old = SettingNames.FindOld(environment);

        // Assert
        old.ReadInstead.Should().Equal("WAM_PAUSE_HOTKEY");
        old.Ignored.Should().Equal("WAM_DENY_PROCESSES", "WAM_LAYOUTS_PATH");
        old.Warnings.Should().HaveCount(2);
        old.Warnings[0].Should().StartWith("WAM_PAUSE_HOTKEY is the name").And.Contain("HELMSMAN_PAUSE_HOTKEY, which is not set")
            .And.Contain("Rename it").And.NotContain("DENY");
        old.Warnings[1].Should().StartWith("WAM_DENY_PROCESSES and WAM_LAYOUTS_PATH are ignored")
            .And.Contain("HELMSMAN_DENY_PROCESSES and HELMSMAN_LAYOUTS_PATH are set too")
            .And.Contain("Remove the old names");
    }

    [Fact]
    public void TheWarnings_NameNoValue()
    {
        // Arrange - every old name set, half of them beside their new name, with values that must not leak
        var variables = SettingNames.All
            .SelectMany((name, i) => i % 2 == 0
                ? new[] { (SettingNames.OldNameOf(name), SecretPath + i) }
                : new[] { (SettingNames.OldNameOf(name), SecretPath + i), (name, "new-value-" + i) })
            .ToArray();

        // Act
        var old = SettingNames.FindOld(Env(variables));

        // Assert
        old.ReadInstead.Should().HaveCount(7);
        old.Ignored.Should().HaveCount(7);
        old.Warnings.Should().HaveCount(2).And.OnlyContain(w => !w.Contains(SecretPath) && !w.Contains("new-value") && !w.Contains("private"));
        string.Join(" ", old.Warnings).Should().ContainAll(SettingNames.All.Select(SettingNames.OldNameOf));
    }

    [Fact]
    public void AnEmptyOldName_IsStillInUse()
    {
        // Present, so it is read (as empty: the default) in place of a new name that is not set, and the user should know
        SettingNames.FindOld(Env(("WAM_TOOLSETS", ""))).ReadInstead.Should().Equal("WAM_TOOLSETS");
    }
}
