using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Generation;

namespace EFCoreSqlPreview.Core.Tests.Execution;

/// <summary>
/// Tests for <see cref="DiagnosticRemapper"/>: parsing MSBuild diagnostic lines and pointing the ones from the
/// generated worker back at the user's selection.
/// </summary>
public class DiagnosticRemapperTests
{
    private const string GeneratedPath = @"C:\scratch\Queries-abc123\worker.cs";

    /// <summary>Free-variable declaration on generated line 35, query body on lines 37 to 40.</summary>
    private static GeneratedWorker Worker() => new(
        SourcePath: GeneratedPath,
        SourceText: string.Empty,
        LineMap: new Dictionary<int, int> { [35] = 4321, [36] = 4400, [37] = 4500, [38] = 4500, [39] = 4500, [40] = 4500 },
        Warnings: Array.Empty<string>());

    [Fact]
    public void A_generated_file_error_is_mapped_back_to_the_selection_offset()
    {
        const string output = @"C:\scratch\Queries-abc123\worker.cs(35,16): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\scratch\Queries-abc123\worker.csproj]";

        var diagnostic = DiagnosticRemapper.Parse(output, Worker()).ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("CS0029");
        diagnostic.Severity.ShouldBe("error");
        diagnostic.IsError.ShouldBeTrue();
        diagnostic.Message.ShouldBe("Cannot implicitly convert type 'string' to 'int'");
        diagnostic.Line.ShouldBe(35);
        diagnostic.Column.ShouldBe(16);
        diagnostic.IsInGeneratedFile.ShouldBeTrue();
        diagnostic.UserSpan.ShouldNotBeNull();
        diagnostic.UserSpan!.Value.Start.ShouldBe(4321);
        diagnostic.UserSpan.Value.Length.ShouldBe(0);
    }

    [Fact]
    public void An_error_in_the_users_own_project_keeps_its_clickable_path_and_is_not_remapped()
    {
        const string output = @"C:\repo\App\ProductService.cs(21,13): error CS1061: 'Product' does not contain a definition for 'Nmae' [C:\repo\App\App.csproj]";

        var diagnostic = DiagnosticRemapper.Parse(output, Worker()).ShouldHaveSingleItem();

        diagnostic.FilePath.ShouldBe(@"C:\repo\App\ProductService.cs");
        diagnostic.IsInGeneratedFile.ShouldBeFalse();
        diagnostic.UserSpan.ShouldBeNull();
        diagnostic.Line.ShouldBe(21);
    }

    [Fact]
    public void An_error_in_the_generated_preamble_stays_unmapped()
    {
        // Line 5 is a #:property directive line, long before anything the line map covers.
        const string output = @"C:\scratch\Queries-abc123\worker.cs(5,1): error CS1519: Invalid token in class declaration";

        var diagnostic = DiagnosticRemapper.Parse(output, Worker()).ShouldHaveSingleItem();

        diagnostic.IsInGeneratedFile.ShouldBeTrue();
        diagnostic.UserSpan.ShouldBeNull();
    }

    [Fact]
    public void An_error_on_a_continuation_line_falls_back_to_the_nearest_preceding_mapped_line()
    {
        // Line 42 is inside the harness types below the query; the nearest mapped line is 40.
        DiagnosticRemapper.MapToUserSpan(Worker(), 42)!.Value.Start.ShouldBe(4500);
        DiagnosticRemapper.MapToUserSpan(Worker(), 36)!.Value.Start.ShouldBe(4400);
    }

    [Fact]
    public void Mapping_is_refused_without_a_worker_a_line_or_a_map()
    {
        DiagnosticRemapper.MapToUserSpan(null!, 35).ShouldBeNull();
        DiagnosticRemapper.MapToUserSpan(Worker(), 0).ShouldBeNull();
        DiagnosticRemapper.MapToUserSpan(Worker(), -3).ShouldBeNull();
        DiagnosticRemapper.MapToUserSpan(
            new GeneratedWorker(GeneratedPath, string.Empty, new Dictionary<int, int>(), Array.Empty<string>()),
            35).ShouldBeNull();
    }

    [Fact]
    public void The_generated_file_is_recognised_by_file_name_so_a_relative_path_still_maps()
    {
        var diagnostic = DiagnosticRemapper.Parse("worker.cs(35,16): error CS0029: nope", Worker()).ShouldHaveSingleItem();

        diagnostic.IsInGeneratedFile.ShouldBeTrue();
        diagnostic.UserSpan!.Value.Start.ShouldBe(4321);
    }

    [Fact]
    public void Warnings_are_parsed_alongside_errors_and_kept_apart()
    {
        const string output = """
C:\repo\App\A.cs(1,1): warning CS8618: Non-nullable field must contain a value.
C:\repo\App\B.cs(2,2): error CS0103: The name 'x' does not exist.
""";

        var diagnostics = DiagnosticRemapper.Parse(output, Worker());

        diagnostics.Count.ShouldBe(2);
        diagnostics[0].Severity.ShouldBe("warning");
        diagnostics[0].IsError.ShouldBeFalse();
        DiagnosticRemapper.Errors(diagnostics).ShouldHaveSingleItem().Id.ShouldBe("CS0103");
    }

    [Fact]
    public void An_identical_diagnostic_printed_twice_is_reported_once()
    {
        const string line = @"C:\scratch\Queries-abc123\worker.cs(35,16): error CS0029: nope";

        DiagnosticRemapper.Parse(line + "\n" + line + "\n" + line, Worker()).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_diagnostic_with_no_position_is_still_parsed()
    {
        var diagnostic = DiagnosticRemapper.Parse(
            @"C:\scratch\worker.csproj : error NU1101: Unable to find package Foo",
            worker: null).ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("NU1101");
        diagnostic.FilePath.ShouldBe(@"C:\scratch\worker.csproj");
        diagnostic.Line.ShouldBe(0);
        diagnostic.Column.ShouldBe(0);
        diagnostic.IsInGeneratedFile.ShouldBeFalse();
    }

    [Fact]
    public void An_MSBuild_level_diagnostic_is_parsed_with_MSBUILD_standing_in_for_the_file()
    {
        var diagnostic = DiagnosticRemapper.Parse("MSBUILD : error MSB1009: Project file does not exist.", worker: null)
            .ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("MSB1009");
        diagnostic.Message.ShouldBe("Project file does not exist.");
    }

    [Fact]
    public void A_diagnostic_with_no_prefix_at_all_is_not_recognised()
    {
        // Known limitation, and a deliberate one: the pattern needs a `<something> : error XXnnnn :` shape so a
        // sentence merely containing the word "error" is not mistaken for a diagnostic. MSBuild and NuGet
        // always emit a file or a tool name, so this shape does not occur in practice.
        DiagnosticRemapper.Parse("error NU1101: Unable to find package Foo", worker: null).ShouldBeEmpty();
    }

    [Fact]
    public void CRLF_output_is_normalised_before_matching()
    {
        var output = "C:\\repo\\A.cs(1,1): error CS0103: nope\r\nC:\\repo\\B.cs(2,2): error CS0104: also nope\r\n";

        DiagnosticRemapper.Parse(output, worker: null).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nothing diagnostic-shaped here")]
    public void Output_with_no_diagnostics_produces_none(string? output)
        => DiagnosticRemapper.Parse(output, Worker()).ShouldBeEmpty();

    [Fact]
    public void Summarize_replaces_the_generated_path_with_a_pointer_at_the_selection()
    {
        const string output = """
C:\scratch\Queries-abc123\worker.cs(35,16): error CS0029: Cannot implicitly convert type 'string' to 'int'
C:\repo\App\A.cs(1,1): error CS0103: The name 'x' does not exist.
""";

        var summary = DiagnosticRemapper.Summarize(DiagnosticRemapper.Parse(output, Worker()));

        summary.ShouldContain("error CS0029: Cannot implicitly convert type 'string' to 'int' (in the selected query)");
        summary.ShouldNotContain(@"C:\scratch\Queries-abc123\worker.cs");
        summary.ShouldContain(@"C:\repo\App\A.cs(1,1): error CS0103");
    }

    [Fact]
    public void Summarize_truncates_a_long_list()
    {
        var output = string.Join(
            "\n",
            Enumerable.Range(1, 30).Select(i => $@"C:\repo\App\A.cs({i},1): error CS010{i % 10}: message {i}"));

        var summary = DiagnosticRemapper.Summarize(DiagnosticRemapper.Parse(output, worker: null), maxCount: 5);

        summary.Split('\n').Length.ShouldBe(6);
        summary.ShouldEndWith("... and 25 more.");
    }

    [Fact]
    public void Summarize_of_nothing_is_empty()
        => DiagnosticRemapper.Summarize(Array.Empty<RemappedDiagnostic>()).ShouldBeEmpty();
}
