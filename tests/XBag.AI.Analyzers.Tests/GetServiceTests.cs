namespace XBag.AI.Analyzers.Tests;

public sealed class GetServiceTests
{
    // A leaf IChatClient; the {GetService} placeholder is the member under test.
    private const string LeafClient = """
        sealed class YerelModel : IChatClient
        {
            private readonly ChatClientMetadata _metadata = new("yerel", defaultModelId: "qwen3");

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Merhaba!")));

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

        {GetService}

            public void Dispose() { }
        }
        """;

    private static string Leaf(string getService) => LeafClient.Replace("{GetService}", getService);

    [Fact]
    public Task Expression_bodied_this_is_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>(Leaf("""
                public object? GetService(Type serviceType, object? serviceKey = null) => {|XBAG002:this|};
            """));

    [Fact]
    public Task Final_return_this_after_a_type_check_is_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>(Leaf("""
                public object? GetService(Type serviceType, object? serviceKey = null)
                {
                    if (serviceType == typeof(ChatClientMetadata))
                    {
                        return _metadata;
                    }

                    return {|XBAG002:this|};
                }
            """));

    [Fact]
    public Task This_as_the_conditional_fallback_is_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>(Leaf("""
                public object? GetService(Type serviceType, object? serviceKey = null) =>
                    serviceType == typeof(ChatClientMetadata) ? _metadata : {|XBAG002:this|};
            """));

    [Fact]
    public Task Explicit_interface_implementation_is_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>(Leaf("""
                object? IChatClient.GetService(Type serviceType, object? serviceKey) => {|XBAG002:this|};
            """));

    [Fact]
    public Task Delegating_agent_override_is_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>("""
            sealed class DenetimAjani(AIAgent inner) : DelegatingAIAgent(inner)
            {
                public override object? GetService(Type serviceType, object? serviceKey = null) =>
                    InnerAgent.GetService(serviceType, serviceKey) ?? {|XBAG002:this|};
            }
            """);

    [Fact]
    public Task Correct_patterns_are_not_reported() =>
        Verify.AnalyzerAsync<GetServiceAnalyzer>(Leaf("""
                public object? GetService(Type serviceType, object? serviceKey = null) =>
                    serviceKey is not null ? null :
                    serviceType == typeof(ChatClientMetadata) ? _metadata :
                    serviceType.IsInstanceOfType(this) ? this :
                    null;
            """) + """

            sealed class KontrollüAjan(AIAgent inner) : DelegatingAIAgent(inner)
            {
                public override object? GetService(Type serviceType, object? serviceKey = null)
                {
                    if (serviceKey is not null || !serviceType.IsInstanceOfType(this))
                    {
                        return base.GetService(serviceType, serviceKey);
                    }

                    return this;
                }
            }

            sealed class Kayit
            {
                // Not a Microsoft.Extensions.AI / Agent Framework member: out of scope.
                public object? GetService(Type serviceType, object? serviceKey = null) => this;
            }
            """);

    [Fact]
    public Task Fix_in_an_interface_implementation_checks_the_type() =>
        Verify.CodeFixAsync<GetServiceAnalyzer, GetServiceCodeFix>(
            Leaf("""
                    public object? GetService(Type serviceType, object? serviceKey = null) => {|XBAG002:this|};
                """),
            Leaf("""
                    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
                """));

    [Fact]
    public Task Fix_parenthesizes_inside_a_conditional() =>
        Verify.CodeFixAsync<GetServiceAnalyzer, GetServiceCodeFix>(
            Leaf("""
                    public object? GetService(Type type, object? key = null) =>
                        type == typeof(ChatClientMetadata) ? _metadata : {|XBAG002:this|};
                """),
            Leaf("""
                    public object? GetService(Type type, object? key = null) =>
                        type == typeof(ChatClientMetadata) ? _metadata : (key is null && type.IsInstanceOfType(this) ? this : null);
                """));

    [Fact]
    public Task Fix_in_an_override_asks_the_base() =>
        Verify.CodeFixAsync<GetServiceAnalyzer, GetServiceCodeFix>(
            """
            sealed class KotaIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override object? GetService(Type serviceType, object? serviceKey = null)
                {
                    if (serviceType == typeof(string))
                    {
                        return "kota";
                    }

                    return {|XBAG002:this|};
                }
            }
            """,
            """
            sealed class KotaIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override object? GetService(Type serviceType, object? serviceKey = null)
                {
                    if (serviceType == typeof(string))
                    {
                        return "kota";
                    }

                    return base.GetService(serviceType, serviceKey);
                }
            }
            """);
}
