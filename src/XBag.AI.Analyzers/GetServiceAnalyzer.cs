using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG002: a <c>GetService(Type, object?)</c> implementation (chat client, embedding generator, agent, any Microsoft.Extensions.AI
/// or Agent Framework service-lookup member) whose fallback is <c>this</c>: <c>=&gt; this</c>, a final <c>return this;</c>,
/// <c>cond ? x : this</c> or <c>x ?? this</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GetServiceAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.GetServiceReturnsThis);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(AnalyzeBlock);
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context)
    {
        if (context.OwningSymbol is not IMethodSymbol method || !IsServiceLookup(method))
        {
            return;
        }

        foreach (IOperation block in context.OperationBlocks)
        {
            if (block is not IBlockOperation body)
            {
                continue;
            }

            // Top-level statements only: a return inside an if is a deliberate answer for one type.
            bool guardedByTypeCheck = false;
            foreach (IOperation statement in body.Operations)
            {
                if (statement is IReturnOperation { ReturnedValue: { } value })
                {
                    if (!guardedByTypeCheck)
                    {
                        ReportFallbackThis(context, method, value);
                    }

                    return;
                }

                guardedByTypeCheck |= ChecksInstanceType(statement);
            }
        }
    }

    // The value a lookup falls back to: the expression itself, the false branch of a conditional, the right side of ??.
    private static void ReportFallbackThis(OperationBlockAnalysisContext context, IMethodSymbol method, IOperation value)
    {
        value = value.WalkDownConversions();
        switch (value)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, IsImplicit: false }:
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.GetServiceReturnsThis, value.Syntax.GetLocation(), method.ContainingType.Name));
                break;

            case IConditionalOperation { WhenFalse: { } whenFalse }:
                ReportFallbackThis(context, method, whenFalse);
                break;

            case ICoalesceOperation coalesce:
                ReportFallbackThis(context, method, coalesce.WhenNull);
                break;
        }
    }

    // `if (!serviceType.IsInstanceOfType(this)) return null;` before a final `return this;` is correct.
    private static bool ChecksInstanceType(IOperation statement) =>
        statement.DescendantsAndSelf().Any(o =>
            o is IInvocationOperation { TargetMethod: { Name: "IsInstanceOfType" or "IsAssignableFrom", ContainingType: var type } }
            && type.ToDisplayString() == "System.Type");

    private static bool IsServiceLookup(IMethodSymbol method)
    {
        if ((method.Name != "GetService" && !method.ExplicitInterfaceImplementations.Any(m => m.Name == "GetService"))
            || method.Parameters.Length != 2
            || method.Parameters[0].Type.ToDisplayString() != "System.Type"
            || method.Parameters[1].Type.SpecialType != SpecialType.System_Object
            || method.ReturnType.SpecialType != SpecialType.System_Object)
        {
            return false;
        }

        if (method.IsOverride)
        {
            IMethodSymbol? root = method.OverriddenMethod;
            while (root?.OverriddenMethod is not null)
            {
                root = root.OverriddenMethod;
            }

            return root is not null && IsMicrosoftAI(root.ContainingType);
        }

        // Interface implementations: IChatClient, IEmbeddingGenerator, ISpeechToTextClient, IImageGenerator, ...
        if (method.ExplicitInterfaceImplementations.Any(m => IsMicrosoftAI(m.ContainingType)))
        {
            return true;
        }

        foreach (INamedTypeSymbol @interface in method.ContainingType.AllInterfaces)
        {
            if (IsMicrosoftAI(@interface) && method.ImplementsInterfaceMember(@interface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMicrosoftAI(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace.ToDisplayString();
        return ns == "Microsoft.Extensions.AI" || ns == "Microsoft.Agents.AI";
    }
}
