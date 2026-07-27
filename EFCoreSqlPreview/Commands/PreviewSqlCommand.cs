using System.Diagnostics;
using EFCoreSqlPreview.Services;
using EFCoreSqlPreview.ToolWindows;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace EFCoreSqlPreview.Commands
{
    /// <summary>
    /// "Preview EF Core SQL" on the C# editor's context menu. Captures the selection and opens the tool window.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class PreviewSqlCommand : Command
    {
        /// <summary>The Visual Studio text editor UI context, so the shortcut cannot collide outside the editor.</summary>
        private const string TextEditorContext = "{5EFC7975-14BC-11CF-9B2B-00AA00573819}";

        private readonly SqlPreviewSession session;
        private readonly TraceSource logger;

        /// <summary>Creates the command.</summary>
        /// <param name="session">The state shared with the tool window.</param>
        /// <param name="traceSource">Diagnostic log supplied by the SDK.</param>
        public PreviewSqlCommand(SqlPreviewSession session, TraceSource traceSource)
        {
            this.session = session;
            this.logger = traceSource;
        }

        /// <inheritdoc />
        public override CommandConfiguration CommandConfiguration => new("%EFCoreSqlPreview.PreviewSqlCommand.DisplayName%")
        {
            TooltipText = "%EFCoreSqlPreview.PreviewSqlCommand.ToolTipText%",
            Icon = new(ImageMoniker.KnownValues.DatabaseScript, IconSettings.IconAndText),

            // The context-menu placement lives in EditorContextMenuPlacement; this adds the discoverable
            // top-level entry. Visual Studio Search finds the command regardless of placement.
            Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],

            // Hidden outside C# documents, and greyed out until the buffer really is C# and the solution is
            // loaded - without a loaded solution there is no .csproj to build the preview against.
            VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorFileName, @"\.cs$"),
            EnabledWhen = ActivationConstraint.EditorContentType("CSharp")
                & ActivationConstraint.SolutionState(SolutionState.FullyLoaded),

            // A two-chord binding: single chords in this range are already taken by Visual Studio.
            Shortcuts =
            [
                new(ModifierKey.ControlShift, Key.Q, ModifierKey.ControlShift, Key.S, new Guid(TextEditorContext)),
            ],
        };

        /// <inheritdoc />
        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            // Nothing may escape this method: out of process, an unhandled exception is invisible to the user
            // and can fault the whole command set.
            try
            {
                var captured = await CaptureAsync(context, cancellationToken);
                if (captured is null)
                {
                    await this.Extensibility.Shell().ShowPromptAsync(
                        "Open a C# file and select a LINQ query, or put the caret inside one, then run Preview EF Core SQL.",
                        PromptOptions.OK,
                        cancellationToken);
                    return;
                }

                // The window is shown first so the user sees the busy indicator for the run that follows.
                await this.Extensibility.Shell()
                    .ShowToolWindowAsync<SqlPreviewToolWindow>(activate: true, cancellationToken);

                this.session.BeginPreview(captured);
            }
            catch (OperationCanceledException)
            {
                // Visual Studio tore the invocation down; there is nothing to report.
            }
            catch (Exception ex)
            {
                this.logger.TraceEvent(TraceEventType.Error, 0, "Preview EF Core SQL failed: {0}", ex);

                await this.Extensibility.Shell().ShowPromptAsync(
                    "EF Core SQL Preview could not read the editor selection. See the extension's log for details.",
                    PromptOptions.OK,
                    CancellationToken.None);
            }
        }

        /// <summary>
        /// Copies everything the preview needs out of the editor snapshot.
        /// </summary>
        /// <param name="context">The client context the command was invoked with.</param>
        /// <param name="cancellationToken">Cancels the round trip to Visual Studio.</param>
        /// <returns>The captured state, or <see langword="null"/> when there is no active text view.</returns>
        /// <remarks>
        /// The snapshot and every <c>TextRange</c> and <c>TextPosition</c> taken from it are RPC-backed and
        /// must not outlive this call, which is why only plain values are returned.
        /// </remarks>
        private static async Task<CapturedSelection?> CaptureAsync(
            IClientContext context,
            CancellationToken cancellationToken)
        {
            using var textView = await context.GetActiveTextViewAsync(cancellationToken);
            if (textView is null)
            {
                return null;
            }

            var document = textView.Document;
            var documentText = document.Text.CopyToString();
            var documentPath = textView.FilePath ?? textView.Uri.LocalPath;

            // Box selection and multi-caret both land here; the first non-empty selection is the one the user
            // means, and a bare caret is fine because the analyzer expands outward to the whole query.
            var selection = textView.Selections.FirstOrDefault(s => !s.IsEmpty);
            if (selection.IsEmpty)
            {
                selection = textView.Selection;
            }

            var start = selection.Extent.Start.Offset;
            var length = selection.IsEmpty ? 0 : selection.Extent.Length;
            if (selection.IsEmpty)
            {
                start = selection.InsertionPosition.Offset;
            }

            var selectedText = selection.IsEmpty ? string.Empty : selection.Extent.CopyToString();

            return new CapturedSelection(documentText, documentPath, start, length, selectedText);
        }
    }
}
