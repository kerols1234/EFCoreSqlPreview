using Microsoft.VisualStudio.Extensibility.UI;

namespace EFCoreSqlPreview.ToolWindows
{
    /// <summary>
    /// Binds the tool window's XAML to its data context.
    /// </summary>
    /// <remarks>
    /// Remote UI forbids code-behind: the template is instantiated inside <c>devenv.exe</c>, which cannot load
    /// this assembly. The XAML is located by an embedded resource named after this type; see the
    /// <c>EmbeddedResource</c> entry in the project file.
    /// </remarks>
    internal sealed class SqlPreviewControl : RemoteUserControl
    {
        /// <summary>Creates the control over the shared view model.</summary>
        /// <param name="dataContext">The view model to bind.</param>
        public SqlPreviewControl(SqlPreviewViewModel dataContext)
            : base(dataContext)
        {
        }
    }
}
