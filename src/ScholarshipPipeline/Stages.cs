using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>The three pure transformation stages. No I/O, no clock, no random, no global state.</summary>
public static class Stages
{
    public const int PointsPerGpaUnit = 20;
    public const int VolunteerHoursPerPoint = 10;
    public const int MaxVolunteerPoints = 10;

    /// <summary>
    /// Stage 1: IReadOnlyList&lt;ScholarshipApplication&gt; -> ImmutableArray&lt;ScholarshipApplication&gt;.
    /// Keeps applications with Gpa &gt;= minGpa (inclusive) and no disciplinary record.
    /// </summary>
    public static ImmutableArray<ScholarshipApplication> SelectEligible(
        IReadOnlyList<ScholarshipApplication> applications, decimal minGpa)
    {
        ArgumentNullException.ThrowIfNull(applications);
        return applications
            .Where(a => a.Gpa >= minGpa && !a.HasDisciplinaryRecord)
            .ToImmutableArray();
    }

    /// <summary>
    /// Stage 2: IReadOnlyList&lt;ScholarshipApplication&gt; + Func -> ImmutableArray&lt;ScoredApplication&gt;.
    /// Higher-order function: calls bonusPolicy once per application.
    /// Score = round(Gpa * 20) + min(VolunteerHours / 10, 10) + bonusPolicy(application).
    /// </summary>
    public static ImmutableArray<ScoredApplication> ScoreApplications(
        IReadOnlyList<ScholarshipApplication> eligible,
        Func<ScholarshipApplication, int> bonusPolicy)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(bonusPolicy);
        return eligible
            .Select(a => new ScoredApplication(a, BaseScore(a) + VolunteerPoints(a) + bonusPolicy(a)))
            .ToImmutableArray();
    }

    /// <summary>
    /// Stage 3: IReadOnlyList&lt;ScoredApplication&gt; + slots -> ScholarshipResult.
    /// Ranks by score (desc), then GPA (desc), then Id (ordinal); the first awardSlots are awarded.
    /// </summary>
    public static ScholarshipResult BuildResult(IReadOnlyList<ScoredApplication> scored, int awardSlots)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentOutOfRangeException.ThrowIfNegative(awardSlots);

        var ranking = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Application.Gpa)
            .ThenBy(s => s.Application.Id, StringComparer.Ordinal)
            .Select((s, index) => new Recommendation(
                index + 1, s.Application.Id, s.Application.Name, s.Score, index < awardSlots))
            .ToImmutableArray();

        var average = scored.Count == 0
            ? 0m
            : Math.Round((decimal)scored.Sum(s => s.Score) / scored.Count, 2, MidpointRounding.AwayFromZero);

        return new ScholarshipResult(ranking, ranking.Length, ranking.Count(r => r.Awarded), average);
    }

    public static int BaseScore(ScholarshipApplication a) =>
        (int)Math.Round(a.Gpa * PointsPerGpaUnit, MidpointRounding.AwayFromZero);

    public static int VolunteerPoints(ScholarshipApplication a) =>
        Math.Min(a.VolunteerHours / VolunteerHoursPerPoint, MaxVolunteerPoints);
}
