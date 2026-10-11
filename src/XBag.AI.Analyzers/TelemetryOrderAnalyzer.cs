using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG001: <c>UseOpenTelemetry()</c> before <c>UseFunctionInvocation()</c> in one <c>ChatClientBuilder</c> chain. The first
/// <c>Use</c> is the outermost client, so <c>FunctionInvokingChatClient</c> wraps a client without an <c>ActivitySource</c>
/// (its constructor reads <c>innerClient.GetService&lt;ActivitySource&gt;()</c>) and emits no tool spans.
/// </summary>
[DiagnosticAnalyzer(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class TelemetryOrderAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.TelemetryOutsideFunctionInvocation);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? builder = start.Compilation.GetTypeByMetadataName(KnownSymbols.ChatClientBuilder);
            INamedTypeSymbol? telemetry = start.Compilation.GetTypeByMetadataName(KnownSymbols.OpenTelemetryChatClientBuilderExtensions);
            INamedTypeSymbol? functions = start.Compilation.GetTypeByMetadataName(KnownSymbols.FunctionInvokingChatClientBuilderExtensions);
            if (builder is null || telemetry is null || functions is null)
            {
                return;
            }

            start.RegisterOperationAction(
                c => Analyze(c, (IInvocationOperation)c.Operation, builder, telemetry, functions),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(
        OperationAnalysisContext context, IInvocationOperation invocation, INamedTypeSymbol builder, INamedTypeSymbol telemetry, INamedTypeSymbol functions)
    {
        if (!Is(invocation, functions, "UseFunctionInvocation"))
        {
            return;
        }

        // A UseOpenTelemetry() after the tool loop (closer to the leaf) gives the loop its ActivitySource; an outer one on top
        // of that is a deliberate whole-request span.
        for (IInvocationOperation? next = invocation.GetNextCallInChain(); next is not null; next = next.GetNextCallInChain())
        {
            if (!SymbolEqualityComparer.Default.Equals(next.Type, builder))
            {
                break;
            }

            if (Is(next, telemetry, "UseOpenTelemetry"))
            {
                return;
            }
        }

        // Earlier calls in the same chain are outer clients.
        for (IOperation? receiver = invocation.GetReceiver(); receiver is IInvocationOperation earlier; receiver = earlier.GetReceiver())
        {
            if (!SymbolEqualityComparer.Default.Equals(earlier.Type, builder))
            {
                return;
            }

            if (Is(earlier, telemetry, "UseOpenTelemetry"))
            {
                Location location = earlier.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
                    ? member.Name.GetLocation()
                    : earlier.Syntax.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.TelemetryOutsideFunctionInvocation,
                    location,
                    additionalLocations: [invocation.Syntax.GetLocation()]));
                return;
            }
        }
    }

    private static bool Is(IInvocationOperation invocation, INamedTypeSymbol containingType, string name) =>
        invocation.TargetMethod.Name == name
        && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, containingType);
}
