namespace XBag.AI.ToolSelection;

/// <summary>Options for <see cref="ToolSearch"/>. Names and descriptions are configurable so they can be tuned in eval loops.</summary>
public sealed class ToolSearchOptions
{
    /// <summary>Gets or sets how <c>search_tools</c> ranks the catalog. Default: <see cref="KeywordToolSelectionStrategy"/>.</summary>
    public IToolSelectionStrategy Strategy { get; set; } = new KeywordToolSelectionStrategy();

    /// <summary>Gets or sets the name of the search tool. Default <c>search_tools</c>.</summary>
    public string SearchToolName { get; set; } = "search_tools";

    /// <summary>Gets or sets the description of the search tool, which is the model's instruction for when to call it.</summary>
    public string SearchToolDescription { get; set; } =
        "Finds and loads tools for a capability you do not have yet. Describe the capability in a few words " +
        "(for example 'weather forecast' or 'currency conversion'). The matching tools are available from your next step.";

    /// <summary>Gets or sets the description of the search tool's <c>query</c> parameter.</summary>
    public string QueryDescription { get; set; } = "A few words describing the capability you need.";

    /// <summary>Gets or sets the name of the unload tool. Default <c>unload_tools</c>.</summary>
    public string UnloadToolName { get; set; } = "unload_tools";

    /// <summary>Gets or sets the description of the unload tool.</summary>
    public string UnloadToolDescription { get; set; } = "Removes loaded tools you no longer need, by name.";

    /// <summary>Gets or sets how many tools one search loads. Default 5.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many searched tools stay loaded at once. Default 20. When a search goes past it, the tools loaded
    /// longest ago are unloaded first. Must be at least <see cref="MaxResults"/>.
    /// </summary>
    public int MaxLoadedTools { get; set; } = 20;
}
