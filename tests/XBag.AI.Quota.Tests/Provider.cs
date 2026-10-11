using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;

namespace XBag.AI.Quota.Tests;

/// <summary>Provider failures shaped like the real ones.</summary>
internal static class Provider
{
    /// <summary>What the OpenAI / Azure OpenAI SDKs throw for a 429: a ClientResultException over the raw HTTP response.</summary>
    public static ClientResultException RateLimited(int retryAfterSeconds) =>
        Error(429, ("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static ClientResultException Error(int status, params (string Name, string Value)[] headers) =>
        new($"HTTP {status} from the model provider", new FakeResponse(status, headers));

    /// <summary>What a plain HttpClient-based adapter throws: a status, but no headers.</summary>
    public static HttpRequestException HttpStatus(HttpStatusCode status) => new($"HTTP {(int)status}", null, status);

    private sealed class FakeResponse(int status, (string Name, string Value)[] headers) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => status == 429 ? "Too Many Requests" : "Error";

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString("{\"error\":{\"message\":\"Rate limit reached\"}}");

        protected override PipelineResponseHeaders HeadersCore => new FakeHeaders(headers);

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);

        public override void Dispose()
        {
        }
    }

    private sealed class FakeHeaders((string Name, string Value)[] headers) : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToArray();
            return values.Any();
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).GetEnumerator();
    }
}

/// <summary>Streams the first update of the inner response, then fails: output is already committed.</summary>
internal sealed class FailsMidStream(IChatClient inner, Exception error) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
            throw error;
        }
    }
}
