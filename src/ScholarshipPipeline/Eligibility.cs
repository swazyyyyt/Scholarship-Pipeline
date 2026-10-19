using System.Collections.Immutable;
using System.Globalization;

namespace ScholarshipPipeline;

/// <summary>
/// Validated value: an application that has PASSED every domain rule (see <see cref="Create"/>).
/// The constructor is private, the class is sealed and the only property is get-only, so code outside this
/// class cannot build one without the checks and cannot change one afterwards (it is not a record, so there is no
/// public copy-with either). Scoring and ranking accept this type, so they cannot be called with an unchecked application.
/// </summary>
public sealed class EligibleApplication
{
    public ScholarshipApplication Application { get; }

    public string Id => Application.Id;
    public string Name => Application.Name;
    public decimal Gpa => Application.Gpa;

    private EligibleApplication(ScholarshipApplication application) => Application = application;

    /// <summary>
    /// Factory. Returns <see cref="EligibilityOutcome.Eligible"/> (carrying the validated value) or
    /// <see cref="EligibilityOutcome.Ineligible"/> (carrying EVERY reason found). No partial value escapes.
    /// Rules: id/name not blank, gpa in [0,4], income &gt;= 0, hours &gt;= 0 (the record has a public constructor,
    /// so these are re-checked), gpa &gt;= minGpa (inclusive) and no disciplinary record.
    /// </summary>
    public static EligibilityOutcome Create(ScholarshipApplication application, decimal minGpa)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (minGpa < 0m || minGpa > 4m)
            throw new ArgumentOutOfRangeException(nameof(minGpa), "minGpa must be between 0 and 4.");

        var reasons = ImmutableArray.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(application.Id)) reasons.Add("id is missing");
        if (string.IsNullOrWhiteSpace(application.Name)) reasons.Add("name is missing");
        if (application.Gpa < 0m || application.Gpa > 4m)
            reasons.Add(Text($"gpa {application.Gpa} must be between 0.0 and 4.0"));
        else if (application.Gpa < minGpa)
            reasons.Add(Text($"gpa {application.Gpa} is below the minimum {minGpa}"));
        if (application.MonthlyFamilyIncome < 0m) reasons.Add("monthlyIncome must not be negative");
        if (application.VolunteerHours < 0) reasons.Add("volunteerHours must not be negative");
        if (application.HasDisciplinaryRecord) reasons.Add("has a disciplinary record");

        return reasons.Count == 0
            ? new EligibilityOutcome.Eligible(new EligibleApplication(application))
            : new EligibilityOutcome.Ineligible(reasons.ToImmutable());
    }

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Closed set of cases (a discriminated union): exactly <see cref="Eligible"/> or <see cref="Ineligible"/>.
/// The base constructor is private, so nobody outside can add a third case.
/// Eligible = the validated value is available. Ineligible = only the reasons are available.
/// </summary>
public abstract record EligibilityOutcome
{
    private EligibilityOutcome() { }

    public sealed record Eligible(EligibleApplication Value) : EligibilityOutcome;

    public sealed record Ineligible(ImmutableArray<string> Reasons) : EligibilityOutcome;
}

/// <summary>An application that was screened out, with all reasons.</summary>
public sealed record Rejection(string Id, ImmutableArray<string> Reasons);

/// <summary>Result of the screening stage: who passed (validated values) and who did not (reasons).</summary>
public sealed record ScreeningResult(
    ImmutableArray<EligibleApplication> Eligible,
    ImmutableArray<Rejection> Rejected);
