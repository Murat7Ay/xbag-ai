using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG005 (info): an agent without a stable id. Always reported when <c>ChatClientAgentOptions.Id</c> is set from
/// <c>Guid.NewGuid()</c>. In projects that reference Agent Framework workflows (where executor ids derive from the agent's
/// Name and Id and checkpoints depend on them), also reported for <c>new ChatClientAgentOptions { ... }</c> without an
/// <c>Id</c>, and for the <c>AsAIAgent(instructions, name, ...)</c> / <c>new ChatClientAgent(client, instructions, name, ...)</c>
/// overloads, which cannot set one.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AgentIdAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property with the constant agent Name, when the options set one (used by the code fix).</summary>
    public const string NameProperty = "Name";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.UnstableAgentId);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? options = start.Compilation.GetTypeByMetadataName(KnownSymbols.ChatClientAgentOptions);
            if (options is null)
            {
                return;
            }

            INamedTypeSymbol? agent = start.Compilation.GetTypeByMetadataName(KnownSymbols.ChatClientAgent);
            bool usesWorkflows = start.Compilation.GetTypeByMetadataName(KnownSymbols.Workflow) is not null;

            start.RegisterOperationAction(c => AnalyzeAssignment(c, options), OperationKind.SimpleAssignment);

            if (usesWorkflows)
            {
                start.RegisterOperationAction(c => AnalyzeOptionsCreation(c, options, agent), OperationKind.ObjectCreation);
                start.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
            }
        });
    }

    // Id = Guid.NewGuid().ToString(...): random on purpose, still random.
    private static void AnalyzeAssignment(OperationAnalysisContext context, INamedTypeSymbol options)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is IPropertyReferenceOperation { Property.Name: "Id" } property
            && SymbolEqualityComparer.Default.Equals(property.Property.ContainingType, options)
            && assignment.Value.DescendantsAndSelf().Any(o => o is IInvocationOperation { TargetMethod: { Name: "NewGuid", ContainingType: var type } } && type.ToDisplayString() == "System.Guid"))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnstableAgentId,
                assignment.Syntax.GetLocation(),
                "The agent id is a new GUID per instance; use a fixed string so a rebuilt workflow keeps its executor ids and can resume checkpoints"));
        }
    }

    private static void AnalyzeOptionsCreation(OperationAnalysisContext context, INamedTypeSymbol options, INamedTypeSymbol? agent)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is { } type && SymbolEqualityComparer.Default.Equals(type, agent) && IsNameOnlyOverload(creation.Constructor))
        {
            ReportNoIdOverload(context, creation, "new ChatClientAgent(client, instructions, name, ...)");
            return;
        }

        if (!SymbolEqualityComparer.Default.Equals(creation.Type, options))
        {
            return;
        }

        if (creation.Initializer?.Initializers.Any(i => i is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Id" } }) == true)
        {
            return;
        }

        // var o = new ChatClientAgentOptions { ... }; o.Id = "...";  (or a field / property set later in the same body)
        if (AssignedLater(creation))
        {
            return;
        }

        // A constant Name is a natural stable id; the code fix offers it.
        string? name = creation.Initializer?.Initializers
            .OfType<ISimpleAssignmentOperation>()
            .FirstOrDefault(i => i.Target is IPropertyReferenceOperation { Property.Name: "Name" })?
            .Value.ConstantValue is { HasValue: true, Value: string constant } ? constant : null;

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.UnstableAgentId,
            creation.Syntax.GetLocation(),
            name is null ? ImmutableDictionary<string, string?>.Empty : ImmutableDictionary<string, string?>.Empty.Add(NameProperty, name),
            "ChatClientAgentOptions has no Id, so the agent gets a random GUID per instance; set a fixed Id so a rebuilt workflow keeps its executor ids and can resume checkpoints"));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        // AsAIAgent lives in Microsoft.Extensions.AI.ChatClientExtensions *in the Agent Framework assembly*: the same metadata
        // name as Microsoft.Extensions.AI's own ChatClientExtensions, so GetTypeByMetadataName returns null. Match the assembly.
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.Name == "AsAIAgent"
            && invocation.TargetMethod.ContainingAssembly?.Name == "Microsoft.Agents.AI"
            && IsNameOnlyOverload(invocation.TargetMethod))
        {
            ReportNoIdOverload(context, invocation, "AsAIAgent(instructions, name, ...)");
        }
    }

    // The overloads that take `string? instructions` instead of ChatClientAgentOptions have no id parameter.
    private static bool IsNameOnlyOverload(IMethodSymbol? method) =>
        method is not null
        && method.Parameters.Any(p => p.Name == "instructions")
        && !method.Parameters.Any(p => p.Name == "id" || p.Type.Name == "ChatClientAgentOptions");

    private static void ReportNoIdOverload(OperationAnalysisContext context, IOperation operation, string overload) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.UnstableAgentId,
            operation.Syntax.GetLocation(),
            $"{overload} cannot set an id, so the agent gets a random GUID per instance; pass ChatClientAgentOptions with a fixed Id so a rebuilt workflow keeps its executor ids and can resume checkpoints"));

    private static bool AssignedLater(IObjectCreationOperation creation)
    {
        ISymbol? holder = creation.Parent?.WalkUpConversions() switch
        {
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } => declarator.Symbol,
            ISimpleAssignmentOperation { Target: ILocalReferenceOperation local } => local.Local,
            ISimpleAssignmentOperation { Target: IMemberReferenceOperation member } => member.Member,
            _ => null,
        };
        if (holder is null)
        {
            return false;
        }

        IOperation root = creation;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        return root.DescendantsAndSelf().Any(o =>
            o is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Id", Instance: { } instance } }
            && (instance.WalkDownConversions() switch
            {
                ILocalReferenceOperation local => SymbolEqualityComparer.Default.Equals(local.Local, holder),
                IMemberReferenceOperation member => SymbolEqualityComparer.Default.Equals(member.Member, holder),
                _ => false,
            }));
    }
}
