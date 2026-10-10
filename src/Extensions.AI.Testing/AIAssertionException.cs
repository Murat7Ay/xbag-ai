namespace Extensions.AI.Testing;

/// <summary>
/// Thrown when a test double's expectation is not met: a scripted fake ran out of steps, steps were left unused,
/// or an assertion helper such as <see cref="ScriptedChatClientAssertions.ShouldHaveCalledTool"/> failed.
/// </summary>
/// <remarks>
/// This is a plain exception so the kit works with any test framework (xUnit, NUnit, MSTest, TUnit):
/// every runner reports an unhandled exception as a test failure with its message.
/// </remarks>
public sealed class AIAssertionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AIAssertionException"/> class.</summary>
    /// <param name="message">A message that describes what was expected and what was observed.</param>
    public AIAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AIAssertionException"/> class.</summary>
    /// <param name="message">A message that describes what was expected and what was observed.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public AIAssertionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
