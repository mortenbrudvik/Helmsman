using System.Text.Json;
using ModelContextProtocol.Protocol;
using Helmsman.Infrastructure;
using Helmsman.Tools;

namespace Helmsman.Tests.Contract;

/// <summary>
/// Contract: find_windows' processName and a window selector's processName, from the client's JSON to the real window
/// service on a fake desktop. The name is read as the target policy reads it; a path, which the policy would read as its
/// file name, is refused with InvalidArguments instead of matching nothing (the reported case:
/// processName "C:\Windows\System32\notepad.exe" returned an empty list).
/// </summary>
public class ProcessNameArgumentContractTests
{
    private readonly FakeWindowDesktop _desktop = new();
    private readonly nint _notepad;

    public ProcessNameArgumentContractTests()
    {
        _desktop.Add("Inbox - Outlook", processName: "OUTLOOK");
        _notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData("C:/Windows/System32/notepad.exe")]
    public async Task FindWindows_WithAPathAsProcessName_IsInvalidArguments_ThatSaysWhichNameToPass(string path)
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?> { ["processName"] = path });

        result.IsError.Should().BeTrue("a path matched nothing and said nothing; now it is refused");
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain("processName").And.Contain("not a path").And.Contain("'notepad'");
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("NOTEPAD.EXE")]
    [InlineData(" notepad.exe ")]
    [InlineData("\"notepad.exe\"")]
    public async Task FindWindows_WithTheProcessNameInAnyFormThePolicyReads_FindsTheWindow(string processName)
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?> { ["processName"] = processName });

        result.IsError.Should().NotBe(true);
        ParsePayload(result).EnumerateArray().Select(w => w.GetProperty("handle").GetInt64()).Should().Equal(_notepad.ToInt64());
    }

    [Fact]
    public async Task AWindowSelectorWithAPathAsProcessName_IsInvalidArguments()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = @"C:\Windows\System32\notepad.exe" }
        });

        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain("window.processName").And.Contain("not a path").And.Contain("'notepad'");
    }

    private Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .AddToolErrorReporting()
                .WithServerTools(new WindowFinderTools(_desktop.CreateService(), securityProbe: Permissive.SecurityProbe)));

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
