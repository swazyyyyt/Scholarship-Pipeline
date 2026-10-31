using System.Collections.Immutable;
using ScholarshipPipeline;

// Imperative shell: the only place that prints and chooses what to do with the FINAL outcome of a Result.
// Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo|a7|a8 [academic|service]|explain <id> [need|merit]]
// Exit codes: 0 = success, 1 = expected domain failure, 2 = operational problem (file) or wrong usage.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo|a7|a8 [academic|service]|explain <id> [need|merit]]");
    return 2;
}

var path = args[0];
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "both";
var settings = new PipelineSettings(MinGpa: 2.5m, AwardSlots: 3);

var policies = SelectPolicies(mode switch { "demo" or "a7" or "a8" => "need", "explain" => (args.Length > 3 ? args[3].ToLowerInvariant() : "need"), _ => mode });
if (policies is null)
{
    Console.Error.WriteLine($"Unknown mode or policy '{mode}'. Use need, merit, both, demo, a7 or explain <id> [need|merit].");
    return 2;
}

// ---- Boundary: load the data. A missing or malformed file is a Result.Failure, reported once, never an unhandled exception. ----
var store = new ApplicationStore();
var loaded = store.Reload(path);
if (loaded is Result<int, AppError>.Failure loadFailure)
{
    Console.WriteLine($"FAILED to load data: {loadFailure.Error}");
    return ExitCodeOf(loadFailure.Error);
}

if (mode == "a8")
{
    var configName = args.Length > 2 ? args[2].ToLowerInvariant() : "academic";
    return Scoring.ByName(configName).Match(
        score => RunA8Demo(path, configName, score, settings),
        () =>
        {
            Console.Error.WriteLine($"Unknown scoring configuration '{configName}'. Use academic or service.");
            return 2;
        });
}

if (mode == "a7")
{
    RunA7Demo(store, path, settings, policies[0].Policy);
    return 0;
}

var opened = AwardRound.Open(store.Current, settings.MinGpa);   // A6: screening through the validated factory

if (mode == "explain")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ... <csv-path> explain <applicant-id> [need|merit]");
        return 2;
    }
    return Report(ScholarshipService.ExplainApplicant(store.Current, args[2], settings, policies[0].Policy),
        v => $"{v.Title}: {v.Details}");
}

Console.WriteLine($"Parsed {store.Current.Length} valid application(s).");
Console.WriteLine();
Console.Write(ResultFormatter.FormatRejections(AwardRound.RejectionsOf(opened)));
Console.WriteLine();

if (mode == "demo")
{
    RunSequenceDemo(opened, policies[0].Policy, settings);
    return 0;
}

var exit = 0;
foreach (var (name, policy) in policies)
{
    // The A7 chain: validate settings -> Map(open) -> Bind(rank) -> Bind(finalize) -> Bind(extract) -> Map(format).
    var code = Report(ScholarshipService.BuildReport(store.Current, settings, name, policy), text => text);
    if (code != 0) exit = code;
}
return exit;

// ===================================================================================================
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

static int ExitCodeOf(AppError error) => error.Kind == ErrorKind.Operational ? 2 : 1;

// The single place where a final Result is handled: explicit case handler (Match).
static int Report<T>(Result<T, AppError> outcome, Func<T, string> render) =>
    outcome.Match(
        value => { Console.WriteLine(render(value)); return 0; },
        error => { Console.WriteLine($"REJECTED {error}"); return ExitCodeOf(error); });

static string Describe(TransitionOutcome outcome) =>
    outcome switch
    {
        TransitionOutcome.Accepted a => $"ACCEPTED -> {a.Next.GetType().Name}",
        TransitionOutcome.Refused r => $"REFUSED: {r.Reason}",
        _ => "REFUSED: unknown outcome"
    };

static string DescribeOption<T>(Option<T> option) => option.Match(v => $"Some({v})", () => "None");

static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

// Assignment 6 demonstration (unchanged behaviour): one invalid order, one invalid input, one repeated call.
static void RunSequenceDemo(RoundState opened, Func<ScholarshipApplication, int> policy, PipelineSettings settings)
{
    var store = new RoundStore(opened);
    Console.WriteLine("--- Sequence demo (need-based) ---");
    Console.WriteLine($"1. Finalize before Rank   : {Describe(store.Apply(AwardRound.Finalize))}");
    Console.WriteLine($"2. Rank with -1 slots     : {Describe(store.Apply(s => AwardRound.Rank(s, policy, -1)))}");
    Console.WriteLine($"   state still unchanged  : {ReferenceEquals(store.Current, opened)}");
    Console.WriteLine($"3. Rank (valid)           : {Describe(store.Apply(s => AwardRound.Rank(s, policy, settings.AwardSlots)))}");
    Console.WriteLine($"4. Rank again             : {Describe(store.Apply(s => AwardRound.Rank(s, policy, settings.AwardSlots)))}");
    Console.WriteLine($"5. Finalize               : {Describe(store.Apply(AwardRound.Finalize))}");
    var before = store.Current;
    Console.WriteLine($"6. Finalize again         : {Describe(store.Apply(AwardRound.Finalize))} (same state: {ReferenceEquals(store.Current, before)})");
}

// Assignment 7 demonstration: normal path, domain failures, present/missing lookups, file failures, state preserved.
static void RunA7Demo(ApplicationStore store, string csvPath, PipelineSettings settings, Func<ScholarshipApplication, int> policy)
{
    var apps = store.Current;
    var dir = Path.GetDirectoryName(csvPath) ?? "";   // keep the path as the user typed it, so messages are readable

    Section("1. Normal path: whole round (validate -> Map open -> Bind rank -> Bind finalize -> Map format)");
    Report(ScholarshipService.BuildReport(apps, settings, "need-based", policy), t => t.TrimEnd());

    Section("2. Normal path: applicant S003 (Find -> Bind eligible -> Bind round -> Bind lookup -> Map view)");
    Report(ScholarshipService.ExplainApplicant(apps, "S003", settings, policy), v => $"{v.Title}: {v.Details}");

    Section("3. Domain failure: invalid input (negative award slots)");
    Report(ScholarshipService.RunRound(apps, settings with { AwardSlots = -1 }, policy), _ => "unexpected success");

    Section("4. Domain failure: state does not permit (Finalize before Rank, then Rank twice)");
    var collected = AwardRound.Open(apps, settings.MinGpa);
    Report(RoundOperations.Finalize(collected), _ => "unexpected success");
    var ranked = RoundOperations.Rank(collected, policy, settings.AwardSlots);
    Report(ranked, s => $"Rank accepted -> {s.GetType().Name}");
    Report(ranked.Bind(s => RoundOperations.Rank(s, policy, settings.AwardSlots)), _ => "unexpected success");

    Section("5. Domain failure: validation (applicants S005 gpa too low, S006 disciplinary record)");
    Report(ScholarshipService.ExplainApplicant(apps, "S005", settings, policy), _ => "unexpected success");
    Report(ScholarshipService.ExplainApplicant(apps, "S006", settings, policy), _ => "unexpected success");

    Section("6. Lookup: present and missing (and a legitimate zero)");
    Console.WriteLine("Hours of S001 : " + DescribeOption(ApplicantLookup.VolunteerHours(apps, "S001")));
    Console.WriteLine("Hours of S004 : " + DescribeOption(ApplicantLookup.VolunteerHours(apps, "S004")) + "   (0 hours is real data, not absence)");
    Console.WriteLine("Hours of S999 : " + DescribeOption(ApplicantLookup.VolunteerHours(apps, "S999")));
    Report(ScholarshipService.ExplainApplicant(apps, "S999", settings, policy), _ => "unexpected success");

    var before = store.Current;

    Section("7. File failure: missing file (stored data preserved)");
    Console.WriteLine(store.Reload(Path.Combine(dir, "does-not-exist.csv")).Match(n => $"OK {n}", e => $"FAILED {e}"));
    Console.WriteLine($"Applications before: {before.Length}, after: {store.Current.Length} (unchanged: {before == store.Current})");

    Section("8. File failure: malformed content (stored data preserved)");
    Console.WriteLine(store.Reload(Path.Combine(dir, "invalid.csv")).Match(n => $"OK {n}", e => $"FAILED {e}"));
    Console.WriteLine($"Applications before: {before.Length}, after: {store.Current.Length} (unchanged: {before == store.Current})");

    Section("9. Pipeline and safe transitions still work after the failures");
    Report(ScholarshipService.ExplainApplicant(store.Current, "S001", settings, policy), v => $"{v.Title}: {v.Details}");
    RunSequenceDemo(AwardRound.Open(store.Current, settings.MinGpa), policy, settings);
}

// Assignment 8 demonstration: configured functions, deferred execution, two reports, native call, export, failure paths.
static int RunA8Demo(string csvPath, string configName, Func<ScholarshipApplication, int> score, PipelineSettings settings)
{
    const int minScore = 60;   // shortlist threshold of report 1
    var dir = Path.GetDirectoryName(csvPath) ?? "";
    var outputDir = "output";
    Directory.CreateDirectory(outputDir);   // file system work belongs to the shell, not to the pure code
    var exportPath = Path.Combine(outputDir, $"payouts-{configName}.csv");
    var apps = ApplicationLoader.Load(csvPath).Match(a => a, e => throw new InvalidOperationException(e.ToString()));

    Section("1. Currying and partial application: SAME dataset, two configured scoring functions");
    Console.WriteLine("Id    Academic  Service   (CreateScore(weights) -> function; weights fixed, application still missing)");
    foreach (var e in Stages.Screen(apps, settings.MinGpa).Eligible)
        Console.WriteLine($"{e.Id,-5} {Scoring.AcademicFocus(e.Application),-9} {Scoring.ServiceFocus(e.Application)}");
    foreach (var (name, fn) in new[] { ("academic", Scoring.AcademicFocus), ("service", Scoring.ServiceFocus) })
    {
        var top = PayoutWorkflow.BuildReports(apps, settings, minScore, fn)
            .Match(b => string.Join(", ", b.Shortlist.Where(r => r.Awarded).Select(r => r.Id)), e => e.ToString());
        Console.WriteLine($"Awarded with {name,-8}: {top}");
    }

    Section("2. Deferred execution: creating the query does not run it");
    var scoreCalls = 0;
    var query = LazyScoring.ScoreEligible(apps, settings.MinGpa, a => { scoreCalls++; return score(a); });
    Console.WriteLine($"after creating the enumerable : score function calls = {scoreCalls}");
    var snapshot = query.ToImmutableArray();
    Console.WriteLine($"after ToImmutableArray()      : score function calls = {scoreCalls}  (snapshot of {snapshot.Length} items)");
    _ = snapshot.Count(); _ = snapshot.Sum(s => s.Score);
    Console.WriteLine($"reusing the snapshot twice    : score function calls = {scoreCalls}  (no recalculation)");
    _ = query.Count();
    Console.WriteLine($"enumerating the lazy query    : score function calls = {scoreCalls}  (recalculated: the query was never materialised)");

    Section($"3. Normal path: both reports, native stipend, export ({configName})");
    var outcome = PayoutWorkflow.Run(csvPath, exportPath, settings, minScore, score, NativeStipendCalculator.Real());
    var code = Report(outcome, r =>
        ReportFormatter.FormatShortlist(configName, r.Shortlist) + Environment.NewLine +
        ReportFormatter.FormatIncomeBands(r.Bands) + Environment.NewLine +
        "--- Payouts (stipend from the native C function sp_stipend_amount) ---" + Environment.NewLine +
        string.Join(Environment.NewLine, r.Payouts.Select(p => $"{p.Row.Id,-5} score {p.Row.Score,-4} income {p.Row.MonthlyIncome,-6} -> stipend {p.Stipend}")) +
        Environment.NewLine + $"Total stipend: {r.TotalStipend}" + Environment.NewLine + $"Exported to: {r.ExportPath}");
    if (code != 0) return code;

    Section("4. Failure: empty data file (valid header, no applications)");
    Report(PayoutWorkflow.Run(Path.Combine(dir, "empty.csv"), Path.Combine(outputDir, "never-written.csv"), settings, minScore, score, NativeStipendCalculator.Real()),
        _ => "unexpected success");

    Section("5. Failure: native library cannot be loaded (nothing is exported)");
    var failPath = Path.Combine(outputDir, "never-written.csv");
    File.Delete(failPath);
    Report(PayoutWorkflow.Run(csvPath, failPath, settings, minScore, score, NativeStipendCalculator.WithMissingLibrary()),
        _ => "unexpected success");
    Console.WriteLine($"export file exists: {File.Exists(failPath)} (state consistent)");

    Section("6. Failure: export directory does not exist");
    Report(PayoutWorkflow.Run(csvPath, Path.Combine(outputDir, "no-such-dir", "x.csv"), settings, minScore, score, NativeStipendCalculator.Real()),
        _ => "unexpected success");

    Section("7. Failure: invalid input to the stipend adapter (rejected before the native call)");
    Report(NativeStipendCalculator.Real().Calculate(999, 100m), v => $"unexpected success {v}");
    return 0;
}
