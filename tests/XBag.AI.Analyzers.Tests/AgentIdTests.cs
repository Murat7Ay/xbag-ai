namespace XBag.AI.Analyzers.Tests;

public sealed class AgentIdTests
{
    [Fact]
    public Task Options_without_Id_in_a_workflow_project_are_reported() =>
        Verify.AnalyzerAsync<AgentIdAnalyzer>("""
            static class Ajanlar
            {
                public static AIAgent Destek(IChatClient model) =>
                    model.AsAIAgent({|XBAG005:new ChatClientAgentOptions
                    {
                        Name = "destek",
                        Description = "Müşteri sorularını yanıtlar.",
                    }|});
            }
            """,
            Verify.Workflows);

    [Fact]
    public Task Overloads_that_cannot_set_an_Id_are_reported_in_a_workflow_project() =>
        Verify.AnalyzerAsync<AgentIdAnalyzer>("""
            static class Ajanlar
            {
                public static AIAgent Fatura(IChatClient model) =>
                    {|XBAG005:model.AsAIAgent(instructions: "Fatura sorularını yanıtla.", name: "fatura")|};

                public static AIAgent Iade(IChatClient model) =>
                    {|XBAG005:new ChatClientAgent(model, instructions: "İade taleplerini işle.", name: "iade")|};
            }
            """,
            Verify.Workflows);

    [Fact]
    public Task A_new_GUID_as_Id_is_reported_even_without_workflows() =>
        Verify.AnalyzerAsync<AgentIdAnalyzer>("""
            static class Ajanlar
            {
                public static AIAgent Destek(IChatClient model) =>
                    model.AsAIAgent(new ChatClientAgentOptions { {|XBAG005:Id = Guid.NewGuid().ToString("N")|}, Name = "destek" });
            }
            """);

    [Fact]
    public Task Stable_ids_and_projects_without_workflows_are_not_reported()
    {
        const string Source = """
            static class Ajanlar
            {
                public static AIAgent Destek(IChatClient model) =>
                    model.AsAIAgent(new ChatClientAgentOptions { Id = "destek-v1", Name = "destek" });

                public static AIAgent Fatura(IChatClient model)
                {
                    var options = new ChatClientAgentOptions { Name = "fatura" };
                    options.Id = "fatura-v1";
                    return model.AsAIAgent(options);
                }
            }
            """;

        return Task.WhenAll(
            Verify.AnalyzerAsync<AgentIdAnalyzer>(Source, Verify.Workflows),
            Verify.AnalyzerAsync<AgentIdAnalyzer>("""
                static class Sohbet
                {
                    // A plain chat agent, no workflows referenced: a random id is harmless.
                    public static AIAgent Asistan(IChatClient model) =>
                        model.AsAIAgent(new ChatClientAgentOptions { Name = "asistan" });

                    public static AIAgent Hizli(IChatClient model) => model.AsAIAgent(instructions: "Kısa yanıt ver.", name: "hizli");
                }
                """));
    }

    [Fact]
    public Task Fix_uses_the_constant_Name_as_Id() =>
        Verify.CodeFixAsync<AgentIdAnalyzer, AgentIdCodeFix>(
            """
            static class Ajanlar
            {
                public static AIAgent Destek(IChatClient model) =>
                    model.AsAIAgent({|XBAG005:new ChatClientAgentOptions
                    {
                        Name = "destek",
                        Description = "Müşteri sorularını yanıtlar.",
                    }|});

                public static AIAgent Kisa(IChatClient model) =>
                    model.AsAIAgent({|XBAG005:new ChatClientAgentOptions { Name = "kisa" }|});
            }
            """,
            """
            static class Ajanlar
            {
                public static AIAgent Destek(IChatClient model) =>
                    model.AsAIAgent(new ChatClientAgentOptions
                    {
                        Id = "destek",
                        Name = "destek",
                        Description = "Müşteri sorularını yanıtlar.",
                    });

                public static AIAgent Kisa(IChatClient model) =>
                    model.AsAIAgent(new ChatClientAgentOptions { Id = "kisa", Name = "kisa" });
            }
            """,
            Verify.Workflows);
}
