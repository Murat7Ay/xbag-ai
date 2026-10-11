using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG003: a <c>DelegatingChatClient</c> subclass that overrides only one of <c>GetResponseAsync</c> /
/// <c>GetStreamingResponseAsync</c>, or a <c>DelegatingAIAgent</c> subclass that overrides only one of <c>RunCoreAsync</c> /
/// <c>RunCoreStreamingAsync</c>. The other path forwards straight to the inner instance and skips the middleware.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StreamingParityAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.StreamingParity);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? chatClient = start.Compilation.GetTypeByMetadataName(KnownSymbols.DelegatingChatClient);
            INamedTypeSymbol? agent = start.Compilation.GetTypeByMetadataName(KnownSymbols.DelegatingAIAgent);
            if (chatClient is null && agent is null)
            {
                return;
            }

            start.RegisterSymbolAction(c =>
            {
                var type = (INamedTypeSymbol)c.Symbol;
                if (type.TypeKind != TypeKind.Class || type.IsAbstract)
                {
                    return;
                }

                Check(c, type, chatClient, KnownSymbols.GetResponseAsync, KnownSymbols.GetStreamingResponseAsync);
                Check(c, type, agent, KnownSymbols.RunCoreAsync, KnownSymbols.RunCoreStreamingAsync);
            }, SymbolKind.NamedType);
        });
    }

    private static void Check(SymbolAnalysisContext context, INamedTypeSymbol type, INamedTypeSymbol? delegating, string buffered, string streaming)
    {
        if (delegating is null || !type.InheritsFromOrEquals(delegating))
        {
            return;
        }

        // Overrides anywhere between this class and the Microsoft base count (a user base class may do one half).
        IMethodSymbol? bufferedOverride = FindOverride(type, delegating, buffered);
        IMethodSymbol? streamingOverride = FindOverride(type, delegating, streaming);
        if ((bufferedOverride is null) == (streamingOverride is null))
        {
            return;
        }

        IMethodSymbol present = bufferedOverride ?? streamingOverride!;

        // On the lone override when this class declares it, else on the class (the override is in a user base class).
        ImmutableArray<Location> locations = SymbolEqualityComparer.Default.Equals(present.ContainingType, type)
            ? present.Locations
            : type.Locations;

        bool onlyBuffered = bufferedOverride is not null;
        foreach (Location location in locations)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.StreamingParity,
                location,
                type.Name,
                onlyBuffered ? buffered : streaming,
                onlyBuffered ? streaming : buffered,
                onlyBuffered ? "streaming" : "non-streaming"));
        }
    }

    private static IMethodSymbol? FindOverride(INamedTypeSymbol type, INamedTypeSymbol delegating, string name)
    {
        for (INamedTypeSymbol? current = type; current is not null && !SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, delegating); current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers(name))
            {
                if (member is IMethodSymbol { IsOverride: true } method && method.Overrides(delegating))
                {
                    return method;
                }
            }
        }

        return null;
    }
}
