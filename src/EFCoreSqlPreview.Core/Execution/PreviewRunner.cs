using System.Diagnostics;
using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Generation;
using EFCoreSqlPreview.Core.Infrastructure;
using EFCoreSqlPreview.Core.Projects;

namespace EFCoreSqlPreview.Core.Execution;

/// <summary>
/// The complete outcome of one preview: what the analyzer understood, what was generated, and what came back.
/// </summary>
/// <param name="Analysis">The analysis of the selection.</param>
/// <param name="Project">The project context, or <see langword="null"/> when no project could be located.</param>
/// <param name="Worker">The generated worker, or <see langword="null"/> when generation never happened.</param>
/// <param name="Response">The worker's payload, or a synthesised failure when it produced none.</param>
/// <param name="RawStandardOutput">The worker's full stdout, kept for the diagnostics tab.</param>
/// <param name="RawStandardError">The worker's full stderr, kept for the diagnostics tab.</param>
/// <param name="ExitCode">The worker's exit code. Non-zero means the build failed before any payload existed.</param>
/// <param name="Elapsed">Wall-clock time spent running the worker.</param>
public sealed record PreviewResult(
    QueryAnalysisResult Analysis,
    ProjectContext? Project,
    GeneratedWorker? Worker,
    PreviewResponse Response,
    string RawStandardOutput,
    string RawStandardError,
    int ExitCode,
    TimeSpan Elapsed)
{
    /// <summary>Build diagnostics parsed out of the run, with generated lines mapped back to the selection.</summary>
    public IReadOnlyList<RemappedDiagnostic> Diagnostics { get; init; } = Array.Empty<RemappedDiagnostic>();

    /// <summary>
    /// Candidate <c>DbContext</c> types a discovery run found, for the context-type picker. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> ContextCandidates { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Runs the whole pipeline: analyse, locate the project, detect the provider, generate the worker, run it,
/// and parse its payload.
/// </summary>
public interface IPreviewRunner
{
    /// <summary>Executes a preview end to end.</summary>
    /// <param name="request">The captured selection and the user's choices.</param>
    /// <param name="cancellationToken">Cancels the run and kills the worker's process tree.</param>
    /// <returns>The result; never throws for an expected failure.</returns>
    Task<PreviewResult> RunAsync(PreviewRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IPreviewRunner"/>: shells out to <c>dotnet run --file</c> and reads the payload from
/// between the sentinels on stdout.
/// </summary>
/// <remarks>
/// Both stdout and stderr must be drained concurrently or a full pipe buffer deadlocks the run, and
/// <c>StandardOutputEncoding</c> must be UTF-8 or non-ASCII identifiers in the SQL come back mangled.
/// </remarks>
public sealed class PreviewRunner : IPreviewRunner
{
    /// <summary>Creates a runner using the default collaborators.</summary>
    public PreviewRunner()
        : this(LinqSelectionAnalyzer.Instance, new ProjectLocator(), new ProviderDetector(), new WorkerCodeGenerator())
    {
    }

    /// <summary>Creates a runner over specific collaborators.</summary>
    /// <param name="analyzer">Turns the selection into an analysis.</param>
    /// <param name="locator">Finds the owning project.</param>
    /// <param name="providerDetector">Detects the EF Core provider.</param>
    /// <param name="generator">Emits the worker program.</param>
    public PreviewRunner(
        ILinqSelectionAnalyzer analyzer,
        IProjectLocator locator,
        IProviderDetector providerDetector,
        IWorkerCodeGenerator generator)
        : this(analyzer, locator, providerDetector, generator, Infrastructure.ProcessRunner.Default)
    {
    }

    /// <summary>Creates a runner over specific collaborators, including the process launcher.</summary>
    /// <param name="analyzer">Turns the selection into an analysis.</param>
    /// <param name="locator">Finds the owning project.</param>
    /// <param name="providerDetector">Detects the EF Core provider.</param>
    /// <param name="generator">Emits the worker program.</param>
    /// <param name="processRunner">Launches <c>dotnet run</c>.</param>
    public PreviewRunner(
        ILinqSelectionAnalyzer analyzer,
        IProjectLocator locator,
        IProviderDetector providerDetector,
        IWorkerCodeGenerator generator,
        IProcessRunner processRunner)
    {
        this.Analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        this.Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.ProviderDetector = providerDetector ?? throw new ArgumentNullException(nameof(providerDetector));
        this.Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.ProcessRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>Turns the selection into an analysis.</summary>
    public ILinqSelectionAnalyzer Analyzer { get; }

    /// <summary>Finds the owning project.</summary>
    public IProjectLocator Locator { get; }

    /// <summary>Detects the EF Core provider.</summary>
    public IProviderDetector ProviderDetector { get; }

    /// <summary>Emits the worker program.</summary>
    public IWorkerCodeGenerator Generator { get; }

    /// <summary>Launches the worker process.</summary>
    public IProcessRunner ProcessRunner { get; }

    /// <summary>The command used to launch the worker; overridable for tests and unusual installs.</summary>
    public string DotnetPath { get; init; } = "dotnet";

    /// <summary>
    /// Extracts the JSON payload from a worker's stdout.
    /// </summary>
    /// <param name="standardOutput">The worker's complete stdout, which also carries build warnings.</param>
    /// <param name="payload">The JSON between the sentinels, when present.</param>
    /// <returns><see langword="true"/> when both sentinels were found in order.</returns>
    public static bool TryExtractPayload(string standardOutput, out string payload)
        => WorkerOutputParser.TryExtractPayload(standardOutput, out payload);

    /// <summary>Deserializes a worker payload using <see cref="PreviewResponse.JsonOptions"/>.</summary>
    /// <param name="payload">The JSON extracted by <see cref="TryExtractPayload"/>.</param>
    /// <returns>The parsed response, or a <see cref="PreviewErrorKind.NoPayload"/> failure when it is malformed.</returns>
    public static PreviewResponse ParsePayload(string payload)
        => WorkerOutputParser.ParsePayload(payload);

    /// <inheritdoc />
    public async Task<PreviewResult> RunAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var analyzerOptions = new AnalyzerOptions
        {
            DbContextTypeOverride = request.DbContextTypeOverride,
            FreeVariableOverrides = request.FreeVariableOverrides,
        };

        var analysis = this.Analyzer.Analyze(request.DocumentText, request.Selection, analyzerOptions);

        if (analysis.OutOfScope.IsHardBlock)
        {
            return Failed(analysis, null, PreviewErrorKind.OutOfScope, analysis.OutOfScope.Message);
        }

        if (!analysis.CanRun)
        {
            return Failed(analysis, null, PreviewErrorKind.Unknown, DescribeAnalysisFailure(analysis));
        }

        var projectPath = request.ProjectPathOverride ?? this.Locator.FindOwningProject(request.DocumentPath);
        if (string.IsNullOrEmpty(projectPath))
        {
            return Failed(
                analysis,
                null,
                PreviewErrorKind.Unknown,
                "No .csproj was found above '" + request.DocumentPath + "', so there is no project to run the query against.");
        }

        ProjectContext project;
        try
        {
            project = this.ProviderDetector.Detect(projectPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(analysis, null, PreviewErrorKind.Unknown, "The project file could not be read: " + ex.Message);
        }

        var scratch = this.Generator.GetScratchDirectory(
            request.DocumentPath,
            analysis.SelectionSpan.Start,
            analysis.SelectionSpan.Length);

        var options = new WorkerGenerationOptions(
            Project: project,
            Provider: request.DialectOverride ?? project.Provider,
            DbContextTypeName: request.DbContextTypeOverride,
            ScratchDirectory: scratch)
        {
            FreeVariableOverrides = request.FreeVariableOverrides,
            IsDialectOverride = request.DialectOverride is not null,
        };

        var attempt = await this.RunAttemptAsync(analysis, project, options, request, cancellationToken).ConfigureAwait(false);

        // A discovery run reports the candidate context types instead of SQL. When exactly one exists the
        // second pass is free of user interaction, which is the common case for a single-context solution.
        if (attempt.Worker?.IsContextDiscovery == true && attempt.ContextCandidates.Count == 1)
        {
            var resolved = options with { DbContextTypeName = attempt.ContextCandidates[0] };
            return await this.RunAttemptAsync(analysis, project, resolved, request, cancellationToken).ConfigureAwait(false);
        }

        // A context with no DbContextOptions constructor can only be reached through a generated subclass,
        // which does not compile for every context, so it is tried only after the ordinary ladder has failed.
        if (attempt.Response.ErrorKind == PreviewErrorKind.ContextActivationFailed
            && attempt.Worker?.IsContextDiscovery == false
            && !options.UseDerivedContext)
        {
            var derived = options with
            {
                DbContextTypeName = options.DbContextTypeName ?? attempt.Worker.ContextTypeName,
                UseDerivedContext = true,
            };

            var retry = await this.RunAttemptAsync(analysis, project, derived, request, cancellationToken).ConfigureAwait(false);
            if (retry.Response.Success)
            {
                return retry;
            }
        }

        return attempt;
    }

    private async Task<PreviewResult> RunAttemptAsync(
        QueryAnalysisResult analysis,
        ProjectContext project,
        WorkerGenerationOptions options,
        PreviewRequest request,
        CancellationToken cancellationToken)
    {
        GeneratedWorker worker;
        try
        {
            worker = this.Generator.Generate(analysis, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(analysis, project, PreviewErrorKind.Unknown, "The generated worker could not be written: " + ex.Message);
        }

        var runRequest = new ProcessRunRequest(
            this.DotnetPath,
            BuildArguments(worker.SourcePath),
            options.ScratchDirectory)
        {
            Timeout = request.Timeout,
            Environment = BuildEnvironment(),
        };

        var stopwatch = Stopwatch.StartNew();
        ProcessRunResult run;
        try
        {
            run = await this.ProcessRunner.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new PreviewResult(
                analysis,
                project,
                worker,
                PreviewResponse.Failure(PreviewErrorKind.Unknown, "The worker could not be started: " + ex.Message),
                string.Empty,
                ex.ToString(),
                -1,
                stopwatch.Elapsed);
        }

        stopwatch.Stop();

        var response = WorkerOutputParser.Parse(
            run.StandardOutput,
            run.StandardError,
            run.ExitCode,
            run.TimedOut,
            run.Canceled);

        response = MergeGeneratorWarnings(response, worker.Warnings);

        var diagnostics = DiagnosticRemapper.Parse(run.StandardOutput + "\n" + run.StandardError, worker);

        return new PreviewResult(
            analysis,
            project,
            worker,
            response,
            run.StandardOutput,
            run.StandardError,
            run.ExitCode,
            run.Elapsed)
        {
            Diagnostics = diagnostics,
            ContextCandidates = WorkerOutputParser.ReadContextCandidates(response),
        };
    }

    /// <summary>
    /// Builds the <c>dotnet run</c> command line for a file-based app.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the generated <c>.cs</c> file.</param>
    /// <returns>The argument string.</returns>
    /// <remarks>
    /// <c>--tl:off</c> turns off the terminal logger, whose cursor control sequences would otherwise be
    /// interleaved with the payload on stdout.
    /// </remarks>
    internal static string BuildArguments(string sourcePath)
        => "run --file \"" + sourcePath + "\" --tl:off";

    private static IReadOnlyDictionary<string, string?> BuildEnvironment()
        => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["MSBUILDTERMINALLOGGER"] = "off",

            // English diagnostics keep DiagnosticRemapper's severity keywords matchable on localised installs.
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        };

    private static PreviewResponse MergeGeneratorWarnings(PreviewResponse response, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return response;
        }

        var merged = new List<string>(warnings);
        merged.AddRange(response.Warnings);
        return response with { Warnings = merged };
    }

    private static string DescribeAnalysisFailure(QueryAnalysisResult analysis)
    {
        var error = analysis.Diagnostics.FirstOrDefault(d => d.Severity == AnalysisDiagnosticSeverity.Error);
        if (error is not null)
        {
            return error.Message;
        }

        return analysis.Status switch
        {
            AnalysisStatus.NoQueryFound => "No LINQ query was found at the selection. Select the query, or place the caret inside it.",
            AnalysisStatus.NotAQuery => "The selection is not a LINQ query.",
            AnalysisStatus.ParseFailure => "The document could not be parsed.",
            _ => "The selection could not be analysed.",
        };
    }

    private static PreviewResult Failed(
        QueryAnalysisResult analysis,
        ProjectContext? project,
        PreviewErrorKind kind,
        string message)
        => new(
            analysis,
            project,
            Worker: null,
            PreviewResponse.Failure(kind, message),
            RawStandardOutput: string.Empty,
            RawStandardError: string.Empty,
            ExitCode: 0,
            Elapsed: TimeSpan.Zero);
}
