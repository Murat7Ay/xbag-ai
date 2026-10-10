using Microsoft.Agents.AI;
using XBag.AI.Testing;

namespace XBag.Agents.Stores.Tests;

/// <summary>
/// What every AgentSessionStore in this repo must do (the contract of Microsoft's [Experimental] AgentSessionStore), run with
/// the real ChatClientAgent. Each backend's test class supplies a store over its storage.
/// </summary>
public abstract class AgentSessionStoreContract
{
    /// <summary>A new store over the same storage, as a freshly started process would build it.</summary>
    protected abstract AgentSessionStore CreateStore(string? agentNamespace = null);

    protected abstract ValueTask<bool> DeleteAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key);

    [Fact]
    public async Task Saved_session_continues_the_conversation_after_a_restart()
    {
        // The agent keeps its history in the session (default InMemoryChatHistoryProvider), so the store alone carries it.
        var model1 = new ScriptedChatClient().Reply("Memnun oldum Murat.");
        AIAgent agent1 = model1.AsAIAgent(name: "asistan");
        var key = new AgentSessionStoreKey("sohbet-1");
        AgentSession session1 = await CreateStore().GetOrCreateSessionAsync(agent1, key);
        await agent1.RunAsync("Merhaba, ben Murat. Ankara'dan yazıyorum.", session1);
        await CreateStore().SaveSessionAsync(agent1, key, session1);

        var model2 = new ScriptedChatClient().Reply(req => $"{req.Messages.Count} mesaj görüyorum.");
        AIAgent agent2 = model2.AsAIAgent(name: "asistan");
        AgentSession session2 = await CreateStore().GetOrCreateSessionAsync(agent2, key);
        AgentResponse answer = await agent2.RunAsync("Nereden yazıyorum?", session2);

        Assert.Equal("3 mesaj görüyorum.", answer.Text);
        Assert.Equal("Merhaba, ben Murat. Ankara'dan yazıyorum.", model2.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task Partitions_are_part_of_the_identity_and_their_order_does_not_matter()
    {
        AIAgent agent = new ScriptedChatClient().AsAIAgent(name: "asistan");
        AgentSessionStore store = CreateStore();
        AgentSessionStoreKey plain = new("s1");
        AgentSessionStoreKey ankara = plain.WithPartition("isolation", "tenant-ankara");
        AgentSessionStoreKey izmir = plain.WithPartition("isolation", "tenant-izmir");
        AgentSessionStoreKey twoParts = new("s1", new Dictionary<string, string> { ["isolation"] = "tenant-ankara", ["bolge"] = "tr" });
        AgentSessionStoreKey sameTwoParts = new("s1", new Dictionary<string, string> { ["bolge"] = "tr", ["isolation"] = "tenant-ankara" });

        await SaveMarkedAsync(store, agent, plain, "düz");
        await SaveMarkedAsync(store, agent, ankara, "Ankara");
        await SaveMarkedAsync(store, agent, izmir, "İzmir");
        await SaveMarkedAsync(store, agent, twoParts, "Ankara/TR");

        AgentSessionStore restarted = CreateStore();
        Assert.Equal("düz", await ReadMarkAsync(restarted, agent, plain));
        Assert.Equal("Ankara", await ReadMarkAsync(restarted, agent, ankara));
        Assert.Equal("İzmir", await ReadMarkAsync(restarted, agent, izmir));
        Assert.Equal("Ankara/TR", await ReadMarkAsync(restarted, agent, sameTwoParts));
        Assert.Null(await restarted.GetSessionAsync(agent, plain.WithPartition("isolation", "tenant-bursa")));
    }

    [Fact]
    public async Task Each_lookup_returns_an_independent_session()
    {
        AIAgent agent = new ScriptedChatClient().AsAIAgent(name: "asistan");
        AgentSessionStore store = CreateStore();
        var key = new AgentSessionStoreKey("bagimsiz");
        await SaveMarkedAsync(store, agent, key, "ilk");

        AgentSession a = (await store.GetSessionAsync(agent, key))!;
        AgentSession b = (await store.GetSessionAsync(agent, key))!;
        a.StateBag.SetValue("sehir", "değişti");

        Assert.NotSame(a, b);
        Assert.Equal("ilk", Mark(b));
        Assert.Equal("ilk", await ReadMarkAsync(store, agent, key));   // not saved, so the store is unchanged
    }

    [Fact]
    public async Task Sessions_are_kept_per_agent_name_and_a_nameless_agent_needs_a_namespace()
    {
        var key = new AgentSessionStoreKey("ortak-id");
        AIAgent asistan = new ScriptedChatClient().AsAIAgent(name: "asistan");
        AIAgent muhasebe = new ScriptedChatClient().AsAIAgent(name: "muhasebe");
        AIAgent nameless = new ScriptedChatClient().AsAIAgent();
        AgentSessionStore store = CreateStore();

        await SaveMarkedAsync(store, asistan, key, "asistan");

        Assert.Null(await store.GetSessionAsync(muhasebe, key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetSessionAsync(nameless, key).AsTask());

        AgentSessionStore shared = CreateStore(agentNamespace: "asistan");
        Assert.Equal("asistan", await ReadMarkAsync(shared, nameless, key));
    }

    [Fact]
    public async Task Unknown_sessions_return_null_and_delete_removes_one()
    {
        AIAgent agent = new ScriptedChatClient().AsAIAgent(name: "asistan");
        AgentSessionStore store = CreateStore();
        var key = new AgentSessionStoreKey("silinecek").WithPartition("isolation", "murat");

        Assert.Null(await store.GetSessionAsync(agent, key));
        await SaveMarkedAsync(store, agent, key, "x");

        Assert.True(await DeleteAsync(store, agent, key));
        Assert.False(await DeleteAsync(store, agent, key));
        Assert.Null(await CreateStore().GetSessionAsync(agent, key));
    }

    private static async Task SaveMarkedAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key, string mark)
    {
        AgentSession session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("sehir", mark);
        await store.SaveSessionAsync(agent, key, session);
    }

    private static async Task<string?> ReadMarkAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key) =>
        await store.GetSessionAsync(agent, key) is { } session ? Mark(session) : null;

    private static string? Mark(AgentSession session) =>
        session.StateBag.TryGetValue("sehir", out string? value) ? value : null;
}
