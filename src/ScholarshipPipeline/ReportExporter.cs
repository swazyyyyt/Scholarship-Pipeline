using System.Collections.Immutable;
using System.Globalization;

namespace ScholarshipPipeline;

/// <summary>One awarded applicant with the stipend computed by the native library.</summary>
public sealed record PayoutLine(ShortlistRow Row, int Stipend);

/// <summary>Everything the integrated workflow produced: both reports, the payouts and the exported file.</summary>
public sealed record PayoutReport(
    ImmutableArray<ShortlistRow> Shortlist,
    ImmutableArray<IncomeBandRow> Bands,
    ImmutableArray<PayoutLine> Payouts,
    int TotalStipend,
    string ExportPath);

/// <summary>
/// File boundary of the export (never used inside the pure reports).
/// OWNER of the writer: <see cref="WriteTo"/> (it opens it through the factory, so tests can observe disposal).
/// RELEASE POINT: the <c>using</c> in WriteTo - on success, on an exception while writing, and before the final move.
/// ExportToFile writes to "&lt;path&gt;.tmp" and moves it over the target only after the writer is closed,
/// so a failure never leaves a half-written target file; the temporary file is removed on failure.
/// </summary>
public static class ReportExporter
{
    public const string CsvHeader = "rank,id,name,score,monthlyIncome,stipend";

    public static IEnumerable<string> PayoutCsvLines(IEnumerable<PayoutLine> payouts)
    {
        ArgumentNullException.ThrowIfNull(payouts);
        yield return CsvHeader;
        foreach (var p in payouts)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{p.Row.Rank},{p.Row.Id},{p.Row.Name},{p.Row.Score},{p.Row.MonthlyIncome},{p.Stipend}");
    }

    /// <summary>Writes all lines and always disposes the writer (success and failure path).</summary>
    public static void WriteTo(Func<TextWriter> openWriter, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(openWriter);
        ArgumentNullException.ThrowIfNull(lines);
        using var writer = openWriter();
        foreach (var line in lines)
            writer.WriteLine(line);
    }

    public static Result<PayoutReport, AppError> ExportPayouts(string path, PayoutReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(path))
            return Fail("No export path was given.");

        var temp = path + ".tmp";
        try
        {
            WriteTo(() => new StreamWriter(temp), PayoutCsvLines(report.Payouts));
            File.Move(temp, path, overwrite: true);
            return Result<PayoutReport, AppError>.Ok(report with { ExportPath = path });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return Fail($"Report could not be exported to '{path}': {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static Result<PayoutReport, AppError> Fail(string message) =>
        Result<PayoutReport, AppError>.Fail(new AppError(ErrorKind.Operational, ErrorCodes.ExportFailed, message));
}
