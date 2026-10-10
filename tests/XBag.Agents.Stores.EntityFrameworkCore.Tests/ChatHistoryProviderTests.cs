using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XBag.AI.Testing;
using XBag.Agents.Stores.EntityFrameworkCore;
using XBag.Agents.Stores.Tests;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>
/// EfChatHistoryProvider under the real Agent Framework ChatClientAgent, with ScriptedChatClient as the model:
/// the shared chat history contract on SQLite, plus EF-specific checks.
/// </summary>
public sealed class ChatHistoryProviderTests : ChatHistoryStoreContract, IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    protected override ChatHistoryProvider CreateProvider() => new EfChatHistoryProvider<AgentStoreDbContext>(_db.CreateFactory());

    protected override Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatHistoryProvider provider, AgentSession session) =>
        ((EfChatHistoryProvider<AgentStoreDbContext>)provider).GetMessagesAsync(session);

    protected override Task<int> ClearAsync(ChatHistoryProvider provider, AgentSession session) =>
        ((EfChatHistoryProvider<AgentStoreDbContext>)provider).ClearAsync(session);

    [Fact]
    public async Task Conversation_survives_a_restart()
    {
        using var db = new TestDatabase();   // SQLite in-memory; each context opens its own connection

        // Process 1: first turn, then keep the small session JSON (it only holds the conversation id).
        var model1 = new ScriptedChatClient().Reply("Merhaba Murat! İstanbul'da hava güneşli.");
        AIAgent agent1 = model1.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatHistoryProvider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
        });
        AgentSession session1 = await agent1.CreateSessionAsync();
        await agent1.RunAsync("Merhaba, ben Murat. İstanbul'da hava nasıl?", session1);
        JsonElement saved = await agent1.SerializeSessionAsync(session1);

        // Process 2: new model, agent, provider and contexts. The history comes back from the database.
        var model2 = new ScriptedChatClient().Reply(req => $"{req.Messages.Count} mesaj hatırlıyorum.");
        AIAgent agent2 = model2.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatHistoryProvider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
        });
        AgentSession session2 = await agent2.DeserializeSessionAsync(saved);
        AgentResponse answer = await agent2.RunAsync("Ben kimim?", session2);

        Assert.Equal("3 mesaj hatırlıyorum.", answer.Text);               // user, assistant, new user message
        Assert.Equal("Merhaba, ben Murat. İstanbul'da hava nasıl?", model2.Requests[0].Messages[0].Text);
        using AgentStoreDbContext tables = db.CreateContext();               // the rows are plain SQL tables
        Assert.Equal(4, await tables.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task A_writer_that_loses_the_race_for_a_sequence_number_retries_after_the_winner()
    {
        using var db = new TestDatabase();
        var intruder = new IntruderInterceptor(db);
        var provider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory(intruder));
        AIAgent agent = new ScriptedChatClient().Reply("Ankara'dan selamlar.")
            .AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession session = await agent.CreateSessionAsync();

        await agent.RunAsync("Merhaba", session);

        Assert.Equal(1, intruder.Collisions);
        Assert.Equal(["Araya giren mesaj", "Merhaba", "Ankara'dan selamlar."], (await provider.GetMessagesAsync(session)).Select(m => m.Text));
    }

    [Fact]
    public async Task Own_DbContext_with_custom_table_and_conversation_id()
    {
        string connectionString = $"Data Source=shop-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var factory = new ShopDbContextFactory(connectionString);
        using (ShopDbContext context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        var provider = new EfChatHistoryProvider<ShopDbContext>(factory, new EfChatHistoryProviderOptions
        {
            ConversationIdFactory = _ => "murat-siparis-42",
        });
        AIAgent agent = new ScriptedChatClient().Reply("Siparişiniz İzmir'e yola çıktı.")
            .AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession session = await agent.CreateSessionAsync();
        await agent.RunAsync("Siparişim nerede?", session);

        using var command = keepAlive.CreateCommand();
        command.CommandText = "SELECT group_concat(role, ',') FROM (SELECT role FROM chat_log WHERE conversation_id = 'murat-siparis-42' ORDER BY sequence)";
        Assert.Equal("user,assistant", command.ExecuteScalar());
        Assert.Equal("murat-siparis-42", provider.GetConversationId(session));
    }

    [Fact]
    public void AddAgentStores_registers_the_chat_history_provider_and_session_store_as_singletons()
    {
        using var db = new TestDatabase();
        using ServiceProvider sp = new ServiceCollection()
            .AddDbContextFactory<AgentStoreDbContext>(o => o.UseSqlite(db.ConnectionString))
            .AddAgentStores<AgentStoreDbContext>(o => o.ChatHistory.StateKey = "sohbet")
            .BuildServiceProvider();

        var provider = sp.GetRequiredService<EfChatHistoryProvider<AgentStoreDbContext>>();

        Assert.Same(provider, sp.GetRequiredService<EfChatHistoryProvider<AgentStoreDbContext>>());
        Assert.Same(sp.GetRequiredService<EfAgentSessionStore<AgentStoreDbContext>>(), sp.GetRequiredService<EfAgentSessionStore<AgentStoreDbContext>>());
        Assert.Equal(["sohbet"], provider.StateKeys);
    }

    /// <summary>Just before the first save, another writer takes the sequence number the save is about to use.</summary>
    private sealed class IntruderInterceptor(TestDatabase db) : SaveChangesInterceptor
    {
        public int Collisions { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Collisions == 0 && eventData.Context!.ChangeTracker.Entries<StoredChatMessage>().FirstOrDefault()?.Entity is { } first)
            {
                Collisions++;
                using AgentStoreDbContext other = db.CreateContext();
                other.ChatMessages.Add(new StoredChatMessage
                {
                    ConversationId = first.ConversationId,
                    Sequence = first.Sequence,
                    Role = "user",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, "Araya giren mesaj"), AIJsonUtilities.DefaultOptions),
                });
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.MapAgentStores(o => o.ChatMessagesTable = "chat_log");
    }

    private sealed class ShopDbContextFactory(string connectionString) : IDbContextFactory<ShopDbContext>
    {
        public ShopDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ShopDbContext>().UseSqlite(connectionString).Options);
    }
}
