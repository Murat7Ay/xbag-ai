using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG004: chat-client middleware that writes to the caller's <c>ChatOptions</c>. Checked in <c>GetResponseAsync</c> /
/// <c>GetStreamingResponseAsync</c> of an <c>IChatClient</c> (including <c>DelegatingChatClient</c> overrides) and in the
/// delegates passed to <c>ChatClientBuilder.Use(...)</c>. A write is a property or indexer assignment, <c>++</c>/<c>--</c>, or a
/// collection call (<c>Add</c>, <c>Remove</c>, <c>Clear</c>, ...) reached from the parameter, before the parameter is
/// reassigned to a copy (<c>options = options?.Clone()</c> or <c>new ChatOptions</c>). One diagnostic per parameter, on the first write.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ChatOptionsMutationAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property with the name of the mutated parameter (used by the code fix).</summary>
    public const string ParameterProperty = "Parameter";

    private static readonly ImmutableHashSet<string> MutatingMethods = ImmutableHashSet.Create(
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "TryAdd", "Sort", "Reverse");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.CallerChatOptionsMutated);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? chatOptions = start.Compilation.GetTypeByMetadataName(KnownSymbols.ChatOptions);
            INamedTypeSymbol? chatClient = start.Compilation.GetTypeByMetadataName(KnownSymbols.IChatClient);
            INamedTypeSymbol? delegating = start.Compilation.GetTypeByMetadataName(KnownSymbols.DelegatingChatClient);
            INamedTypeSymbol? builder = start.Compilation.GetTypeByMetadataName(KnownSymbols.ChatClientBuilder);
            if (chatOptions is null || chatClient is null)
            {
                return;
            }

            var symbols = new Symbols(chatOptions, chatClient, delegating, builder);
            start.RegisterOperationBlockStartAction(block => AnalyzeBlock(block, symbols));
        });
    }

    private static void AnalyzeBlock(OperationBlockStartAnalysisContext context, Symbols symbols)
    {
        // Parameter -> writes (in source order once sorted) and the positions where it was replaced by an owned copy.
        var writes = new ConcurrentBag<(IParameterSymbol Parameter, IOperation Write)>();
        var copies = new ConcurrentBag<(IParameterSymbol Parameter, int Position)>();

        context.RegisterOperationAction(c =>
        {
            var assignment = (IAssignmentOperation)c.Operation;
            if (assignment.Target is IParameterReferenceOperation target)
            {
                if (assignment is ISimpleAssignmentOperation && CreatesCopy(assignment.Value, symbols.ChatOptions))
                {
                    copies.Add((target.Parameter, assignment.Syntax.SpanStart));
                }

                return;
            }

            if (IsInitializerTarget(assignment.Target))
            {
                return;
            }

            if (GetMemberRoot(assignment.Target) is { } parameter && IsCallerOptions(parameter, assignment, symbols))
            {
                writes.Add((parameter, assignment.Target));
            }
        }, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment);

        context.RegisterOperationAction(c =>
        {
            var increment = (IIncrementOrDecrementOperation)c.Operation;
            if (GetMemberRoot(increment.Target) is { } parameter && IsCallerOptions(parameter, increment, symbols))
            {
                writes.Add((parameter, increment.Target));
            }
        }, OperationKind.Increment, OperationKind.Decrement);

        context.RegisterOperationAction(c =>
        {
            var invocation = (IInvocationOperation)c.Operation;
            if (!MutatingMethods.Contains(invocation.TargetMethod.Name) || invocation.Instance is null)
            {
                return;
            }

            // options.Tools.Add(...), not options.Add(...) (ChatOptions has no such member) or a call on the parameter itself.
            if (GetMemberRoot(invocation.Instance) is { } parameter && IsCallerOptions(parameter, invocation, symbols))
            {
                writes.Add((parameter, invocation));
            }
        }, OperationKind.Invocation);

        context.RegisterOperationBlockEndAction(c =>
        {
            foreach (IGrouping<ISymbol, (IParameterSymbol Parameter, IOperation Write)> group in writes.GroupBy(w => (ISymbol)w.Parameter, SymbolEqualityComparer.Default))
            {
                int[] copiedAt = copies.Where(x => SymbolEqualityComparer.Default.Equals(x.Parameter, group.Key)).Select(x => x.Position).ToArray();
                IOperation? first = group
                    .Select(w => w.Write)
                    .Where(w => !copiedAt.Any(position => position < w.Syntax.SpanStart))
                    .OrderBy(w => w.Syntax.SpanStart)
                    .FirstOrDefault();
                if (first is not null)
                {
                    c.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.CallerChatOptionsMutated,
                        first.Syntax.GetLocation(),
                        ImmutableDictionary<string, string?>.Empty.Add(ParameterProperty, group.Key.Name),
                        group.Key.Name));
                }
            }
        });
    }

    // options.X, options.X.Y, options.X[k], options?.X: the parameter at the root of a member chain (at least one member deep).
    private static IParameterSymbol? GetMemberRoot(IOperation operation)
    {
        int depth = 0;
        IOperation? current = operation.WalkDownConversions();
        while (true)
        {
            switch (current)
            {
                case IPropertyReferenceOperation property:
                    current = property.Instance?.WalkDownConversions();
                    depth++;
                    continue;
                case IFieldReferenceOperation field:
                    current = field.Instance?.WalkDownConversions();
                    depth++;
                    continue;
                case IConditionalAccessInstanceOperation conditionalInstance:
                    current = FindConditionalAccess(conditionalInstance)?.Operation.WalkDownConversions();
                    continue;
                case IParameterReferenceOperation parameter when depth > 0:
                    return parameter.Parameter;
                default:
                    return null;
            }
        }
    }

    private static IConditionalAccessOperation? FindConditionalAccess(IConditionalAccessInstanceOperation instance)
    {
        for (IOperation? parent = instance.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IConditionalAccessOperation access && access.WhenNotNull.DescendantsAndSelf().Contains(instance))
            {
                return access;
            }
        }

        return null;
    }

    // new ChatOptions { X = 1 } assigns X on the new object, not on the parameter.
    private static bool IsInitializerTarget(IOperation target) =>
        target is IMemberReferenceOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } };

    private static bool CreatesCopy(IOperation value, INamedTypeSymbol chatOptions) =>
        value.DescendantsAndSelf().Any(o =>
            (o is IInvocationOperation { TargetMethod.Name: "Clone" } clone && clone.TargetMethod.ContainingType.InheritsFromOrEquals(chatOptions))
            || (o is IObjectCreationOperation creation && creation.Type.InheritsFromOrEquals(chatOptions)));

    private static bool IsCallerOptions(IParameterSymbol parameter, IOperation write, Symbols symbols)
    {
        if (!parameter.Type.InheritsFromOrEquals(symbols.ChatOptions) || parameter.ContainingSymbol is not IMethodSymbol method)
        {
            return false;
        }

        if (method.MethodKind == MethodKind.AnonymousFunction)
        {
            return IsBuilderUseDelegate(method, write, symbols);
        }

        if (method.Name is not (KnownSymbols.GetResponseAsync or KnownSymbols.GetStreamingResponseAsync)
            && !method.ExplicitInterfaceImplementations.Any(m => m.Name is KnownSymbols.GetResponseAsync or KnownSymbols.GetStreamingResponseAsync))
        {
            return false;
        }

        return method.Overrides(symbols.DelegatingChatClient)
            || method.ImplementsInterfaceMember(symbols.ChatClient)
            || method.ExplicitInterfaceImplementations.Any(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, symbols.ChatClient));
    }

    // The lambda that owns the parameter is an argument of ChatClientBuilder.Use(...).
    private static bool IsBuilderUseDelegate(IMethodSymbol lambda, IOperation write, Symbols symbols)
    {
        if (symbols.Builder is null)
        {
            return false;
        }

        for (IOperation? current = write.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation function && SymbolEqualityComparer.Default.Equals(function.Symbol, lambda))
            {
                IOperation? parent = function.Parent;
                while (parent is IDelegateCreationOperation or IConversionOperation)
                {
                    parent = parent.Parent;
                }

                return parent is IArgumentOperation { Parent: IInvocationOperation invocation }
                    && invocation.TargetMethod.Name == "Use"
                    && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, symbols.Builder);
            }
        }

        return false;
    }

    private sealed class Symbols(INamedTypeSymbol chatOptions, INamedTypeSymbol chatClient, INamedTypeSymbol? delegatingChatClient, INamedTypeSymbol? builder)
    {
        public INamedTypeSymbol ChatOptions { get; } = chatOptions;

        public INamedTypeSymbol ChatClient { get; } = chatClient;

        public INamedTypeSymbol? DelegatingChatClient { get; } = delegatingChatClient;

        public INamedTypeSymbol? Builder { get; } = builder;
    }
}
