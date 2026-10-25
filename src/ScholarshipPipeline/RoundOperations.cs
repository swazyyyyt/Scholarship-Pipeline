using System.Globalization;

namespace ScholarshipPipeline;

/// <summary>
/// Result-returning versions of the Assignment 6 safe transitions, so they can be chained with Map/Bind.
/// The A6 pure transitions (AwardRound.Rank / Finalize) stay the single source of the rules; this layer only
/// converts their outcome: Accepted -> Success(next state), Refused -> Failure(Domain error with the same code).
/// </summary>
public static class RoundOperations
{
    public static Result<RoundState, AppError> ToResult(TransitionOutcome outcome) =>
        outcome switch
        {
            TransitionOutcome.Accepted a => Result<RoundState, AppError>.Ok(a.Next),
            TransitionOutcome.Refused r => Result<RoundState, AppError>.Fail(
                new AppError(ErrorKind.Domain, r.Code, r.Reason)),
            _ => Result<RoundState, AppError>.Fail(
                new AppError(ErrorKind.Domain, ErrorCodes.UnknownState, "Unknown transition outcome."))
        };

    public static Result<RoundState, AppError> Rank(RoundState state, Func<ScholarshipApplication, int> policy, int awardSlots) =>
        ToResult(AwardRound.Rank(state, policy, awardSlots));

    public static Result<RoundState, AppError> Finalize(RoundState state) =>
        ToResult(AwardRound.Finalize(state));

    /// <summary>Pure extraction of the ranking from a ranked or finalized state; a Collected round has no ranking yet.</summary>
    public static Result<ScholarshipResult, AppError> ResultOf(RoundState state) =>
        state switch
        {
            RoundState.Ranked r => Result<ScholarshipResult, AppError>.Ok(r.Result),
            RoundState.Finalized f => Result<ScholarshipResult, AppError>.Ok(f.Result),
            RoundState.Collected => Result<ScholarshipResult, AppError>.Fail(
                new AppError(ErrorKind.Domain, ErrorCodes.NotRankedResult, "The round has no ranking yet: rank it first.")),
            _ => Result<ScholarshipResult, AppError>.Fail(
                new AppError(ErrorKind.Domain, ErrorCodes.UnknownState, "Unknown round state."))
        };

    /// <summary>
    /// Settings check as a Result: an invalid min GPA is an input problem of the caller, reported as a domain failure.
    /// (The pure workflow below it still throws ArgumentOutOfRange for programmer errors; this layer prevents reaching it.)
    /// </summary>
    public static Result<PipelineSettings, AppError> ValidateSettings(PipelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MinGpa < 0m || settings.MinGpa > 4m)
            return Result<PipelineSettings, AppError>.Fail(new AppError(ErrorKind.Domain, ErrorCodes.InvalidSettings,
                string.Create(CultureInfo.InvariantCulture, $"Minimum GPA {settings.MinGpa} must be between 0 and 4.")));
        if (settings.AwardSlots < 0)
            return Result<PipelineSettings, AppError>.Fail(new AppError(ErrorKind.Domain, ErrorCodes.InvalidAwardSlots,
                $"Award slots must not be negative (got {settings.AwardSlots})."));
        return Result<PipelineSettings, AppError>.Ok(settings);
    }
}
