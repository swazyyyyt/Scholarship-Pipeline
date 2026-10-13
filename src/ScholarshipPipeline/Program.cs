using ScholarshipPipeline;

// Imperative shell: reading files, choosing the policy and printing happen here, outside the pure core.
// Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both]");
    return 2;
}

var path = args[0];
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "both";

if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 2;
}

var policies = SelectPolicies(mode);
if (policies is null)
{
    Console.Error.WriteLine($"Unknown policy '{mode}'. Use need, merit or both.");
    return 2;
}

var report = ApplicationCsvParser.Parse(File.ReadAllText(path));
foreach (var error in report.Errors)
    Console.WriteLine($"REJECTED: {error}");
if (!report.HasErrors)
    Console.WriteLine($"Parsed {report.Valid.Length} valid application(s).");
else
    Console.WriteLine($"Parsed {report.Valid.Length} valid application(s), {report.Errors.Length} rejected.");
Console.WriteLine();

var settings = new PipelineSettings(MinGpa: 2.5m, AwardSlots: 3);

foreach (var (name, policy) in policies)
{
    var result = ScholarshipWorkflow.Run(report.Valid, settings, policy);
    Console.WriteLine(ResultFormatter.Format(name, result));
}

return report.HasErrors ? 1 : 0;

static IReadOnlyList<(string Name, Func<ScholarshipApplication, int> Policy)>? SelectPolicies(string mode) =>
    mode switch
    {
        "need" => new[] { ("need-based", (Func<ScholarshipApplication, int>)BonusPolicies.NeedBased) },
        "merit" => new[] { ("merit-based", (Func<ScholarshipApplication, int>)BonusPolicies.MeritBased) },
        "both" => new[]
        {
            ("need-based", (Func<ScholarshipApplication, int>)BonusPolicies.NeedBased),
            ("merit-based", (Func<ScholarshipApplication, int>)BonusPolicies.MeritBased)
        },
        _ => null
    };
