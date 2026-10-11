using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG006: <c>response.Messages = ...</c> inside agent-run middleware: a delegate passed to <c>AIAgentBuilder.Use(...)</c> or
/// a <c>RunCoreAsync</c> override of an <c>AIAgent</c> subclass. <c>OpenTelemetryAgent</c> turns the response back into the
/// <c>ChatResponse</c> held in <c>RawRepresentation</c>, which still has the old list. Object initializers
/// (<c>new AgentResponse { Messages = ... }</c>) are fine: a new response has no stale raw representation.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AgentResponseMessagesAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.AgentResponseMessagesReplaced);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? response = start.Compilation.GetTypeByMetadataName(KnownSymbols.AgentResponse);
            INamedTypeSymbol? agent = start.Compilation.GetTypeByMetadataName(KnownSymbols.AIAgent);
            INamedTypeSymbol? builder = start.Compilation.GetTypeByMetadataName(KnownSymbols.AIAgentBuilder);
            if (response is null || agent is null)
            {
                return;
            }

            start.RegisterOperationAction(c => Analyze(c, response, agent, builder), OperationKind.SimpleAssignment);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol response, INamedTypeSymbol agent, INamedTypeSymbol? builder)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is not IPropertyReferenceOperation { Property.Name: "Messages" } target
            || !target.Property.ContainingType.InheritsFromOrEquals(response)
            || target.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver })
        {
            return;
        }

        if (InRunMiddleware(assignment, context.ContainingSymbol, agent, builder))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.AgentResponseMessagesReplaced, assignment.Syntax.GetLocation()));
        }
    }

    private static bool InRunMiddleware(IOperation operation, ISymbol containingSymbol, INamedTypeSymbol agent, INamedTypeSymbol? builder)
    {
        // A lambda (at any depth) passed to AIAgentBuilder.Use(...).
        for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is not IAnonymousFunctionOperation)
            {
                continue;
            }

            IOperation? parent = current.Parent;
            while (parent is IDelegateCreationOperation or IConversionOperation)
            {
                parent = parent.Parent;
            }

            if (parent is IArgumentOperation { Parent: IInvocationOperation invocation }
                && invocation.TargetMethod.Name == "Use"
                && builder is not null
                && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, builder))
            {
                return true;
            }
        }

        // RunCoreAsync of a custom or delegating agent.
        return containingSymbol is IMethodSymbol { Name: KnownSymbols.RunCoreAsync, IsOverride: true } method
            && method.ContainingType.InheritsFromOrEquals(agent);
    }
}
