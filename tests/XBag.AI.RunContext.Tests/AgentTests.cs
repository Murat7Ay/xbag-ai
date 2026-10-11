using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using XBag.Agents.RunContext;

namespace XBag.AI.RunContext.Tests;

/// <summary>RunContext on the real ChatClientAgent (which adds FunctionInvokingChatClient itself).</summary>
public sealed class AgentTests
{
    public static TheoryData<string, bool> Ways => new()
    {
        { "run options", false }, { "run options", true },
        { "UseRunContext resolver", false }, { "UseRunContext resolver", true },
        { "caller scope", false }, { "caller scope", true },
    };

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task Concurrent_runs_for_different_users_never_see_each_other(string way, bool streaming)
    {
        AppUser[] users = Users.Many(24);
        Dictionary<string, AppUser> signedIn = users.ToDictionary(u => u.UserId);
        ScriptedChatClient model = Models.CallThenEcho(users.Length);

        // One singleton agent and one tool instance for every user, as in a web app.
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Tools.WhoAmI()] } })
            .AsBuilder()
            .UseRunContext<AppUser>(run => run.RunOptions?.AdditionalProperties?.TryGetValue("oturum", out string? id) == true
                ? signedIn[id!]
                : null)
            .Build();

        string[] answers = await Task.WhenAll(users.Select(user => Task.Run(() => RunAs(user))));

        Assert.Equal(users.Select(u => $"Sen {u.UserId}@{u.Tenant}"), answers);

        async Task<string> RunAs(AppUser user)
        {
            AgentRunOptions options = way switch
            {
                "run options" => new AgentRunOptions().WithRunContext(user),
                "UseRunContext resolver" => new AgentRunOptions { AdditionalProperties = new() { ["oturum"] = user.UserId } },
                _ => new AgentRunOptions(),
            };

            using (way == "caller scope" ? RunContext<AppUser>.Begin(user) : null)
            {
                AgentResponse response = streaming
                    ? await agent.RunStreamingAsync("Ben kimim?", options: options).ToAgentResponseAsync()
                    : await agent.RunAsync("Ben kimim?", options: options);
                return response.Text;
            }
        }
    }

    [Theory]
    [InlineData("run options only", "Kullanıcı: yok")]
    [InlineData("run options + UseRunContext", "Kullanıcı: murat, firma: İzmir Lojistik A.Ş.")]
    [InlineData("caller scope", "Kullanıcı: murat, firma: İzmir Lojistik A.Ş.")]
    public async Task Context_providers_see_the_run_options_context_only_with_UseRunContext(string way, string expected)
    {
        var model = new ScriptedChatClient().Reply("Merhaba!");
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [new UserInstructions()] });
        if (way == "run options + UseRunContext")
        {
            agent = agent.AsBuilder().UseRunContext<AppUser>().Build();
        }

        using (way == "caller scope" ? RunContext<AppUser>.Begin(Users.Murat) : null)
        {
            await agent.RunAsync("Merhaba", options: way == "caller scope" ? null : new AgentRunOptions().WithRunContext(Users.Murat));
        }

        // Without the decorator a run-options value only becomes visible inside tool calls (FunctionInvokingChatClient.CurrentContext).
        Assert.Equal(expected, model.Requests[0].Instructions);
    }

    [Fact]
    public async Task The_resolver_can_read_the_session()
    {
        ScriptedChatClient model = Models.CallThenEcho(1);
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Tools.WhoAmI()] } })
            .AsBuilder()
            .UseRunContext<AppUser>(run => run.Session?.StateBag.GetValue<string>("kullanıcı") == "ayşe" ? Users.Ayse : null)
            .Build();
        AgentSession session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("kullanıcı", "ayşe");

        AgentResponse response = await agent.RunAsync("Ben kimim?", session);

        Assert.Equal("Sen ayşe@İstanbul Kargo A.Ş.", response.Text);
    }

    [Theory]
    [InlineData(false, "Sen murat@İzmir Lojistik A.Ş.")]
    [InlineData(true, "Sen kimse")]
    public async Task A_scope_opened_naively_in_a_streaming_decorator_is_lost_after_the_first_update(bool streaming, string expected)
    {
        ScriptedChatClient model = Models.CallThenEcho(1);
        AIAgent agent = new NaiveScopeAgent(
            model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Tools.WhoAmI()] } }), Users.Murat);

        AgentResponse response = streaming
            ? await agent.RunStreamingAsync("Ben kimim?").ToAgentResponseAsync()
            : await agent.RunAsync("Ben kimim?");

        // The tool runs after the tool-call update was yielded, on the consumer's ExecutionContext, where the scope was never set.
        Assert.Equal(expected, response.Text);
    }

    [Fact]
    public async Task UseRunContext_keeps_the_context_across_streaming_updates_and_consumer_code()
    {
        ScriptedChatClient model = Models.CallThenEcho(1);
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Tools.WhoAmI()] } })
            .AsBuilder()
            .UseRunContext<AppUser>(_ => Users.Murat)
            .Build();

        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Ben kimim?"))
        {
            // The consumer's own code between updates does not run inside the agent's scope.
            Assert.Null(RunContext<AppUser>.Current);
            updates.Add(update);
            await Task.Yield();
        }

        Assert.Equal("Sen murat@İzmir Lojistik A.Ş.", updates.ToAgentResponse().Text);
    }

    [Theory]
    [InlineData(false, "Sen kimse")]
    [InlineData(true, "Sen murat@İzmir Lojistik A.Ş.")]
    public async Task A_sub_agent_run_by_hand_from_a_tool_inherits_the_user_only_with_UseRunContext(bool useRunContext, string expected)
    {
        ScriptedChatClient innerModel = Models.CallThenEcho(1);
        AIAgent inner = innerModel.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [Tools.WhoAmI()] } });

        // A tool that runs another agent itself, without passing run options along.
        AIFunction askInner = AIFunctionFactory.Create(
            async (string soru) => (await inner.RunAsync(soru)).Text,
            new AIFunctionFactoryOptions { Name = "kimlik_ajani" });
        var outerModel = new ScriptedChatClient()
            .CallTool("kimlik_ajani", new { soru = "Ben kimim?" })
            .Reply(r => r.LastToolResult<string>()!);
        AIAgent outer = outerModel.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [askInner] } });
        if (useRunContext)
        {
            outer = outer.AsBuilder().UseRunContext<AppUser>().Build();
        }

        AgentResponse response = await outer.RunAsync("Ben kimim?", options: new AgentRunOptions().WithRunContext(Users.Murat));

        Assert.Equal(expected, response.Text);
    }

    [Fact]
    public async Task A_sub_agent_made_with_AsAIFunction_inherits_the_user_from_the_run_options()
    {
        ScriptedChatClient innerModel = Models.CallThenEcho(1);
        AIAgent inner = innerModel.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "kimlik_ajani",
            Description = "Kullanıcının kim olduğunu söyler.",
            ChatOptions = new() { Tools = [Tools.WhoAmI()] },
        });
        var outerModel = new ScriptedChatClient()
            .CallTool("kimlik_ajani", new { query = "Ben kimim?" })
            .Reply(r => r.LastToolResult<string>()!);
        AIAgent outer = outerModel.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [inner.AsAIFunction()] } });

        // AsAIFunction copies the parent tool call's AdditionalProperties into the sub-agent's run options.
        AgentResponse response = await outer.RunAsync("Ben kimim?", options: new AgentRunOptions().WithRunContext(Users.Murat));

        Assert.Equal("Sen murat@İzmir Lojistik A.Ş.", response.Text);
    }

    private sealed class UserInstructions : AIContextProvider
    {
        public override IReadOnlyList<string> StateKeys => [];

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            new(new AIContext
            {
                Instructions = RunContext<AppUser>.Current is { } user
                    ? $"Kullanıcı: {user.UserId}, firma: {user.Tenant}"
                    : "Kullanıcı: yok",
            });
    }

    /// <summary>What not to do: an AsyncLocal set once at the top of an async iterator.</summary>
    private sealed class NaiveScopeAgent(AIAgent innerAgent, AppUser user) : DelegatingAIAgent(innerAgent)
    {
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            using (RunContext<AppUser>.Begin(user))
            {
                return await InnerAgent.RunAsync(messages, session, options, cancellationToken);
            }
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using (RunContext<AppUser>.Begin(user))
            {
                await foreach (AgentResponseUpdate update in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
                {
                    yield return update;
                }
            }
        }
    }
}
