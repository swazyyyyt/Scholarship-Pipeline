using System.Globalization;
using System.Text;

namespace ScholarshipPipeline;

/// <summary>Pure formatting: result -> text. Printing happens in Program.cs.</summary>
public static class ResultFormatter
{
    public static string Format(string policyName, ScholarshipResult result)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine($"=== Policy: {policyName} ===");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Eligible: {result.EligibleCount} | Awarded: {result.AwardedCount} | Average score: {result.AverageScore:0.00}"));

        if (result.Ranking.IsEmpty)
        {
            sb.AppendLine("(no eligible applications)");
            return sb.ToString();
        }

        sb.AppendLine("Rank  Id    Name        Score  Awarded");
        foreach (var r in result.Ranking)
            sb.AppendLine($"{r.Rank,-5} {r.Id,-5} {r.Name,-11} {r.Score,-6} {(r.Awarded ? "yes" : "no")}");
        return sb.ToString();
    }

    public static string FormatRejections(IEnumerable<Rejection> rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        var sb = new StringBuilder();
        foreach (var r in rejected)
            sb.AppendLine($"NOT ELIGIBLE: {r.Id} - {string.Join("; ", r.Reasons)}");
        return sb.ToString();
    }
}
