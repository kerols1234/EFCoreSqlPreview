using EFCoreSqlPreview.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace EFCoreSqlPreview.ToolWindows
{
    /// <summary>
    /// The dockable "EF Core SQL Preview" pane.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class SqlPreviewToolWindow : ToolWindow
    {
        private readonly SqlPreviewSession session;
        private SqlPreviewControl? control;

        /// <summary>Creates the tool window.</summary>
        /// <param name="extensibility">The extensibility object supplied by the SDK.</param>
        /// <param name="session">The state shared with the editor command.</param>
        public SqlPreviewToolWindow(VisualStudioExtensibility extensibility, SqlPreviewSession session)
            : base(extensibility)
        {
            this.session = session;
            this.Title = "EF Core SQL Preview";
        }

        /// <inheritdoc />
        public override ToolWindowConfiguration ToolWindowConfiguration => new()
        {
            // Docked under the document well rather than inside it: the point of the window is to be read
            // beside the query, and a bare DocumentWell placement opens it as a tab over the very code the
            // user just right-clicked. Visual Studio remembers wherever the user moves it afterwards.
            Placement = ToolWindowPlacement.DocumentWell,
            DockDirection = Dock.Bottom,

            // Without this Visual Studio resurrects the window on restart with no query to show.
            AllowAutoCreation = false,
        };

        /// <inheritdoc />
        public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
            => Task.FromResult<IRemoteUserControl>(
                this.control ??= new SqlPreviewControl(this.session.ViewModel));

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.control?.Dispose();
                this.control = null;
            }

            base.Dispose(disposing);
        }
    }
}
