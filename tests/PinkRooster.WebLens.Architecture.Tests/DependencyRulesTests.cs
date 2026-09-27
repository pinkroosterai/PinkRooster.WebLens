using System.Reflection;
using NetArchTest.Rules;

namespace PinkRooster.WebLens.Architecture.Tests;

/// <summary>Which assembly may reference which. MCP, like HTTP, is the host's concern.</summary>
public class DependencyRulesTests
{
    private const string SearchNs = "PinkRooster.WebLens.Search";
    private const string FetchNs = "PinkRooster.WebLens.Fetch";
    private const string ApiNs = "PinkRooster.WebLens.Api";

    private static Assembly Load(string name) => Assembly.Load(name);

    private static readonly Assembly Search = Load(SearchNs);
    private static readonly Assembly Fetch = Load(FetchNs);
    private static readonly Assembly Api = Load(ApiNs);

    public static TheoryData<Assembly, string[]> Forbidden => new()
    {
        { Search, [FetchNs, ApiNs, "Microsoft.AspNetCore", "ModelContextProtocol"] },
        { Fetch, [SearchNs, ApiNs, "Microsoft.AspNetCore", "ModelContextProtocol"] },
    };

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void Module_assembly_does_not_reference_forbidden_assemblies(Assembly module, string[] forbidden)
    {
        var referenced = module.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        var offending = referenced.Where(r => forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal))).ToList();

        Assert.True(offending.Count == 0, $"{module.GetName().Name} references: {string.Join(", ", offending)}");
    }

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void Module_types_do_not_use_forbidden_namespaces(Assembly module, string[] forbidden)
    {
        var result = Types.InAssembly(module).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Each_module_exposes_one_service_interface()
    {
        Assert.Single(Search.GetExportedTypes(), t => t.IsInterface);
        Assert.Single(Fetch.GetExportedTypes(), t => t.IsInterface);
    }

    [Fact]
    public void Api_references_the_modules()
    {
        var referenced = Api.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains(SearchNs, referenced);
        Assert.Contains(FetchNs, referenced);
    }
}
