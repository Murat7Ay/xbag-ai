using System.Collections.Concurrent;
using System.Net;
using Microsoft.Agents.AI;
using XBag.Agents.RunContext;

namespace XBag.AI.RunContext.Tests;

/// <summary>RunContextBearerHandler: one long-lived HttpClient, every request stamped with the current user's token.</summary>
public sealed class BearerHandlerTests
{
    private static readonly Uri Api = new("https://api.izmir-lojistik.example");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_runs_call_the_api_with_their_own_users_token(bool streaming)
    {
        var server = new RecordingHandler();
        using HttpClient http = new(new RunContextBearerHandler<AppUser>(ExchangeAsync, [Api]) { InnerHandler = server });

        // The tool knows nothing about tokens: it just calls the API (an MCP client's HttpClient works the same way).
        AIFunction shipments = AIFunctionFactory.Create(
            async (AppUser user, CancellationToken ct) =>
                await http.GetStringAsync(new Uri(Api, $"/kargolar?kullanici={Uri.EscapeDataString(user.UserId)}"), ct),
            new AIFunctionFactoryOptions { Name = "kargolarim" }.BindRunContext<AppUser>());

        AppUser[] users = Users.Many(16);
        ScriptedChatClient model = Models.CallThenEcho(users.Length, "kargolarim");
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions { ChatOptions = new() { Tools = [shipments] } })
            .AsBuilder()
            .UseRunContext<AppUser>()
            .Build();

        await Task.WhenAll(users.Select(user => Task.Run(async () =>
        {
            AgentRunOptions options = new AgentRunOptions().WithRunContext(user);
            _ = streaming
                ? await agent.RunStreamingAsync("Kargolarım nerede?", options: options).ToAgentResponseAsync()
                : await agent.RunAsync("Kargolarım nerede?", options: options);
        })));

        Assert.Equal(users.Length, server.Requests.Count);
        Assert.All(server.Requests, request =>
        {
            string userId = Uri.UnescapeDataString(request.Url.Split("kullanici=")[1]);
            Assert.Equal($"Bearer obo:{users.Single(u => u.UserId == userId).AccessToken}", request.Authorization);
        });
    }

    [Theory]
    [InlineData("https://api.izmir-lojistik.example/kargolar", true)]
    [InlineData("https://API.Izmir-Lojistik.example:443/kargolar", true)]
    [InlineData("http://api.izmir-lojistik.example/kargolar", false)]
    [InlineData("https://api.izmir-lojistik.example:8443/kargolar", false)]
    [InlineData("https://api.izmir-lojistik.example.evil.example/kargolar", false)]
    [InlineData("https://hava-durumu.example/izmir", false)]
    public async Task Only_the_allowed_origin_ever_gets_the_token(string url, bool stamped)
    {
        var server = new RecordingHandler();
        using HttpClient http = new(new RunContextBearerHandler<AppUser>(ExchangeAsync, [Api]) { InnerHandler = server });

        using (RunContext<AppUser>.Begin(Users.Murat))
        {
            await http.GetStringAsync(new Uri(url));
        }

        Assert.Equal(stamped ? "Bearer obo:tok-murat-35" : null, Assert.Single(server.Requests).Authorization);
    }

    [Fact]
    public async Task Without_a_context_the_request_goes_out_without_a_token_or_is_refused()
    {
        var server = new RecordingHandler();
        using HttpClient lenient = new(new RunContextBearerHandler<AppUser>(ExchangeAsync, [Api]) { InnerHandler = server });
        using HttpClient strict = new(new RunContextBearerHandler<AppUser>(ExchangeAsync, [Api])
        {
            InnerHandler = server,
            RequireRunContext = true,
        });

        await lenient.GetStringAsync(new Uri(Api, "/mcp"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => strict.GetStringAsync(new Uri(Api, "/mcp")));

        Assert.Null(Assert.Single(server.Requests).Authorization);
        Assert.Contains("AppUser", error.Message);
    }

    [Fact]
    public void Allowed_origins_must_be_https_or_loopback()
    {
        Assert.Throws<ArgumentException>(() => new RunContextBearerHandler<AppUser>(ExchangeAsync, [new Uri("http://api.izmir-lojistik.example")]));
        Assert.Throws<ArgumentException>(() => new RunContextBearerHandler<AppUser>(ExchangeAsync, []));

        using var local = new RunContextBearerHandler<AppUser>(ExchangeAsync, [new Uri("http://localhost:5080")]);
    }

    /// <summary>Stands in for an on-behalf-of exchange: asynchronous, one token per user.</summary>
    private static async ValueTask<string?> ExchangeAsync(AppUser user, CancellationToken cancellationToken)
    {
        await Task.Delay(Random.Shared.Next(1, 5), cancellationToken);
        return "obo:" + user.AccessToken;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(string Url, string? Authorization)> _requests = new();

        public IReadOnlyCollection<(string Url, string? Authorization)> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("2 kargo yolda") });
        }
    }
}
