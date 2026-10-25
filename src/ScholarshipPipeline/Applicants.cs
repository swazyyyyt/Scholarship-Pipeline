using System.Collections.Immutable;
using System.Globalization;

namespace ScholarshipPipeline;

/// <summary>Pure lookups. Absence is an Option.None, never null/default and never a fabricated record.</summary>
public static class ApplicantLookup
{
    public static Option<ScholarshipApplication> Find(IReadOnlyList<ScholarshipApplication> applications, string id)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var match = applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        return match is null ? Option<ScholarshipApplication>.Empty() : Option<ScholarshipApplication>.Of(match);
    }

    /// <summary>Volunteer hours of an applicant. 0 hours is a LEGITIMATE value (Some(0)); an unknown id is None.</summary>
    public static Option<int> VolunteerHours(IReadOnlyList<ScholarshipApplication> applications, string id) =>
        Find(applications, id).Match(a => Option<int>.Of(a.VolunteerHours), Option<int>.Empty);

    public static Option<Recommendation> FindRecommendation(ScholarshipResult result, string id)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var r in result.Ranking)
            if (string.Equals(r.Id, id, StringComparison.Ordinal))
                return Option<Recommendation>.Of(r);
        return Option<Recommendation>.Empty();
    }
}

/// <summary>Display model of one applicant's outcome (built with Map from a successful Recommendation).</summary>
public sealed record ApplicantView(string Title, string Details);

/// <summary>
/// The application-level Result chains (Assignment 7). Pure: no file access, no exceptions for business failures.
/// Every step that can fail returns Result; Map is used for pure transformations, Bind for steps that may reject.
/// </summary>
public static class ScholarshipService
{
    /// <summary>
    /// Whole round as one chain:
    ///   ValidateSettings                       Result&lt;PipelineSettings&gt;
    ///   .Map  Open (screen via the factory)    pure: settings -> Collected state
    ///   .Bind Rank                             may refuse (invalid slots / wrong state)
    ///   .Bind Finalize                         may refuse (not ranked)
    /// A failure at any step skips all later steps.
    /// </summary>
    public static Result<RoundState, AppError> RunRound(
        IReadOnlyList<ScholarshipApplication> applications,
        PipelineSettings settings,
        Func<ScholarshipApplication, int> bonusPolicy)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bonusPolicy);

        return RoundOperations.ValidateSettings(settings)
            .Map(valid => AwardRound.Open(applications, valid.MinGpa))
            .Bind(collected => RoundOperations.Rank(collected, bonusPolicy, settings.AwardSlots))
            .Bind(RoundOperations.Finalize);
    }

    /// <summary>Round report: the chain above, then Bind (extract ranking) and Map (display text) at the end.</summary>
    public static Result<string, AppError> BuildReport(
        IReadOnlyList<ScholarshipApplication> applications,
        PipelineSettings settings,
        string policyName,
        Func<ScholarshipApplication, int> bonusPolicy) =>
        RunRound(applications, settings, bonusPolicy)
            .Bind(RoundOperations.ResultOf)
            .Map(result => ResultFormatter.Format(policyName, result));

    /// <summary>
    /// "What happened to applicant X?" - the quote-style chain of this project:
    ///   Find (Option) -> ToResult   required applicant missing        -> APPLICANT_NOT_FOUND (later steps skipped)
    ///   .Bind RequireEligible       validation by the A6 factory      -> NOT_ELIGIBLE with all reasons
    ///   .Bind RunRound              the full round (Rank, Finalize)   -> may fail with a state / input error
    ///   .Bind FindRecommendation    lookup in the ranking             -> Option -> Result
    ///   .Map  ToView                pure display model
    /// </summary>
    public static Result<ApplicantView, AppError> ExplainApplicant(
        IReadOnlyList<ScholarshipApplication> applications,
        string applicantId,
        PipelineSettings settings,
        Func<ScholarshipApplication, int> bonusPolicy)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bonusPolicy);

        return ApplicantLookup.Find(applications, applicantId)
            .ToResult(() => new AppError(ErrorKind.Domain, ErrorCodes.ApplicantNotFound,
                $"Applicant '{applicantId}' does not exist in the loaded applications."))
            .Bind(application => RequireEligible(application, settings.MinGpa))
            .Bind(eligible => RunRound(applications, settings, bonusPolicy)
                .Bind(RoundOperations.ResultOf)
                .Bind(result => ApplicantLookup.FindRecommendation(result, eligible.Id)
                    .ToResult(() => new AppError(ErrorKind.Domain, ErrorCodes.ApplicantNotFound,
                        $"Applicant '{eligible.Id}' is eligible but missing from the ranking."))))
            .Map(ToView);
    }

    /// <summary>
    /// Validation failure (rule-based) vs workflow state (what the round currently allows):
    /// NOT_ELIGIBLE says the DATA of the applicant breaks a rule; ROUND_NOT_RANKED / ALREADY_RANKED say the data is fine
    /// but the CURRENT STATE of the round does not permit the operation.
    /// </summary>
    public static Result<EligibleApplication, AppError> RequireEligible(ScholarshipApplication application, decimal minGpa)
    {
        if (minGpa < 0m || minGpa > 4m)
            return Result<EligibleApplication, AppError>.Fail(new AppError(ErrorKind.Domain, ErrorCodes.InvalidSettings,
                string.Create(CultureInfo.InvariantCulture, $"Minimum GPA {minGpa} must be between 0 and 4.")));

        return EligibleApplication.Create(application, minGpa) switch
        {
            EligibilityOutcome.Eligible e => Result<EligibleApplication, AppError>.Ok(e.Value),
            EligibilityOutcome.Ineligible i => Result<EligibleApplication, AppError>.Fail(new AppError(
                ErrorKind.Domain, ErrorCodes.NotEligible,
                $"Applicant '{application.Id}' is not eligible: {string.Join("; ", i.Reasons)}.")),
            _ => Result<EligibleApplication, AppError>.Fail(new AppError(
                ErrorKind.Domain, ErrorCodes.UnknownState, "Unknown eligibility outcome."))
        };
    }

    public static ApplicantView ToView(Recommendation r)
    {
        var inv = CultureInfo.InvariantCulture;
        return new ApplicantView(
            string.Create(inv, $"{r.Id} {r.Name}"),
            string.Create(inv, $"rank {r.Rank}, score {r.Score}, {(r.Awarded ? "AWARDED" : "not awarded")}"));
    }
}
