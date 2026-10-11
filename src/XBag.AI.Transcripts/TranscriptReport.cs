namespace XBag.AI.Transcripts;

/// <summary>The result of <see cref="TranscriptValidator.Validate"/>: every issue found, in message order.</summary>
public sealed class TranscriptReport
{
    internal TranscriptReport(IReadOnlyList<TranscriptIssue> issues, int messageCount)
    {
        Issues = issues;
        MessageCount = messageCount;
    }

    /// <summary>Gets the issues, ordered by message index.</summary>
    public IReadOnlyList<TranscriptIssue> Issues { get; }

    /// <summary>Gets the number of messages that were validated.</summary>
    public int MessageCount { get; }

    /// <summary>Gets a value indicating whether the transcript has no issues.</summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>Throws <see cref="InvalidTranscriptException"/> when the transcript has issues; handy in tests.</summary>
    /// <exception cref="InvalidTranscriptException">The transcript has at least one issue.</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidTranscriptException(this);
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsValid
            ? $"Transcript of {MessageCount} message(s) is valid."
            : $"Transcript of {MessageCount} message(s) has {Issues.Count} issue(s):{Environment.NewLine}" +
              string.Join(Environment.NewLine, Issues.Select(issue => "- " + issue));
}

/// <summary>Thrown when a transcript has issues and the caller asked for an exception instead of a repair.</summary>
public sealed class InvalidTranscriptException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="InvalidTranscriptException"/> class.</summary>
    /// <param name="report">The report with the issues.</param>
    public InvalidTranscriptException(TranscriptReport report)
        : base(Describe(report))
    {
        Report = report;
    }

    private static string Describe(TranscriptReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.ToString();
    }

    /// <summary>Gets the report with the issues.</summary>
    public TranscriptReport Report { get; }
}
