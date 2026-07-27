using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Covers collecting the using directives and namespace context the worker must reproduce.
/// </summary>
public class UsingsTests
{
    private const string MixedUsings =
        "global using System.Threading.Tasks;\r\n" +
        "using System;\r\n" +
        "using System.Linq;\r\n" +
        "using EF = Microsoft.EntityFrameworkCore;";

    [Fact]
    public void Collect_MixedDirectives_ReportsGlobalStaticAndAliasFlags()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.ToList()|];",
            usings: MixedUsings);

        var directives = result.Usings.Directives;
        directives.Count.ShouldBe(4);
        directives[0].Name.ShouldBe("System.Threading.Tasks");
        directives[0].IsGlobal.ShouldBeTrue();
        directives[1].Name.ShouldBe("System");
        directives[1].IsGlobal.ShouldBeFalse();
        directives[3].Alias.ShouldBe("EF");
        directives[3].Name.ShouldBe("Microsoft.EntityFrameworkCore");
        directives.ShouldAllBe(d => d.FullText.EndsWith(";"));
    }

    [Fact]
    public void Collect_FileScopedNamespace_ReportsTheNamespaceAndItsChain()
    {
        var result = Fixture.Analyze("        var x = [|_db.Products.ToList()|];");

        result.Usings.ContainingNamespace.ShouldBe("Demo.Services");
        result.Usings.NamespaceChain.ShouldBe(new[] { "Demo", "Demo.Services" });
        result.Namespace.ShouldBe("Demo.Services");
    }

    [Fact]
    public void Collect_UsingsInsideABlockNamespace_AreCollectedToo()
    {
        var document =
            "using System;\r\n" +
            "\r\n" +
            "namespace Demo\r\n" +
            "{\r\n" +
            "    using System.Linq;\r\n" +
            "\r\n" +
            "    public class Service\r\n" +
            "    {\r\n" +
            "        private readonly AppDbContext _db;\r\n" +
            "\r\n" +
            "        public void Run()\r\n" +
            "        {\r\n" +
            "            var x = [|_db.Products.ToList()|];\r\n" +
            "        }\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.Usings.Directives.Select(d => d.Name).ShouldBe(new[] { "System", "System.Linq" });
        result.Usings.ContainingNamespace.ShouldBe("Demo");
    }

    [Fact]
    public void Collect_NestedBlockNamespaces_JoinTheNamespaceName()
    {
        var document =
            "namespace Outer\r\n" +
            "{\r\n" +
            "    namespace Inner\r\n" +
            "    {\r\n" +
            "        public class Service\r\n" +
            "        {\r\n" +
            "            private readonly AppDbContext _db;\r\n" +
            "\r\n" +
            "            public void Run()\r\n" +
            "            {\r\n" +
            "                var x = [|_db.Products.ToList()|];\r\n" +
            "            }\r\n" +
            "        }\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.Usings.ContainingNamespace.ShouldBe("Outer.Inner");
        result.Usings.NamespaceChain.ShouldBe(new[] { "Outer", "Outer.Inner" });
    }

    [Fact]
    public void Collect_TopLevelStatements_ReportNoNamespace()
    {
        var document =
            "using System.Linq;\r\n" +
            "\r\n" +
            "var db = new AppDbContext();\r\n" +
            "var items = [|db.Products.ToList()|];\r\n";

        var result = Fixture.AnalyzeRaw(document);

        result.Usings.ContainingNamespace.ShouldBeNull();
        result.Usings.NamespaceChain.ShouldBeEmpty();
    }

    [Fact]
    public void ToWorkerUsingLines_StripsGlobalSubtractsImplicitAndAppendsTheNamespaceChain()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.ToList()|];",
            usings: MixedUsings);

        var lines = result.Usings.ToWorkerUsingLines();

        lines.ShouldContain("using EF = Microsoft.EntityFrameworkCore;");
        lines.ShouldContain("using Demo;");
        lines.ShouldContain("using Demo.Services;");
        lines.ShouldNotContain("using System;");
        lines.ShouldNotContain("using System.Linq;");
        lines.ShouldAllBe(l => !l.StartsWith("global "));
    }

    [Fact]
    public void ToWorkerUsingLines_WithoutImplicitSubtraction_KeepsEveryDirective()
    {
        var result = Fixture.Analyze(
            "        var x = [|_db.Products.ToList()|];",
            usings: MixedUsings);

        var lines = result.Usings.ToWorkerUsingLines(subtractImplicitUsings: false);

        lines.ShouldContain("using System;");
        lines.ShouldContain("using System.Linq;");
        lines.ShouldContain("using System.Threading.Tasks;");
    }

    [Fact]
    public void ToWorkerUsingLines_PreservesUsingStaticAndCollapsesDuplicates()
    {
        var usings =
            "using static System.Math;\r\n" +
            "using Demo.Models;\r\n" +
            "using Demo.Models;";

        var result = Fixture.Analyze("        var x = [|_db.Products.ToList()|];", usings: usings);

        var lines = result.Usings.ToWorkerUsingLines();

        lines.Count(l => l == "using Demo.Models;").ShouldBe(1);
        lines.ShouldContain("using static System.Math;");
    }

    [Fact]
    public void ToWorkerUsingLines_GlobalAndPlainOfTheSameNamespace_CollapseToOneLine()
    {
        var usings =
            "global using Demo.Models;\r\n" +
            "using Demo.Models;";

        var result = Fixture.Analyze("        var x = [|_db.Products.ToList()|];", usings: usings);

        var lines = result.Usings.ToWorkerUsingLines();

        lines.Count(l => l == "using Demo.Models;").ShouldBe(1);
    }

    [Fact]
    public void ToWorkerUsingLines_AliasesComeBeforeStaticsAndPlainDirectives()
    {
        var usings =
            "using Demo.Models;\r\n" +
            "using static System.Math;\r\n" +
            "using EF = Microsoft.EntityFrameworkCore;";

        var result = Fixture.Analyze("        var x = [|_db.Products.ToList()|];", usings: usings);

        var lines = result.Usings.ToWorkerUsingLines();

        lines[0].ShouldBe("using EF = Microsoft.EntityFrameworkCore;");
        lines[1].ShouldBe("using static System.Math;");
        lines[2].ShouldBe("using Demo.Models;");
    }

    [Fact]
    public void ToWorkerUsingLines_OnTheEmptyInfo_ProducesNothing()
        => UsingsInfo.Empty.ToWorkerUsingLines().ShouldBeEmpty();
}
