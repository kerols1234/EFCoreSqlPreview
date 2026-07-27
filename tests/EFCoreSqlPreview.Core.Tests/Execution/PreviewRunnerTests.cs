using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Generation;
using EFCoreSqlPreview.Core.Projects;
using EFCoreSqlPreview.Core.Tests.Fakes;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.Execution;

/// <summary>
/// Tests for <see cref="PreviewRunner"/> with a fake process launcher, so the whole pipeline is exercised
/// without paying for a real <c>dotnet run</c>.
/// </summary>
public class PreviewRunnerTests
{
    private const string Document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public async Task<List<Product>> Go()
    {
        decimal minPrice = 100m;
        return await this.db.Products.Where(p => p.Price > minPrice).ToListAsync();
    }
}
""";

    private const string Begin = PreviewResponse.PayloadBeginSentinel;
    private const string End = PreviewResponse.PayloadEndSentinel;

    private const string SuccessPayload =
        """{"success":true,"provider":"SqlServer","contextType":"Demo.AppDbContext","commands":[{"sql":"SELECT [p].[Id] FROM [Products] AS [p] WHERE [p].[Price] > @minPrice","parameters":[{"name":"@minPrice","dbType":"Decimal","clrType":"System.Decimal","value":"100","isNull":false,"size":0,"direction":"Input"}]}],"result":{"isAsync":true,"shape":"List","elementType":"Product","elementKind":"Entity"},"warnings":[]}""";

    [Fact]
    public async Task A_successful_run_returns_the_workers_payload()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(process);

        result.Response.Success.ShouldBeTrue();
        result.Response.Commands.ShouldHaveSingleItem().Sql.ShouldContain("FROM [Products] AS [p]");
        result.Response.Result!.Shape.ShouldBe("List");
        result.ExitCode.ShouldBe(0);
        result.Analysis.CanRun.ShouldBeTrue();
        result.Project.ShouldNotBeNull();
        result.Worker.ShouldNotBeNull();
        result.Worker!.IsContextDiscovery.ShouldBeFalse();
    }

    [Fact]
    public async Task The_worker_is_launched_as_a_file_based_app_from_its_own_scratch_directory()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(process);

        var request = process.Requests.ShouldHaveSingleItem();
        request.FileName.ShouldBe("dotnet");
        request.Arguments.ShouldBe($"run --file \"{result.Worker!.SourcePath}\" --tl:off");
        request.WorkingDirectory.ShouldBe(System.IO.Path.GetDirectoryName(result.Worker.SourcePath));

        // --tl:off keeps the terminal logger's cursor sequences off the stream carrying the payload.
        request.Arguments.ShouldContain("--tl:off");
    }

    [Fact]
    public async Task The_child_environment_is_pinned_to_quiet_english_output()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        await RunAsync(process);

        var environment = process.Requests.ShouldHaveSingleItem().Environment;

        environment["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("1");
        environment["DOTNET_NOLOGO"].ShouldBe("1");
        environment["MSBUILDTERMINALLOGGER"].ShouldBe("off");

        // Localised diagnostics would not match the remapper's `error`/`warning` keywords.
        environment["DOTNET_CLI_UI_LANGUAGE"].ShouldBe("en");
    }

    [Fact]
    public async Task The_requests_timeout_is_passed_through_to_the_process()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        await RunAsync(process, request => request with { Timeout = TimeSpan.FromSeconds(7) });

        process.Requests.ShouldHaveSingleItem().Timeout.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_non_zero_exit_with_compiler_errors_is_reported_as_a_compile_error_and_remapped()
    {
        var process = new FakeProcessRunner().Enqueue(request => FakeProcessRunner.Failed(
            standardOutput: $"{ScratchFile(request)}(35,16): error CS0029: Cannot implicitly convert type 'string' to 'int'\n",
            standardError: "The build failed.",
            exitCode: 1));

        var result = await RunAsync(process);

        result.ExitCode.ShouldBe(1);
        result.Response.Success.ShouldBeFalse();
        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.CompileError);
        result.Response.Error!.ShouldContain("CS0029");

        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("CS0029");
        diagnostic.IsInGeneratedFile.ShouldBeTrue();
    }

    [Fact]
    public async Task A_restore_failure_is_reported_as_a_provider_version_mismatch()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Failed(
            "worker.csproj : error NU1102: Unable to find package Pomelo.EntityFrameworkCore.MySql with version (>= 10.0.0)",
            "The build failed.",
            1));

        var result = await RunAsync(process, request => request with { DialectOverride = EfProvider.MySql });

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.ProviderVersionMismatch);
        result.Worker!.Provider.ShouldBe(EfProvider.MySql);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_a_timeout()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.TimedOut());

        var result = await RunAsync(process);

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.Timeout);
        result.Response.Error!.ShouldContain("did not finish in time");
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task A_cancelled_run_is_reported_as_cancelled_and_the_token_reaches_the_launcher()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Canceled());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await RunAsync(process, cancellationToken: cancellation.Token);

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.Timeout);
        result.Response.Error.ShouldBe("The preview was cancelled.");
        process.ObservedCancellation.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_that_produced_nothing_is_reported_as_NoPayload()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Failed(string.Empty, string.Empty, 3));

        var result = await RunAsync(process);

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.NoPayload);
        result.Response.Error!.ShouldContain("exited with code 3");
    }

    [Fact]
    public async Task A_launcher_that_throws_is_reported_rather_than_propagated()
    {
        var process = new FakeProcessRunner { ThrowOnRun = new System.ComponentModel.Win32Exception("dotnet not found") };

        var result = await RunAsync(process);

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.Unknown);
        result.Response.Error!.ShouldContain("The worker could not be started");
        result.RawStandardError.ShouldContain("dotnet not found");
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Generator_warnings_are_merged_ahead_of_the_workers_own()
    {
        const string payload = """{"success":true,"provider":"SqlServer","commands":[{"sql":"SELECT 1","parameters":[]}],"warnings":["from the worker"]}""";
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + payload + End));

        // An undetectable provider makes the generator warn before the worker ever runs.
        var result = await RunAsync(process, project: Project() with { Provider = EfProvider.Unknown });

        result.Response.Warnings.ShouldContain(w => w.Contains("No EF Core provider package was found"));
        result.Response.Warnings.Last().ShouldBe("from the worker");
    }

    [Fact]
    public async Task An_out_of_scope_selection_is_refused_before_anything_is_launched()
    {
        const string document = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly AppDbContext db;

    public Task<int> Go() => this.db.Products.Where(p => p.IsActive).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
}
""";

        var process = new FakeProcessRunner();
        var result = await RunAsync(process, document: document, startsAt: "this.db.Products", endsAfter: "false))");

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.OutOfScope);
        result.Response.Error.ShouldNotBeNullOrWhiteSpace();
        result.Worker.ShouldBeNull();
        process.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_selection_that_is_not_a_query_is_refused_before_anything_is_launched()
    {
        var process = new FakeProcessRunner();
        var runner = Runner(process);

        var result = await runner.RunAsync(
            new PreviewRequest("class C { int x = 1; }", @"C:\repo\App\Svc.cs", new TextSpan(10, 8)),
            CancellationToken.None);

        result.Response.Success.ShouldBeFalse();
        result.Response.Error.ShouldNotBeNullOrWhiteSpace();
        result.Worker.ShouldBeNull();
        process.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_under_no_project_is_refused_before_anything_is_launched()
    {
        var process = new FakeProcessRunner();
        var runner = Runner(process, locator: new StubLocator(null));

        var result = await RunAsync(process, runner: runner);

        result.Response.Success.ShouldBeFalse();
        result.Response.Error!.ShouldContain("No .csproj was found above");
        result.Project.ShouldBeNull();
        process.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_path_override_bypasses_the_locator()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));
        var locator = new StubLocator(null);

        var result = await RunAsync(
            process,
            request => request with { ProjectPathOverride = @"C:\repo\Other\Other.csproj" },
            runner: Runner(process, locator: locator));

        result.Response.Success.ShouldBeTrue();
        locator.FindCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_dialect_override_reaches_the_generated_worker()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(process, request => request with { DialectOverride = EfProvider.Sqlite });

        result.Worker!.Provider.ShouldBe(EfProvider.Sqlite);
        result.Worker.SourceText.ShouldContain("b.UseSqlite(Neutral)");
        result.Worker.SourcePath.ShouldEndWith("worker-Sqlite.cs");
    }

    [Fact]
    public async Task A_free_variable_override_reaches_the_generated_worker()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(
            process,
            request => request with
            {
                FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["minPrice"] = "42.5m" },
            });

        result.Worker!.SourceText.ShouldContain("decimal minPrice = 42.5m;");
    }

    [Fact]
    public async Task A_discovery_run_that_finds_exactly_one_context_reruns_against_it()
    {
        const string discovery = """{"success":false,"provider":"SqlServer","commands":[],"warnings":["EFSP-CONTEXT-CANDIDATE: Demo.AppDbContext"],"error":"Pick a context.","errorKind":"ContextActivationFailed"}""";

        var process = new FakeProcessRunner()
            .Enqueue(FakeProcessRunner.Ok(Begin + discovery + End))
            .Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(process, document: UnresolvableContextDocument, startsAt: "this._uow.Context.Products", endsAfter: ".ToListAsync()");

        process.Requests.Count.ShouldBe(2);
        process.Requests[0].Arguments.ShouldContain("discover.cs");
        process.Requests[1].Arguments.ShouldContain("worker.cs");
        result.Response.Success.ShouldBeTrue();
        result.Worker!.ContextTypeName.ShouldBe("Demo.AppDbContext");
    }

    [Fact]
    public async Task A_discovery_run_that_finds_several_contexts_stops_and_reports_them()
    {
        const string discovery = """{"success":false,"provider":"SqlServer","commands":[],"warnings":["EFSP-CONTEXT-CANDIDATE: Demo.AppDbContext","EFSP-CONTEXT-CANDIDATE: Demo.AuditDbContext"],"error":"Pick a context.","errorKind":"ContextActivationFailed"}""";

        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + discovery + End));

        var result = await RunAsync(process, document: UnresolvableContextDocument, startsAt: "this._uow.Context.Products", endsAfter: ".ToListAsync()");

        process.Requests.Count.ShouldBe(1);
        result.ContextCandidates.ShouldBe(new[] { "Demo.AppDbContext", "Demo.AuditDbContext" });
        result.Response.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_activation_is_retried_through_a_derived_context()
    {
        const string activationFailure = """{"success":false,"provider":"SqlServer","contextType":"Demo.AppDbContext","commands":[],"warnings":[],"error":"No usable constructor.","errorKind":"ContextActivationFailed"}""";

        var process = new FakeProcessRunner()
            .Enqueue(FakeProcessRunner.Ok(Begin + activationFailure + End))
            .Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        var result = await RunAsync(process);

        process.Requests.Count.ShouldBe(2);
        process.Requests[1].Arguments.ShouldContain("worker-derived.cs");
        result.Response.Success.ShouldBeTrue();
        result.Worker!.SourceText.ShouldContain("PreviewDerived_AppDbContext");
    }

    [Fact]
    public async Task A_derived_context_retry_that_also_fails_keeps_the_original_result()
    {
        const string activationFailure = """{"success":false,"provider":"SqlServer","contextType":"Demo.AppDbContext","commands":[],"warnings":[],"error":"No usable constructor.","errorKind":"ContextActivationFailed"}""";

        var process = new FakeProcessRunner()
            .Enqueue(FakeProcessRunner.Ok(Begin + activationFailure + End))
            .Enqueue(FakeProcessRunner.Failed("worker-derived.cs(700,1): error CS7036: no argument given", "The build failed.", 1));

        var result = await RunAsync(process);

        process.Requests.Count.ShouldBe(2);
        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.ContextActivationFailed);
        result.Response.Error.ShouldBe("No usable constructor.");
        result.Worker!.SourcePath.ShouldEndWith("worker.cs");
    }

    [Fact]
    public async Task A_successful_run_is_not_retried()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + SuccessPayload + End));

        await RunAsync(process);

        process.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_rejects_a_null_request()
        => await Should.ThrowAsync<ArgumentNullException>(
            () => new PreviewRunner().RunAsync(null!, CancellationToken.None));

    [Fact]
    public void Constructing_without_collaborators_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => new PreviewRunner(null!, new StubLocator(), new StubDetector(Project()), Generator()));
        Should.Throw<ArgumentNullException>(() => new PreviewRunner(LinqSelectionAnalyzer.Instance, null!, new StubDetector(Project()), Generator()));
        Should.Throw<ArgumentNullException>(() => new PreviewRunner(LinqSelectionAnalyzer.Instance, new StubLocator(), null!, Generator()));
        Should.Throw<ArgumentNullException>(() => new PreviewRunner(LinqSelectionAnalyzer.Instance, new StubLocator(), new StubDetector(Project()), null!));
        Should.Throw<ArgumentNullException>(() => new PreviewRunner(LinqSelectionAnalyzer.Instance, new StubLocator(), new StubDetector(Project()), Generator(), null!));
    }

    [Fact]
    public void The_default_runner_wires_up_the_real_collaborators()
    {
        var runner = new PreviewRunner();

        runner.Analyzer.ShouldBeOfType<LinqSelectionAnalyzer>();
        runner.Locator.ShouldBeOfType<ProjectLocator>();
        runner.ProviderDetector.ShouldBeOfType<ProviderDetector>();
        runner.Generator.ShouldBeOfType<WorkerCodeGenerator>();
        runner.DotnetPath.ShouldBe("dotnet");
    }

    private const string UnresolvableContextDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Svc
{
    private readonly IUnitOfWork _uow;

    public Task<List<Product>> Go() => this._uow.Context.Products.ToListAsync();
}
""";

    /// <summary>A handler whose filter values arrive on an injected request object, as MediatR handlers do.</summary>
    private const string ParameterDocument = """
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class Handler
{
    private readonly AppDbContext db;

    public async Task<List<Product>> Go(SearchQuery request, CancellationToken cancellationToken)
    {
        return await this.db.Products.Where(p => p.CategoryId == request.CategoryId).ToListAsync(cancellationToken);
    }
}
""";

    /// <summary>EF's own wording when a captured variable is null; it never names the variable.</summary>
    private const string ParameterFailurePayload =
        """{"success":false,"errorKind":"Unknown","error":"InvalidOperationException: An exception was thrown while attempting to evaluate a LINQ query parameter expression.","commands":[],"warnings":[]}""";

    [Fact]
    public async Task A_null_captured_variable_is_reported_against_the_variable_the_analyzer_could_not_value()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + ParameterFailurePayload + End));

        var result = await RunAsync(
            process,
            document: ParameterDocument,
            startsAt: "this.db.Products",
            endsAfter: ".ToListAsync(cancellationToken)");

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.FreeVariableValueRequired);
        result.Response.Error.ShouldNotBeNull();
        result.Response.Error!.ShouldContain("'request'");
        result.Response.Error!.ShouldContain("free-variables panel");

        // The original EF wording is kept underneath so the real cause is still visible.
        result.Response.Error!.ShouldContain("evaluate a LINQ query parameter expression");

        // CancellationToken.None is a real value, so that row is never the one to go and edit.
        result.Response.Error!.ShouldNotContain("cancellationToken");
    }

    [Fact]
    public async Task A_variable_the_user_has_already_supplied_is_not_blamed_for_the_failure()
    {
        var process = new FakeProcessRunner().Enqueue(FakeProcessRunner.Ok(Begin + ParameterFailurePayload + End));

        var result = await RunAsync(
            process,
            configure: r => r with
            {
                FreeVariableOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["request"] = "new SearchQuery(1)",
                },
            },
            document: ParameterDocument,
            startsAt: "this.db.Products",
            endsAfter: ".ToListAsync(cancellationToken)");

        result.Response.ErrorKind.ShouldBe(PreviewErrorKind.Unknown);
        result.Response.Error.ShouldNotBeNull();
        result.Response.Error!.ShouldNotContain("free-variables panel");
    }

    private static Task<PreviewResult> RunAsync(
        FakeProcessRunner process,
        Func<PreviewRequest, PreviewRequest>? configure = null,
        ProjectContext? project = null,
        PreviewRunner? runner = null,
        string document = Document,
        string startsAt = "this.db.Products",
        string endsAfter = ".ToListAsync()",
        CancellationToken cancellationToken = default)
    {
        var start = document.IndexOf(startsAt, StringComparison.Ordinal);
        var end = document.IndexOf(endsAfter, start, StringComparison.Ordinal) + endsAfter.Length;

        var request = new PreviewRequest(document, @"C:\repo\App\Svc.cs", TextSpan.FromBounds(start, end));
        if (configure is not null)
        {
            request = configure(request);
        }

        return (runner ?? Runner(process, project: project)).RunAsync(request, cancellationToken);
    }

    private static PreviewRunner Runner(
        FakeProcessRunner process,
        IProjectLocator? locator = null,
        ProjectContext? project = null)
        => new(
            LinqSelectionAnalyzer.Instance,
            locator ?? new StubLocator(),
            new StubDetector(project ?? Project()),
            Generator(),
            process);

    private static WorkerCodeGenerator Generator() => new(new InMemoryFileSystem());

    private static ProjectContext Project()
        => new(
            ProjectPath: @"C:\repo\App\App.csproj",
            ProjectName: "App",
            Provider: EfProvider.SqlServer,
            ProviderPackageVersion: "10.0.10",
            EfCoreVersion: "10.0.10",
            TargetFramework: "net10.0",
            AdditionalProjectPaths: Array.Empty<string>());

    private static string ScratchFile(Core.Infrastructure.ProcessRunRequest request)
    {
        var start = request.Arguments.IndexOf('"') + 1;
        return request.Arguments.Substring(start, request.Arguments.LastIndexOf('"') - start);
    }

    /// <summary>A locator that always answers the same thing and counts its calls.</summary>
    private sealed class StubLocator : IProjectLocator
    {
        private readonly string? projectPath;

        public StubLocator(string? projectPath = @"C:\repo\App\App.csproj") => this.projectPath = projectPath;

        public int FindCalls { get; private set; }

        public string? FindOwningProject(string documentPath)
        {
            this.FindCalls++;
            return this.projectPath;
        }

        public IReadOnlyList<string> GetTransitiveProjectReferences(string projectPath) => Array.Empty<string>();
    }

    /// <summary>A detector that always returns a fixed context.</summary>
    private sealed class StubDetector : IProviderDetector
    {
        private readonly ProjectContext context;

        public StubDetector(ProjectContext context) => this.context = context;

        public ProjectContext Detect(string projectPath) => this.context;
    }
}
