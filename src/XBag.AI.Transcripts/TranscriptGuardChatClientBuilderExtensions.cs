using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XBag.AI.Transcripts;

/// <summary>Adds <see cref="TranscriptGuardChatClient"/> to a chat pipeline.</summary>
public static class TranscriptGuardChatClientBuilderExtensions
{
    /// <summary>
    /// Validates and repairs the messages of every request. Call it BEFORE <c>UseFunctionInvocation()</c> (above the tool loop):
    /// <c>builder.UseTranscriptGuard().UseFunctionInvocation()</c>. Agent Framework's <c>ChatClientAgent</c> keeps a
    /// <c>FunctionInvokingChatClient</c> that is already in the client it is given, so the same line works for agents.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Sets the mode, the repair policies and the callbacks; repair everything when <see langword="null"/>.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Issues and repairs are logged through the <see cref="ILoggerFactory"/> of the builder's service provider, if any.</remarks>
    public static ChatClientBuilder UseTranscriptGuard(this ChatClientBuilder builder, Action<TranscriptGuardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new TranscriptGuardOptions();
        configure?.Invoke(options);
        return builder.Use((inner, services) => new TranscriptGuardChatClient(inner, options, services.GetService<ILoggerFactory>()));
    }
}
