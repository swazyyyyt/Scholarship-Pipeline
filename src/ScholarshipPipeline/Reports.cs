using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace ScholarshipPipeline;

/// <summary>Report 1 parameters: only applications with Score &gt;= MinScore are shortlisted; the first AwardSlots are awarded.</summary>
public sealed record ShortlistOptions(int MinScore, int AwardSlots);

/// <summary>Report 1 output row.</summary>
public sealed record ShortlistRow(int Rank, string Id, string Name, int Score, decimal Gpa, decimal MonthlyIncome, bool Awarded);

/// <summary>Income band used as the grouping key of report 2 (Order makes the output order explicit).</summary>
public readonly record struct IncomeBand(int Order, string Name);

/// <summary>Report 2 output row: one row per income band that has at least one eligible applicant.</summary>
public sealed record IncomeBandRow(string Band, int Applicants, int TotalScore, decimal AverageScore, int TotalVolunteerHours);

/// <summary>
/// Assignment 8, Task 2: two pure LINQ reports over the materialised snapshot of scored applications.
/// No console, no files, no native code here; the input is a snapshot, so the reports can be run any number of times.
///
/// REPORT 1 - Award shortlist (user: scholarship committee chair, decides who gets the awards)
///   input   : scored eligible applications (snapshot) + ShortlistOptions
///   stages  : Where (score &gt;= MinScore) -> OrderForRanking (score desc, GPA desc, Id asc) -> Select (rank, awarded flag)
///   output  : ShortlistRow per shortlisted application, best first
/// REPORT 2 - Income band summary (user: financial office, plans the budget per social group)
///   input   : the same snapshot
///   stages  : GroupBy (income band) -> Select (Count, Sum, Aggregate, average) -> OrderBy (band order) ThenBy (band name)
///   output  : IncomeBandRow per non-empty band
/// </summary>
public static class Reports
{
    public const decimal LowIncomeLimit = 500m;
    public const decimal MiddleIncomeLimit = 1000m;

    public static ImmutableArray<ShortlistRow> Shortlist(IReadOnlyList<ScoredApplication> scored, ShortlistOptions options)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AwardSlots);

        return scored
            .Where(s => s.Score >= options.MinScore)
            .OrderForRanking()
            .Select((s, index) => new ShortlistRow(
                index + 1, s.Candidate.Id, s.Candidate.Name, s.Score, s.Candidate.Gpa,
                s.Candidate.Application.MonthlyFamilyIncome, index < options.AwardSlots))
            .ToImmutableArray();
    }

    public static ImmutableArray<IncomeBandRow> IncomeBands(IReadOnlyList<ScoredApplication> scored)
    {
        ArgumentNullException.ThrowIfNull(scored);

        return scored
            .GroupBy(s => BandOf(s.Candidate.Application.MonthlyFamilyIncome))
            .Select(group =>
            {
                var applicants = group.Count();
                var totalScore = group.Sum(s => s.Score);
                return new
                {
                    group.Key,
                    Row = new IncomeBandRow(
                        group.Key.Name,
                        applicants,
                        totalScore,
                        Math.Round((decimal)totalScore / applicants, 2, MidpointRounding.AwayFromZero),
                        group.Aggregate(0, (hours, s) => hours + s.Candidate.Application.VolunteerHours))
                };
            })
            .OrderBy(x => x.Key.Order)
            .ThenBy(x => x.Key.Name, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToImmutableArray();
    }

    /// <summary>low: income &lt;= 500, middle: &lt;= 1000, high: above (same limits as the need-based bonus).</summary>
    public static IncomeBand BandOf(decimal monthlyIncome) =>
        monthlyIncome switch
        {
            <= LowIncomeLimit => new IncomeBand(1, "low"),
            <= MiddleIncomeLimit => new IncomeBand(2, "middle"),
            _ => new IncomeBand(3, "high")
        };
}

/// <summary>Pure text rendering of the reports (printing and writing files happens in Program / ReportExporter).</summary>
public static class ReportFormatter
{
    public static string FormatShortlist(string title, ImmutableArray<ShortlistRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- Report 1: award shortlist ({title}) ---");
        if (rows.IsEmpty)
        {
            sb.AppendLine("(no application reaches the minimum score)");
            return sb.ToString();
        }
        sb.AppendLine("Rank  Id    Name        Score  Gpa   Awarded");
        foreach (var r in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Rank,-5} {r.Id,-5} {r.Name,-11} {r.Score,-6} {r.Gpa,-5:0.0} {(r.Awarded ? "yes" : "no")}"));
        return sb.ToString();
    }

    public static string FormatIncomeBands(ImmutableArray<IncomeBandRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Report 2: income band summary ---");
        if (rows.IsEmpty)
        {
            sb.AppendLine("(no eligible applications)");
            return sb.ToString();
        }
        sb.AppendLine("Band    Applicants  TotalScore  AvgScore  VolunteerHours");
        foreach (var r in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Band,-7} {r.Applicants,-11} {r.TotalScore,-11} {r.AverageScore,-9:0.00} {r.TotalVolunteerHours}"));
        return sb.ToString();
    }
}
