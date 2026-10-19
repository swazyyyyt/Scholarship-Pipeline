using ScholarshipPipeline;

// Imperative shell: reading files, choosing the policy and printing happen here, outside the pure core.
// Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/ScholarshipPipeline -- <csv-path> [need|merit|both|demo]");
    return 2;
}

var path = args[0];
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "both";

if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 2;
}

var policies = mode == "demo" ? SelectPolicies("need") : SelectPolicies(mode);
if (policies is null)
{
    Console.Error.WriteLine($"Unknown policy '{mode}'. Use need, merit, both or demo.");
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

// Screening goes through the validated factory (EligibleApplication.Create); the result is the first explicit state.
var opened = AwardRound.Open(report.Valid, settings.MinGpa);
Console.Write(ResultFormatter.FormatRejections(AwardRound.RejectionsOf(opened)));
Console.WriteLine();

if (mode == "demo")
{
    RunSequenceDemo(opened, policies[0].Policy, settings);
    return 0;
}

var failed = false;
foreach (var (name, policy) in policies)
{
    // Each policy gets its own store, starting from the same immutable first state.
    var store = new RoundStore(opened);
    var ranked = store.Apply(s => AwardRound.Rank(s, policy, settings.AwardSlots));
    var finalized = store.Apply(AwardRound.Finalize);

    switch (store.Current)
    {
        case RoundState.Finalized f:
            Console.WriteLine(ResultFormatter.Format(name, f.Result));
            break;
        default:
            Console.WriteLine($"Policy {name}: round not finalized. {Describe(ranked)} {Describe(finalized)}");
            failed = true;
            break;
    }
}

if (failed) return 1;
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

static string Describe(TransitionOutcome outcome) =>
    outcome switch
    {
        TransitionOutcome.Accepted a => $"ACCEPTED -> {a.Next.GetType().Name}",
        TransitionOutcome.Refused r => $"REFUSED: {r.Reason}",
        _ => "REFUSED: unknown outcome"
    };

// Demonstration of the safe sequence: one invalid order, one invalid input, one repeated call.
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
