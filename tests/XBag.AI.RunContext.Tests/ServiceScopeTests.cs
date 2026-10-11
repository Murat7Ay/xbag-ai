using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using XBag.AI.RunContext.Agents;

namespace XBag.AI.RunContext.Tests;

/// <summary>WithServiceScope: a DI scope per tool call, with a scoped service built from the run's user.</summary>
public sealed class ServiceScopeTests
{
    [Fact]
    public async Task Every_tool_call_gets_its_own_scope_disposed_after_the_call()
    {
        ConcurrentQueue<OrderDb> created = [];
        await using ServiceProvider root = BuildServices(created);
        AIFunction tool = OrdersTool().WithServiceScope();

        var model = new ScriptedChatClient()
            .CallTool("siparis_say", new { durum = "yolda" })
            .CallTool("siparis_say", new { durum = "teslim" })
            .Reply(r => string.Join(" | ", r.ToolResults.Select(t => t.Result)));
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [tool] } }, services: root);

        AgentResponse response = await agent.RunAsync("Kaç siparişim var?", options: new AgentRunOptions().WithRunContext(Users.Murat));

        OrderDb[] dbs = [.. created];
        Assert.Equal(2, dbs.Length);
        Assert.NotEqual(dbs[0].Id, dbs[1].Id);
        Assert.All(dbs, db => Assert.True(db.Disposed));
        Assert.Equal($"murat yolda (db {dbs[0].Id}) | murat teslim (db {dbs[1].Id})", response.Text);
    }

    [Fact]
    public async Task Without_a_scope_the_root_provider_refuses_the_scoped_service()
    {
        await using ServiceProvider root = BuildServices([]);
        var model = new ScriptedChatClient().CallTool("siparis_say", new { durum = "yolda" }).Reply("Bir hata oldu.");
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [OrdersTool()] } }, services: root);

        await agent.RunAsync("Kaç siparişim var?", options: new AgentRunOptions().WithRunContext(Users.Murat));

        // FunctionInvokingChatClient hands tools the provider it was built with: here the root, where scoped services are refused.
        var error = Assert.IsType<InvalidOperationException>(model.Requests[1].FindLastToolResult()!.Exception);
        Assert.Contains("root provider", error.Message);
    }

    [Fact]
    public async Task Without_any_provider_the_call_fails_with_a_clear_message()
    {
        var model = new ScriptedChatClient().CallTool("siparis_say", new { durum = "yolda" }).Reply("Bir hata oldu.");
        IChatClient client = model.AsBuilder().UseFunctionInvocation().Build();

        await client.GetResponseAsync("Kaç siparişim var?", new ChatOptions { Tools = [OrdersTool().WithServiceScope()] });

        var error = Assert.IsType<InvalidOperationException>(model.Requests[1].FindLastToolResult()!.Exception);
        Assert.Contains("no service provider", error.Message);
    }

    private static AIFunction OrdersTool() => AIFunctionFactory.Create(
        (IServiceProvider services, string durum) =>
        {
            OrderDb db = services.GetRequiredService<OrderDb>();
            return $"{db.User.UserId} {durum} (db {db.Id})";
        },
        new AIFunctionFactoryOptions { Name = "siparis_say" });

    private static ServiceProvider BuildServices(ConcurrentQueue<OrderDb> created)
    {
        ServiceCollection services = new();

        // A scoped "DbContext" that knows its user: resolved inside the tool call, so the run context is there.
        services.AddScoped(_ =>
        {
            OrderDb db = new(RunContext<AppUser>.GetRequired());
            created.Enqueue(db);
            return db;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class OrderDb(AppUser user) : IDisposable
    {
        private static int s_lastId;

        public int Id { get; } = Interlocked.Increment(ref s_lastId);

        public AppUser User { get; } = user;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
