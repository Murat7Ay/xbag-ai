using System.Net.Http.Headers;

namespace XBag.AI.RunContext;

/// <summary>
/// An <see cref="HttpClient"/> handler that stamps the current request's bearer token on calls to allowed origins: the
/// <c>Authorization</c> header is computed from <see cref="RunContext{T}.Current"/> on every HTTP request.
/// </summary>
/// <typeparam name="T">The context type that holds (or leads to) the token.</typeparam>
/// <remarks>
/// <para>
/// Use it for the <see cref="HttpClient"/> of an MCP client (<c>new HttpClientTransport(options, httpClient)</c>) or of an internal
/// API that a tool calls, so one long-lived client serves every user with that user's token. The token function runs per HTTP
/// request; cache there (for example an on-behalf-of exchange cached per user and scope).
/// </para>
/// <para>
/// Security: a token is only ever attached to the allowed origins (scheme, host and port), which must be HTTPS (HTTP only for
/// loopback). A request to any other origin passes through untouched. Turn off automatic redirects on the inner handler, so a
/// redirect cannot be followed without this check. Only <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/>
/// is stamped; the synchronous <c>Send</c> passes through without a token.
/// </para>
/// </remarks>
public sealed class RunContextBearerHandler<T> : DelegatingHandler
    where T : class
{
    private readonly Func<T, CancellationToken, ValueTask<string?>> _getToken;
    private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="RunContextBearerHandler{T}"/> class.</summary>
    /// <param name="getToken">Gets the token for a context (for example its forwarded token, or an on-behalf-of token); <see langword="null"/> or empty sends no header.</param>
    /// <param name="allowedOrigins">The only origins that receive a token, for example <c>https://mcp.izmir-lojistik.example</c>. HTTPS, or HTTP on loopback.</param>
    public RunContextBearerHandler(Func<T, CancellationToken, ValueTask<string?>> getToken, IEnumerable<Uri> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(getToken);
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        _getToken = getToken;
        foreach (Uri origin in allowedOrigins)
        {
            ArgumentNullException.ThrowIfNull(origin, nameof(allowedOrigins));
            if (!origin.IsAbsoluteUri || !(origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)))
            {
                throw new ArgumentException($"Origin '{origin}' must be an absolute https URI (http only for loopback).", nameof(allowedOrigins));
            }

            _allowedOrigins.Add(OriginOf(origin));
        }

        if (_allowedOrigins.Count == 0)
        {
            throw new ArgumentException("At least one allowed origin is required.", nameof(allowedOrigins));
        }
    }

    /// <summary>
    /// Gets a value indicating whether a request to an allowed origin fails with <see cref="InvalidOperationException"/> when there is
    /// no run context. Default <see langword="false"/>: the request is sent without a token (an MCP handshake at startup, for example).
    /// </summary>
    public bool RequireRunContext { get; init; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { IsAbsoluteUri: true } uri && _allowedOrigins.Contains(OriginOf(uri)))
        {
            T? context = RunContext<T>.Current;
            if (context is null)
            {
                if (RequireRunContext)
                {
                    throw new InvalidOperationException(
                        $"No run context of type '{typeof(T).Name}' for {request.Method} {uri.GetLeftPart(UriPartial.Path)}; refusing to call it without the user's token.");
                }
            }
            else if (await _getToken(context, cancellationToken).ConfigureAwait(false) is { Length: > 0 } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string OriginOf(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}
