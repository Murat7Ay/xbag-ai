using Microsoft.CodeAnalysis;

namespace XBag.AI.Analyzers;

/// <summary>The diagnostic ids reported by XBag.AI.Analyzers.</summary>
public static class DiagnosticIds
{
    /// <summary><c>UseOpenTelemetry()</c> is outside <c>UseFunctionInvocation()</c> in one <c>ChatClientBuilder</c> chain.</summary>
    public const string TelemetryOutsideFunctionInvocation = "XBAG001";

    /// <summary><c>GetService</c> returns <c>this</c> for service types the instance is not.</summary>
    public const string GetServiceReturnsThis = "XBAG002";

    /// <summary>A delegating client or agent overrides only the buffered or only the streaming path.</summary>
    public const string StreamingParity = "XBAG003";

    /// <summary>Middleware mutates the caller's <c>ChatOptions</c>.</summary>
    public const string CallerChatOptionsMutated = "XBAG004";

    /// <summary>An agent has no stable id in code that builds workflows.</summary>
    public const string UnstableAgentId = "XBAG005";

    /// <summary>Agent-run middleware replaces <c>AgentResponse.Messages</c>.</summary>
    public const string AgentResponseMessagesReplaced = "XBAG006";
}

internal static class Descriptors
{
    private const string HelpLinkBase = "https://github.com/Murat7Ay/xbag-ai/blob/main/src/XBag.AI.Analyzers/README.md#";

    public static readonly DiagnosticDescriptor TelemetryOutsideFunctionInvocation = Create(
        DiagnosticIds.TelemetryOutsideFunctionInvocation,
        "UseOpenTelemetry is outside UseFunctionInvocation",
        "UseOpenTelemetry() runs outside UseFunctionInvocation() in this ChatClientBuilder chain, so the tool loop finds no ActivitySource and emits no tool spans; call UseFunctionInvocation() first",
        "Telemetry",
        DiagnosticSeverity.Warning,
        "FunctionInvokingChatClient reads innerClient.GetService<ActivitySource>() once, in its constructor. The first Use() in a ChatClientBuilder is the outermost client, so with UseOpenTelemetry() first the tool loop's inner client has no ActivitySource: no orchestrate_tools or execute_tool spans.");

    public static readonly DiagnosticDescriptor GetServiceReturnsThis = Create(
        DiagnosticIds.GetServiceReturnsThis,
        "GetService returns this for every service type",
        "'{0}.GetService' returns 'this' for service types it is not; return it only when serviceKey is null and serviceType.IsInstanceOfType(this), otherwise null or the inner service",
        "Design",
        DiagnosticSeverity.Warning,
        "GetService is how a pipeline finds services (ChatClientMetadata, ActivitySource, the provider SDK client) through its layers. Returning this for every type hides the inner client's services and hands non-generic callers an object of the wrong type.");

    public static readonly DiagnosticDescriptor StreamingParity = Create(
        DiagnosticIds.StreamingParity,
        "Middleware overrides only one of the buffered and streaming paths",
        "'{0}' overrides {1} but not {2}, so {3} calls skip this middleware",
        "Reliability",
        DiagnosticSeverity.Warning,
        "A delegating chat client or agent forwards every call it does not override straight to the inner instance. Overriding only the buffered method means streaming callers (chat UIs, AG-UI, streaming agents) bypass the logic, and the other way round.");

    public static readonly DiagnosticDescriptor CallerChatOptionsMutated = Create(
        DiagnosticIds.CallerChatOptionsMutated,
        "Middleware mutates the caller's ChatOptions",
        "'{0}' is the caller's ChatOptions and this middleware changes it; clone it first ({0} = {0}?.Clone())",
        "Reliability",
        DiagnosticSeverity.Warning,
        "The ChatOptions passed to a chat client belongs to the caller: FunctionInvokingChatClient passes the same instance to every model call of the tool loop, and agents and applications reuse one instance across requests. Changing it leaks the change into later calls and other users.");

    public static readonly DiagnosticDescriptor UnstableAgentId = Create(
        DiagnosticIds.UnstableAgentId,
        "Agent has no stable id",
        "{0}",
        "Reliability",
        DiagnosticSeverity.Info,
        "An agent without ChatClientAgentOptions.Id gets a random GUID per instance. Workflows derive executor ids from the agent's Name and Id, so a workflow rebuilt per request or process cannot resume checkpoints that a previous instance wrote.");

    public static readonly DiagnosticDescriptor AgentResponseMessagesReplaced = Create(
        DiagnosticIds.AgentResponseMessagesReplaced,
        "Agent middleware replaces AgentResponse.Messages",
        "Replacing AgentResponse.Messages in agent middleware is lost when UseOpenTelemetry() wraps the agent; change the list in place or return a new AgentResponse",
        "Reliability",
        DiagnosticSeverity.Warning,
        "OpenTelemetryAgent passes the inner agent's response through AsChatResponse(), which returns the original ChatResponse in RawRepresentation. The ChatResponse still holds the old message list, so a replaced list (for example redacted messages) never reaches the caller.");

    private static DiagnosticDescriptor Create(string id, string title, string message, string category, DiagnosticSeverity severity, string description) =>
        new(id, title, message, category, severity, isEnabledByDefault: true, description, HelpLinkBase + id.ToLowerInvariant());
}
