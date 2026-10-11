namespace XBag.AI.Transcripts;

/// <summary>What <see cref="TranscriptGuardChatClient"/> does when the messages of a request have issues.</summary>
public enum TranscriptGuardMode
{
    /// <summary>Repair them following <see cref="TranscriptGuardOptions.Repair"/> and send the repaired copy (default).</summary>
    Repair,

    /// <summary>Throw <see cref="InvalidTranscriptException"/> before anything is sent.</summary>
    Throw,

    /// <summary>Only log and report them (<see cref="TranscriptGuardOptions.OnIssues"/>); send the messages unchanged.</summary>
    Report,
}

/// <summary>Options for <see cref="TranscriptGuardChatClient"/>.</summary>
public sealed class TranscriptGuardOptions
{
    /// <summary>Gets or sets what happens to a request whose messages have issues. Default <see cref="TranscriptGuardMode.Repair"/>.</summary>
    public TranscriptGuardMode Mode { get; set; } = TranscriptGuardMode.Repair;

    /// <summary>Gets the repair policies used in <see cref="TranscriptGuardMode.Repair"/> mode.</summary>
    public TranscriptRepairOptions Repair { get; } = new();

    /// <summary>Gets or sets a callback for every request whose messages have issues, before the guard acts (all modes).</summary>
    public Action<TranscriptReport>? OnIssues { get; set; }

    /// <summary>Gets or sets a callback for every repair the guard makes, with what was fixed.</summary>
    public Action<TranscriptRepairResult>? OnRepaired { get; set; }
}
