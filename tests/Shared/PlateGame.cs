using Microsoft.Agents.AI.Workflows;

namespace XBag.Agents.Stores.Tests;

/// <summary>Signals between the two executors of <see cref="PlateGame"/>.</summary>
public enum PlateSignal
{
    Start,
    Lower,
    Higher,
}

/// <summary>Bounds kept by the guesser across checkpoints.</summary>
public sealed record PlateBounds(int Lower, int Upper);

/// <summary>
/// A two-executor loop that finds a Turkish licence plate code (1-81) by binary search, after the Agent Framework
/// CheckpointAndRehydrate sample. Both executors save their state in checkpoints and restore it on resume.
/// </summary>
internal static class PlateGame
{
    public static Workflow Build()
    {
        var guesser = new PlateGuesser();
        var judge = new PlateJudge(target: 35);
        return new WorkflowBuilder(guesser)
            .AddEdge(guesser, judge)
            .AddEdge(judge, guesser)
            .WithOutputFrom(judge)
            .Build();
    }
}

[SendsMessage(typeof(int))]
internal sealed class PlateGuesser() : Executor<PlateSignal>("Guesser")
{
    private const string StateKey = "bounds";
    private PlateBounds _bounds = new(1, 81);

    private int Guess => (_bounds.Lower + _bounds.Upper) / 2;

    public override async ValueTask HandleAsync(PlateSignal message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _bounds = message switch
        {
            PlateSignal.Lower => _bounds with { Upper = Guess - 1 },
            PlateSignal.Higher => _bounds with { Lower = Guess + 1 },
            _ => _bounds,
        };
        await context.SendMessageAsync(Guess, cancellationToken: cancellationToken);
    }

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.QueueStateUpdateAsync(StateKey, _bounds, cancellationToken: cancellationToken);

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        _bounds = await context.ReadStateAsync<PlateBounds>(StateKey, cancellationToken: cancellationToken) ?? _bounds;
}

[SendsMessage(typeof(PlateSignal))]
[YieldsOutput(typeof(string))]
internal sealed class PlateJudge(int target) : Executor<int>("Judge")
{
    private const string StateKey = "tries";
    private int _tries;

    public override async ValueTask HandleAsync(int message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _tries++;
        if (message == target)
        {
            await context.YieldOutputAsync($"{target} İzmir: {_tries} denemede bulundu", cancellationToken: cancellationToken);
        }
        else
        {
            await context.SendMessageAsync(message > target ? PlateSignal.Lower : PlateSignal.Higher, cancellationToken: cancellationToken);
        }
    }

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.QueueStateUpdateAsync(StateKey, _tries, cancellationToken: cancellationToken);

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken = default) =>
        _tries = await context.ReadStateAsync<int>(StateKey, cancellationToken: cancellationToken);
}
