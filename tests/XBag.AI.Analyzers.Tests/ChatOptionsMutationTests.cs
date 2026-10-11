namespace XBag.AI.Analyzers.Tests;

public sealed class ChatOptionsMutationTests
{
    [Fact]
    public Task Property_write_in_a_delegating_client_is_reported_once() =>
        Verify.AnalyzerAsync<ChatOptionsMutationAnalyzer>("""
            sealed class SicaklikIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    options ??= new ChatOptions();
                    {|XBAG004:options.Temperature|} = 0.2f;
                    options.ModelId = "gpt-5.4-mini";
                    return base.GetResponseAsync(messages, options, cancellationToken);
                }
            }
            """);

    [Fact]
    public Task Collection_and_dictionary_writes_are_reported() =>
        Verify.AnalyzerAsync<ChatOptionsMutationAnalyzer>("""
            sealed class AracEkleyen(IChatClient inner, AITool tool) : DelegatingChatClient(inner)
            {
                public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    {|XBAG004:options!.Tools!.Add(tool)|};
                    return base.GetResponseAsync(messages, options, cancellationToken);
                }

                public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    if (options?.AdditionalProperties is { } properties)
                    {
                        properties["kiraci"] = "acme";
                    }

                    {|XBAG004:options!.AdditionalProperties!["kanal"]|} = "web";
                    return base.GetStreamingResponseAsync(messages, options, cancellationToken);
                }
            }
            """);

    [Fact]
    public Task Write_in_a_ChatClientBuilder_Use_delegate_is_reported() =>
        Verify.AnalyzerAsync<ChatOptionsMutationAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .Use(async (messages, options, next, cancellationToken) =>
                        {
                            {|XBAG004:options!.ModelId|} = "gpt-5.4-mini";
                            await next(messages, options, cancellationToken);
                        })
                        .Build();
            }
            """);

    [Fact]
    public Task Plain_IChatClient_implementation_is_reported() =>
        Verify.AnalyzerAsync<ChatOptionsMutationAnalyzer>("""
            sealed class Sarmalayici(IChatClient inner) : IChatClient
            {
                public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    if (options is not null)
                    {
                        {|XBAG004:options.MaxOutputTokens|} ??= 1024;
                    }

                    return inner.GetResponseAsync(messages, options, cancellationToken);
                }

                public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                    inner.GetStreamingResponseAsync(messages, options, cancellationToken);

                public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

                public void Dispose() => inner.Dispose();
            }
            """);

    [Fact]
    public Task Writes_after_cloning_and_outside_middleware_are_not_reported() =>
        Verify.AnalyzerAsync<ChatOptionsMutationAnalyzer>("""
            sealed class KopyalayanIstemci(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    options = options?.Clone() ?? new ChatOptions();
                    options.Temperature = 0.2f;
                    options.Tools?.Clear();
                    return base.GetResponseAsync(messages, options, cancellationToken);
                }

                public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    ChatOptions copy = options?.Clone() ?? new() { Temperature = 0 };
                    copy.Temperature = 0.2f;
                    return base.GetStreamingResponseAsync(messages, copy, cancellationToken);
                }
            }

            static class Ayarlar
            {
                // Not middleware: the caller asked for this object to be configured.
                public static void Uygula(ChatOptions options) => options.Temperature = 0.2f;

                // ConfigureOptions hands the callback a clone already.
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder().ConfigureOptions(o => o.Temperature = 0.2f).Build();
            }
            """);

    [Fact]
    public Task Fix_clones_at_the_top_of_the_method() =>
        Verify.CodeFixAsync<ChatOptionsMutationAnalyzer, ChatOptionsMutationCodeFix>(
            """
            sealed class SicaklikIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    options ??= new ChatOptions();
                    {|XBAG004:options.Temperature|} = 0.2f;
                    return base.GetResponseAsync(messages, options, cancellationToken);
                }
            }
            """,
            """
            sealed class SicaklikIstemcisi(IChatClient inner) : DelegatingChatClient(inner)
            {
                public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                {
                    options = options?.Clone();
                    options ??= new ChatOptions();
                    options.Temperature = 0.2f;
                    return base.GetResponseAsync(messages, options, cancellationToken);
                }
            }
            """);

    [Fact]
    public Task Fix_clones_at_the_top_of_a_Use_delegate() =>
        Verify.CodeFixAsync<ChatOptionsMutationAnalyzer, ChatOptionsMutationCodeFix>(
            """
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .Use(async (messages, options, next, cancellationToken) =>
                        {
                            {|XBAG004:options!.ModelId|} = "gpt-5.4-mini";
                            await next(messages, options, cancellationToken);
                        })
                        .Build();
            }
            """,
            """
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .Use(async (messages, options, next, cancellationToken) =>
                        {
                            options = options?.Clone();
                            options!.ModelId = "gpt-5.4-mini";
                            await next(messages, options, cancellationToken);
                        })
                        .Build();
            }
            """);
}
