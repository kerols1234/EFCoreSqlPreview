using EFCoreSqlPreview.Core.Projects;
using EFCoreSqlPreview.Core.Tests.Fakes;

namespace EFCoreSqlPreview.Core.Tests.Projects;

/// <summary>
/// Behavioural tests for <see cref="ProjectLocator"/> over a real temp directory tree.
/// </summary>
public class ProjectLocatorTests : IDisposable
{
    private const string Empty = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private readonly TempWorkspace workspace = new();
    private readonly ProjectLocator locator = new();

    /// <inheritdoc />
    public void Dispose() => this.workspace.Dispose();

    [Fact]
    public void FindOwningProject_finds_the_project_in_the_documents_own_folder()
    {
        var project = this.workspace.Write("App/App.csproj", Empty);
        var document = this.workspace.At("App", "Service.cs");

        this.locator.FindOwningProject(document).ShouldBe(project);
    }

    [Fact]
    public void FindOwningProject_walks_up_through_intermediate_folders()
    {
        var project = this.workspace.Write("App/App.csproj", Empty);
        var document = this.workspace.At("App", "Features", "Catalog", "Queries.cs");

        this.locator.FindOwningProject(document).ShouldBe(project);
    }

    [Fact]
    public void FindOwningProject_stops_at_the_nearest_project_not_the_outermost()
    {
        this.workspace.Write("Outer.csproj", Empty);
        var inner = this.workspace.Write("App/Inner.csproj", Empty);
        var document = this.workspace.At("App", "Sub", "Service.cs");

        this.locator.FindOwningProject(document).ShouldBe(inner);
    }

    [Fact]
    public void FindOwningProject_returns_null_when_the_document_is_under_no_project()
    {
        var document = this.workspace.At("Loose", "Notes", "Service.cs");

        this.locator.FindOwningProject(document).ShouldBeNull();
    }

    [Fact]
    public void FindOwningProject_prefers_the_project_named_after_its_folder()
    {
        this.workspace.Write("App/Alpha.csproj", Empty);
        var expected = this.workspace.Write("App/App.csproj", Empty);
        this.workspace.Write("App/Zulu.csproj", Empty);

        this.locator.FindOwningProject(this.workspace.At("App", "Service.cs")).ShouldBe(expected);
    }

    [Fact]
    public void FindOwningProject_falls_back_to_the_first_candidate_when_no_name_matches()
    {
        var first = this.workspace.Write("App/Alpha.csproj", Empty);
        this.workspace.Write("App/Zulu.csproj", Empty);

        this.locator.FindOwningProject(this.workspace.At("App", "Service.cs")).ShouldBe(first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FindOwningProject_returns_null_for_a_blank_path(string? path)
        => this.locator.FindOwningProject(path!).ShouldBeNull();

    [Fact]
    public void FindOwningProject_accepts_a_directory_as_well_as_a_file()
    {
        var project = this.workspace.Write("App/App.csproj", Empty);
        var directory = this.workspace.Directory_("App/Features");

        this.locator.FindOwningProject(directory).ShouldBe(project);
    }

    [Fact]
    public void GetTransitiveProjectReferences_follows_the_chain_to_the_DbContext_project()
    {
        var app = this.workspace.Write("App/App.csproj", Reference("../Data/Data.csproj"));
        this.workspace.Write("Data/Data.csproj", Reference("../Domain/Domain.csproj"));
        var domain = this.workspace.At("Domain", "Domain.csproj");
        this.workspace.Write("Domain/Domain.csproj", Empty);

        var references = this.locator.GetTransitiveProjectReferences(app);

        references.Count.ShouldBe(2);
        references[0].ShouldBe(this.workspace.At("Data", "Data.csproj"));
        references[1].ShouldBe(domain);
        references.ShouldNotContain(app);
    }

    [Fact]
    public void GetTransitiveProjectReferences_visits_a_diamond_once()
    {
        var app = this.workspace.Write("App/App.csproj", Reference("../Left/Left.csproj", "../Right/Right.csproj"));
        this.workspace.Write("Left/Left.csproj", Reference("../Shared/Shared.csproj"));
        this.workspace.Write("Right/Right.csproj", Reference("../Shared/Shared.csproj"));
        this.workspace.Write("Shared/Shared.csproj", Empty);

        var references = this.locator.GetTransitiveProjectReferences(app);

        references.Count.ShouldBe(3);
        references.Count(p => p.EndsWith("Shared.csproj", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public void GetTransitiveProjectReferences_terminates_on_a_cycle()
    {
        var a = this.workspace.Write("A/A.csproj", Reference("../B/B.csproj"));
        this.workspace.Write("B/B.csproj", Reference("../A/A.csproj"));

        this.locator.GetTransitiveProjectReferences(a).ShouldHaveSingleItem()
            .ShouldBe(this.workspace.At("B", "B.csproj"));
    }

    [Fact]
    public void GetTransitiveProjectReferences_skips_unevaluated_properties_and_globs()
    {
        var app = this.workspace.Write(
            "App/App.csproj",
            Reference("$(SharedRoot)/Shared.csproj", "../Plugins/*/Plugin.csproj", "../Data/Data.csproj"));
        this.workspace.Write("Data/Data.csproj", Empty);

        this.locator.GetTransitiveProjectReferences(app).ShouldHaveSingleItem()
            .ShouldBe(this.workspace.At("Data", "Data.csproj"));
    }

    [Fact]
    public void GetTransitiveProjectReferences_skips_references_that_are_not_on_disk()
    {
        var app = this.workspace.Write("App/App.csproj", Reference("../Gone/Gone.csproj"));

        this.locator.GetTransitiveProjectReferences(app).ShouldBeEmpty();
    }

    [Fact]
    public void ReadProjectReferences_accepts_forward_slashes()
    {
        var app = this.workspace.Write("App/App.csproj", Reference("../Data/Data.csproj"));
        this.workspace.Write("Data/Data.csproj", Empty);

        this.locator.ReadProjectReferences(app).ShouldHaveSingleItem()
            .ShouldBe(this.workspace.At("Data", "Data.csproj"));
    }

    [Fact]
    public void ReadProjectReferences_accepts_back_slashes()
    {
        var app = this.workspace.Write("App/App.csproj", Reference(@"..\Data\Data.csproj"));
        this.workspace.Write("Data/Data.csproj", Empty);

        this.locator.ReadProjectReferences(app).ShouldHaveSingleItem()
            .ShouldBe(this.workspace.At("Data", "Data.csproj"));
    }

    [Fact]
    public void ReadProjectReferences_reads_through_an_xml_namespace()
    {
        var app = this.workspace.Write(
            "App/App.csproj",
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <ProjectReference Include="../Data/Data.csproj" />
              </ItemGroup>
            </Project>
            """);
        this.workspace.Write("Data/Data.csproj", Empty);

        this.locator.ReadProjectReferences(app).ShouldHaveSingleItem();
    }

    [Fact]
    public void LoadProjectXml_returns_null_for_malformed_xml()
    {
        var broken = this.workspace.Write("App/App.csproj", "<Project><ItemGroup></Project>");

        this.locator.LoadProjectXml(broken).ShouldBeNull();
        this.locator.ReadProjectReferences(broken).ShouldBeEmpty();
        this.locator.GetTransitiveProjectReferences(broken).ShouldBeEmpty();
    }

    [Fact]
    public void LoadProjectXml_returns_null_for_a_missing_or_empty_file()
    {
        this.locator.LoadProjectXml(this.workspace.At("nope.csproj")).ShouldBeNull();
        this.locator.LoadProjectXml(this.workspace.Write("empty.csproj", "   ")).ShouldBeNull();
    }

    [Fact]
    public void GetTransitiveProjectReferences_returns_empty_for_a_blank_path()
        => this.locator.GetTransitiveProjectReferences("  ").ShouldBeEmpty();

    [Fact]
    public void Constructing_without_a_file_system_is_rejected()
        => Should.Throw<ArgumentNullException>(() => new ProjectLocator(null!));

    private static string Reference(params string[] includes)
        => "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + string.Concat(includes.Select(i => $"<ProjectReference Include=\"{i}\" />"))
            + "</ItemGroup></Project>";
}
