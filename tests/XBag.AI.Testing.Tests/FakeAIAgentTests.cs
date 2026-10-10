using System.Runtime.CompilerServices;
using System.Text.Json;
using XBag.AI.Testing;
using XBag.AI.Testing.Agents;
using Microsoft.Agents.AI;

namespace XBag.AI.Testing.Tests;

public sealed class FakeAIAgentTests
{
    /// <summary>A typical decorator under test: prefixes every answer and counts runs.</summary>
    private sealed class PrefixingAgent(AIAgent inner, string prefix) : DelegatingAIAgent(inner)
    {
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            AgentResponse response = await base.RunCoreAsync(messages, session, options, cancellationToken);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, prefix + response.Text)) { Usage = response.Usage };
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, prefix) { MessageId = "prefix" };
            await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken))
            {
                update.MessageId = "prefix";
                yield return update;
            }
        }
    }

    [Fact]
    public async Task Reply_returns_scripted_response_and_records_the_run()
    {
        var agent = new FakeAIAgent(name: "inner", id: "fake-1").Reply("hello").Usage(5, 1);
        AgentSession session = await agent.CreateSessionAsync();
        var options = new AgentRunOptions();

        AgentResponse response = await agent.RunAsync("hi", session, options);

        Assert.Equal("hello", response.Text);
        Assert.Equal("fake-1", response.AgentId);
        Assert.Equal("inner", response.Messages[0].AuthorName);
        Assert.Equal(6, response.Usage!.TotalTokenCount);

        RecordedRun run = Assert.Single(agent.Runs);
        Assert.Equal("hi", run.LastUserText);
        Assert.Same(session, run.Session);
        Assert.Same(options, run.Options);
        Assert.False(run.IsStreaming);
        agent.ShouldBeExhausted();
    }

    [Fact]
    public async Task Reply_steps_stream_and_Stream_steps_aggregate()
    {
        var agent = new FakeAIAgent()
            .Reply("whole answer")
            .Stream("Hel", "lo ", "world").Usage(3, 3);

        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("a"))
        {
            updates.Add(update);
        }

        AgentResponse aggregated = await agent.RunAsync("b");

        Assert.Equal("whole answer", updates.ToAgentResponse().Text);
        Assert.Equal("Hello world", aggregated.Text);
        Assert.Single(aggregated.Messages);
        Assert.Equal(6, aggregated.Usage!.TotalTokenCount);
        Assert.True(agent.Runs[0].IsStreaming);
    }

    [Fact]
    public async Task Explicit_updates_are_streamed_as_given()
    {
        var agent = new FakeAIAgent().Stream(
            new AgentResponseUpdate(ChatRole.Assistant, "a") { MessageId = "m1" },
            new AgentResponseUpdate(ChatRole.Assistant, "b") { MessageId = "m2" });

        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("go"))
        {
            updates.Add(update);
        }

        Assert.Equal(["m1", "m2"], updates.Select(u => u.MessageId));
        Assert.Equal(2, updates.ToAgentResponse().Messages.Count);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task Works_as_the_inner_agent_of_a_DelegatingAIAgent_decorator()
    {
        var inner = new FakeAIAgent().Reply(run => $"echo {run.LastUserText}").Reply("streamed");
        AIAgent decorated = new PrefixingAgent(inner, "[audited] ");

        AgentResponse response = await decorated.RunAsync("ping");
        AgentResponse streamed = await decorated.RunStreamingAsync("pong").ToAgentResponseAsync();

        Assert.Equal("[audited] echo ping", response.Text);
        Assert.Equal("[audited] streamed", streamed.Text);
        inner.ShouldHaveReceivedRuns(2);
        Assert.Equal(["ping", "pong"], inner.Runs.Select(r => r.LastUserText));
        Assert.Same(inner, decorated.GetService<FakeAIAgent>());
        inner.ShouldBeExhausted();
    }

    [Fact]
    public async Task Works_under_AIAgentBuilder_middleware()
    {
        var inner = new FakeAIAgent().Reply("from inner");
        List<string> seen = [];

        AIAgent pipeline = inner.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, next, ct) =>
                {
                    seen.Add(messages.Last().Text);
                    return await next.RunAsync(messages.Append(new ChatMessage(ChatRole.User, "(added by middleware)")), session, options, ct);
                },
                runStreamingFunc: null)
            .Build();

        AgentResponse response = await pipeline.RunAsync("original");

        Assert.Equal("from inner", response.Text);
        Assert.Equal(["original"], seen);
        Assert.Equal("(added by middleware)", inner.Runs[0].LastUserText);
    }

    [Fact]
    public async Task Fail_and_exhaustion_surface_as_exceptions()
    {
        var agent = new FakeAIAgent(name: "flaky").Fail(new TimeoutException("slow"));

        await Assert.ThrowsAsync<TimeoutException>(() => agent.RunAsync("x"));
        var ex = await Assert.ThrowsAsync<AIAssertionException>(() => agent.RunAsync("again"));

        Assert.Contains("FakeAIAgent 'flaky' received run #2", ex.Message);
        Assert.Equal(2, agent.Runs.Count);
        Assert.Contains("Reply('later')", Assert.Throws<AIAssertionException>(new FakeAIAgent().Reply("later").ShouldBeExhausted).Message);
    }

    [Fact]
    public async Task Sessions_round_trip_through_serialization()
    {
        var agent = new FakeAIAgent();
        AgentSession session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("note", "remember me");

        JsonElement json = await agent.SerializeSessionAsync(session);
        AgentSession restored = await agent.DeserializeSessionAsync(json);

        Assert.IsType<FakeAgentSession>(restored);
        Assert.Equal("remember me", restored.StateBag.GetValue<string>("note"));
    }

    [Fact]
    public void GetService_returns_metadata_and_itself()
    {
        var agent = new FakeAIAgent();

        Assert.Equal("fake", agent.GetService<AIAgentMetadata>()!.ProviderName);
        Assert.Same(agent, agent.GetService<AIAgent>());
        Assert.Null(agent.GetService<ChatClientAgent>());
    }
}
