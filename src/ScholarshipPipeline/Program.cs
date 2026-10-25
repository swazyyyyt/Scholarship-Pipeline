using ScholarshipPipeline;

// Imperative shell: the only place that prints and chooses what to do with the FINAL outcome of a Result.
// Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo|a7|explain <id> [need|merit]]
// Exit codes: 0 = success, 1 = expected domain failure, 2 = operational problem (file) or wrong usage.

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo|a7|explain <id> [need|merit]]");
    return 2;
}

var path = args[0];
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "both";
var settings = new PipelineSettings(MinGpa: 2.5m, AwardSlots: 3);

var policies = SelectPolicies(mode switch { "demo" or "a7" => "need", "explain" => (args.Length > 3 ? args[3].ToLowerInvariant() : "need"), _ => mode });
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
