using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace XBag.AI.Analyzers;

/// <summary>Metadata names the rules match on. Types are resolved per compilation, so no Microsoft package is referenced.</summary>
internal static class KnownSymbols
{
    public const string ChatClientBuilder = "Microsoft.Extensions.AI.ChatClientBuilder";
    public const string OpenTelemetryChatClientBuilderExtensions = "Microsoft.Extensions.AI.OpenTelemetryChatClientBuilderExtensions";
    public const string FunctionInvokingChatClientBuilderExtensions = "Microsoft.Extensions.AI.FunctionInvokingChatClientBuilderExtensions";
    public const string ChatOptions = "Microsoft.Extensions.AI.ChatOptions";
    public const string IChatClient = "Microsoft.Extensions.AI.IChatClient";
    public const string DelegatingChatClient = "Microsoft.Extensions.AI.DelegatingChatClient";

    public const string AIAgent = "Microsoft.Agents.AI.AIAgent";
    public const string DelegatingAIAgent = "Microsoft.Agents.AI.DelegatingAIAgent";
    public const string AIAgentBuilder = "Microsoft.Agents.AI.AIAgentBuilder";
    public const string AgentResponse = "Microsoft.Agents.AI.AgentResponse";
    public const string ChatClientAgent = "Microsoft.Agents.AI.ChatClientAgent";
    public const string ChatClientAgentOptions = "Microsoft.Agents.AI.ChatClientAgentOptions";
    public const string Workflow = "Microsoft.Agents.AI.Workflows.Workflow";

    public const string GetResponseAsync = "GetResponseAsync";
    public const string GetStreamingResponseAsync = "GetStreamingResponseAsync";
    public const string RunCoreAsync = "RunCoreAsync";
    public const string RunCoreStreamingAsync = "RunCoreStreamingAsync";

    /// <summary>True when <paramref name="type"/> is <paramref name="baseType"/> or derives from it.</summary>
    public static bool InheritsFromOrEquals(this ITypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the method overrides (directly or through user classes) a method declared on <paramref name="type"/>.</summary>
    public static bool Overrides(this IMethodSymbol method, INamedTypeSymbol? type)
    {
        for (IMethodSymbol? current = method.OverriddenMethod; current is not null; current = current.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(current.ContainingType.OriginalDefinition, type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the method implements the interface member of the same name on <paramref name="interfaceType"/>.</summary>
    public static bool ImplementsInterfaceMember(this IMethodSymbol method, INamedTypeSymbol? interfaceType)
    {
        if (interfaceType is null)
        {
            return false;
        }

        foreach (ISymbol member in interfaceType.GetMembers(method.Name))
        {
            if (SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Skips implicit conversions and parentheses-free wrappers the operation tree adds around a value.</summary>
    public static IOperation WalkDownConversions(this IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>The first operation above <paramref name="operation"/> that is not an implicit conversion (itself if none).</summary>
    public static IOperation WalkUpConversions(this IOperation operation)
    {
        while (operation is IConversionOperation && operation.Parent is not null)
        {
            operation = operation.Parent;
        }

        return operation;
    }

    /// <summary>The receiver of an instance call or the <c>this</c> argument of an extension method call.</summary>
    public static IOperation? GetReceiver(this IInvocationOperation invocation)
    {
        if (invocation.Instance is not null)
        {
            return invocation.Instance.WalkDownConversions();
        }

        if (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0)
        {
            foreach (IArgumentOperation argument in invocation.Arguments)
            {
                if (argument.Parameter?.Ordinal == 0)
                {
                    return argument.Value.WalkDownConversions();
                }
            }
        }

        return null;
    }

    /// <summary>The call that uses <paramref name="operation"/> as its receiver (the next call in a fluent chain), if any.</summary>
    public static IInvocationOperation? GetNextCallInChain(this IOperation operation)
    {
        IOperation? parent = operation.Parent;
        while (parent is IConversionOperation)
        {
            operation = parent;
            parent = parent.Parent;
        }

        if (parent is IInvocationOperation instanceCall && instanceCall.Instance == operation)
        {
            return instanceCall;
        }

        if (parent is IArgumentOperation { Parameter.Ordinal: 0 } argument
            && argument.Parent is IInvocationOperation extensionCall
            && extensionCall.TargetMethod.IsExtensionMethod)
        {
            return extensionCall;
        }

        return null;
    }
}
