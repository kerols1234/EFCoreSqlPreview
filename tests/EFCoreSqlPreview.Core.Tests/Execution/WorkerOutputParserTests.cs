using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Projects;

namespace EFCoreSqlPreview.Core.Tests.Execution;

/// <summary>
/// Tests for <see cref="WorkerOutputParser"/>: finding the payload amid build noise and classifying a run that
/// produced none.
/// </summary>
public class WorkerOutputParserTests
{
    private const string Begin = PreviewResponse.PayloadBeginSentinel;
    private const string End = PreviewResponse.PayloadEndSentinel;

    private const string SuccessPayload = """
{"schemaVersion":1,"success":true,"provider":"SqlServer","efCoreVersion":"10.0.10","contextType":"SampleShop.ShopDbContext",
"activationStrategy":"DesignTimeFactory","captureMode":"SyntheticRow","connectionString":"Server=.;Database=EFCoreSqlPreview",
"commands":[{"sql":"SELECT [p].[Id]\nFROM [Products] AS [p]\nWHERE [p].[Price] > @min","parameters":[
{"name":"@min","dbType":"Decimal","clrType":"System.Decimal","value":"100","isNull":false,"size":0,"direction":"Input"}]}],
"result":{"isAsync":true,"shape":"List","elementType":"ProductDto","elementKind":"Dto","declaredResultType":"System.Collections.Generic.List`1[ProductDto]"},
"warnings":["a note"]}
""";

    [Fact]
    public void A_bare_payload_is_extracted_and_deserialized()
    {
        var response = WorkerOutputParser.Parse(Begin + SuccessPayload + End, string.Empty, 0);

        response.Success.ShouldBeTrue();
        response.SchemaVersion.ShouldBe(PreviewResponse.CurrentSchemaVersion);
        response.Provider.ShouldBe(EfProvider.SqlServer);
        response.EfCoreVersion.ShouldBe("10.0.10");
        response.ContextType.ShouldBe("SampleShop.ShopDbContext");
        response.ActivationStrategy.ShouldBe("DesignTimeFactory");
        response.CaptureMode.ShouldBe("SyntheticRow");
        response.ErrorKind.ShouldBe(PreviewErrorKind.None);
        response.Warnings.ShouldHaveSingleItem().ShouldBe("a note");

        var command = response.Commands.ShouldHaveSingleItem();
        command.Sql.ShouldContain("FROM [Products] AS [p]");

        var parameter = command.Parameters.ShouldHaveSingleItem();
        parameter.Name.ShouldBe("@min");
        parameter.DbType.ShouldBe("Decimal");
        parameter.ClrType.ShouldBe("System.Decimal");
        parameter.Value.ShouldBe("100");
        parameter.IsNull.ShouldBeFalse();
        parameter.Direction.ShouldBe("Input");

        response.Result.ShouldNotBeNull();
        response.Result!.IsAsync.ShouldBeTrue();
        response.Result.Shape.ShouldBe("List");
        response.Result.ElementType.ShouldBe("ProductDto");
        response.Result.ElementKind.ShouldBe("Dto");
    }

    [Fact]
    public void A_payload_buried_in_build_noise_is_still_found()
    {
        var noisy = string.Join(
            "\n",
            Enumerable.Range(0, 98).Select(i => $@"C:\proj\File{i}.cs(3,9): warning CS8618: Non-nullable field must contain a value."))
            + "\n  Determining projects to restore...\n  App -> C:\\proj\\bin\\App.dll\n"
            + Begin + SuccessPayload + End + "\n";

        WorkerOutputParser.TryExtractPayload(noisy, out var payload).ShouldBeTrue();
        payload.ShouldStartWith("{");
        WorkerOutputParser.ParsePayload(payload).Success.ShouldBeTrue();
    }

    [Fact]
    public void The_last_begin_sentinel_wins_so_a_quoted_one_in_a_compiler_error_is_ignored()
    {
        var output = $"C:\\s\\worker.cs(57,29): error CS1002: ; expected in `const string Begin = \"{Begin}\";`\n"
            + Begin + SuccessPayload + End;

        WorkerOutputParser.TryExtractPayload(output, out var payload).ShouldBeTrue();
        payload.ShouldStartWith("{");
        payload.ShouldEndWith("}");
    }

    [Fact]
    public void A_payload_is_returned_even_when_the_worker_reports_failure()
    {
        const string failure = """
{"schemaVersion":1,"success":false,"provider":"SqlServer","errorKind":"NotTranslatable",
"error":"The LINQ expression could not be translated.","commands":[],"warnings":[]}
""";

        var response = WorkerOutputParser.Parse(Begin + failure + End, string.Empty, 0);

        response.Success.ShouldBeFalse();
        response.ErrorKind.ShouldBe(PreviewErrorKind.NotTranslatable);
        response.Error.ShouldBe("The LINQ expression could not be translated.");
        response.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void Several_commands_are_read_in_order()
    {
        const string split = """
{"success":true,"provider":"SqlServer","commands":[
{"sql":"SELECT [o].[Id] FROM [Orders] AS [o]","parameters":[]},
{"sql":"SELECT [l].[Id] FROM [OrderLines] AS [l]","parameters":[{"name":"@id","dbType":"Int32","clrType":"System.Int32","value":"7","isNull":false,"size":0,"direction":"Input"}]}],
"warnings":[]}
""";

        var response = WorkerOutputParser.Parse(Begin + split + End, string.Empty, 0);

        response.Commands.Count.ShouldBe(2);
        response.Commands[0].Sql.ShouldContain("[Orders]");
        response.Commands[0].Parameters.ShouldBeEmpty();
        response.Commands[1].Sql.ShouldContain("[OrderLines]");
        response.Commands[1].Parameters.ShouldHaveSingleItem().Value.ShouldBe("7");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no sentinels at all")]
    public void Missing_sentinels_yield_no_payload(string? output)
        => WorkerOutputParser.TryExtractPayload(output, out _).ShouldBeFalse();

    [Fact]
    public void A_begin_with_no_end_yields_no_payload()
        => WorkerOutputParser.TryExtractPayload(Begin + "{\"success\":true}", out _).ShouldBeFalse();

    [Fact]
    public void An_end_before_the_begin_yields_no_payload()
        => WorkerOutputParser.TryExtractPayload(End + "{}" + Begin, out _).ShouldBeFalse();

    [Fact]
    public void An_empty_payload_between_the_sentinels_yields_no_payload()
        => WorkerOutputParser.TryExtractPayload(Begin + "   \n  " + End, out _).ShouldBeFalse();

    [Fact]
    public void Malformed_json_between_the_sentinels_is_reported_as_NoPayload()
    {
        var response = WorkerOutputParser.Parse(Begin + "{\"success\": tru" + End, string.Empty, 0);

        response.Success.ShouldBeFalse();
        response.ErrorKind.ShouldBe(PreviewErrorKind.NoPayload);
        response.Error!.ShouldStartWith("The worker payload could not be read:");
    }

    [Fact]
    public void A_json_null_payload_is_reported_as_NoPayload()
        => WorkerOutputParser.ParsePayload("null").ErrorKind.ShouldBe(PreviewErrorKind.NoPayload);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_payload_is_reported_as_NoPayload(string payload)
    {
        var response = WorkerOutputParser.ParsePayload(payload);

        response.ErrorKind.ShouldBe(PreviewErrorKind.NoPayload);
        response.Error.ShouldBe("The worker produced an empty payload.");
    }

    [Fact]
    public void A_run_with_no_output_at_all_is_reported_as_NoPayload_with_the_exit_code()
    {
        var response = WorkerOutputParser.Parse(string.Empty, string.Empty, 42);

        response.ErrorKind.ShouldBe(PreviewErrorKind.NoPayload);
        response.Error!.ShouldContain("exited with code 42");
    }

    [Fact]
    public void A_compiler_error_is_classified_as_CompileError_and_summarized_to_the_diagnostic_lines()
    {
        const string output = """
  Determining projects to restore...
C:\scratch\worker.cs(35,16): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\scratch\worker.csproj]
C:\scratch\worker.cs(35,16): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\scratch\worker.csproj]
  0 Warning(s)
""";

        var response = WorkerOutputParser.Parse(output, "The build failed.", 1);

        response.ErrorKind.ShouldBe(PreviewErrorKind.CompileError);
        response.Error!.ShouldContain("CS0029");
        response.Error!.ShouldNotContain("Determining projects to restore");

        // The summary deduplicates the repeated diagnostic MSBuild prints once per target.
        response.Error!.Split('\n').Length.ShouldBe(1);
    }

    [Fact]
    public void A_restore_failure_is_classified_before_the_compile_failure_it_also_looks_like()
    {
        const string output = """
C:\scratch\worker.csproj : error NU1102: Unable to find package Pomelo.EntityFrameworkCore.MySql with version (>= 10.0.0)
C:\scratch\worker.cs(1,1): error CS0006: Metadata file not found
""";

        var response = WorkerOutputParser.Parse(output, "The build failed.", 1);

        response.ErrorKind.ShouldBe(PreviewErrorKind.ProviderVersionMismatch);
        response.Error!.ShouldContain("no build matching the project's EF Core major version");
        response.Error!.ShouldContain("NU1102");
    }

    [Fact]
    public void A_timeout_is_reported_even_when_the_output_looks_like_a_build_failure()
    {
        var response = WorkerOutputParser.Parse("error CS1002: ; expected", string.Empty, -1, timedOut: true);

        response.ErrorKind.ShouldBe(PreviewErrorKind.Timeout);
        response.Error!.ShouldContain("did not finish in time");
    }

    [Fact]
    public void A_cancellation_is_reported_as_a_Timeout_kind_with_its_own_message()
    {
        var response = WorkerOutputParser.Parse(string.Empty, string.Empty, -1, canceled: true);

        response.ErrorKind.ShouldBe(PreviewErrorKind.Timeout);
        response.Error.ShouldBe("The preview was cancelled.");
    }

    [Fact]
    public void A_payload_beats_a_timeout_because_a_result_already_exists()
    {
        var response = WorkerOutputParser.Parse(Begin + SuccessPayload + End, string.Empty, -1, timedOut: true);

        response.Success.ShouldBeTrue();
    }

    [Theory]
    [InlineData("C:\\a.cs(1,1): error CS0029: nope", true)]
    [InlineData("proj.csproj : error NU1102: nope", true)]
    [InlineData("C:\\a.cs(1,1): warning CS8618: nope", false)]
    [InlineData("", false)]
    public void LooksLikeCompileFailure_recognises_diagnostic_lines(string output, bool expected)
        => WorkerOutputParser.LooksLikeCompileFailure(output).ShouldBe(expected);

    [Theory]
    [InlineData("proj.csproj : error NU1102: nope", true)]
    [InlineData("proj.csproj : error nu1107: nope", true)]
    [InlineData("C:\\a.cs(1,1): error CS0029: nope", false)]
    public void LooksLikeRestoreFailure_recognises_NuGet_errors(string output, bool expected)
        => WorkerOutputParser.LooksLikeRestoreFailure(output).ShouldBe(expected);

    [Fact]
    public void SummarizeBuildFailure_falls_back_to_the_tail_when_no_diagnostic_is_recognised()
        => WorkerOutputParser.SummarizeBuildFailure("something went wrong").ShouldBe("something went wrong");

    [Fact]
    public void Context_candidates_are_read_out_of_the_discovery_warnings()
    {
        var response = new PreviewResponse
        {
            Warnings = new[]
            {
                "EFSP-CONTEXT-CANDIDATE: SampleShop.ShopDbContext",
                "an unrelated warning",
                "EFSP-CONTEXT-CANDIDATE:  SampleShop.DiShopDbContext ",
            },
        };

        WorkerOutputParser.ReadContextCandidates(response)
            .ShouldBe(new[] { "SampleShop.ShopDbContext", "SampleShop.DiShopDbContext" });
    }

    [Fact]
    public void Reading_context_candidates_from_nothing_is_empty()
        => WorkerOutputParser.ReadContextCandidates(null!).ShouldBeEmpty();

    [Fact]
    public void PreviewRunner_exposes_the_same_extraction_helpers()
    {
        PreviewRunner.TryExtractPayload(Begin + SuccessPayload + End, out var payload).ShouldBeTrue();
        PreviewRunner.ParsePayload(payload).Success.ShouldBeTrue();
    }
}
