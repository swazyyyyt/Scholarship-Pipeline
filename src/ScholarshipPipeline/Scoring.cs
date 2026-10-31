namespace ScholarshipPipeline;

/// <summary>Weights of the configurable score. One immutable value = one scoring configuration.</summary>
/// <param name="GpaPointsPerUnit">Points per GPA unit: round(Gpa * GpaPointsPerUnit).</param>
/// <param name="HoursPerPoint">Volunteer hours needed for one point (must be &gt; 0).</param>
/// <param name="MaxVolunteerPoints">Cap of the volunteer points (must be &gt;= 0).</param>
/// <param name="Bonus">Bonus policy (need-based or merit-based) added on top.</param>
public sealed record ScoreWeights(
    int GpaPointsPerUnit,
    int HoursPerPoint,
    int MaxVolunteerPoints,
    Func<ScholarshipApplication, int> Bonus);

/// <summary>
/// Assignment 8, Task 3: currying and partial application.
///
///   Compute      : (ScoreWeights, ScholarshipApplication) -> int          ordinary two-argument function
///   CreateScore  : ScoreWeights -> (ScholarshipApplication -> int)         CURRIED: arguments arrive in successive calls
///   Academic     = CreateScore(academicWeights)                            PARTIAL APPLICATION: weights fixed, application missing
///   Service      = CreateScore(serviceWeights)
///
/// The configured function is passed into the workflow as a value (Func); no mutable global setting is switched.
/// </summary>
public static class Scoring
{
    /// <summary>Pure score: round(Gpa * w.GpaPointsPerUnit) + min(hours / w.HoursPerPoint, w.MaxVolunteerPoints) + w.Bonus(a).</summary>
    public static int Compute(ScoreWeights w, ScholarshipApplication a) =>
        (int)Math.Round(a.Gpa * w.GpaPointsPerUnit, MidpointRounding.AwayFromZero)
        + Math.Min(a.VolunteerHours / w.HoursPerPoint, w.MaxVolunteerPoints)
        + w.Bonus(a);

    /// <summary>
    /// CreateScore : ScoreWeights -> (ScholarshipApplication -> int).
    /// The first call fixes (and validates) the configuration, the returned function accepts the remaining input.
    /// </summary>
    public static readonly Func<ScoreWeights, Func<ScholarshipApplication, int>> CreateScore =
        weights =>
        {
            ArgumentNullException.ThrowIfNull(weights);
            ArgumentNullException.ThrowIfNull(weights.Bonus);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weights.HoursPerPoint);
            ArgumentOutOfRangeException.ThrowIfNegative(weights.GpaPointsPerUnit);
            ArgumentOutOfRangeException.ThrowIfNegative(weights.MaxVolunteerPoints);
            return application => Compute(weights, application);
        };

    /// <summary>Partial application 1: grades matter most, volunteering is capped at 5 points, merit bonus.</summary>
    public static readonly Func<ScholarshipApplication, int> AcademicFocus =
        CreateScore(new ScoreWeights(GpaPointsPerUnit: 25, HoursPerPoint: 20, MaxVolunteerPoints: 5, Bonus: BonusPolicies.MeritBased));

    /// <summary>Partial application 2: community service matters (1 point per 5 hours, cap 20), need-based bonus.</summary>
    public static readonly Func<ScholarshipApplication, int> ServiceFocus =
        CreateScore(new ScoreWeights(GpaPointsPerUnit: 15, HoursPerPoint: 5, MaxVolunteerPoints: 20, Bonus: BonusPolicies.NeedBased));

    /// <summary>Lookup by the name used on the command line. Absence is an Option, never null.</summary>
    public static Option<Func<ScholarshipApplication, int>> ByName(string name) =>
        name?.ToLowerInvariant() switch
        {
            "academic" => Option<Func<ScholarshipApplication, int>>.Of(AcademicFocus),
            "service" => Option<Func<ScholarshipApplication, int>>.Of(ServiceFocus),
            _ => Option<Func<ScholarshipApplication, int>>.Empty()
        };
}
