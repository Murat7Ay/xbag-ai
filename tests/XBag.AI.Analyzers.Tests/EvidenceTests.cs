using System.Diagnostics;
using Microsoft.Agents.AI;

namespace XBag.AI.Analyzers.Tests;

/// <summary>
/// The footguns behind XBAG001 and XBAG006, reproduced at runtime against Microsoft.Extensions.AI 10.10.0 and
/// Microsoft.Agents.AI 1.24.0 with a scripted model. If Microsoft fixes one, its test fails and the rule can retire.
/// </summary>
public sealed class EvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XBAG001_telemetry_outside_the_tool_loop_records_no_tool_spans(bool toolLoopFirst)
    {
        string source = "xbag-evidence-" + Guid.NewGuid().ToString("N");
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (spans) { spans.Add(a.DisplayName); } },
        };
        ActivitySource.AddActivityListener(listener);

        var model = new ScriptedChatClient().CallTool("saat").Reply("Saat 10:00.");
        ChatClientBuilder builder = model.AsBuilder();
        IChatClient client = (toolLoopFirst
                ? builder.UseFunctionInvocation().UseOpenTelemetry(sourceName: source)
                : builder.UseOpenTelemetry(sourceName: source).UseFunctionInvocation())
            .Build();

        ChatResponse response = await client.GetResponseAsync("Saat kaç?", new ChatOptions { Tools = [AIFunctionFactory.Create(() => "10:00", "saat")] });

        Assert.Equal("Saat 10:00.", response.Text);
        Assert.Equal(toolLoopFirst, spans.Any(s => s.StartsWith("execute_tool", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("none", false, "[GİZLİ]")]      // no telemetry: the replaced list arrives
    [InlineData("outer", false, "TR12 3456")]   // telemetry wraps the middleware: the replaced list is lost (XBAG006)
    [InlineData("outer", true, "[GİZLİ]")]      // telemetry wraps it, list changed in place (the code fix): arrives
    [InlineData("inner", false, "[GİZLİ]")]     // telemetry below the middleware: nothing re-reads the response after it
    public async Task XBAG006_a_replaced_message_list_is_lost_under_telemetry(string telemetry, bool inPlace, string expected)
    {
        var model = new ScriptedChatClient().Reply("IBAN: TR12 3456");
        AIAgentBuilder builder = model.AsAIAgent(new ChatClientAgentOptions { Id = "destek", Name = "destek" }).AsBuilder();
        if (telemetry == "outer")
        {
            builder = builder.UseOpenTelemetry(sourceName: "xbag-evidence");   // first Use = outermost: wraps the middleware
        }

        builder = builder
            .Use(
                runFunc: async (messages, session, options, inner, cancellationToken) =>
                {
                    AgentResponse response = await inner.RunAsync(messages, session, options, cancellationToken);
                    List<ChatMessage> masked = [.. response.Messages.Select(m => new ChatMessage(m.Role, m.Text.Replace("TR12 3456", "[GİZLİ]")))];
                    if (inPlace)
                    {
                        response.Messages.Clear();
                        foreach (ChatMessage message in masked)
                        {
                            response.Messages.Add(message);
                        }
                    }
                    else
                    {
                        response.Messages = masked;
                    }

                    return response;
                },
                runStreamingFunc: null);

        if (telemetry == "inner")
        {
            builder = builder.UseOpenTelemetry(sourceName: "xbag-evidence");
        }

        AgentResponse result = await builder.Build().RunAsync("IBAN'ım neydi?");

        Assert.Equal($"IBAN: {expected}", result.Text);
    }
}