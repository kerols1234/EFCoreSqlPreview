using EFCoreSqlPreview.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace EFCoreSqlPreview
{
    /// <summary>
    /// Entry point for the out-of-process VisualStudio.Extensibility extension. This assembly runs in its own
    /// .NET process, so there is no Visual Studio main thread to marshal to and no <c>JoinableTaskFactory</c>.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class ExtensionEntrypoint : Extension
    {
        /// <inheritdoc/>
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            Metadata = new(
                id: "EFCoreSqlPreview.c0c485c6-6184-48c6-9202-16af3633e868",
                version: this.ExtensionAssemblyVersion,
                publisherName: "kerols1234",
                displayName: "EF Core SQL Preview",
                description: "Shows the SQL EF Core generates for a selected LINQ query, without running your project and without a database.")
            {
                Preview = false,
                MoreInfo = "https://github.com/kerols1234/EFCoreSqlPreview",
                Tags = ["entity framework", "ef core", "sql", "linq", "orm", "database"],
                InstallationTargetVersion = "[17.14,)",
            },
        };

        /// <inheritdoc />
        protected override void InitializeServices(IServiceCollection serviceCollection)
        {
            base.InitializeServices(serviceCollection);

            // The command and the tool window must observe the same view model instance. Resolving the tool
            // window through GetToolWindow<T>() would race with its first creation, so the shared state is
            // injected into both instead.
            serviceCollection.AddSingleton<SqlPreviewSession>();
        }
    }
}
