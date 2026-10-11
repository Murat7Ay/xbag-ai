namespace XBag.AI.Analyzers.Tests;

public sealed class TelemetryOrderTests
{
    [Fact]
    public Task Telemetry_before_the_tool_loop_is_reported() =>
        Verify.AnalyzerAsync<TelemetryOrderAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .{|XBAG001:UseOpenTelemetry|}(sourceName: "destek-asistani")
                        .UseFunctionInvocation()
                        .Build();
            }
            """);

    [Fact]
    public Task Telemetry_further_out_with_other_middleware_between_is_reported() =>
        Verify.AnalyzerAsync<TelemetryOrderAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    new ChatClientBuilder(model).{|XBAG001:UseOpenTelemetry|}().UseLogging().UseFunctionInvocation().Build();
            }
            """);

    [Fact]
    public Task Tool_loop_first_is_not_reported() =>
        Verify.AnalyzerAsync<TelemetryOrderAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .UseFunctionInvocation()
                        .UseOpenTelemetry()
                        .Build();
            }
            """);

    [Fact]
    public Task An_extra_outer_span_on_top_of_an_inner_one_is_not_reported() =>
        Verify.AnalyzerAsync<TelemetryOrderAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .UseOpenTelemetry(sourceName: "istek")
                        .UseFunctionInvocation()
                        .UseOpenTelemetry(sourceName: "model")
                        .Build();
            }
            """);

    [Fact]
    public Task Separate_chains_are_not_reported() =>
        Verify.AnalyzerAsync<TelemetryOrderAnalyzer>("""
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model)
                {
                    IChatClient traced = model.AsBuilder().UseOpenTelemetry().Build();
                    return traced.AsBuilder().UseFunctionInvocation().Build();
                }
            }
            """);

    [Fact]
    public Task Fix_moves_only_the_telemetry_call_after_the_tool_loop() =>
        Verify.CodeFixAsync<TelemetryOrderAnalyzer, TelemetryOrderCodeFix>(
            """
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .{|XBAG001:UseOpenTelemetry|}(sourceName: "destek-asistani")
                        .UseLogging()
                        .UseFunctionInvocation()
                        .Build();
            }
            """,
            """
            static class Pipeline
            {
                public static IChatClient Build(IChatClient model) =>
                    model.AsBuilder()
                        .UseLogging()
                        .UseFunctionInvocation()
                        .UseOpenTelemetry(sourceName: "destek-asistani")
                        .Build();
            }
            """);

    [Fact]
    public Task Fix_works_on_one_line_and_as_the_last_call() =>
        Verify.CodeFixAsync<TelemetryOrderAnalyzer, TelemetryOrderCodeFix>(
            """
            static class Pipeline
            {
                public static ChatClientBuilder Configure(ChatClientBuilder builder) => builder.{|XBAG001:UseOpenTelemetry|}().UseFunctionInvocation();
            }
            """,
            """
            static class Pipeline
            {
                public static ChatClientBuilder Configure(ChatClientBuilder builder) => builder.UseFunctionInvocation().UseOpenTelemetry();
            }
            """);
}
