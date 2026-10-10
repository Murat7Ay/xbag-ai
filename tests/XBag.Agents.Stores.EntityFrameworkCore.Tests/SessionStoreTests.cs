using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using XBag.AI.Testing;
using XBag.Agents.Stores.EntityFrameworkCore;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>EfAgentSessionStore: the shared session store contract on SQLite, plus the readable key columns.</summary>
public sealed class SessionStoreTests : AgentSessionStoreContract, IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    protected override AgentSessionStore CreateStore(string? agentNamespace = null) =>
        new EfAgentSessionStore<AgentStoreDbContext>(_db.CreateFactory(), new EfAgentSessionStoreOptions { AgentNamespace = agentNamespace });

    protected override ValueTask<bool> DeleteAsync(AgentSessionStore store, AIAgent agent, AgentSessionStoreKey key) =>
        ((EfAgentSessionStore<AgentStoreDbContext>)store).DeleteSessionAsync(agent, key);

    [Fact]
    public async Task Tenant_partition_is_stored_in_its_own_column()
    {
        AIAgent agent = new ScriptedChatClient().AsAIAgent(name: "asistan");
        var key = new AgentSessionStoreKey("sohbet-7", new Dictionary<string, string> { ["isolation"] = "tenant-izmir", ["bolge"] = "ege" });

        await CreateStore().SaveSessionAsync(agent, key, await agent.CreateSessionAsync());
        await CreateStore().SaveSessionAsync(agent, key, await agent.CreateSessionAsync());   // second save updates the row

        using AgentStoreDbContext context = _db.CreateContext();
        StoredAgentSession row = await context.Sessions.SingleAsync(s => s.IsolationKey == "tenant-izmir");
        Assert.Equal(("asistan", "sohbet-7"), (row.AgentNamespace, row.SessionId));
        Assert.Equal("""{"bolge":"ege","isolation":"tenant-izmir"}""", row.Partitions);
        Assert.Equal(64, row.SessionKey.Length);
    }
}
