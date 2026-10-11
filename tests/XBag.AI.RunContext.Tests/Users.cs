namespace XBag.AI.RunContext.Tests;

/// <summary>The context the tests flow: who is calling, for which company, with which token.</summary>
public sealed record AppUser(string UserId, string Tenant, string AccessToken);

internal static class Users
{
    public static readonly AppUser Murat = new("murat", "İzmir Lojistik A.Ş.", "tok-murat-35");
    public static readonly AppUser Ayse = new("ayşe", "İstanbul Kargo A.Ş.", "tok-ayse-34");

    public static AppUser[] Many(int count) => Enumerable.Range(1, count)
        .Select(i => new AppUser($"kullanıcı{i}", i % 2 == 0 ? "İzmir Lojistik A.Ş." : "İstanbul Kargo A.Ş.", $"tok-{i}"))
        .ToArray();
}

internal static class Tools
{
    /// <summary>A tool whose user is a hidden parameter: the model only sees <c>durum</c>.</summary>
    public static AIFunction MyOrders() => AIFunctionFactory.Create(
        (AppUser user, string durum) => $"{user.UserId} ({user.Tenant}): 3 {durum} sipariş",
        new AIFunctionFactoryOptions { Name = "siparislerim" }.BindRunContext<AppUser>());

    /// <summary>Answers with the caller, or "kimse" (nobody) when there is no context; yields first so the call really is asynchronous.</summary>
    public static AIFunction WhoAmI() => AIFunctionFactory.Create(
        async (AppUser? user = null) =>
        {
            await Task.Delay(Random.Shared.Next(1, 10));
            return user is null ? "kimse" : $"{user.UserId}@{user.Tenant}";
        },
        new AIFunctionFactoryOptions { Name = "ben_kimim" }.BindRunContext<AppUser>());
}

internal static class Models
{
    /// <summary>
    /// A scripted model whose every step fits any request: it calls <paramref name="tool"/> first, then answers "Sen &lt;result&gt;".
    /// So concurrent runs can share one model whatever order their calls arrive in.
    /// </summary>
    public static ScriptedChatClient CallThenEcho(int runs, string tool = "ben_kimim")
    {
        var model = new ScriptedChatClient();
        for (int i = 0; i < runs * 2; i++)
        {
            model.Reply(r => r.FindLastToolResult() is { } result
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Sen {result.Result}"))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{r.Index}", tool)])));
        }

        return model;
    }
}
