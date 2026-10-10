using System.Text.Json;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.EntityFrameworkCore;

/// <summary>Options for <see cref="EfChatHistoryProvider{TContext}"/>.</summary>
public sealed class EfChatHistoryProviderOptions
{
    /// <summary>
    /// Gets or sets the key under which the provider keeps its per-session state (the conversation id) in
    /// <see cref="AgentSession.StateBag"/>. Default: <see cref="EfChatHistoryProvider{TContext}.DefaultStateKey"/>.
    /// Change it only when two providers of this type serve the same agent.
    /// </summary>
    public string? StateKey { get; set; }

    /// <summary>
    /// Gets or sets the function that picks the conversation id for a session the first time the provider sees it.
    /// Default: a new GUID (32 hex digits). Return your own id (for example your app's chat id) to make rows easy to find;
    /// it must be unique per conversation, non-empty and at most 200 characters.
    /// </summary>
    public Func<AgentSession?, string>? ConversationIdFactory { get; set; }

    /// <summary>
    /// Gets or sets the JSON options used to serialize messages and the session state. Default:
    /// <c>AgentAbstractionsJsonUtilities.DefaultOptions</c>, which covers every <see cref="AIContent"/> type of
    /// Microsoft.Extensions.AI. Custom options must still be able to serialize <see cref="ChatMessage"/>.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}
