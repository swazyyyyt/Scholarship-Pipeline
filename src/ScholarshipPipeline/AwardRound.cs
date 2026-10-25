using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>
/// Explicit states of one scholarship round (closed set, each case carries only its own data):
/// Collected = applications screened, nothing ranked yet;
/// Ranked    = ranking computed, can still be finalized;
/// Finalized = awards are final.
/// </summary>
public abstract record RoundState
{
    private RoundState() { }

    public sealed record Collected(ImmutableArray<EligibleApplication> Eligible, ImmutableArray<Rejection> Rejected) : RoundState;

    public sealed record Ranked(ScholarshipResult Result, ImmutableArray<Rejection> Rejected) : RoundState;

    public sealed record Finalized(ScholarshipResult Result, ImmutableArray<Rejection> Rejected) : RoundState;
}

/// <summary>Result of a transition: either the next state or a clear refusal. Never an invalid "success".</summary>
public abstract record TransitionOutcome
{
    private TransitionOutcome() { }

    public sealed record Accepted(RoundState Next) : TransitionOutcome;

    /// <summary>Refusal with a readable reason and a stable code (the code lets the Result layer build a structured error).</summary>
    public sealed record Refused(string Reason, string Code = ErrorCodes.UnknownState) : TransitionOutcome;
}

/// <summary>
/// Pure transitions. The required order (open -> rank -> finalize) is visible in the types:
/// every transition receives the current state and either returns the next state or refuses.
/// Nothing is mutated; a refused call leaves the given state exactly as it was.
/// Repeat rules: Rank on an already ranked/finalized round is REFUSED; Finalize on a finalized round returns
/// the SAME state again (idempotent, documented safe rule).
/// </summary>
public static class AwardRound
{
    /// <summary>Screens applications through the validated factory and returns the first state.</summary>
    public static RoundState Open(IReadOnlyList<ScholarshipApplication> applications, decimal minGpa)
    {
        var screening = Stages.Screen(applications, minGpa);
        return new RoundState.Collected(screening.Eligible, screening.Rejected);
    }

    public static TransitionOutcome Rank(
        RoundState state, Func<ScholarshipApplication, int> bonusPolicy, int awardSlots)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bonusPolicy);

        if (awardSlots < 0)
            return new TransitionOutcome.Refused($"Award slots must not be negative (got {awardSlots}).", ErrorCodes.InvalidAwardSlots);

        return state switch
        {
            RoundState.Collected c => new TransitionOutcome.Accepted(
                new RoundState.Ranked(
                    Stages.BuildResult(Stages.ScoreApplications(c.Eligible, bonusPolicy), awardSlots),
                    c.Rejected)),
            RoundState.Ranked => new TransitionOutcome.Refused("The round is already ranked; finalize it.", ErrorCodes.AlreadyRanked),
            RoundState.Finalized => new TransitionOutcome.Refused("The round is already finalized; ranking is closed.", ErrorCodes.RoundFinalized),
            _ => new TransitionOutcome.Refused("Unknown round state.")
        };
    }

    public static TransitionOutcome Finalize(RoundState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state switch
        {
            RoundState.Collected => new TransitionOutcome.Refused("The round must be ranked before it can be finalized.", ErrorCodes.RoundNotRanked),
            RoundState.Ranked r => new TransitionOutcome.Accepted(new RoundState.Finalized(r.Result, r.Rejected)),
            RoundState.Finalized f => new TransitionOutcome.Accepted(f),   // repeated call: same outcome, no change
            _ => new TransitionOutcome.Refused("Unknown round state.")
        };
    }

    public static ImmutableArray<Rejection> RejectionsOf(RoundState state) =>
        state switch
        {
            RoundState.Collected c => c.Rejected,
            RoundState.Ranked r => r.Rejected,
            RoundState.Finalized f => f.Rejected,
            _ => ImmutableArray<Rejection>.Empty
        };
}

/// <summary>
/// Boundary holder of the current state (the only mutable thing). The new state is stored only when the
/// transition is Accepted; a refusal or an exception leaves the stored state untouched.
/// </summary>
public sealed class RoundStore
{
    public RoundState Current { get; private set; }

    public RoundStore(RoundState initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        Current = initial;
    }

    public TransitionOutcome Apply(Func<RoundState, TransitionOutcome> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var outcome = transition(Current);          // computed first; Current is not touched yet
        switch (outcome)
        {
            case TransitionOutcome.Accepted a: Current = a.Next; break;
            case TransitionOutcome.Refused: break;
            default: throw new InvalidOperationException("Unknown transition outcome.");
        }
        return outcome;
    }
}
