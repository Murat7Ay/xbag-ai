using Extensions.AI.Testing.Internal;

namespace Extensions.AI.Testing;

/// <summary>
/// Framework-neutral assertions over the requests a <see cref="ScriptedChatClient"/> recorded.
/// Each method throws <see cref="AIAssertionException"/> with a descriptive message on failure.
/// </summary>
/// <remarks>
/// Tool assertions look at what the pipeline sent back to the model. A tool counts as <em>called</em> when a
/// <see cref="FunctionCallContent"/> for it and a <see cref="FunctionResultContent"/> with the same call id appear in the
/// recorded requests, which is what happens after <c>FunctionInvokingChatClient</c> runs the tool and asks the model again.
/// Calls are de-duplicated by call id, because each request repeats the history.
/// </remarks>
public static class ScriptedChatClientAssertions
{
    /// <summary>Gets the tool calls that were executed (a result was sent back), in the order they first appeared.</summary>
    /// <param name="client">The scripted client.</param>
    /// <returns>The executed calls.</returns>
    public static IReadOnlyList<FunctionCallContent> GetExecutedToolCalls(this ScriptedChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        (List<FunctionCallContent> calls, HashSet<string> resultIds) = Collect(client.Requests);
        return calls.Where(c => resultIds.Contains(c.CallId)).ToArray();
    }

    /// <summary>Asserts that the tool <paramref name="name"/> was executed at least once, optionally with matching arguments.</summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="name">The function name.</param>
    /// <param name="argumentsMatch">An optional predicate over the call's arguments (an empty dictionary when the call had none).</param>
    /// <returns>The first executed call that matched.</returns>
    /// <exception cref="AIAssertionException">No matching executed call was found.</exception>
    public static FunctionCallContent ShouldHaveCalledTool(
        this ScriptedChatClient client, string name, Func<IDictionary<string, object?>, bool>? argumentsMatch = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(name);

        IReadOnlyList<RecordedRequest> requests = client.Requests;
        (List<FunctionCallContent> calls, HashSet<string> resultIds) = Collect(requests);

        List<FunctionCallContent> named = calls.Where(c => c.Name == name).ToList();
        List<FunctionCallContent> executed = named.Where(c => resultIds.Contains(c.CallId)).ToList();
        FunctionCallContent? match = executed.FirstOrDefault(c => argumentsMatch is null || argumentsMatch(c.Arguments ?? new Dictionary<string, object?>()));
        if (match is not null)
        {
            return match;
        }

        string reason =
            executed.Count > 0 ? $"it was called, but no call matched the argument predicate. Calls: {string.Join("; ", executed.Select(Describe.Call))}"
            : named.Count > 0 ? $"the model requested it ({string.Join("; ", named.Select(Describe.Call))}) but no result was sent back. " +
                                "Is FunctionInvokingChatClient missing, does the tool require approval, or did the loop terminate?"
            : $"no call to it appears in the {requests.Count} recorded request(s). {SeenCalls(calls, resultIds)}";

        throw new AIAssertionException($"Expected tool '{name}' to have been called, but {reason}");
    }

    /// <summary>Asserts that the tool <paramref name="name"/> was never executed.</summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="name">The function name.</param>
    /// <exception cref="AIAssertionException">The tool was executed.</exception>
    public static void ShouldNotHaveCalledTool(this ScriptedChatClient client, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        List<FunctionCallContent> executed = client.GetExecutedToolCalls().Where(c => c.Name == name).ToList();
        if (executed.Count > 0)
        {
            throw new AIAssertionException(
                $"Expected tool '{name}' not to have been called, but it was called {executed.Count} time(s): {string.Join("; ", executed.Select(Describe.Call))}");
        }
    }

    /// <summary>Asserts that the tool <paramref name="name"/> was executed exactly <paramref name="times"/> times.</summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="name">The function name.</param>
    /// <param name="times">The expected number of executed calls.</param>
    /// <exception cref="AIAssertionException">The count differs.</exception>
    public static void ShouldHaveCalledToolTimes(this ScriptedChatClient client, string name, int times)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(times);

        IReadOnlyList<FunctionCallContent> all = client.GetExecutedToolCalls();
        int actual = all.Count(c => c.Name == name);
        if (actual != times)
        {
            throw new AIAssertionException(
                $"Expected tool '{name}' to have been called {times} time(s), but it was called {actual} time(s). " +
                $"Executed calls: {(all.Count == 0 ? "(none)" : string.Join("; ", all.Select(Describe.Call)))}");
        }
    }

    /// <summary>Asserts that exactly these tools were executed, in this order.</summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="names">The expected function names, in execution order.</param>
    /// <exception cref="AIAssertionException">The executed sequence differs.</exception>
    public static void ShouldHaveCalledToolsInOrder(this ScriptedChatClient client, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        string[] actual = client.GetExecutedToolCalls().Select(c => c.Name).ToArray();
        if (!actual.SequenceEqual(names, StringComparer.Ordinal))
        {
            throw new AIAssertionException(
                $"Expected tools to have been called in order [{string.Join(", ", names)}], but the executed order was [{string.Join(", ", actual)}].");
        }
    }

    /// <summary>Asserts how many model calls the client received.</summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="count">The expected number of requests.</param>
    /// <exception cref="AIAssertionException">The count differs.</exception>
    public static void ShouldHaveReceivedRequests(this ScriptedChatClient client, int count)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        IReadOnlyList<RecordedRequest> requests = client.Requests;
        if (requests.Count != count)
        {
            throw new AIAssertionException(
                $"Expected {count} model request(s), but received {requests.Count}:\n{string.Join("\n", requests.Select(r => "  " + r))}");
        }
    }

    /// <summary>
    /// Asserts that the system guidance (<see cref="ChatOptions.Instructions"/> plus system messages, see
    /// <see cref="RecordedRequest.SystemPrompt"/>) contained <paramref name="expected"/> (ordinal comparison).
    /// </summary>
    /// <param name="client">The scripted client.</param>
    /// <param name="expected">The text that must appear.</param>
    /// <param name="requestIndex">The zero-based request to check, or <see langword="null"/> to pass when any request matches.</param>
    /// <returns>The first request that matched.</returns>
    /// <exception cref="AIAssertionException">No checked request contained the text.</exception>
    public static RecordedRequest ShouldHaveSentSystemPrompt(this ScriptedChatClient client, string expected, int? requestIndex = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(expected);

        IReadOnlyList<RecordedRequest> requests = client.Requests;
        if (requestIndex is int index && (index < 0 || index >= requests.Count))
        {
            throw new AIAssertionException($"Expected a system prompt in request index {index}, but only {requests.Count} request(s) were received.");
        }

        IEnumerable<RecordedRequest> candidates = requestIndex is int i ? [requests[i]] : requests;
        RecordedRequest? match = candidates.FirstOrDefault(r => r.SystemPrompt.Contains(expected, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
        }

        string seen = requests.Count == 0
            ? "no requests were received"
            : "system prompts sent:\n" + string.Join("\n", candidates.Select(r => $"  #{r.Index + 1}: {Describe.Text(r.SystemPrompt)}"));
        throw new AIAssertionException($"Expected a system prompt containing {Describe.Text(expected)}, but {seen}");
    }

    private static (List<FunctionCallContent> Calls, HashSet<string> ResultIds) Collect(IEnumerable<RecordedRequest> requests)
    {
        List<FunctionCallContent> calls = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> resultIds = new(StringComparer.Ordinal);

        foreach (AIContent content in requests.SelectMany(r => r.Messages).SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call when seen.Add(call.CallId):
                    calls.Add(call);
                    break;
                case FunctionResultContent result:
                    resultIds.Add(result.CallId);
                    break;
            }
        }

        return (calls, resultIds);
    }

    private static string SeenCalls(List<FunctionCallContent> calls, HashSet<string> resultIds) =>
        calls.Count == 0
            ? "No tool calls were seen at all."
            : "Tool calls seen: " + string.Join("; ", calls.Select(c => $"{Describe.Call(c)}{(resultIds.Contains(c.CallId) ? string.Empty : " (no result)")}"));
}
