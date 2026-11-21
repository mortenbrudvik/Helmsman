using System.Text.RegularExpressions;

namespace WinDesktopMcp.Tests.Unit;

/// <summary>
/// A v* tag makes build.yml publish a GitHub release, and the plugin's users get that release. The
/// test suite (test.yml) does not run for a tag on its own, so build.yml calls it for the tag and its
/// release job needs that job: a tag whose tests fail must not be released.
/// These tests only read files in the repository. The workflows are read as text, by the two-space
/// indentation GitHub's YAML files use, since the test project has no YAML parser.
/// </summary>
public class ReleaseWorkflowTests
{
    private const string TagCondition = "startsWith(github.ref, 'refs/tags/v')";

    [Fact]
    public void BuildWorkflow_CallsTheTestWorkflowForAVersionTag()
    {
        // Arrange
        var test = Job(ReadWorkflow("build.yml"), "release-tests");

        // Assert
        Value(test, "uses").Should().Be("./.github/workflows/test.yml");
        Value(test, "if").Should().Be(TagCondition,
            "test.yml runs by itself on pushes and pull requests to main, so the build workflow runs it only for a tag");
    }

    [Fact]
    public void ReleaseJob_NeedsTheBuildAndTheReleaseTestsJob()
    {
        // Arrange
        var release = Job(ReadWorkflow("build.yml"), "release");

        // Assert
        Needs(release).Should().Contain(["build", "release-tests"], "a release must wait for the tests of the tagged commit");
        Value(release, "if").Should().Be(TagCondition,
            "an if with a status function such as always() or failure() would let the release run after the tests failed");
    }

    [Fact]
    public void TestWorkflow_CanBeCalled_AndAsksForNoMoreThanReadAccess()
    {
        // Arrange - a called workflow cannot be granted more than its caller, build.yml, has
        var workflow = ReadWorkflow("test.yml");

        // Assert
        Block(workflow, "on", indent: 0).Should().Contain(line => line.TrimEnd() == "  workflow_call:");
        Block(workflow, "permissions", indent: 0).Select(line => line.Trim()).Should().Equal("contents: read");
    }

    [Theory]
    [InlineData("    needs: build", new[] { "build" })]
    [InlineData("    needs: [build, release-tests]", new[] { "build", "release-tests" })]
    [InlineData("    needs:\n      - build\n      - 'release-tests'", new[] { "build", "release-tests" })]
    public void Needs_ReadsTheScalarFlowAndBlockForms(string text, string[] expected)
    {
        Needs(text.Split('\n').ToList()).Should().Equal(expected);
    }

    private static List<string> ReadWorkflow(string name) =>
        File.ReadAllLines(RepositoryFiles.PathOf(".github", "workflows", name))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToList();

    private static List<string> Job(List<string> workflow, string name) =>
        Block(Block(workflow, "jobs", indent: 0), name, indent: 2);

    /// <summary>The lines below <paramref name="key"/> at the given indentation, up to the next key at that indentation.</summary>
    private static List<string> Block(List<string> lines, string key, int indent)
    {
        var prefix = new string(' ', indent) + key + ":";
        var start = lines.FindIndex(line => line.TrimEnd() == prefix);
        start.Should().BeGreaterThanOrEqualTo(0, "the workflow has '{0}'", prefix.Trim());
        return lines.Skip(start + 1).TakeWhile(line => Indentation(line) > indent).ToList();
    }

    private static string? Value(List<string> job, string key)
    {
        var match = job.Select(line => Regex.Match(line, $@"^    {Regex.Escape(key)}:\s*(?<value>.+?)\s*$"))
            .FirstOrDefault(m => m.Success);
        return match?.Groups["value"].Value;
    }

    private static List<string> Needs(List<string> job)
    {
        var value = Value(job, "needs");
        IEnumerable<string> names = value switch
        {
            null => Block(job, "needs", indent: 4).Select(line => line.Trim().TrimStart('-')),
            _ when value.StartsWith('[') => value.Trim('[', ']').Split(','),
            _ => [value],
        };
        return names.Select(name => name.Trim().Trim('\'', '"')).Where(name => name.Length > 0).ToList();
    }

    private static int Indentation(string line) => line.Length - line.TrimStart(' ').Length;
}
