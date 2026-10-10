using Microsoft.Extensions.DependencyInjection;
using XBag.AI.SpendCaps;

namespace XBag.AI.SpendCaps.Tests;

/// <summary>services.AddSpendCaps() and UseSpendCaps() resolving options and the shared ledger from DI.</summary>
public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task Pipelines_built_from_one_provider_share_the_registered_options_and_ledger()
    {
        var model = new ScriptedChatClient()
            .Reply("Birinci.").Usage(100, 50)
            .Reply("never sent");

        var services = new ServiceCollection();
        services.AddSpendCaps(o =>
        {
            o.PerTenant = new SpendLimit { MaxTokens = 300 };
            o.EstimateInputTokens = (_, _) => 100;
        });
        services.AddChatClient(model).UseFunctionInvocation().UseSpendCaps();
        services.AddKeyedChatClient("second", model).UseSpendCaps();
        using ServiceProvider provider = services.BuildServiceProvider();

        IChatClient first = provider.GetRequiredService<IChatClient>();
        IChatClient second = provider.GetRequiredKeyedService<IChatClient>("second");
        SpendLedger ledger = provider.GetRequiredService<SpendLedger>();
        var options = new ChatOptions
        {
            MaxOutputTokens = 100,
            AdditionalProperties = new() { [SpendCapsPropertyNames.TenantId] = "Anadolu Lojistik A.Ş." },
        };

        await first.GetResponseAsync("Merhaba", options);
        var ex = await Assert.ThrowsAsync<SpendCapExceededException>(() => second.GetResponseAsync("Merhaba", options));

        Assert.Same(ledger, first.GetService<SpendLedger>());
        Assert.Same(ledger, second.GetService<SpendLedger>());
        Assert.Equal(SpendScopeKind.Tenant, ex.Breach.Scope);
        Assert.Equal(150, ledger.GetTotals(SpendScopeKind.Tenant, "Anadolu Lojistik A.Ş.").Tokens);
    }
}
