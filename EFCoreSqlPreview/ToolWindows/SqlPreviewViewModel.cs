using System.Runtime.Serialization;
using System.Text;
using EFCoreSqlPreview.Core.Analysis;
using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Core.Generation;
using EFCoreSqlPreview.Core.Presentation;
using EFCoreSqlPreview.Core.Projects;
using EFCoreSqlPreview.Services;
using EFCoreSqlPreview.Settings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Extensibility.UI;

namespace EFCoreSqlPreview.ToolWindows
{
    /// <summary>
    /// Backing data for the tool window. Remote UI marshals this across the process boundary, so every bound
    /// member carries <see cref="DataMemberAttribute"/> and every mutable one raises change notifications.
    /// </summary>
    /// <remarks>
    /// Property setters and command callbacks arrive on arbitrary thread-pool threads, serialized one at a
    /// time until the first <c>await</c>. Long work therefore runs behind <see cref="gate"/> rather than
    /// relying on that serialization.
    /// </remarks>
    [DataContract]
    internal sealed class SqlPreviewViewModel : NotifyPropertyChangedObject
    {
        private const string AutoDialectName = nameof(EfProvider.Unknown);

        private readonly IPreviewRunner runner;
        private readonly PreviewSettings settings;
        private readonly SemaphoreSlim gate = new(1, 1);

        private CapturedSelection? selection;
        private CancellationTokenSource? runCts;

        /// <summary>
        /// Set when the user clicks Cancel. The runner reports a cancelled run with
        /// <see cref="PreviewErrorKind.Timeout"/>, which would otherwise be explained as "the preview took
        /// longer than N seconds" to someone who stopped it deliberately.
        /// </summary>
        private volatile bool userCanceled;

        private string status = "Select a LINQ query in the C# editor, then invoke Preview EF Core SQL.";
        private string projectName = "-";
        private string contextType = "-";
        private string providerText = "-";
        private string activationStrategy = "-";
        private string resultSummary = "Result: not determined yet.";
        private string querySummary = string.Empty;
        private string diagnostics = string.Empty;
        private string generatedProgram = string.Empty;
        private string warnings = string.Empty;
        private string errorMessage = string.Empty;
        private string documentName = "-";
        private bool isBusy;
        private bool hasWarnings;
        private bool hasError;
        private bool hasMultipleCommands;
        private bool hasResult;
        private bool hasVariables;
        private bool verboseMode;
        private CommandRow? selectedCommand;
        private DialectOption? selectedDialect;
        private bool showPlainSql;
        private bool showColorizedSql;

        /// <summary>Creates the view model.</summary>
        /// <param name="runner">Runs the analyse-generate-execute pipeline.</param>
        /// <param name="settings">Persisted user preferences.</param>
        public SqlPreviewViewModel(IPreviewRunner runner, PreviewSettings settings)
        {
            this.runner = runner;
            this.settings = settings;
            this.verboseMode = settings.VerboseMode;
            this.showPlainSql = settings.ShowPlainSql;

            this.RerunCommand = new AsyncCommand((_, _) => this.RunAsync());
            this.CancelCommand = new AsyncCommand((_, _) => this.CancelRunAsync());
            this.CopySqlCommand = new AsyncCommand((_, _) =>
            {
                this.CopySql();
                return Task.CompletedTask;
            });
            this.CopyAllCommand = new AsyncCommand((_, _) =>
            {
                this.CopyAll();
                return Task.CompletedTask;
            });
            this.CopyErrorCommand = new AsyncCommand((_, _) =>
            {
                this.CopyError();
                return Task.CompletedTask;
            });
            this.CopyDiagnosticsCommand = new AsyncCommand((_, _) =>
            {
                this.ReportCopy(ClipboardWriter.TrySetText(this.Diagnostics), "Diagnostics");
                return Task.CompletedTask;
            });

            this.Dialects.AddRange(BuildDialectOptions());
            this.selectedDialect = this.FindDialect(settings.ProviderOverride) ?? this.Dialects[0];
        }

        /// <summary>The line that tells the user what the tool is doing right now.</summary>
        [DataMember]
        public string Status
        {
            get => this.status;
            set => this.SetProperty(ref this.status, value);
        }

        /// <summary>Whether a preview is currently running.</summary>
        [DataMember]
        public bool IsBusy
        {
            get => this.isBusy;
            private set
            {
                if (this.SetProperty(ref this.isBusy, value))
                {
                    this.RaiseNotifyPropertyChangedEvent(nameof(this.IsIdle));
                }
            }
        }

        /// <summary>Whether no preview is running; drives the enabled state of every action but Cancel.</summary>
        [DataMember]
        public bool IsIdle => !this.isBusy;

        /// <summary>File name of the document the current preview came from.</summary>
        [DataMember]
        public string DocumentName
        {
            get => this.documentName;
            private set => this.SetProperty(ref this.documentName, value);
        }

        /// <summary>File name of the project the query was resolved against.</summary>
        [DataMember]
        public string ProjectName
        {
            get => this.projectName;
            private set => this.SetProperty(ref this.projectName, value);
        }

        /// <summary>The <c>DbContext</c> type the worker activated.</summary>
        [DataMember]
        public string ContextType
        {
            get => this.contextType;
            private set => this.SetProperty(ref this.contextType, value);
        }

        /// <summary>The provider that produced the SQL, with a note when it differs from the detected one.</summary>
        [DataMember]
        public string ProviderText
        {
            get => this.providerText;
            private set => this.SetProperty(ref this.providerText, value);
        }

        /// <summary>Which rung of the context-activation ladder worked.</summary>
        [DataMember]
        public string ActivationStrategy
        {
            get => this.activationStrategy;
            private set => this.SetProperty(ref this.activationStrategy, value);
        }

        /// <summary>The one-line description of what the query returns.</summary>
        [DataMember]
        public string ResultSummary
        {
            get => this.resultSummary;
            private set => this.SetProperty(ref this.resultSummary, value);
        }

        /// <summary>The query text the preview ran, shown so the user can confirm the selection was expanded correctly.</summary>
        [DataMember]
        public string QuerySummary
        {
            get => this.querySummary;
            private set => this.SetProperty(ref this.querySummary, value);
        }

        /// <summary>Analyzer findings plus compiler errors remapped onto the user's document.</summary>
        [DataMember]
        public string Diagnostics
        {
            get => this.diagnostics;
            private set => this.SetProperty(ref this.diagnostics, value);
        }

        /// <summary>The generated worker program, kept for debugging and bug reports.</summary>
        [DataMember]
        public string GeneratedProgram
        {
            get => this.generatedProgram;
            private set => this.SetProperty(ref this.generatedProgram, value);
        }

        /// <summary>Non-fatal notes, one per line.</summary>
        [DataMember]
        public string Warnings
        {
            get => this.warnings;
            private set => this.SetProperty(ref this.warnings, value);
        }

        /// <summary>Whether <see cref="Warnings"/> has anything worth showing.</summary>
        [DataMember]
        public bool HasWarnings
        {
            get => this.hasWarnings;
            private set => this.SetProperty(ref this.hasWarnings, value);
        }

        /// <summary>The actionable failure message, never a bare stack trace.</summary>
        [DataMember]
        public string ErrorMessage
        {
            get => this.errorMessage;
            private set => this.SetProperty(ref this.errorMessage, value);
        }

        /// <summary>Whether the error banner is showing.</summary>
        [DataMember]
        public bool HasError
        {
            get => this.hasError;
            private set => this.SetProperty(ref this.hasError, value);
        }

        /// <summary>Whether the run produced more than one command, e.g. a split query.</summary>
        [DataMember]
        public bool HasMultipleCommands
        {
            get => this.hasMultipleCommands;
            private set => this.SetProperty(ref this.hasMultipleCommands, value);
        }

        /// <summary>Whether the last run produced at least one command, which is what colours the result line.</summary>
        [DataMember]
        public bool HasResult
        {
            get => this.hasResult;
            private set => this.SetProperty(ref this.hasResult, value);
        }

        /// <summary>Whether the query referenced any variable declared outside it.</summary>
        [DataMember]
        public bool HasVariables
        {
            get => this.hasVariables;
            private set => this.SetProperty(ref this.hasVariables, value);
        }

        /// <summary>Whether the diagnostics tab also carries the worker's raw output.</summary>
        [DataMember]
        public bool VerboseMode
        {
            get => this.verboseMode;
            set
            {
                if (this.SetProperty(ref this.verboseMode, value))
                {
                    this.settings.VerboseMode = value;
                    this.settings.Save();
                }
            }
        }

        /// <summary>Every SQL command EF Core built, in the order it built them.</summary>
        [DataMember]
        public ObservableList<CommandRow> Commands { get; } = new();

        /// <summary>The command whose SQL and parameters are on screen.</summary>
        [DataMember]
        public CommandRow? SelectedCommand
        {
            get => this.selectedCommand;
            set
            {
                if (this.SetProperty(ref this.selectedCommand, value))
                {
                    this.RefreshSqlView();
                }
            }
        }

        /// <summary>
        /// Whether the SQL tab shows the plain, selectable text instead of the coloured view.
        /// </summary>
        /// <remarks>
        /// The coloured view is built from many small elements, none of which can be selected with the mouse,
        /// so the plain view has to stay reachable for anyone who wants to drag-select part of a statement.
        /// </remarks>
        [DataMember]
        public bool ShowPlainSql
        {
            get => this.showPlainSql;
            set
            {
                if (this.SetProperty(ref this.showPlainSql, value))
                {
                    this.settings.ShowPlainSql = value;
                    this.settings.Save();
                    this.RefreshSqlView();
                }
            }
        }

        /// <summary>Whether the coloured SQL view is the one currently on screen.</summary>
        [DataMember]
        public bool ShowColorizedSql
        {
            get => this.showColorizedSql;
            private set => this.SetProperty(ref this.showColorizedSql, value);
        }

        /// <summary>The free variables the query referenced, with editable values.</summary>
        [DataMember]
        public ObservableList<VariableRow> Variables { get; } = new();

        /// <summary>The dialect picker's entries, starting with auto-detect.</summary>
        [DataMember]
        public ObservableList<DialectOption> Dialects { get; } = new();

        /// <summary>The dialect the next run will use.</summary>
        [DataMember]
        public DialectOption? SelectedDialect
        {
            get => this.selectedDialect;
            set
            {
                if (!this.SetProperty(ref this.selectedDialect, value) || value is null)
                {
                    return;
                }

                this.settings.ProviderOverride = value.ToProvider();
                this.settings.Save();

                // Comparing dialects is the whole point of the picker, so choosing one re-runs immediately.
                // The setter is only ever reached from the combo box: the constructor assigns the backing
                // field directly and nothing else writes the property.
                if (this.selection is not null)
                {
                    _ = this.RunAsync();
                }
            }
        }

        /// <summary>Re-runs the preview, picking up edited variable values and the chosen dialect.</summary>
        [DataMember]
        public AsyncCommand RerunCommand { get; }

        /// <summary>Cancels the running preview and kills the worker's process tree.</summary>
        [DataMember]
        public AsyncCommand CancelCommand { get; }

        /// <summary>Copies the selected command's SQL to the clipboard.</summary>
        [DataMember]
        public AsyncCommand CopySqlCommand { get; }

        /// <summary>Copies the SQL and parameters of every command to the clipboard.</summary>
        [DataMember]
        public AsyncCommand CopyAllCommand { get; }

        /// <summary>Copies the error banner, with the diagnostics that explain it, to the clipboard.</summary>
        [DataMember]
        public AsyncCommand CopyErrorCommand { get; }

        /// <summary>Copies the Diagnostics tab to the clipboard.</summary>
        [DataMember]
        public AsyncCommand CopyDiagnosticsCommand { get; }

        /// <summary>
        /// Accepts a new selection from the editor command and starts a preview for it.
        /// </summary>
        /// <param name="captured">The editor snapshot taken by the command.</param>
        public void StartPreview(CapturedSelection captured)
        {
            this.selection = captured;
            this.DocumentName = Path.GetFileName(captured.DocumentPath);
            this.QuerySummary = captured.SelectedText.Length > 0
                ? captured.SelectedText
                : "(no selection - the statement around the caret will be used)";

            // A new selection invalidates the values the user typed for the previous query.
            this.Variables.Clear();
            this.HasVariables = false;

            _ = this.RunAsync();
        }

        private static IReadOnlyList<DialectOption> BuildDialectOptions()
        {
            var options = new List<DialectOption>
            {
                new() { DisplayName = "Auto (detect from project)", ProviderName = AutoDialectName },
            };

            options.AddRange(EfProviders.All.Select(descriptor => new DialectOption
            {
                DisplayName = descriptor.DisplayName,
                ProviderName = descriptor.Provider.ToString(),
            }));

            return options;
        }

        private DialectOption? FindDialect(EfProvider provider)
            => this.Dialects.FirstOrDefault(d => d.ToProvider() == provider);

        private async Task CancelRunAsync()
        {
            var cts = this.runCts;
            if (cts is null)
            {
                return;
            }

            try
            {
                this.userCanceled = true;
                this.Status = "Cancelling…";
                await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the click and here; nothing to cancel.
            }
        }

        private void CopySql()
        {
            var sql = this.SelectedCommand?.Sql ?? this.Commands.FirstOrDefault()?.Sql;
            this.ReportCopy(ClipboardWriter.TrySetText(sql), "SQL");
        }

        /// <summary>
        /// Copies the failure as a self-contained report.
        /// </summary>
        /// <remarks>
        /// The banner alone is rarely enough to act on or to paste into an issue, so the compiler diagnostics
        /// and any warnings go with it - which is exactly the material the troubleshooting section asks for.
        /// </remarks>
        private void CopyError()
        {
            var builder = new StringBuilder();
            builder.AppendLine(this.ErrorMessage.Length > 0 ? this.ErrorMessage : "(no error message)");

            if (this.HasWarnings)
            {
                builder.AppendLine().AppendLine("--- warnings ---").AppendLine(this.Warnings);
            }

            if (this.Diagnostics.Length > 0)
            {
                builder.AppendLine().AppendLine("--- diagnostics ---").AppendLine(this.Diagnostics);
            }

            this.ReportCopy(ClipboardWriter.TrySetText(builder.ToString().TrimEnd()), "Error");
        }

        private void CopyAll()
        {
            var builder = new StringBuilder();
            var index = 1;

            foreach (var command in this.Commands)
            {
                if (this.Commands.Count > 1)
                {
                    builder.Append("-- Command ").Append(index++).AppendLine();
                }

                builder.AppendLine(command.Sql);

                foreach (var parameter in command.Parameters)
                {
                    builder
                        .Append("-- ").Append(parameter.Name)
                        .Append(" (").Append(parameter.DbType).Append(", ").Append(parameter.ClrType).Append(") = ")
                        .AppendLine(parameter.Value);
                }

                builder.AppendLine();
            }

            this.ReportCopy(ClipboardWriter.TrySetText(builder.ToString().TrimEnd()), "SQL and parameters");
        }

        private void ReportCopy(bool copied, string what)
            => this.Status = copied
                ? $"Copied the {what} to the clipboard."
                : $"Could not reach the clipboard. Select the {what} in the text box and press Ctrl+C.";

        private async Task RunAsync()
        {
            var captured = this.selection;
            if (captured is null)
            {
                this.Status = "Select a LINQ query in the C# editor, then invoke Preview EF Core SQL.";
                return;
            }

            this.userCanceled = false;

            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref this.runCts, cts);
            if (previous is not null)
            {
                try
                {
                    await previous.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Already finished.
                }
            }

            await this.gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                this.IsBusy = true;
                this.HasError = false;
                this.ErrorMessage = string.Empty;
                this.Status = "Analyzing the selection…";

                // The runner shells out to `dotnet run --file`; a stuck restore must not hold the window
                // hostage, so the budget is enforced here as well as inside the runner.
                cts.CancelAfter(this.settings.EffectiveTimeout() + TimeSpan.FromSeconds(15));
                this.ApplyDotnetPathOverride();

                var request = new PreviewRequest(
                    captured.DocumentText,
                    captured.DocumentPath,
                    new TextSpan(captured.SelectionStart, captured.SelectionLength))
                {
                    DialectOverride = this.ResolveDialectOverride(),
                    FreeVariableOverrides = this.CollectOverrides(),
                    Timeout = this.settings.EffectiveTimeout(),
                    KeepGeneratedSource = true,
                };

                this.Status = "Building and running the preview worker. No database is contacted.";

                var result = await this.runner.RunAsync(request, cts.Token).ConfigureAwait(false);

                // The runner reports a cancelled run as a normal result rather than throwing, so a click on
                // Cancel would otherwise be explained to the user as a timeout.
                if (this.userCanceled)
                {
                    this.Status = "Cancelled.";
                    return;
                }

                this.Apply(result, captured);
            }
            catch (OperationCanceledException)
            {
                this.Status = "Cancelled.";
            }
            catch (NotImplementedException)
            {
                this.Fail(
                    "This build of EF Core SQL Preview does not have the analysis engine wired up yet.",
                    detail: null);
            }
            catch (Exception ex)
            {
                this.Fail(
                    "EF Core SQL Preview hit an unexpected problem. The details are on the Diagnostics tab.",
                    ex.ToString());
            }
            finally
            {
                this.IsBusy = false;
                this.gate.Release();

                // Clear the field only if this run is still the current one, but always dispose: a run that a
                // newer one superseded no longer owns the field and would otherwise leak its CancelAfter timer.
                Interlocked.CompareExchange(ref this.runCts, null, cts);
                cts.Dispose();
            }
        }

        private EfProvider? ResolveDialectOverride()
        {
            var provider = this.SelectedDialect?.ToProvider() ?? EfProvider.Unknown;
            return provider == EfProvider.Unknown ? null : provider;
        }

        private IReadOnlyDictionary<string, string> CollectOverrides()
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var variable in this.Variables)
            {
                if (variable.IsOverridden && variable.ValueExpression.Trim().Length > 0)
                {
                    overrides[variable.Name] = variable.ValueExpression.Trim();
                }
            }

            return overrides;
        }

        /// <summary>
        /// Points the worker at a specific SDK. The runner builds its own <c>ProcessStartInfo</c>, and child
        /// processes inherit this process's environment, so setting the variable here is what reaches it.
        /// </summary>
        private void ApplyDotnetPathOverride()
        {
            if (this.settings.DotnetPath.Length == 0 || !File.Exists(this.settings.DotnetPath))
            {
                return;
            }

            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", this.settings.DotnetPath);

            var directory = Path.GetDirectoryName(this.settings.DotnetPath);
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (directory is { Length: > 0 } && !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            }
        }

        private void Fail(string message, string? detail)
        {
            this.ErrorMessage = message;
            this.HasError = true;
            this.Status = "Failed.";

            if (detail is { Length: > 0 })
            {
                this.Diagnostics = detail;
            }
        }

        private void Apply(PreviewResult result, CapturedSelection captured)
        {
            var analysis = result.Analysis;
            var response = result.Response;

            this.ProjectName = result.Project is null
                ? "(no .csproj found above the document)"
                : Path.GetFileName(result.Project.ProjectPath);

            this.ContextType = response.ContextType
                ?? analysis?.ContextRoot.DbContextTypeName
                ?? "(not resolved)";

            this.ProviderText = DescribeProvider(response.Provider, result.Project?.Provider);
            this.ActivationStrategy = response.ActivationStrategy ?? "-";
            this.ResultSummary = PreviewFormatting.DescribeResult(analysis, response.Result);

            if (analysis is not null && analysis.QueryExpression.Length > 0)
            {
                this.QuerySummary = analysis.QueryExpression;
            }

            this.GeneratedProgram = result.Worker?.SourceText ?? string.Empty;
            this.ApplyCommands(response);
            this.ApplyVariables(analysis);
            this.ApplyWarnings(result);
            this.ApplyDiagnostics(result, captured);
            this.ApplyOutcome(result);
        }

        private static string DescribeProvider(EfProvider ran, EfProvider? detected)
        {
            var detectedName = detected is null or EfProvider.Unknown
                ? null
                : EfProviders.Describe(detected.Value)?.DisplayName ?? detected.Value.ToString();

            // Before the worker reports back there is only the detected provider to show.
            if (ran == EfProvider.Unknown)
            {
                return detectedName is null ? "(not detected)" : detectedName + " (detected)";
            }

            var ranName = EfProviders.Describe(ran)?.DisplayName ?? ran.ToString();
            return detectedName is null || detected == ran
                ? ranName
                : $"{ranName} (project references {detectedName})";
        }

        private void ApplyCommands(PreviewResponse response)
        {
            this.Commands.Clear();

            var rows = new List<CommandRow>(response.Commands.Count);
            for (var i = 0; i < response.Commands.Count; i++)
            {
                var command = response.Commands[i];
                var row = new CommandRow
                {
                    Title = response.Commands.Count > 1
                        ? $"Command {i + 1} of {response.Commands.Count}"
                        : "Command",
                    Sql = command.Sql,
                    ParameterSummary = command.Parameters.Count == 1
                        ? "1 parameter"
                        : $"{command.Parameters.Count} parameters",
                };

                row.Parameters.AddRange(command.Parameters.Select(p => new ParameterRow
                {
                    Name = p.Name,
                    DbType = p.DbType ?? "-",
                    ClrType = p.ClrType ?? "-",
                    Value = p.IsNull ? "NULL" : p.Value ?? string.Empty,
                }));

                Colorize(row);
                rows.Add(row);
            }

            this.Commands.AddRange(rows);
            this.HasMultipleCommands = rows.Count > 1;
            this.HasResult = rows.Count > 0;
            this.SelectedCommand = rows.FirstOrDefault();
        }

        /// <summary>
        /// Recomputes which of the two SQL views is showing.
        /// </summary>
        /// <remarks>
        /// This is a single bool rather than a pair of conditions evaluated in XAML because the remote UI has
        /// no converters, and a <c>MultiDataTrigger</c> spanning the selected command and a user preference is
        /// harder to follow than deriving it once here.
        /// </remarks>
        private void RefreshSqlView()
            => this.ShowColorizedSql = !this.ShowPlainSql && this.SelectedCommand?.IsColorized == true;

        /// <summary>Largest command the tokenized view is built for.</summary>
        /// <remarks>
        /// Every run crosses the remote-UI boundary as its own element. A command this size is already past
        /// the point of being read on screen, so it keeps the plain view rather than paying that cost.
        /// </remarks>
        private const int ColorizeCharacterLimit = 40_000;

        /// <summary>Fills a row's coloured runs, leaving it plain when the command is too large.</summary>
        /// <param name="row">The row to populate.</param>
        private static void Colorize(CommandRow row)
        {
            if (row.Sql.Length > ColorizeCharacterLimit)
            {
                row.IsColorized = false;
                return;
            }

            foreach (var line in SqlTokenizer.Tokenize(row.Sql))
            {
                var lineRow = new SqlLineRow();
                lineRow.Tokens.AddRange(line.Select(token => new SqlTokenRow
                {
                    Text = token.Text,
                    Kind = token.Kind.ToString(),
                }));

                row.Lines.Add(lineRow);
            }

            row.IsColorized = true;
        }

        private void ApplyVariables(QueryAnalysisResult? analysis)
        {
            // Values the user typed survive a re-run: the analyzer re-derives the same variables every time,
            // and wiping the panel on each run would make the Re-run button useless.
            //
            // The *original* suggestion is carried across too, not just the typed value. Once an override is
            // sent, the analyzer echoes it back as that variable's suggestion, so comparing against the fresh
            // suggestion would make IsOverridden false and silently drop the override on the next run - while
            // the box still displayed the user's value.
            var edited = this.Variables
                .Where(v => v.IsOverridden)
                .ToDictionary(
                    v => v.Name,
                    v => (Typed: v.ValueExpression, Original: v.SuggestedValue),
                    StringComparer.Ordinal);

            this.Variables.Clear();

            if (analysis is null || analysis.FreeVariables.Count == 0)
            {
                this.HasVariables = false;
                return;
            }

            var rows = analysis.FreeVariables.Select(variable =>
            {
                var suggested = variable.SuggestedValueExpression ?? string.Empty;
                var hasEdit = edited.TryGetValue(variable.Name, out var previous);

                return new VariableRow
                {
                    Name = variable.Name,
                    TypeName = variable.DeclaredTypeName ?? "?",
                    Source = variable.ValueSource.ToString(),
                    RequiresValue = variable.RequiresUserValue,
                    SuggestedValue = hasEdit ? previous.Original : suggested,
                    ValueExpression = hasEdit ? previous.Typed : suggested,
                };
            }).ToList();

            this.Variables.AddRange(rows);
            this.HasVariables = rows.Count > 0;
        }

        private void ApplyWarnings(PreviewResult result)
        {
            var lines = new List<string>();
            lines.AddRange(result.Response.Warnings);

            if (result.Worker is not null)
            {
                lines.AddRange(result.Worker.Warnings);
            }

            if (result.Analysis is { } analysis)
            {
                lines.AddRange(analysis.Diagnostics
                    .Where(d => d.Severity == AnalysisDiagnosticSeverity.Warning)
                    .Select(d => $"{d.Id}: {d.Message}"));

                if (analysis.OutOfScope.IsOutOfScope && !analysis.OutOfScope.IsHardBlock)
                {
                    lines.Add(PreviewFormatting.DescribeOutOfScope(analysis.OutOfScope));
                }
            }

            // The discovery worker reports candidate context types as prefixed warnings so Core can parse them
            // back out. That prefix is an internal wire detail; ExplainFailure renders the candidates properly.
            var distinct = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l => !l.StartsWith(WorkerTemplate.ContextCandidatePrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            this.Warnings = string.Join(Environment.NewLine, distinct);
            this.HasWarnings = distinct.Count > 0;
        }

        private void ApplyDiagnostics(PreviewResult result, CapturedSelection captured)
        {
            var workerOutput = string.Concat(result.RawStandardOutput, "\n", result.RawStandardError);
            var text = PreviewFormatting.DescribeDiagnostics(
                result.Analysis,
                result.Worker,
                captured.DocumentText,
                captured.DocumentPath,
                workerOutput);

            if (this.VerboseMode)
            {
                var builder = new StringBuilder(text);
                builder.AppendLine().AppendLine()
                    .Append("Worker exit code: ").Append(result.ExitCode)
                    .Append("   elapsed: ").Append(result.Elapsed.TotalSeconds.ToString("0.0")).AppendLine("s")
                    .AppendLine()
                    .AppendLine("stdout").AppendLine("------").AppendLine(result.RawStandardOutput)
                    .AppendLine().AppendLine("stderr").AppendLine("------").AppendLine(result.RawStandardError);
                text = builder.ToString();
            }

            this.Diagnostics = text;
        }

        private void ApplyOutcome(PreviewResult result)
        {
            var analysis = result.Analysis;

            if (analysis is not null && analysis.OutOfScope.IsHardBlock)
            {
                this.ErrorMessage = PreviewFormatting.DescribeOutOfScope(analysis.OutOfScope);
                this.HasError = true;
                this.Status = "SELECT queries only.";
                return;
            }

            if (result.Response.Success)
            {
                this.ErrorMessage = string.Empty;
                this.HasError = false;
                this.Status = string.Format(
                    "{0} in {1:0.0}s. No database was contacted.",
                    this.Commands.Count == 1 ? "1 command captured" : $"{this.Commands.Count} commands captured",
                    result.Elapsed.TotalSeconds);
                return;
            }

            this.ErrorMessage = this.ExplainFailure(result);
            this.HasError = true;
            this.Status = "Failed. See the message above and the Diagnostics tab.";
        }

        private string ExplainFailure(PreviewResult result)
        {
            var response = result.Response;
            var detail = response.Error is { Length: > 0 } ? " " + response.Error : string.Empty;

            // The single hard prerequisite is a .NET 10 SDK, and every way of not having one lands in a generic
            // bucket further down, so it is checked before anything else.
            var sdkProblem = DescribeSdkProblem(result);
            if (sdkProblem is not null)
            {
                return sdkProblem;
            }

            // A discovery run cannot pick a context for the user, and 1.0 has no picker, so the message has to
            // name the candidates and give a way forward that does not need one.
            if (result.ContextCandidates.Count > 1)
            {
                return "Your project defines more than one DbContext ("
                    + string.Join(", ", result.ContextCandidates)
                    + ") and the selection did not say which to use. This version has no context picker; either write the query against a DbContext-typed field, property or local whose declaration is in the same file, or add an IDesignTimeDbContextFactory<T> for the one you want - it wins the activation ladder.";
            }

            return response.ErrorKind switch
            {
                PreviewErrorKind.CompileError =>
                    "The preview program did not compile against your project. Check that your own project builds first, then open the Diagnostics tab: the errors are mapped back onto your selection. The usual causes are a variable the analyzer could not reproduce - fix its value in the Variables tab and Re-run - and a type that is not visible from the document's project."
                    + detail,
                PreviewErrorKind.ContextActivationFailed =>
                    "No DbContext could be created. Add an IDesignTimeDbContextFactory<T> to your project, or give the context a constructor that takes only DbContextOptions<T>."
                    + detail,
                PreviewErrorKind.NotTranslatable =>
                    "EF Core refused to translate this query to SQL, so there is nothing to preview. Part of it would run on the client."
                    + detail,
                PreviewErrorKind.ProviderError =>
                    "The provider rejected the query. This usually means the query uses something exclusive to another dialect - try switching the dialect picker back to Auto."
                    + detail,
                PreviewErrorKind.ProviderVersionMismatch =>
                    "That dialect has no build matching your project's EF Core version, so it cannot be previewed here."
                    + detail,
                PreviewErrorKind.Timeout =>
                    $"The preview took longer than {this.settings.EffectiveTimeout().TotalSeconds:0} seconds and was stopped. A first run also restores and builds your project, so try again, or raise the timeout in settings.json."
                    + detail,
                PreviewErrorKind.NoPayload =>
                    "The preview program ran but produced no result. The Diagnostics tab has its raw output; turn on verbose mode for the full build log."
                    + detail,
                PreviewErrorKind.OutOfScope =>
                    "EF Core SQL Preview handles SELECT (read) queries only." + detail,

                // Core has already named the variables in the message, so repeating them here would only
                // push the useful part further down the banner.
                PreviewErrorKind.FreeVariableValueRequired =>
                    (result.Response.Error ?? "A value is needed before this query can run.") + detail,
                _ => this.ExplainAnalysisFailure(result) ?? ("The preview did not produce any SQL." + detail),
            };
        }

        /// <summary>
        /// Recognises the ways a machine without a usable .NET 10 SDK fails. Each of them otherwise surfaces as
        /// "no payload" or "could not be started", which says nothing about the one thing the user must install.
        /// </summary>
        /// <param name="result">The failed run.</param>
        /// <returns>An actionable message, or <see langword="null"/> when the SDK is not the problem.</returns>
        private string? DescribeSdkProblem(PreviewResult result)
        {
            var output = result.RawStandardOutput + "\n" + result.RawStandardError;
            if (output.Length == 0)
            {
                return null;
            }

            var configured = this.settings.DotnetPath.Length > 0
                ? $" The DotnetPath setting currently points at '{this.settings.DotnetPath}'."
                : " Then either put it first on PATH, or set DotnetPath in "
                    + PreviewSettings.SettingsPath
                    + " to an absolute dotnet.exe from a 10.0 install.";

            // `dotnet` itself is missing, or the path override points at nothing.
            if (Contains(output, "The system cannot find the file specified")
                || Contains(output, "No such file or directory"))
            {
                return "The 'dotnet' command could not be started, so the preview never ran. Install the .NET 10 SDK from https://dotnet.microsoft.com/download."
                    + configured;
            }

            // An SDK is present but too old: `--file` arrived with the .NET 10 file-based-app support.
            if (Contains(output, "A compatible .NET SDK was not found")
                || Contains(output, "Requested SDK version")
                || Contains(output, "Unrecognized command or argument '--file'")
                || Contains(output, "Unrecognized option '--file'")
                || Contains(output, "MSB1003"))
            {
                return "The .NET SDK on this machine cannot run the preview. It needs .NET SDK 10.0 or newer, because the preview program is a file-based app ('dotnet run --file'), which older SDKs do not support. Run 'dotnet --list-sdks' to see what is installed and install 10.0 or newer from https://dotnet.microsoft.com/download."
                    + configured;
            }

            return null;

            static bool Contains(string haystack, string needle)
                => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string? ExplainAnalysisFailure(PreviewResult result)
        {
            var analysis = result.Analysis;
            if (analysis is null)
            {
                return null;
            }

            return analysis.Status switch
            {
                AnalysisStatus.NoQueryFound =>
                    "No LINQ query was found at the caret. Select the query expression, or put the caret inside it, and try again.",
                AnalysisStatus.NotAQuery =>
                    "The selection is not a LINQ query over a DbSet. Select an expression that starts from your DbContext.",
                AnalysisStatus.ParseFailure =>
                    "The document could not be parsed. Fix the syntax errors around the selection and try again.",
                AnalysisStatus.OutOfScope =>
                    PreviewFormatting.DescribeOutOfScope(analysis.OutOfScope),
                _ => null,
            };
        }
    }
}
