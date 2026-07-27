using EFCoreSqlPreview.Core.Projects;
using EFCoreSqlPreview.Core.Tests.Fakes;

namespace EFCoreSqlPreview.Core.Tests.Projects;

/// <summary>
/// Behavioural tests for <see cref="ProviderDetector"/>: package references, central package management,
/// property expansion, transitive project references and the solution-wide fallback.
/// </summary>
public class ProviderDetectorTests : IDisposable
{
    private readonly TempWorkspace workspace = new();
    private readonly ProviderDetector detector = new();

    /// <inheritdoc />
    public void Dispose() => this.workspace.Dispose();

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", EfProvider.SqlServer)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", EfProvider.PostgreSql)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", EfProvider.Sqlite)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", EfProvider.MySql)]
    [InlineData("Oracle.EntityFrameworkCore", EfProvider.Oracle)]
    public void A_PackageReference_in_the_project_identifies_the_provider(string packageId, EfProvider expected)
    {
        var project = this.workspace.Write("App/App.csproj", Csproj("net10.0", (packageId, "9.0.4")));

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(expected);
        context.ProviderPackageVersion.ShouldBe("9.0.4");
        context.ProjectName.ShouldBe("App");
        context.ProjectPath.ShouldBe(project);
        context.TargetFramework.ShouldBe("net10.0");
    }

    [Fact]
    public void The_package_id_is_matched_case_insensitively()
    {
        var project = this.workspace.Write("App/App.csproj", Csproj("net10.0", ("microsoft.entityframeworkcore.sqlite", "10.0.10")));

        this.detector.Detect(project).Provider.ShouldBe(EfProvider.Sqlite);
    }

    [Fact]
    public void A_project_with_no_provider_package_is_Unknown()
    {
        var project = this.workspace.Write("App/App.csproj", Csproj("net10.0", ("Serilog", "4.0.0")));

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.Unknown);
        context.ProviderPackageVersion.ShouldBeNull();
        context.AdditionalProjectPaths.ShouldBeEmpty();
    }

    [Fact]
    public void Central_package_management_supplies_the_version()
    {
        this.workspace.Write(
            "Directory.Packages.props",
            """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.10" />
                <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.10" />
              </ItemGroup>
            </Project>
            """);

        var project = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
                <PackageReference Include="Microsoft.EntityFrameworkCore" />
              </ItemGroup>
            </Project>
            """);

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.SqlServer);
        context.ProviderPackageVersion.ShouldBe("10.0.10");
        context.EfCoreVersion.ShouldBe("10.0.10");
        context.EfCoreMajorVersion.ShouldBe(10);
    }

    [Fact]
    public void A_VersionOverride_wins_over_the_central_version()
    {
        this.workspace.Write(
            "Directory.Packages.props",
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"9.0.0\" /></ItemGroup></Project>");

        var project = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" VersionOverride="10.0.10" />
              </ItemGroup>
            </Project>
            """);

        this.detector.Detect(project).ProviderPackageVersion.ShouldBe("10.0.10");
    }

    [Fact]
    public void A_version_written_as_an_MSBuild_property_is_expanded_from_Directory_Build_props()
    {
        this.workspace.Write(
            "Directory.Build.props",
            "<Project><PropertyGroup><EfCoreVersion>10.0.10</EfCoreVersion></PropertyGroup></Project>");

        var project = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="$(EfCoreVersion)" />
              </ItemGroup>
            </Project>
            """);

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.PostgreSql);
        context.ProviderPackageVersion.ShouldBe("10.0.10");
    }

    [Fact]
    public void An_unresolvable_property_version_becomes_null_rather_than_a_literal_dollar_sign()
    {
        var project = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="$(NeverDefined)" />
              </ItemGroup>
            </Project>
            """);

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.SqlServer);
        context.ProviderPackageVersion.ShouldBeNull();
        context.EfCoreMajorVersion.ShouldBeNull();
    }

    [Fact]
    public void The_provider_is_found_through_a_transitive_ProjectReference()
    {
        this.workspace.Write("Data/Data.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore.SqlServer", "10.0.10")));
        var app = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Data/Data.csproj" /></ItemGroup>
            </Project>
            """);

        var context = this.detector.Detect(app);

        context.Provider.ShouldBe(EfProvider.SqlServer);
        context.ProviderPackageVersion.ShouldBe("10.0.10");

        // The provider project is already in the reference closure, so #:project needs no extra entry.
        context.AdditionalProjectPaths.ShouldBeEmpty();
        context.AllProjectPaths.ShouldHaveSingleItem().ShouldBe(app);
    }

    [Fact]
    public void The_root_project_wins_over_a_referenced_one_when_both_carry_a_provider()
    {
        this.workspace.Write("Data/Data.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore.Sqlite", "10.0.10")));
        var app = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.10" />
                <ProjectReference Include="../Data/Data.csproj" />
              </ItemGroup>
            </Project>
            """);

        this.detector.Detect(app).Provider.ShouldBe(EfProvider.SqlServer);
    }

    [Fact]
    public void A_provider_bearing_sibling_in_the_solution_is_added_to_the_project_paths()
    {
        this.workspace.Write(
            "Shop.slnx",
            """
            <Solution>
              <Project Path="Lib/Lib.csproj" />
              <Project Path="Host/Host.csproj" />
            </Solution>
            """);

        var lib = this.workspace.Write("Lib/Lib.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore", "10.0.10")));
        var host = this.workspace.Write("Host/Host.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore.SqlServer", "10.0.10")));

        var context = this.detector.Detect(lib);

        context.Provider.ShouldBe(EfProvider.SqlServer);
        context.AdditionalProjectPaths.ShouldHaveSingleItem().ShouldBe(host);
        context.AllProjectPaths.ShouldBe(new[] { lib, host });
    }

    [Fact]
    public void A_provider_bearing_sibling_is_also_found_through_a_classic_sln()
    {
        this.workspace.Write(
            "Shop.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Host", "Host\Host.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var lib = this.workspace.Write("Lib/Lib.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore", "10.0.10")));
        var host = this.workspace.Write("Host/Host.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore.Sqlite", "10.0.10")));

        var context = this.detector.Detect(lib);

        context.Provider.ShouldBe(EfProvider.Sqlite);
        context.AdditionalProjectPaths.ShouldHaveSingleItem().ShouldBe(host);
    }

    [Fact]
    public void The_EF_Core_version_comes_from_the_core_package_when_the_provider_is_versioned_differently()
    {
        var project = this.workspace.Write(
            "App/App.csproj",
            Csproj("net10.0", ("Microsoft.EntityFrameworkCore", "10.0.10"), ("Pomelo.EntityFrameworkCore.MySql", "9.0.0")));

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.MySql);
        context.ProviderPackageVersion.ShouldBe("9.0.0");
        context.EfCoreVersion.ShouldBe("10.0.10");
        context.EfCoreMajorVersion.ShouldBe(10);
    }

    [Fact]
    public void The_EF_Core_version_falls_back_to_the_provider_version()
    {
        var project = this.workspace.Write("App/App.csproj", Csproj("net10.0", ("Microsoft.EntityFrameworkCore.SqlServer", "8.0.11")));

        var context = this.detector.Detect(project);

        context.EfCoreVersion.ShouldBe("8.0.11");
        context.EfCoreMajorVersion.ShouldBe(8);
    }

    [Fact]
    public void The_first_of_several_target_frameworks_is_reported()
    {
        var project = this.workspace.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.10" /></ItemGroup>
            </Project>
            """);

        this.detector.Detect(project).TargetFramework.ShouldBe("net8.0");
    }

    [Fact]
    public void A_project_file_that_cannot_be_parsed_yields_an_Unknown_context_rather_than_throwing()
    {
        var project = this.workspace.Write("App/App.csproj", "<Project><ItemGroup>");

        var context = this.detector.Detect(project);

        context.Provider.ShouldBe(EfProvider.Unknown);
        context.TargetFramework.ShouldBeNull();
    }

    [Fact]
    public void A_project_path_that_does_not_exist_yields_an_Unknown_context()
    {
        var context = this.detector.Detect(this.workspace.At("Ghost", "Ghost.csproj"));

        context.Provider.ShouldBe(EfProvider.Unknown);
        context.ProjectName.ShouldBe("Ghost");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_rejects_a_blank_project_path(string path)
        => Should.Throw<ArgumentException>(() => this.detector.Detect(path));

    [Fact]
    public void ReadPackages_exposes_the_resolved_package_table()
    {
        var project = this.workspace.Write(
            "App/App.csproj",
            Csproj("net10.0", ("Microsoft.EntityFrameworkCore.SqlServer", "10.0.10"), ("Serilog", "4.0.0")));

        var packages = this.detector.ReadPackages(project);

        packages.ProjectPath.ShouldBe(project);
        packages.TargetFramework.ShouldBe("net10.0");
        packages.Packages["Microsoft.EntityFrameworkCore.SqlServer"].ShouldBe("10.0.10");
        packages.Packages["serilog"].ShouldBe("4.0.0");
    }

    [Fact]
    public void Constructing_without_collaborators_is_rejected()
    {
        Should.Throw<ArgumentNullException>(() => new ProviderDetector(null!));
        Should.Throw<ArgumentNullException>(() => new ProviderDetector(new ProjectLocator(), null!));
    }

    private static string Csproj(string targetFramework, params (string Id, string Version)[] packages)
        => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{targetFramework}</TargetFramework></PropertyGroup>
              <ItemGroup>
            {string.Join("\n", packages.Select(p => $"    <PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />"))}
              </ItemGroup>
            </Project>
            """;
}
