using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XBag.AI.Testing;
using XBag.Agents.Stores.EntityFrameworkCore;

namespace XBag.Agents.Stores.EntityFrameworkCore.Tests;

/// <summary>EfChatHistoryProvider under the real Agent Framework ChatClientAgent, with ScriptedChatClient as the model.</summary>
public sealed class ChatHistoryProviderTests
{
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
    public async Task Tool_calls_and_tool_results_round_trip()
    {
        using var db = new TestDatabase();
        AIFunction weather = AIFunctionFactory.Create(([Description("City")] string city) => $"{city}: 27C, güneşli", "get_weather");

        var model1 = new ScriptedChatClient()
            .CallTool("get_weather", new { city = "İzmir" }, callId: "call_izmir")
            .Reply(req => $"İzmir'de hava {req.LastToolResult<string>()}");
        AIAgent agent1 = CreateAgent(model1, db.CreateFactory(), weather);
        AgentSession session1 = await agent1.CreateSessionAsync();
        await agent1.RunAsync("İzmir'de hava nasıl?", session1);
        JsonElement saved = await agent1.SerializeSessionAsync(session1);

        var model2 = new ScriptedChatClient().Reply("Rica ederim.");
        AIAgent agent2 = CreateAgent(model2, db.CreateFactory(), weather);
        await agent2.RunAsync("Teşekkürler!", await agent2.DeserializeSessionAsync(saved));

        RecordedRequest request = Assert.Single(model2.Requests);
        FunctionCallContent call = Assert.Single(request.ToolCalls);
        Assert.Equal(("call_izmir", "get_weather", "İzmir"), (call.CallId, call.Name, call.Arguments!["city"]?.ToString()));
        FunctionResultContent result = Assert.Single(request.ToolResults);
        Assert.Equal("call_izmir", result.CallId);
        Assert.Equal("İzmir: 27C, güneşli", request.LastToolResult<string>());

        using AgentStoreDbContext context = db.CreateContext();
        string[] roles = await context.ChatMessages.OrderBy(m => m.Sequence).Select(m => m.Role).ToArrayAsync();
        Assert.Equal(["user", "assistant", "tool", "assistant", "user", "assistant"], roles);
    }

    [Fact]
    public async Task Concurrent_sessions_stay_isolated()
    {
        using var db = new TestDatabase();
        string[] cities = ["İstanbul", "Ankara", "İzmir", "Bursa", "Antalya", "Konya", "Adana", "Trabzon"];
        const int Turns = 3;

        var model = new ScriptedChatClient();
        for (int i = 0; i < cities.Length * Turns; i++)
        {
            model.Reply(req => $"Cevap: {req.LastUserText}");
        }

        var provider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory());
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });

        AgentSession[] sessions = await Task.WhenAll(cities.Select(city => Task.Run(async () =>
        {
            AgentSession session = await agent.CreateSessionAsync();
            for (int turn = 1; turn <= Turns; turn++)
            {
                await agent.RunAsync($"{city} {turn}", session);
            }

            return session;
        })));

        for (int i = 0; i < cities.Length; i++)
        {
            IReadOnlyList<ChatMessage> history = await provider.GetMessagesAsync(sessions[i]);
            Assert.Equal(Turns * 2, history.Count);
            Assert.All(history, m => Assert.Contains(cities[i] + " ", m.Text + " "));
            Assert.Equal($"Cevap: {cities[i]} {Turns}", history[^1].Text);
        }

        Assert.Equal(cities.Length, sessions.Select(provider.GetConversationId).Distinct().Count());
        model.ShouldBeExhausted();
    }

    [Fact]
    public async Task Concurrent_appends_to_one_conversation_keep_every_message_once()
    {
        using var db = new TestDatabase();
        AIAgent agent = new ScriptedChatClient().AsAIAgent();
        AgentSession session = await agent.CreateSessionAsync();
        var writers = new[]
        {
            new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
            new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory()),
        };
        string conversationId = writers[0].GetConversationId(session);   // both writers share the session state key

        await Task.WhenAll(Enumerable.Range(1, 20).Select(n => Task.Run(() => writers[n % 2].InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session,
                [new ChatMessage(ChatRole.User, $"soru {n}")],
                [new ChatMessage(ChatRole.Assistant, $"cevap {n}")])).AsTask())));

        using AgentStoreDbContext context = db.CreateContext();
        List<StoredChatMessage> rows = await context.ChatMessages.Where(m => m.ConversationId == conversationId).OrderBy(m => m.Sequence).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), rows.Select(r => r.Sequence));

        IReadOnlyList<ChatMessage> history = await writers[0].GetMessagesAsync(session);
        for (int n = 1; n <= 20; n++)
        {
            int question = history.ToList().FindIndex(m => m.Text == $"soru {n}");
            Assert.Equal($"cevap {n}", history[question + 1].Text);   // each run's messages stay together and in order
        }
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
    public async Task Streaming_runs_are_stored_and_a_failed_run_stores_nothing()
    {
        using var db = new TestDatabase();
        var model = new ScriptedChatClient()
            .Reply("Ankara'da hava serin.")
            .Fail(new HttpRequestException("429 Too Many Requests"));
        var provider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory());
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse streamed = await agent.RunStreamingAsync("Ankara'da hava nasıl?", session).ToAgentResponseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => agent.RunAsync("Peki yarın?", session));

        Assert.Equal("Ankara'da hava serin.", streamed.Text);
        Assert.Equal(["Ankara'da hava nasıl?", "Ankara'da hava serin."], (await provider.GetMessagesAsync(session)).Select(m => m.Text));
    }

    [Fact]
    public async Task Clear_deletes_only_that_sessions_history()
    {
        using var db = new TestDatabase();
        var model = new ScriptedChatClient().Reply("Bir.").Reply("İki.").Reply(req => $"{req.Messages.Count}");
        var provider = new EfChatHistoryProvider<AgentStoreDbContext>(db.CreateFactory());
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = provider });
        AgentSession murat = await agent.CreateSessionAsync();
        AgentSession ayse = await agent.CreateSessionAsync();
        await agent.RunAsync("Merhaba", murat);
        await agent.RunAsync("Selam", ayse);

        int deleted = await provider.ClearAsync(murat);
        AgentResponse afterClear = await agent.RunAsync("Yeniden merhaba", murat);

        Assert.Equal(2, deleted);
        Assert.Equal("1", afterClear.Text);                          // only the new message was sent
        Assert.Equal(2, (await provider.GetMessagesAsync(ayse)).Count);
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
    public async Task AddAgentStores_registers_both_stores_as_singletons()
    {
        using var db = new TestDatabase();
        var services = new ServiceCollection()
            .AddDbContextFactory<AgentStoreDbContext>(o => o.UseSqlite(db.ConnectionString))
            .AddAgentStores<AgentStoreDbContext>(o => o.StateKey = "sohbet");
        using ServiceProvider sp = services.BuildServiceProvider();

        var provider = sp.GetRequiredService<EfChatHistoryProvider<AgentStoreDbContext>>();
        var store = sp.GetRequiredService<EfCheckpointStore<AgentStoreDbContext>>();

        Assert.Same(provider, sp.GetRequiredService<EfChatHistoryProvider<AgentStoreDbContext>>());
        Assert.Same(store, sp.GetRequiredService<EfCheckpointStore<AgentStoreDbContext>>());
        Assert.Equal(["sohbet"], provider.StateKeys);
    }

    private static AIAgent CreateAgent(IChatClient model, IDbContextFactory<AgentStoreDbContext> factory, params AITool[] tools) =>
        model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatOptions = new() { Tools = tools },
            ChatHistoryProvider = new EfChatHistoryProvider<AgentStoreDbContext>(factory),
        });

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
