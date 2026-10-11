using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace XBag.AI.Analyzers.Tests;

/// <summary>
/// Runs an analyzer (and optionally its code fix) over a C# sample compiled against the real Microsoft packages. Expected
/// diagnostics are written in the sample as <c>{|XBAG001:span|}</c>; anything else the analyzer reports fails the test.
/// </summary>
internal static class Verify
{
    private static readonly ImmutableArray<PackageIdentity> AIPackages =
    [
        new("Microsoft.Extensions.AI", "10.10.0"),
        new("Microsoft.Agents.AI", "1.24.0"),
    ];

    // .NET 10 reference assemblies. Not .NET 8: Agent Framework 1.24.0 is built against System.Text.Json 10.0, and the .NET 8
    // reference pack's System.Text.Json 8.0 wins over the package, so every sample fails with CS1705. (The testing library's
    // built-in list stops at .NET 8/9; this is the same shape it uses for those.)
    private static readonly ReferenceAssemblies Net10 = new(
        "net10.0",
        new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0"),
        Path.Combine("ref", "net10.0"));

    /// <summary>.NET 10 + Microsoft.Extensions.AI 10.10.0 + Microsoft.Agents.AI 1.24.0.</summary>
    public static readonly ReferenceAssemblies Agents = Net10.AddPackages(AIPackages);

    /// <summary>The same plus Microsoft.Agents.AI.Workflows 1.24.0 (a project that builds workflows).</summary>
    public static readonly ReferenceAssemblies Workflows = Agents.AddPackages([new("Microsoft.Agents.AI.Workflows", "1.24.0")]);

    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Agents.AI;
        using Microsoft.Extensions.AI;

        """;

    public static Task AnalyzerAsync<TAnalyzer>(string source, ReferenceAssemblies? references = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = Usings + source,
            ReferenceAssemblies = references ?? Agents,
        };
        test.SolutionTransforms.Add(EnableNullable);
        return test.RunAsync();
    }

    public static Task CodeFixAsync<TAnalyzer, TCodeFix>(string source, string fixedSource, ReferenceAssemblies? references = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            TestCode = Usings + source,
            FixedCode = Usings + fixedSource,
            ReferenceAssemblies = references ?? Agents,
        };
        test.SolutionTransforms.Add(EnableNullable);
        return test.RunAsync();
    }

    private static Solution EnableNullable(Solution solution, ProjectId projectId)
    {
        var options = (CSharpCompilationOptions)solution.GetProject(projectId)!.CompilationOptions!;
        return solution.WithProjectCompilationOptions(projectId, options.WithNullableContextOptions(NullableContextOptions.Enable));
    }
}
