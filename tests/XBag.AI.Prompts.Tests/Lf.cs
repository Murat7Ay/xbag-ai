namespace XBag.AI.Prompts.Tests;

/// <summary>
/// Templates render with "\n" line endings everywhere; a multi-line raw string in this file has the checkout's line endings
/// ("\r\n" on Windows with core.autocrlf). Expected texts go through this so the tests pass on both.
/// </summary>
internal static class Lf
{
    public static string Of(string text) => text.ReplaceLineEndings("\n");
}
