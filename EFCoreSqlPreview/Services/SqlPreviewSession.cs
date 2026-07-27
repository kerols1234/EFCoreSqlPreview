using EFCoreSqlPreview.Core.Execution;
using EFCoreSqlPreview.Settings;
using EFCoreSqlPreview.ToolWindows;

namespace EFCoreSqlPreview.Services
{
    /// <summary>
    /// The single piece of state shared between the editor command and the tool window. Registered as a
    /// singleton so both resolve the same instance.
    /// </summary>
    internal sealed class SqlPreviewSession
    {
        /// <summary>Creates the session and the view model the tool window will bind to.</summary>
        public SqlPreviewSession()
        {
            this.Settings = PreviewSettings.Load();
            this.ViewModel = new SqlPreviewViewModel(new PreviewRunner(), this.Settings);
        }

        /// <summary>The view model bound by the tool window's Remote UI control.</summary>
        public SqlPreviewViewModel ViewModel { get; }

        /// <summary>Persisted user preferences.</summary>
        public PreviewSettings Settings { get; }

        /// <summary>
        /// Hands a fresh selection to the view model and starts the preview.
        /// </summary>
        /// <param name="selection">The captured editor state.</param>
        public void BeginPreview(CapturedSelection selection) => this.ViewModel.StartPreview(selection);
    }
}
