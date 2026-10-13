using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>A validated scholarship application (domain data, never raw text).</summary>
public sealed record ScholarshipApplication(
    string Id,
    string Name,
    decimal Gpa,
    decimal MonthlyFamilyIncome,
    int VolunteerHours,
    bool HasDisciplinaryRecord);

/// <summary>Output of stage 2: an application together with its computed score.</summary>
public sealed record ScoredApplication(ScholarshipApplication Application, int Score);

/// <summary>One row of the final ranking.</summary>
public sealed record Recommendation(int Rank, string Id, string Name, int Score, bool Awarded);

/// <summary>Output of stage 3: ranking plus a small summary.</summary>
public sealed record ScholarshipResult(
    ImmutableArray<Recommendation> Ranking,
    int EligibleCount,
    int AwardedCount,
    decimal AverageScore)
{
    public static ScholarshipResult Empty { get; } =
        new(ImmutableArray<Recommendation>.Empty, 0, 0, 0m);
}

/// <summary>Stable configuration passed explicitly into the pipeline.</summary>
public sealed record PipelineSettings(decimal MinGpa, int AwardSlots);

/// <summary>Result of parsing at the application boundary.</summary>
public sealed record ParseReport(
    ImmutableArray<ScholarshipApplication> Valid,
    ImmutableArray<string> Errors)
{
    public bool HasErrors => !Errors.IsEmpty;
}
