using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>Output of the pure report stage: the snapshot is materialised once and both reports are derived from it.</summary>
public sealed record ReportBundle(ImmutableArray<ShortlistRow> Shortlist, ImmutableArray<IncomeBandRow> Bands);

/// <summary>
/// Assignment 8 integration. The chain keeps the Result style of Assignment 7:
///   Load (file boundary) -> Ensure (not empty) -> Bind BuildReports (PURE) -> Bind AddStipends (native boundary)
///   -> Bind Export (file boundary).
/// A failure at any step skips the rest and nothing is exported, so the outside world stays consistent.
/// </summary>
public static class PayoutWorkflow
{
    /// <summary>
    /// PURE: validates settings (Result), then deferred scoring -> ONE explicit materialisation (the snapshot) ->
    /// both LINQ reports from the snapshot. No console, no file, no native code.
    /// </summary>
    public static Result<ReportBundle, AppError> BuildReports(
        IReadOnlyList<ScholarshipApplication> applications,
        PipelineSettings settings,
        int minScore,
        Func<ScholarshipApplication, int> score)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(score);

        return RoundOperations.ValidateSettings(settings).Map(valid =>
        {
            // Materialisation point: the lazy query runs here, exactly once; both reports reuse the stable snapshot.
            var snapshot = LazyScoring.ScoreEligible(applications, valid.MinGpa, score).ToImmutableArray();
            return new ReportBundle(
                Reports.Shortlist(snapshot, new ShortlistOptions(minScore, valid.AwardSlots)),
                Reports.IncomeBands(snapshot));
        });
    }

    /// <summary>Native boundary: one stipend per awarded row; the first failure stops the remaining native calls.</summary>
    public static Result<ImmutableArray<PayoutLine>, AppError> AddStipends(
        ImmutableArray<ShortlistRow> shortlist, IStipendCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        return shortlist
            .Where(row => row.Awarded)
            .Traverse(row => calculator.Calculate(row.Score, row.MonthlyIncome)
                .Map(stipend => new PayoutLine(row, stipend)));
    }

    public static Result<PayoutReport, AppError> Run(
        string csvPath,
        string exportPath,
        PipelineSettings settings,
        int minScore,
        Func<ScholarshipApplication, int> score,
        IStipendCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(calculator);

        return ApplicationLoader.Load(csvPath)
            .Ensure(apps => !apps.IsEmpty, _ => new AppError(ErrorKind.Domain, ErrorCodes.NoApplications,
                $"Data file '{csvPath}' contains no applications, so there is nothing to report."))
            .Bind(apps => BuildReports(apps, settings, minScore, score))
            .Bind(bundle => AddStipends(bundle.Shortlist, calculator)
                .Map(payouts => new PayoutReport(
                    bundle.Shortlist, bundle.Bands, payouts, payouts.Sum(p => p.Stipend), ExportPath: "")))
            .Bind(report => ReportExporter.ExportPayouts(exportPath, report));
    }
}
