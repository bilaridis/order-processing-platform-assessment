using System.Reflection;

namespace OrderProcessing.ArchitectureTests;

/// <summary>
/// The dependency rules of ADR-0002 as build-breaking tests. They inspect the compiled
/// assemblies, so a rule cannot be bypassed by adding a project reference: if code crosses a boundary,
/// CI fails. Rules are expressed on assembly names, which follow the convention
/// OrderProcessing.{Module}[.{Layer}].
/// </summary>
public class ModuleBoundaryTests
{
    private const string Prefix = "OrderProcessing.";
    private const string BuildingBlocks = "BuildingBlocks";

    private static readonly IReadOnlyList<Assembly> Platform =
    [
        .. Directory
            .GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom),
    ];

    private static IEnumerable<Assembly> Modules => Platform.Where(assembly => ModuleOf(assembly) is not (BuildingBlocks or "Host"));

    [Fact]
    public void The_platform_assemblies_are_discovered()
    {
        // Guards the other tests against silently passing on an empty list.
        Assert.Contains(Platform, assembly => assembly.GetName().Name == "OrderProcessing.Ordering.Domain");
        Assert.Equal(7, Modules.Select(ModuleOf).Distinct().Count());
    }

    [Fact]
    public void A_module_uses_other_modules_only_through_their_Contracts()
    {
        var violations =
            from assembly in Modules
            from reference in PlatformReferences(assembly)
            where ModuleOf(reference) != ModuleOf(assembly)
                && ModuleOf(reference) != BuildingBlocks
                && LayerOf(reference) != "Contracts"
            select $"{assembly.GetName().Name} -> {reference}";

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_depend_on_nothing_but_the_building_block_abstractions()
    {
        var violations =
            from assembly in Modules
            where LayerOf(assembly.GetName().Name!) == "Contracts"
            from reference in AllReferences(assembly)
            where !IsRuntime(reference)
                && reference is not (Prefix + "BuildingBlocks.Domain" or Prefix + "BuildingBlocks.Application")
            select $"{assembly.GetName().Name} -> {reference}";

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_depends_only_on_the_shared_kernel()
    {
        var violations =
            from assembly in Platform
            where LayerOf(assembly.GetName().Name!) == "Domain"
            from reference in AllReferences(assembly)
            where !IsRuntime(reference) && reference != Prefix + "BuildingBlocks.Domain"
            select $"{assembly.GetName().Name} -> {reference}";

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_knows_nothing_about_infrastructure_or_the_web()
    {
        var violations =
            from assembly in Platform
            where LayerOf(assembly.GetName().Name!) == "Application"
            from reference in AllReferences(assembly)
            where reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || LayerOf(reference) is "Infrastructure" or "Module" or "Functions"
            select $"{assembly.GetName().Name} -> {reference}";

        Assert.Empty(violations);
    }

    [Fact]
    public void Building_blocks_do_not_depend_on_modules()
    {
        var violations =
            from assembly in Platform
            where ModuleOf(assembly) == BuildingBlocks
            from reference in PlatformReferences(assembly)
            where ModuleOf(reference) != BuildingBlocks
            select $"{assembly.GetName().Name} -> {reference}";

        Assert.Empty(violations);
    }

    [Fact]
    public void Only_module_entry_points_and_options_are_public_outside_Contracts_and_Domain()
    {
        // Everything else is internal, so even a wrongly added project reference gives access to nothing.
        var violations =
            from assembly in Modules
            where LayerOf(assembly.GetName().Name!) is not ("Contracts" or "Domain")
            // The Order API is the presentation layer: its GraphQL schema is built from public types
            // (ADR-0013). No service may reference it, which the first test in this class enforces.
            where ModuleOf(assembly) != "OrderApi"
            from type in assembly.GetExportedTypes()
            where !type.Name.EndsWith("Module", StringComparison.Ordinal)
                && !type.Name.EndsWith("Options", StringComparison.Ordinal)
                && !type.Name.EndsWith("Infrastructure", StringComparison.Ordinal)
            select type.FullName;

        Assert.Empty(violations);
    }

    private static IEnumerable<string> AllReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(reference => reference.Name!);

    private static IEnumerable<string> PlatformReferences(Assembly assembly) =>
        AllReferences(assembly).Where(name => name.StartsWith(Prefix, StringComparison.Ordinal));

    private static bool IsRuntime(string assemblyName) =>
        assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName is "netstandard" or "mscorlib";

    private static string ModuleOf(Assembly assembly) => ModuleOf(assembly.GetName().Name!);

    /// <summary>"OrderProcessing.Ordering.Domain" → "Ordering".</summary>
    private static string ModuleOf(string assemblyName) => assemblyName[Prefix.Length..].Split('.')[0];

    /// <summary>"OrderProcessing.Ordering.Domain" → "Domain"; "OrderProcessing.Pricing" → "".</summary>
    private static string LayerOf(string assemblyName) =>
        assemblyName.StartsWith(Prefix, StringComparison.Ordinal) && assemblyName[Prefix.Length..].Split('.') is [_, var layer]
            ? layer
            : string.Empty;
}
