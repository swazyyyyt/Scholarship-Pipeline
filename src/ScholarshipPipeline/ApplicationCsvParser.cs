using System.Collections.Immutable;
using System.Globalization;

namespace ScholarshipPipeline;

/// <summary>
/// Boundary code: turns raw CSV text into validated domain records.
/// Invalid rows are reported with a clear message and are never replaced by defaults.
/// Limitation (documented): fields may not contain commas or quotes.
/// </summary>
public static class ApplicationCsvParser
{
    private static readonly string[] ExpectedHeader =
        { "id", "name", "gpa", "monthlyIncome", "volunteerHours", "hasDisciplinaryRecord" };

    /// <param name="csvText">Raw CSV text. null = input is missing (throws).
    /// A header-only text is a valid empty dataset.</param>
    public static ParseReport Parse(string? csvText)
    {
        ArgumentNullException.ThrowIfNull(csvText);

        var lines = csvText
            .Split('\n')
            .Select((text, index) => (Number: index + 1, Text: text.TrimEnd('\r')))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToList();

        if (lines.Count == 0)
            return Failure("Input is empty: the header row is missing.");

        var header = lines[0].Text.Split(',').Select(h => h.Trim());
        if (!header.SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase))
            return Failure($"Line {lines[0].Number}: unexpected header. Expected: {string.Join(",", ExpectedHeader)}");

        var valid = new List<ScholarshipApplication>();
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (number, text) in lines.Skip(1))
        {
            var (application, error) = ParseRow(number, text);
            if (error is not null)
                errors.Add(error);
            else if (!seenIds.Add(application!.Id))
                errors.Add($"Line {number}: duplicate id '{application.Id}'.");
            else
                valid.Add(application);
        }

        return new ParseReport(valid.ToImmutableArray(), errors.ToImmutableArray());
    }

    private static ParseReport Failure(string message) =>
        new(ImmutableArray<ScholarshipApplication>.Empty, ImmutableArray.Create(message));

    private static (ScholarshipApplication? Application, string? Error) ParseRow(int line, string text)
    {
        var f = text.Split(',').Select(x => x.Trim()).ToArray();
        if (f.Length != ExpectedHeader.Length)
            return (null, $"Line {line}: expected {ExpectedHeader.Length} fields but found {f.Length}.");

        if (f[0].Length == 0) return (null, Problem(line, "id", f[0], "a non-empty text"));
        if (f[1].Length == 0) return (null, Problem(line, "name", f[1], "a non-empty text"));

        if (f[2].Length == 0 || !decimal.TryParse(f[2], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var gpa))
            return (null, Problem(line, "gpa", f[2], "a number"));
        if (gpa < 0m || gpa > 4m)
            return (null, $"Line {line}: gpa {f[2]} must be between 0.0 and 4.0.");

        if (f[3].Length == 0 || !decimal.TryParse(f[3], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var income))
            return (null, Problem(line, "monthlyIncome", f[3], "a number"));
        if (income < 0m)
            return (null, $"Line {line}: monthlyIncome {f[3]} must not be negative.");

        if (f[4].Length == 0 || !int.TryParse(f[4], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var hours))
            return (null, Problem(line, "volunteerHours", f[4], "a whole number"));
        if (hours < 0)
            return (null, $"Line {line}: volunteerHours {f[4]} must not be negative.");

        if (!bool.TryParse(f[5], out var disciplinary))
            return (null, Problem(line, "hasDisciplinaryRecord", f[5], "'true' or 'false'"));

        return (new ScholarshipApplication(f[0], f[1], gpa, income, hours, disciplinary), null);
    }

    private static string Problem(int line, string field, string value, string expectation) =>
        value.Length == 0
            ? $"Line {line}: {field} is missing."
            : $"Line {line}: {field} '{value}' is not {expectation}.";
}
