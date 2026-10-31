using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>The three pure transformation stages. No I/O, no clock, no random, no global state.</summary>
public static class Stages
{
    public const int PointsPerGpaUnit = 20;
    public const int VolunteerHoursPerPoint = 10;
    public const int MaxVolunteerPoints = 10;

    /// <summary>
    /// Stage 1: IReadOnlyList&lt;ScholarshipApplication&gt; -> ScreeningResult.
    /// Every application goes through the factory EligibleApplication.Create; the outcome is matched explicitly.
    /// </summary>
    public static ScreeningResult Screen(IReadOnlyList<ScholarshipApplication> applications, decimal minGpa)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var eligible = ImmutableArray.CreateBuilder<EligibleApplication>();
        var rejected = ImmutableArray.CreateBuilder<Rejection>();

        foreach (var application in applications)
        {
            switch (EligibleApplication.Create(application, minGpa))
            {
                case EligibilityOutcome.Eligible e: eligible.Add(e.Value); break;
                case EligibilityOutcome.Ineligible i: rejected.Add(new Rejection(application.Id, i.Reasons)); break;
                default: throw new InvalidOperationException("Unknown eligibility outcome.");
            }
        }
        return new ScreeningResult(eligible.ToImmutable(), rejected.ToImmutable());
    }

    /// <summary>
    /// Stage 2: IReadOnlyList&lt;EligibleApplication&gt; + Func -> ImmutableArray&lt;ScoredApplication&gt;.
    /// Higher-order function: calls bonusPolicy once per application.
    /// Score = round(Gpa * 20) + min(VolunteerHours / 10, 10) + bonusPolicy(application).
    /// </summary>
    public static ImmutableArray<ScoredApplication> ScoreApplications(
        IReadOnlyList<EligibleApplication> eligible,
        Func<ScholarshipApplication, int> bonusPolicy)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(bonusPolicy);
        return eligible
            .Select(a => new ScoredApplication(a, BaseScore(a.Application) + VolunteerPoints(a.Application) + bonusPolicy(a.Application)))
            .ToImmutableArray();
    }

    /// <summary>
    /// A8 overload: the whole score is ONE configured function (see Scoring.CreateScore), calculated once per application.
    /// The A5 overload above is the same stage with the base + volunteer + bonus composition fixed.
    /// </summary>
    public static ImmutableArray<ScoredApplication> ScoreApplicationsWith(
        IReadOnlyList<EligibleApplication> eligible,
        Func<ScholarshipApplication, int> score)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(score);
        return eligible.Select(a => new ScoredApplication(a, score(a.Application))).ToImmutableArray();
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
            .OrderForRanking()   // A8 extension: the single ranking rule (score desc, GPA desc, Id asc)
            .Select((s, index) => new Recommendation(
                index + 1, s.Candidate.Id, s.Candidate.Name, s.Score, index < awardSlots))
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
