using Microsoft.CodeAnalysis.Testing;

namespace XBag.AI.Analyzers.Tests;

public sealed class StreamingParityTests
{
    private const string Buffered = """
            public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                base.GetResponseAsync(messages, options, cancellationToken);
        """;

    private const string Streaming = """
            public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                base.GetStreamingResponseAsync(messages, options, cancellationToken);
        """;

    [Fact]
    public async Task Chat_client_overriding_only_the_buffered_path_is_reported()
    {
        var test = new Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<StreamingParityAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = Verify.Agents,
            TestCode = """
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.Extensions.AI;

                #nullable enable
                sealed class MaskeleyenIstemci(IChatClient inner) : DelegatingChatClient(inner)
                {
                    public override Task<ChatResponse> {|#0:GetResponseAsync|}(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                        base.GetResponseAsync(messages, options, cancellationToken);
                }
                """,
        };

        // The message names the class, the overridden path, the missing one, and which calls skip the middleware.
        test.ExpectedDiagnostics.Add(new DiagnosticResult(DiagnosticIds.StreamingParity, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments("MaskeleyenIstemci", "GetResponseAsync", "GetStreamingResponseAsync", "streaming"));
        await test.RunAsync();
    }

    [Fact]
    public Task Chat_client_overriding_only_the_streaming_path_is_reported() =>
        Verify.AnalyzerAsync<StreamingParityAnalyzer>($$"""
            sealed class AkisIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override IAsyncEnumerable<ChatResponseUpdate> {|XBAG003:GetStreamingResponseAsync|}(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                    base.GetStreamingResponseAsync(messages, options, cancellationToken);
            }
            """);

    [Fact]
    public Task Agent_overriding_only_RunCoreAsync_is_reported() =>
        Verify.AnalyzerAsync<StreamingParityAnalyzer>("""
            sealed class DenetimAjani(AIAgent inner) : DelegatingAIAgent(inner)
            {
                protected override Task<AgentResponse> {|XBAG003:RunCoreAsync|}(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
                    base.RunCoreAsync(messages, session, options, cancellationToken);
            }
            """);

    [Fact]
    public Task Lone_override_inherited_from_an_abstract_base_is_reported_on_the_concrete_class() =>
        Verify.AnalyzerAsync<StreamingParityAnalyzer>($$"""
            abstract class TemelIstemci(IChatClient inner) : DelegatingChatClient(inner)
            {
            {{Buffered}}
            }

            sealed class {|XBAG003:SomutIstemci|}(IChatClient inner) : TemelIstemci(inner);
            """);

    [Fact]
    public Task Both_paths_are_not_reported() =>
        Verify.AnalyzerAsync<StreamingParityAnalyzer>($$"""
            sealed class TamIstemci(IChatClient inner) : DelegatingChatClient(inner)
            {
            {{Buffered}}

            {{Streaming}}
            }

            abstract class TemelIstemci(IChatClient inner) : DelegatingChatClient(inner)
            {
            {{Buffered}}
            }

            sealed class TamamlayanIstemci(IChatClient inner) : TemelIstemci(inner)
            {
            {{Streaming}}
            }

            sealed class TamAjan(AIAgent inner) : DelegatingAIAgent(inner)
            {
                protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
                    base.RunCoreAsync(messages, session, options, cancellationToken);

                protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
                    base.RunCoreStreamingAsync(messages, session, options, cancellationToken);
            }

            // Overriding neither (only GetService) is a pass-through for both paths.
            sealed class MetaIstemci(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override object? GetService(Type serviceType, object? serviceKey = null) => base.GetService(serviceType, serviceKey);
            }
            """);
}
