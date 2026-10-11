using System.Text.Json;

namespace XBag.AI.ToolSelection.Internal;

/// <summary><c>search_tools</c> or <c>unload_tools</c>; <see cref="ToolSelectingChatClient"/> finds its owner through it.</summary>
internal sealed class LoaderFunction(ToolSearch owner, bool isSearch, string name, string description, JsonElement schema) : AIFunction
{
    public ToolSearch Owner { get; } = owner;

    public override string Name { get; } = name;

    public override string Description { get; } = description;

    public override JsonElement JsonSchema { get; } = schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        isSearch
            ? await Owner.SearchAsync(arguments, cancellationToken).ConfigureAwait(false)
            : Owner.Unload(arguments);
}
