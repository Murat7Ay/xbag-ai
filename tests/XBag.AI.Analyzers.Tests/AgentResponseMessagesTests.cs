namespace XBag.AI.Analyzers.Tests;

public sealed class AgentResponseMessagesTests
{
    private const string Redact = """

            static class Gizlilik
            {
                public static ChatMessage Maskele(ChatMessage message) => new(message.Role, message.Text.Replace("TR12 3456", "[GİZLİ]"));
            }
            """;

    [Fact]
    public Task Replacing_Messages_in_a_Use_delegate_is_reported() =>
        Verify.AnalyzerAsync<AgentResponseMessagesAnalyzer>("""
            static class Pipeline
            {
                public static AIAgent Build(AIAgent agent) =>
                    agent.AsBuilder()
                        .Use(
                            runFunc: async (messages, session, options, inner, cancellationToken) =>
                            {
                                AgentResponse response = await inner.RunAsync(messages, session, options, cancellationToken);
                                {|XBAG006:response.Messages = response.Messages.Select(Gizlilik.Maskele).ToList()|};
                                return response;
                            },
                            runStreamingFunc: null)
                        .UseOpenTelemetry()
                        .Build();
            }
            """ + Redact);

    [Fact]
    public Task Replacing_Messages_in_RunCoreAsync_is_reported() =>
        Verify.AnalyzerAsync<AgentResponseMessagesAnalyzer>("""
            sealed class MaskeleyenAjan(AIAgent inner) : DelegatingAIAgent(inner)
            {
                protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
                {
                    AgentResponse response = await base.RunCoreAsync(messages, session, options, cancellationToken);
                    {|XBAG006:response.Messages = [.. response.Messages.Select(Gizlilik.Maskele)]|};
                    return response;
                }
            }
            """ + Redact);

    [Fact]
    public Task New_responses_in_place_updates_and_code_outside_middleware_are_not_reported() =>
        Verify.AnalyzerAsync<AgentResponseMessagesAnalyzer>("""
            static class Pipeline
            {
                public static AIAgent Build(AIAgent agent) =>
                    agent.AsBuilder()
                        .Use(
                            runFunc: async (messages, session, options, inner, cancellationToken) =>
                            {
                                AgentResponse response = await inner.RunAsync(messages, session, options, cancellationToken);
                                for (int i = 0; i < response.Messages.Count; i++)
                                {
                                    response.Messages[i] = Gizlilik.Maskele(response.Messages[i]);
                                }

                                return new AgentResponse { Messages = response.Messages.ToList(), Usage = response.Usage };
                            },
                            runStreamingFunc: null)
                        .Build();

                // Application code after the run: nothing wraps this response any more.
                public static async Task<string> SorAsync(AIAgent agent)
                {
                    AgentResponse response = await agent.RunAsync("Fatura tutarım ne kadar?");
                    response.Messages = response.Messages.Select(Gizlilik.Maskele).ToList();
                    return response.Text;
                }
            }
            """ + Redact);

    [Fact]
    public Task Fix_updates_the_list_in_place() =>
        Verify.CodeFixAsync<AgentResponseMessagesAnalyzer, AgentResponseMessagesCodeFix>(
            """
            sealed class MaskeleyenAjan(AIAgent inner) : DelegatingAIAgent(inner)
            {
                protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
                {
                    AgentResponse response = await base.RunCoreAsync(messages, session, options, cancellationToken);
                    {|XBAG006:response.Messages = response.Messages.Select(Gizlilik.Maskele).ToList()|};
                    return response;
                }
            }
            """ + Redact,
            """
            sealed class MaskeleyenAjan(AIAgent inner) : DelegatingAIAgent(inner)
            {
                protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
                {
                    AgentResponse response = await base.RunCoreAsync(messages, session, options, cancellationToken);
                    var newMessages = new List<ChatMessage>(response.Messages.Select(Gizlilik.Maskele).ToList());
                    response.Messages.Clear();
                    foreach (var message in newMessages)
                    {
                        response.Messages.Add(message);
                    }
                    return response;
                }
            }
            """ + Redact);
}
