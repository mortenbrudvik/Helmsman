using System.Reflection;

namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// The Win32 values the allowlist for the agent's host keeps a copy of (<c>HostInputRules.Win32</c>): Services may not depend
/// on Native (the layering test's rule), so it cannot read NativeEnums itself. Every copy must have a NativeEnums constant of
/// the same name with the same value; a copy without one fails. Both types are internal, so they are read by reflection.
/// </summary>
public class NativeEnumsMirrorTests
{
    private const BindingFlags Constants = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void EveryCopyInHostInputRules_IsTheNativeEnumsConstantOfTheSameName()
    {
        // Arrange
        var assembly = typeof(HostInputRules).Assembly;
        var native = assembly.GetType("WinDesktopMcp.Native.NativeEnums")!;
        var copies = typeof(HostInputRules).GetNestedType("Win32", BindingFlags.NonPublic | BindingFlags.Public)!
            .GetFields(Constants).Where(f => f.IsLiteral).ToList();

        // Assert
        copies.Should().NotBeEmpty();
        foreach (var copy in copies)
        {
            var original = native.GetField(copy.Name, Constants);
            original.Should().NotBeNull($"{copy.Name} is a copy of a NativeEnums constant of that name");
            Convert.ToUInt64(copy.GetValue(null)).Should().Be(Convert.ToUInt64(original!.GetValue(null)), copy.Name);
        }
    }
}
