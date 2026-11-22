using System.Text.RegularExpressions;

namespace Helmsman.Tests.Unit;

/// <summary>
/// A v* tag makes build.yml publish a GitHub release, and the plugin's users get that release. The
/// test suite (test.yml) does not run for a tag on its own, so build.yml calls it for the tag and its
/// release job needs that job: a tag whose tests fail must not be released. The release carries
/// SHA256SUMS.txt and a build provenance attestation of the zip, so a user can check a download.
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

    [Fact]
    public void ReleaseJob_WritesTheChecksumFile_InTheFormatSha256sumReads()
    {
        // Arrange
        var step = Step(Job(ReadWorkflow("build.yml"), "release"), "Compute checksums");
        var script = string.Join("\n", step);

        // Assert - properties of the script, not its exact lines, so renaming a variable keeps this passing
        step.Should().Contain(line => line.Trim() == "shell: pwsh", "the step is PowerShell on every runner image");
        script.Should().MatchRegex(@"Get-FileHash\b[^\n]*-Algorithm\s+SHA256\b", "each file is hashed with SHA-256");
        script.Should().MatchRegex(@"\.ToLower(Invariant)?\(\)",
            "checksum files carry lower-case hex; Get-FileHash returns upper case");
        script.Should().MatchRegex(@"""\$\w+ {2}\$", "sha256sum -c reads '<hex>  <name>', with two spaces");
        script.Should().MatchRegex(@"Get-ChildItem\b[^\n]*-Recurse",
            "the files in the zip are listed too, so an extracted helmsman.exe can be checked");
        script.Should().MatchRegex(@"\[(System\.)?IO\.File\]::WriteAllText\([^\n]*SHA256SUMS\.txt",
            "the file is written as the script builds it, with no encoding or line ending added");
        script.Should().MatchRegex(@"-join\s+""`n""", "the lines are joined with LF").And.NotContain("`r`n");
        script.Should().MatchRegex(@"UTF8Encoding\]::new\(\s*\$false\s*\)|UTF8Encoding\s+\$false", "UTF-8 without a BOM")
            .And.NotMatchRegex(@"UTF8Encoding\]::new\(\s*\$true|Encoding\]::UTF8\b", "those encodings write a BOM");
        script.Should().NotContainAny(new[] { "Out-File", "Set-Content", "Add-Content" },
            "PowerShell's own file writers add a BOM or CRLF line endings on some versions, which sha256sum -c cannot read");
        script.Should().NotMatchRegex(@">\s*['""]?[^\s'""]*SHA256SUMS", "a redirect is PowerShell's file writer too");
        script.Should().MatchRegex(@"if\s*\([^\n]*helmsman\\?\.exe[^\n]*\)\s*\{\s*throw\b",
            "a release folder without helmsman.exe fails the step instead of publishing checksums without the exe");
    }

    [Fact]
    public void ReleaseJob_UploadsTheChecksumFile_InTheCallThatCreatesTheRelease()
    {
        // Arrange
        var release = Job(ReadWorkflow("build.yml"), "release");
        var script = Step(release, "Create Release").Select(line => line.Trim()).ToList();

        // Assert
        StepIndex(release, "Compute checksums").Should().BeLessThan(StepIndex(release, "Create Release"));
        script.Should().Contain("$sums = 'SHA256SUMS.txt'");
        script.Should().ContainSingle(line => line.StartsWith("gh release create ", StringComparison.Ordinal))
            .Which.Should().StartWith("gh release create $tag $zip $sums ",
                "the checksums go up with the zip in the call that creates the release, so no release exists without them")
            .And.Contain("--verify-tag")
            .And.Contain("--generate-notes --notes-file $notes", "the checksums are put before the generated release notes");
        script.Should().ContainSingle(line => line.StartsWith("gh release upload ", StringComparison.Ordinal))
            .Which.Should().Be("gh release upload $tag $zip $sums --clobber",
                "a re-run replaces the zip and its checksums together, since a zip made again has other bytes");
    }

    [Fact]
    public void ReleaseJob_AttestsTheZipAndTheExe_BeforeCreatingTheRelease()
    {
        // Arrange
        var release = Job(ReadWorkflow("build.yml"), "release");
        var attest = Step(release, "Attest build provenance").Select(line => line.Trim()).ToList();

        // Assert
        attest.Should().Contain("uses: actions/attest@v4", "GitHub's own attestation action, on its major version tag");
        attest.Should().Contain("helmsman-${{ github.ref_name }}-win-x64.zip").And.Contain("release/helmsman.exe");
        StepIndex(release, "Attest build provenance").Should().BeLessThan(StepIndex(release, "Create Release"),
            "a failed attestation must leave no release behind");
    }

    [Fact]
    public void ReleaseJob_AloneGetsTheWritePermissions_AndOnlyThoseItsStepsNeed()
    {
        // Arrange
        var workflow = ReadWorkflow("build.yml");
        var release = Job(workflow, "release");

        // Assert
        Block(workflow, "permissions", indent: 0).Select(line => line.Trim()).Should().Equal("contents: read");
        Block(release, "permissions", indent: 4).Select(line => line.Trim()).Should().BeEquivalentTo(
            new[] { "contents: write", "id-token: write", "attestations: write" },
            "creating the release needs contents, and the attestation needs an OIDC token and to store the attestation");
        workflow.Count(line => line.Trim().StartsWith("id-token:", StringComparison.Ordinal)).Should().Be(1,
            "only the release job may mint an OIDC token");
        workflow.Count(line => line.Trim().StartsWith("permissions:", StringComparison.Ordinal)).Should().Be(2,
            "the workflow's read-only default and the release job's block; no other job asks for more");
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

    /// <summary>The lines of a job's step: its "- name:" line and the lines below it, up to the next step.</summary>
    private static List<string> Step(List<string> job, string name)
    {
        var steps = Block(job, "steps", indent: 4);
        var start = StepIndex(job, name);
        start.Should().BeGreaterThanOrEqualTo(0, "the job has a step named '{0}'", name);
        return steps.Skip(start).Take(1).Concat(steps.Skip(start + 1).TakeWhile(line => Indentation(line) > 6)).ToList();
    }

    /// <summary>Where a job's step is among the job's step lines; -1 when the job has no such step.</summary>
    private static int StepIndex(List<string> job, string name) =>
        Block(job, "steps", indent: 4).FindIndex(line => line.TrimEnd() == "      - name: " + name);

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
