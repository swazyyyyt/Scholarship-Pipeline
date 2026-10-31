using System.Collections;
using System.Collections.Immutable;

namespace ScholarshipPipeline.Tests;

/// <summary>
/// Assignment 8 tests: extensions, LINQ reports, configured (curried) functions, deferred execution, resource lifetime,
/// the native C call, complete workflows and file boundaries. Expected values were calculated by hand (see README).
/// </summary>
public sealed class Assignment8Tests : IDisposable
{
    private static readonly PipelineSettings Settings = new(MinGpa: 2.5m, AwardSlots: 3);
    private const string Header = "id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord";

    // File tests use a unique temp directory per test instance and delete it afterwards.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scholarship-a8-" + Guid.NewGuid().ToString("N"));

    public Assignment8Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---------- helpers ----------
    private static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    private static ImmutableArray<ScholarshipApplication> Sample() =>
        ApplicationLoader.Load(DataFile("applications.csv")).Match(a => a, e => throw new InvalidOperationException(e.ToString()));

    private static ScholarshipApplication App(string id, decimal gpa, decimal income = 400m, int hours = 0, bool disciplinary = false) =>
        new(id, "Name-" + id, gpa, income, hours, disciplinary);

    private static ScoredApplication Scored(string id, decimal gpa, int score, decimal income = 400m, int hours = 0)
    {
        var outcome = EligibleApplication.Create(App(id, gpa, income, hours), 0m);
        return new ScoredApplication(Assert.IsType<EligibilityOutcome.Eligible>(outcome).Value, score);
    }

    private static ImmutableArray<ScoredApplication> Snapshot(Func<ScholarshipApplication, int> score) =>
        LazyScoring.ScoreEligible(Sample(), 2.5m, score).ToImmutableArray();

    private static T Success<T>(Result<T, AppError> r) =>
        r.Match(v => v, e => throw new InvalidOperationException("expected success but got " + e));

    private static AppError Failure<T>(Result<T, AppError> r) =>
        r.Match<AppError>(_ => throw new InvalidOperationException("expected failure but got success"), e => e);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Result<PayoutReport, AppError> RunWorkflow(string csv, string export, IStipendCalculator calc,
        Func<ScholarshipApplication, int>? score = null, PipelineSettings? settings = null) =>
        PayoutWorkflow.Run(csv, export, settings ?? Settings, minScore: 60, score ?? Scoring.AcademicFocus, calc);

    // ---------- test doubles / probes ----------
    private sealed class CountingSource : IEnumerable<ScholarshipApplication>
    {
        private readonly IReadOnlyList<ScholarshipApplication> _items;
        public int Enumerations { get; private set; }
        public int ItemsPulled { get; private set; }
        public CountingSource(IReadOnlyList<ScholarshipApplication> items) => _items = items;

        public IEnumerator<ScholarshipApplication> GetEnumerator()
        {
            Enumerations++;
            foreach (var item in _items)
            {
                ItemsPulled++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProbeReader : TextReader
    {
        private readonly Queue<string> _lines;
        private readonly int _throwAfter;
        private int _read;
        public bool Disposed { get; private set; }
        public ProbeReader(IEnumerable<string> lines, int throwAfter = int.MaxValue)
        {
            _lines = new Queue<string>(lines);
            _throwAfter = throwAfter;
        }

        public override string? ReadLine()
        {
            if (_read++ >= _throwAfter) throw new IOException("simulated read failure");
            return _lines.Count == 0 ? null : _lines.Dequeue();
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ProbeWriter : StringWriter
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeCalculator : IStipendCalculator
    {
        private readonly Func<int, decimal, Result<int, AppError>> _f;
        public int Calls { get; private set; }
        public FakeCalculator(Func<int, decimal, Result<int, AppError>> f) => _f = f;
        public Result<int, AppError> Calculate(int score, decimal monthlyIncome)
        {
            Calls++;
            return _f(score, monthlyIncome);
        }
    }

    // =====================================================================
    // Reports (Task 2)
    // =====================================================================

    [Fact]
    public void Shortlist_NormalResult_FilteredOrderedRankedAndAwarded()
    {
        // Academic scores (calculated by hand): S001 123, S007 120, S002 101, S003 82, S004 63. MinScore 80 drops S004.
        var rows = Reports.Shortlist(Snapshot(Scoring.AcademicFocus), new ShortlistOptions(MinScore: 80, AwardSlots: 3));

        Assert.Equal(new[] { "S001", "S007", "S002", "S003" }, rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 123, 120, 101, 82 }, rows.Select(r => r.Score).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Rank).ToArray());
        Assert.Equal(new[] { true, true, true, false }, rows.Select(r => r.Awarded).ToArray());
    }

    [Fact]
    public void Shortlist_EmptyDataset_GivesEmptyReport()
    {
        var rows = Reports.Shortlist(ImmutableArray<ScoredApplication>.Empty, new ShortlistOptions(0, 3));
        Assert.Empty(rows);
        Assert.Contains("no application", ReportFormatter.FormatShortlist("x", rows));
    }

    [Fact]
    public void Shortlist_NoMatchingRecords_GivesEmptyReport()
    {
        Assert.Empty(Reports.Shortlist(Snapshot(Scoring.AcademicFocus), new ShortlistOptions(MinScore: 1000, AwardSlots: 3)));
    }

    [Fact]
    public void Shortlist_TiedPrimaryValues_AreOrderedByGpaThenIdRegardlessOfInputOrder()
    {
        var items = new[]
        {
            Scored("B", 3.0m, 80), Scored("A", 3.0m, 80), Scored("C", 3.5m, 80), Scored("D", 2.9m, 90)
        };
        // Expected by hand: D (score 90); then score 80 ties: C (gpa 3.5), then A and B tie on gpa -> Id ordinal.
        var expected = new[] { "D", "C", "A", "B" };

        Assert.Equal(expected, Reports.Shortlist(items, new ShortlistOptions(0, 4)).Select(r => r.Id).ToArray());
        Assert.Equal(expected, Reports.Shortlist(items.Reverse().ToArray(), new ShortlistOptions(0, 4)).Select(r => r.Id).ToArray());
    }

    [Fact]
    public void IncomeBands_GroupingAndTotals_AreCorrect()
    {
        // Hand calculation (academic): low = S001(123,120h) + S003(82,50h); middle = S004(63,0h) + S007(120,100h); high = S002(101,30h).
        var rows = Reports.IncomeBands(Snapshot(Scoring.AcademicFocus));

        Assert.Equal(new[] { "low", "middle", "high" }, rows.Select(r => r.Band).ToArray());
        Assert.Equal(new IncomeBandRow("low", 2, 205, 102.5m, 170), rows[0]);
        Assert.Equal(new IncomeBandRow("middle", 2, 183, 91.5m, 100), rows[1]);
        Assert.Equal(new IncomeBandRow("high", 1, 101, 101m, 30), rows[2]);
    }

    [Fact]
    public void IncomeBands_EmptyInput_OrderIsDeterministic_AndBandEdgesAreInclusiveUpward()
    {
        Assert.Empty(Reports.IncomeBands(ImmutableArray<ScoredApplication>.Empty));

        Assert.Equal("low", Reports.BandOf(500m).Name);
        Assert.Equal("middle", Reports.BandOf(500.01m).Name);
        Assert.Equal("middle", Reports.BandOf(1000m).Name);
        Assert.Equal("high", Reports.BandOf(1000.01m).Name);

        var items = new[] { Scored("H", 3m, 50, income: 2000m), Scored("L", 3m, 50, income: 10m), Scored("M", 3m, 50, income: 700m) };
        var forward = Reports.IncomeBands(items).Select(r => r.Band).ToArray();
        var backward = Reports.IncomeBands(items.Reverse().ToArray()).Select(r => r.Band).ToArray();
        Assert.Equal(new[] { "low", "middle", "high" }, forward);
        Assert.Equal(forward, backward);
    }

    // =====================================================================
    // Custom extensions (Task 1)
    // =====================================================================

    [Fact]
    public void OrderForRanking_ScoreThenGpaThenId_AndInputIsNotChanged()
    {
        var input = new[] { Scored("S2", 3.0m, 70), Scored("S1", 3.0m, 70), Scored("S3", 3.9m, 70), Scored("S4", 2.5m, 71) };
        var before = input.Select(s => s.Candidate.Id).ToArray();

        var ordered = input.OrderForRanking().Select(s => s.Candidate.Id).ToArray();

        Assert.Equal(new[] { "S4", "S3", "S1", "S2" }, ordered);
        Assert.Equal(before, input.Select(s => s.Candidate.Id).ToArray());   // receiver unchanged
        Assert.Equal(ordered, Stages.BuildResult(input, 4).Ranking.Select(r => r.Id).ToArray());   // used by the A5 ranking stage
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<ScoredApplication>)null!).OrderForRanking());
    }

    [Fact]
    public void Ensure_KeepsValidValue_FailsInvalidValue_AndSkipsPredicateOnFailure()
    {
        var ok = Result<int, AppError>.Ok(5);
        Assert.Equal(5, Success(ok.Ensure(v => v > 0, _ => new AppError(ErrorKind.Domain, "X", "x"))));

        var rejected = ok.Ensure(v => v > 10, v => new AppError(ErrorKind.Domain, "TOO_SMALL", $"{v} is too small"));
        Assert.Equal("TOO_SMALL", Failure(rejected).Code);

        var predicateCalls = 0;
        var original = Result<int, AppError>.Fail(new AppError(ErrorKind.Operational, "ORIGINAL", "first failure"));
        var passed = original.Ensure(_ => { predicateCalls++; return true; }, _ => new AppError(ErrorKind.Domain, "OTHER", "other"));
        Assert.Equal("ORIGINAL", Failure(passed).Code);
        Assert.Equal(0, predicateCalls);
    }

    [Fact]
    public void Traverse_AllSucceed_KeepsOrder_FirstFailureStopsFurtherCalls_EmptyIsSuccess()
    {
        var calls = 0;
        Result<int, AppError> Step(int x)
        {
            calls++;
            return x == 3 ? Result<int, AppError>.Fail(new AppError(ErrorKind.Domain, "THREE", "3 is not allowed"))
                          : Result<int, AppError>.Ok(x * 10);
        }

        Assert.Equal(new[] { 10, 20 }, Success(new[] { 1, 2 }.Traverse(Step)).ToArray());

        calls = 0;
        var failed = new[] { 1, 2, 3, 4, 5 }.Traverse(Step);
        Assert.Equal("THREE", Failure(failed).Code);
        Assert.Equal(3, calls);   // items 4 and 5 were never visited

        Assert.Empty(Success(Array.Empty<int>().Traverse(Step)));
    }

    // =====================================================================
    // Currying and partial application (Task 3)
    // =====================================================================

    [Fact]
    public void ConfiguredFunctions_SameInput_GiveDocumentedDifferentResults()
    {
        var s001 = Sample().Single(a => a.Id == "S001");
        var s002 = Sample().Single(a => a.Id == "S002");
        var s003 = Sample().Single(a => a.Id == "S003");

        // S001 academic: round(3.9*25)=98 + min(120/20,5)=5 + merit 20 = 123;  service: round(3.9*15)=59 + min(120/5,20)=20 + need 20 = 99
        Assert.Equal(123, Scoring.AcademicFocus(s001));
        Assert.Equal(99, Scoring.ServiceFocus(s001));
        // S002 academic: 90 + 1 + 10 = 101;  service: 54 + 6 + 0 = 60
        Assert.Equal(101, Scoring.AcademicFocus(s002));
        Assert.Equal(60, Scoring.ServiceFocus(s002));
        // S003 academic: 80 + 2 + 0 = 82;  service: 48 + 10 + 20 = 78
        Assert.Equal(82, Scoring.AcademicFocus(s003));
        Assert.Equal(78, Scoring.ServiceFocus(s003));
    }

    [Fact]
    public void ConfiguredFunctions_ChangeTheAwardedSet_OnTheSameDataset_WithoutGlobalState()
    {
        List<string> Awarded(Func<ScholarshipApplication, int> f) =>
            Success(PayoutWorkflow.BuildReports(Sample(), Settings, 60, f)).Shortlist.Where(r => r.Awarded).Select(r => r.Id).ToList();

        var academic = Awarded(Scoring.AcademicFocus);
        var service = Awarded(Scoring.ServiceFocus);
        Assert.Equal(new[] { "S001", "S007", "S002" }, academic);
        Assert.Equal(new[] { "S001", "S007", "S003" }, service);   // the documented difference: S002 vs S003
        Assert.Equal(academic, Awarded(Scoring.AcademicFocus));    // switching back gives the first answer again
    }

    [Fact]
    public void CreateScore_IsCurried_AndRejectsInvalidWeights()
    {
        var weights = new ScoreWeights(20, 10, 10, BonusPolicies.NeedBased);
        var partial = Scoring.CreateScore(weights);                 // partial application: weights fixed
        var s001 = Sample().Single(a => a.Id == "S001");
        Assert.Equal(Scoring.Compute(weights, s001), partial(s001));
        Assert.Equal(108, partial(s001));                           // 78 + 10 + 20, the A5 need-based score of S001

        Assert.Throws<ArgumentOutOfRangeException>(() => Scoring.CreateScore(weights with { HoursPerPoint = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scoring.CreateScore(weights with { MaxVolunteerPoints = -1 }));
        Assert.Throws<ArgumentNullException>(() => Scoring.CreateScore(weights with { Bonus = null! }));
    }

    [Fact]
    public void ScoringByName_FindsKnownConfigurationsAndReportsUnknownAsNone()
    {
        Assert.True(Scoring.ByName("Academic").IsSome);
        Assert.True(Scoring.ByName("service").IsSome);
        Assert.False(Scoring.ByName("nope").IsSome);
    }

    // =====================================================================
    // Deferred execution (Task 4)
    // =====================================================================

    [Fact]
    public void LazyScoring_CreatingTheEnumerable_DoesNotProcessAnything()
    {
        var source = new CountingSource(Sample());
        var scoreCalls = 0;

        var query = LazyScoring.ScoreEligible(source, 2.5m, a => { scoreCalls++; return 1; });

        Assert.Equal(0, source.Enumerations);
        Assert.Equal(0, source.ItemsPulled);
        Assert.Equal(0, scoreCalls);
        Assert.NotNull(query);
    }

    [Fact]
    public void LazyScoring_Iteration_PullsAndScoresOneItemAtATime_AndSkipsIneligible()
    {
        var source = new CountingSource(Sample());
        var scoreCalls = 0;
        var query = LazyScoring.ScoreEligible(source, 2.5m, a => { scoreCalls++; return Scoring.AcademicFocus(a); });

        using var e = query.GetEnumerator();
        Assert.True(e.MoveNext());                         // S001 is eligible
        Assert.Equal(1, source.ItemsPulled);
        Assert.Equal(1, scoreCalls);
        Assert.Equal("S001", e.Current.Candidate.Id);

        var rest = new List<string>();
        while (e.MoveNext()) rest.Add(e.Current.Candidate.Id);
        Assert.Equal(new[] { "S002", "S003", "S004", "S007" }, rest);   // S005 (gpa) and S006 (record) were skipped
        Assert.Equal(7, source.ItemsPulled);
        Assert.Equal(5, scoreCalls);                       // the score function ran only for the 5 eligible ones
    }

    [Fact]
    public void LazyScoring_Materialisation_GivesStableSnapshot_WhileRepeatedEnumerationRecomputes()
    {
        var source = new CountingSource(Sample());
        var scoreCalls = 0;
        var query = LazyScoring.ScoreEligible(source, 2.5m, a => { scoreCalls++; return 1; });

        _ = query.Count(); _ = query.Count();              // not materialised: the work is repeated
        Assert.Equal(2, source.Enumerations);
        Assert.Equal(10, scoreCalls);

        var snapshot = query.ToImmutableArray();           // deliberate materialisation point
        var callsAfterSnapshot = scoreCalls;
        Assert.Equal(3, source.Enumerations);
        _ = snapshot.Count(); _ = snapshot.Sum(s => s.Score); _ = snapshot.ToList();
        Assert.Equal(callsAfterSnapshot, scoreCalls);      // reuse does not recompute
        Assert.Equal(3, source.Enumerations);              // and does not re-read the source
    }

    [Fact]
    public void LazyScoring_InvalidArguments_AreRejectedImmediately()
    {
        Assert.Throws<ArgumentNullException>(() => LazyScoring.ScoreEligible(null!, 2.5m, _ => 0));
        Assert.Throws<ArgumentNullException>(() => LazyScoring.ScoreEligible(Sample(), 2.5m, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => LazyScoring.ScoreEligible(Sample(), 4.5m, _ => 0));
    }

    // =====================================================================
    // Resource lifetime (Task 5)
    // =====================================================================

    [Fact]
    public void ReadLines_OpensOnlyOnEnumeration_AndDisposesAfterSuccess()
    {
        var reader = new ProbeReader(new[] { "a", "b", "c" });
        var opened = 0;
        var lines = LineSource.ReadLines(() => { opened++; return reader; });

        Assert.Equal(0, opened);                           // deferred: nothing opened yet
        Assert.Equal(new[] { "a", "b", "c" }, lines.ToArray());
        Assert.Equal(1, opened);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public void ReadLines_DisposesOnEarlyStop()
    {
        var reader = new ProbeReader(new[] { "a", "b", "c" });
        var first = LineSource.ReadLines(() => reader).First();

        Assert.Equal("a", first);
        Assert.True(reader.Disposed);                      // iteration stopped after one line, the resource is still released
    }

    [Fact]
    public void ReadLines_DisposesWhenReadingFails()
    {
        var reader = new ProbeReader(new[] { "a", "b", "c" }, throwAfter: 1);

        Assert.Throws<IOException>(() => LineSource.ReadLines(() => reader).ToArray());
        Assert.True(reader.Disposed);
    }

    [Fact]
    public void ReadFileLines_ReleasesTheFileHandle_AfterCompletionAndAfterEarlyStop()
    {
        var path = WriteFile("lines.txt", "one\ntwo\nthree\n");

        Assert.Equal(3, LineSource.ReadFileLines(path).Count());
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // exclusive open succeeds: handle released

        Assert.Equal("one", LineSource.ReadFileLines(path).First());
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void WriteTo_DisposesWriterOnSuccessAndOnFailure()
    {
        var ok = new ProbeWriter();
        ReportExporter.WriteTo(() => ok, new[] { "x", "y" });
        Assert.True(ok.Disposed);

        var failing = new ProbeWriter();
        IEnumerable<string> Broken() { yield return "x"; throw new InvalidOperationException("boom"); }
        Assert.Throws<InvalidOperationException>(() => ReportExporter.WriteTo(() => failing, Broken()));
        Assert.True(failing.Disposed);
    }

    // =====================================================================
    // Native C library (Tasks 7-9)
    // =====================================================================

    [Fact]
    public void NativeCall_RealLibrary_ReturnsManuallyCalculatedValues()
    {
        var native = NativeStipendCalculator.Real();

        // 123 * 100 = 12300; income 400 <= 500 -> +25 % = +3075 -> 15375
        Assert.Equal(15375, Success(native.Calculate(123, 400m)));
        // 120 * 100 = 12000; income 1000 <= 1000 -> +10 % -> 13200
        Assert.Equal(13200, Success(native.Calculate(120, 1000m)));
        // 101 * 100 = 10100; income 1500 -> no supplement
        Assert.Equal(10100, Success(native.Calculate(101, 1500m)));
        // band edges and minimum: 500 -> 25 %, 501 -> 10 %, 0 -> 0
        Assert.Equal(12500, Success(native.Calculate(100, 500m)));
        Assert.Equal(11000, Success(native.Calculate(100, 501m)));
        Assert.Equal(0, Success(native.Calculate(0, 0m)));
        // fractional income is truncated toward zero before the call: 500.9 -> 500 -> 25 %
        Assert.Equal(12500, Success(native.Calculate(100, 500.9m)));
    }

    [Fact]
    public void NativeFunction_ItselfReturnsMinusOne_ForOutOfRangeInput()
    {
        Assert.Equal(-1, NativeMethods.StipendAmount(201, 100));
        Assert.Equal(-1, NativeMethods.StipendAmount(-1, 100));
        Assert.Equal(-1, NativeMethods.StipendAmount(100, -1));
        Assert.Equal(-1, NativeMethods.StipendAmount(100, 1_000_001));
        Assert.Equal(15375, NativeMethods.StipendAmount(123, 400));
    }

    [Fact]
    public void Adapter_InvalidDomainInput_IsRejectedBeforeTheNativeCall()
    {
        var calls = 0;
        var adapter = new NativeStipendCalculator((s, i) => { calls++; return 1; });

        Assert.Equal(ErrorCodes.InvalidStipendInput, Failure(adapter.Calculate(201, 100m)).Code);
        Assert.Equal(ErrorCodes.InvalidStipendInput, Failure(adapter.Calculate(-1, 100m)).Code);
        Assert.Equal(ErrorCodes.InvalidStipendInput, Failure(adapter.Calculate(10, -0.01m)).Code);
        Assert.Equal(ErrorCodes.InvalidStipendInput, Failure(adapter.Calculate(10, 1_000_001m)).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Adapter_LoadAndBindingFailures_BecomeClearResultFailures()
    {
        // A REAL load failure: the binding points to a library that does not exist (fresh load, no file deletion needed).
        var missing = Failure(NativeStipendCalculator.WithMissingLibrary().Calculate(100, 100m));
        Assert.Equal(ErrorCodes.NativeLoadFailed, missing.Code);
        Assert.Equal(ErrorKind.Operational, missing.Kind);
        Assert.Contains("could not be loaded", missing.Message);

        // Controlled failures through the injectable delegate.
        Assert.Equal(ErrorCodes.NativeBindingFailed,
            Failure(new NativeStipendCalculator((s, i) => throw new EntryPointNotFoundException("no such export")).Calculate(100, 100m)).Code);
        Assert.Equal(ErrorCodes.NativeLoadFailed,
            Failure(new NativeStipendCalculator((s, i) => throw new BadImageFormatException("wrong architecture")).Calculate(100, 100m)).Code);
        Assert.Equal(ErrorCodes.NativeRejectedInput,
            Failure(new NativeStipendCalculator((s, i) => -1).Calculate(100, 100m)).Code);
    }

    // =====================================================================
    // Complete workflows (integration)
    // =====================================================================

    [Fact]
    public void Workflow_NormalPath_ReportsNativeStipendsAndExport()
    {
        var export = Path.Combine(_dir, "payouts.csv");

        var report = Success(RunWorkflow(DataFile("applications.csv"), export, NativeStipendCalculator.Real()));

        Assert.Equal(5, report.Shortlist.Length);   // minScore 60: S004 (63) is included
        Assert.Equal(3, report.Bands.Length);
        Assert.Equal(new[] { 15375, 13200, 10100 }, report.Payouts.Select(p => p.Stipend).ToArray());   // real native values
        Assert.Equal(38675, report.TotalStipend);
        Assert.Equal(export, report.ExportPath);
        Assert.Equal(
            new[] { ReportExporter.CsvHeader, "1,S001,Aigerim,123,400,15375", "2,S007,Hanna,120,1000,13200", "3,S002,Bekzat,101,1500,10100" },
            File.ReadAllLines(export));
        Assert.False(File.Exists(export + ".tmp"));
    }

    [Fact]
    public void Workflow_NativeFailure_ExportsNothing_AndKeepsAnExistingFileUntouched()
    {
        var export = WriteFile("payouts.csv", "previous content");

        var error = Failure(RunWorkflow(DataFile("applications.csv"), export, NativeStipendCalculator.WithMissingLibrary()));

        Assert.Equal(ErrorCodes.NativeLoadFailed, error.Code);
        Assert.Equal("previous content", File.ReadAllText(export));
        Assert.False(File.Exists(export + ".tmp"));
    }

    [Fact]
    public void Workflow_FirstNativeFailure_StopsRemainingNativeCalls()
    {
        var calc = new FakeCalculator((s, i) => Result<int, AppError>.Fail(new AppError(ErrorKind.Operational, "FAKE", "fake failure")));

        var error = Failure(RunWorkflow(DataFile("applications.csv"), Path.Combine(_dir, "x.csv"), calc));

        Assert.Equal("FAKE", error.Code);
        Assert.Equal(1, calc.Calls);                       // three awarded applicants, but only one native call was made
        Assert.False(File.Exists(Path.Combine(_dir, "x.csv")));
    }

    [Fact]
    public void Workflow_NoMatchingApplicants_ProducesEmptyReportsAndHeaderOnlyExport()
    {
        var csv = WriteFile("ineligible.csv", Header + "\nX1,Low,2.0,300,10,false\nX2,Record,3.9,300,10,true\n");
        var export = Path.Combine(_dir, "empty-payouts.csv");

        var report = Success(RunWorkflow(csv, export, NativeStipendCalculator.Real()));

        Assert.Empty(report.Shortlist);
        Assert.Empty(report.Bands);
        Assert.Empty(report.Payouts);
        Assert.Equal(0, report.TotalStipend);
        Assert.Equal(new[] { ReportExporter.CsvHeader }, File.ReadAllLines(export));
    }

    [Fact]
    public void Workflow_EmptyDataFile_IsADomainFailure_AndNothingIsExported()
    {
        var export = Path.Combine(_dir, "payouts.csv");

        var error = Failure(RunWorkflow(DataFile("empty.csv"), export, NativeStipendCalculator.Real()));

        Assert.Equal(ErrorCodes.NoApplications, error.Code);
        Assert.Equal(ErrorKind.Domain, error.Kind);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public void Workflow_InvalidSettings_AreRefusedBeforeAnyWork()
    {
        var calc = new FakeCalculator((s, i) => Result<int, AppError>.Ok(1));
        var export = Path.Combine(_dir, "payouts.csv");

        var error = Failure(RunWorkflow(DataFile("applications.csv"), export, calc, settings: new PipelineSettings(2.5m, -1)));

        Assert.Equal(ErrorCodes.InvalidAwardSlots, error.Code);
        Assert.Equal(0, calc.Calls);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public void Workflow_ExportFailure_IsAClearFailure_AndLeavesNoTemporaryFile()
    {
        var export = Path.Combine(_dir, "no-such-dir", "payouts.csv");

        var error = Failure(RunWorkflow(DataFile("applications.csv"), export, NativeStipendCalculator.Real()));

        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
        Assert.Equal(ErrorKind.Operational, error.Kind);
        Assert.False(Directory.Exists(Path.Combine(_dir, "no-such-dir")));
    }

    // =====================================================================
    // File boundaries (the loader now reads through LineSource)
    // =====================================================================

    [Fact]
    public void Workflow_MissingFile_IsASafeFailure_AndNothingIsExported()
    {
        var export = Path.Combine(_dir, "payouts.csv");

        var error = Failure(RunWorkflow(Path.Combine(_dir, "missing.csv"), export, NativeStipendCalculator.Real()));

        Assert.Equal(ErrorCodes.FileNotFound, error.Code);
        Assert.Contains("missing.csv", error.Message);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public void Workflow_MalformedContent_IsASafeFailure_AndNothingIsExported()
    {
        var export = Path.Combine(_dir, "payouts.csv");

        var error = Failure(RunWorkflow(DataFile("invalid.csv"), export, NativeStipendCalculator.Real()));

        Assert.Equal(ErrorCodes.MalformedContent, error.Code);
        Assert.False(File.Exists(export));
    }

    [Fact]
    public void Loader_ReadingThroughLineSource_KeepsTheEarlierBehaviour_AndReleasesTheFile()
    {
        var path = WriteFile("a.csv", Header + "\r\nS1,Ann,3.0,100,5,false\r\n\r\nS2,Bob,3.5,900,0,false\r\n");

        var loaded = Success(ApplicationLoader.Load(path));
        Assert.Equal(new[] { "S1", "S2" }, loaded.Select(a => a.Id).ToArray());
        File.Delete(path);                                  // would fail on Windows if the loader still held the file
        Assert.False(File.Exists(path));

        Assert.Equal(ErrorCodes.FileNotFound, Failure(ApplicationLoader.Load(Path.Combine(_dir, "nope", "x.csv"))).Code);
        Assert.Equal(ErrorCodes.FileUnreadable, Failure(ApplicationLoader.Load(_dir)).Code);   // a directory is not a readable file
    }
}
