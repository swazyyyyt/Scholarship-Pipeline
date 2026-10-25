using System.Collections.Immutable;

namespace ScholarshipPipeline.Tests;

/// <summary>Assignment 7 tests: Result/Option, Map/Bind, domain failures, lookups and the file boundary.</summary>
public sealed class ErrorHandlingTests : IDisposable
{
    private static readonly PipelineSettings Settings = new(MinGpa: 2.5m, AwardSlots: 3);

    // File tests use a unique temp directory per test instance and delete it afterwards.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scholarship-a7-" + Guid.NewGuid().ToString("N"));

    public ErrorHandlingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---------- helpers ----------
    private const string Header = "id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord";

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadData(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file));

    private static ImmutableArray<ScholarshipApplication> Sample()
    {
        var report = ApplicationCsvParser.Parse(ReadData("applications.csv"));
        Assert.False(report.HasErrors);
        return report.Valid;
    }

    private static AppError Err(string code) => new(ErrorKind.Domain, code, "test error " + code);

    private static T Success<T>(Result<T, AppError> r) =>
        r.Match(v => v, e => throw new InvalidOperationException("expected success but got " + e));

    private static AppError Failure<T>(Result<T, AppError> r) =>
        r.Match<AppError>(_ => throw new InvalidOperationException("expected failure but got success"), e => e);

    /// <summary>Policy test double: counts how many times the scoring stage called it.</summary>
    private sealed class CountingPolicy
    {
        public int Calls { get; private set; }

        public int Invoke(ScholarshipApplication a)
        {
            Calls++;
            return BonusPolicies.NeedBased(a);
        }
    }

    // =====================================================================
    // Task 1 / Task 3: Result, Map, Bind
    // =====================================================================

    [Fact]
    public void MapSuccess_TransformationReceivesValueAndReturnsNewValue()
    {
        int received = 0;

        var mapped = Result<int, AppError>.Ok(20).Map(x => { received = x; return x + 1; });

        Assert.Equal(20, received);
        Assert.Equal(21, Success(mapped));
    }

    [Fact]
    public void BindSuccess_NextOperationReceivesValue_AndItsResultIsPreserved()
    {
        int received = 0;
        var next = Result<string, AppError>.Ok("done");

        var bound = Result<int, AppError>.Ok(7).Bind(x => { received = x; return next; });

        Assert.Equal(7, received);
        Assert.Same(next, bound);                                    // not re-wrapped, the same Result object
    }

    [Fact]
    public void BindSuccess_FailureReturnedByNextOperationIsPreserved()
    {
        var bound = Result<int, AppError>.Ok(7).Bind(_ => Result<string, AppError>.Fail(Err("NEXT_FAILED")));

        Assert.Equal("NEXT_FAILED", Failure(bound).Code);
    }

    [Fact]
    public void FailurePropagation_MapAndBind_KeepTheErrorAndNeverCallTheFunction()
    {
        int mapCalls = 0, bindCalls = 0;
        var failed = Result<int, AppError>.Fail(Err("BOOM"));

        var afterMap = failed.Map(x => { mapCalls++; return x * 2; });
        var afterBind = failed.Bind(x => { bindCalls++; return Result<int, AppError>.Ok(x); });

        Assert.Equal(0, mapCalls);
        Assert.Equal(0, bindCalls);
        Assert.Equal("BOOM", Failure(afterMap).Code);
        Assert.Equal("BOOM", Failure(afterBind).Code);
    }

    [Fact]
    public void ResultAndOption_CannotRepresentContradictoryStates()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string, AppError>.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => Result<string, AppError>.Fail(null!));
        Assert.Throws<ArgumentNullException>(() => Option<string>.Of(null!));
    }

    // =====================================================================
    // Task 2: Option - present and absent, legitimate primitive values
    // =====================================================================

    [Fact]
    public void Option_ZeroFalseAndEmptyString_AreSomeNotNone()
    {
        Assert.True(Option<int>.Of(0).IsSome);
        Assert.True(Option<bool>.Of(false).IsSome);
        Assert.True(Option<string>.Of("").IsSome);
        Assert.False(Option<int>.Empty().IsSome);
    }

    [Fact]
    public void Option_ToResult_PresentKeepsValue_AbsentBecomesMeaningfulFailure()
    {
        var present = Option<int>.Of(0).ToResult(() => Err("MISSING"));
        var absent = Option<int>.Empty().ToResult(() => Err("MISSING"));

        Assert.Equal(0, Success(present));
        Assert.Equal("MISSING", Failure(absent).Code);
    }

    [Fact]
    public void LookupPresentAndAbsent_ApplicantAndRecommendation()
    {
        var apps = Sample();

        var present = ApplicantLookup.Find(apps, "S003");
        var absent = ApplicantLookup.Find(apps, "S999");

        Assert.Equal("Dana", present.Match(a => a.Name, () => "none"));
        Assert.False(absent.IsSome);
        Assert.False(ApplicantLookup.Find(apps, "").IsSome);
        Assert.False(ApplicantLookup.Find(apps, "s003").IsSome);        // ids are compared ordinally

        var result = Success(ScholarshipService.RunRound(apps, Settings, BonusPolicies.NeedBased).Bind(RoundOperations.ResultOf));
        Assert.True(ApplicantLookup.FindRecommendation(result, "S001").IsSome);
        Assert.False(ApplicantLookup.FindRecommendation(result, "S005").IsSome);   // screened out: not in the ranking
    }

    [Fact]
    public void LookupLegitimateZero_ZeroVolunteerHoursIsSomeZero_NotAbsent()
    {
        var apps = Sample();      // S004 has 0 volunteer hours

        Assert.Equal(0, ApplicantLookup.VolunteerHours(apps, "S004").Match(h => h, () => -1));
        Assert.Equal(120, ApplicantLookup.VolunteerHours(apps, "S001").Match(h => h, () => -1));
        Assert.Equal(-1, ApplicantLookup.VolunteerHours(apps, "S999").Match(h => h, () => -1));
    }

    // =====================================================================
    // Task 1 + Task 3: integrated chain, success and domain failures
    // =====================================================================

    [Fact]
    public void CompleteSuccess_RunRound_ProducesFinalizedStateWithDocumentedAwards()
    {
        var state = Success(ScholarshipService.RunRound(Sample(), Settings, BonusPolicies.NeedBased));

        var finalized = Assert.IsType<RoundState.Finalized>(state);
        Assert.Equal(new[] { "S001", "S007", "S003" },
            finalized.Result.Ranking.Where(r => r.Awarded).Select(r => r.Id).ToArray());
        Assert.Equal(85.60m, finalized.Result.AverageScore);
        Assert.Equal(2, finalized.Rejected.Length);                     // S005, S006 are kept as rejections
    }

    [Fact]
    public void CompleteSuccess_BuildReport_MapsRankingToDisplayText()
    {
        var text = Success(ScholarshipService.BuildReport(Sample(), Settings, "need-based", BonusPolicies.NeedBased));

        Assert.Contains("=== Policy: need-based ===", text);
        Assert.Contains("Eligible: 5 | Awarded: 3 | Average score: 85.60", text);
        Assert.Contains("S003", text);
    }

    [Theory]
    [InlineData("need", "S003", 3, 89, true)]
    [InlineData("merit", "S003", 4, 69, false)]
    [InlineData("merit", "S002", 3, 85, true)]
    public void CompleteSuccess_ExplainApplicant_FullChainGivesCorrectView(
        string policyName, string id, int rank, int score, bool awarded)
    {
        Func<ScholarshipApplication, int> policy = policyName == "need" ? BonusPolicies.NeedBased : BonusPolicies.MeritBased;

        var view = Success(ScholarshipService.ExplainApplicant(Sample(), id, Settings, policy));

        Assert.Contains(id, view.Title);
        Assert.Equal($"rank {rank}, score {score}, {(awarded ? "AWARDED" : "not awarded")}", view.Details);
    }

    [Fact]
    public void DomainFailure_InvalidAwardSlots_ReportsRuleAndValue()
    {
        var error = Failure(ScholarshipService.RunRound(Sample(), Settings with { AwardSlots = -1 }, BonusPolicies.NeedBased));

        Assert.Equal(ErrorKind.Domain, error.Kind);
        Assert.Equal(ErrorCodes.InvalidAwardSlots, error.Code);
        Assert.Contains("-1", error.Message);
    }

    [Fact]
    public void DomainFailure_InvalidMinGpa_IsAFailureNotAnException()
    {
        var error = Failure(ScholarshipService.RunRound(Sample(), Settings with { MinGpa = 4.5m }, BonusPolicies.NeedBased));

        Assert.Equal(ErrorCodes.InvalidSettings, error.Code);
        Assert.Contains("4.5", error.Message);
    }

    [Fact]
    public void DomainFailure_StateDoesNotPermit_FinalizeBeforeRank()
    {
        var collected = AwardRound.Open(Sample(), 2.5m);

        var error = Failure(RoundOperations.Finalize(collected));

        Assert.Equal(ErrorCodes.RoundNotRanked, error.Code);
        Assert.Contains("ranked before", error.Message);
    }

    [Fact]
    public void DomainFailure_StateDoesNotPermit_RankTwice_AndStateStaysRanked()
    {
        var collected = AwardRound.Open(Sample(), 2.5m);
        var ranked = Success(RoundOperations.Rank(collected, BonusPolicies.NeedBased, 3));

        var error = Failure(RoundOperations.Rank(ranked, BonusPolicies.MeritBased, 3));

        Assert.Equal(ErrorCodes.AlreadyRanked, error.Code);
        Assert.IsType<RoundState.Ranked>(ranked);                           // the state value is unchanged
    }

    [Theory]
    [InlineData("S005", "gpa 2.4 is below the minimum 2.5")]
    [InlineData("S006", "has a disciplinary record")]
    public void DomainFailure_NotEligible_NamesTheApplicantAndTheViolatedRule(string id, string reason)
    {
        var error = Failure(ScholarshipService.ExplainApplicant(Sample(), id, Settings, BonusPolicies.NeedBased));

        Assert.Equal(ErrorCodes.NotEligible, error.Code);
        Assert.Contains(id, error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void DomainFailure_RequiredApplicantMissing_BecomesApplicantNotFound()
    {
        var error = Failure(ScholarshipService.ExplainApplicant(Sample(), "S999", Settings, BonusPolicies.NeedBased));

        Assert.Equal(ErrorKind.Domain, error.Kind);
        Assert.Equal(ErrorCodes.ApplicantNotFound, error.Code);
        Assert.Contains("S999", error.Message);
    }

    [Fact]
    public void FailurePropagation_MissingOrIneligibleApplicant_SkipsTheWholeRound()
    {
        var policy = new CountingPolicy();
        var apps = Sample();

        var missing = ScholarshipService.ExplainApplicant(apps, "S999", Settings, policy.Invoke);
        var ineligible = ScholarshipService.ExplainApplicant(apps, "S005", Settings, policy.Invoke);

        Assert.Equal(ErrorCodes.ApplicantNotFound, Failure(missing).Code);
        Assert.Equal(ErrorCodes.NotEligible, Failure(ineligible).Code);
        Assert.Equal(0, policy.Calls);              // scoring (RunRound) never ran after the first failure
    }

    [Fact]
    public void FailurePropagation_InvalidSettings_StopsBeforeOpenRankAndFinalize()
    {
        int opened = 0, ranked = 0, finalized = 0;

        var chain = RoundOperations.ValidateSettings(Settings with { MinGpa = -1m })
            .Map(s => { opened++; return AwardRound.Open(Sample(), s.MinGpa); })
            .Bind(c => { ranked++; return RoundOperations.Rank(c, BonusPolicies.NeedBased, 3); })
            .Bind(r => { finalized++; return RoundOperations.Finalize(r); });

        Assert.Equal(0, opened);
        Assert.Equal(0, ranked);
        Assert.Equal(0, finalized);
        Assert.Equal(ErrorCodes.InvalidSettings, Failure(chain).Code);
    }

    [Fact]
    public void SafeTransitions_StayActive_FailedResultLeavesStoredStateUnchanged()
    {
        var opened = AwardRound.Open(Sample(), 2.5m);
        var store = new RoundStore(opened);

        var refused = Failure(RoundOperations.ToResult(store.Apply(AwardRound.Finalize)));

        Assert.Equal(ErrorCodes.RoundNotRanked, refused.Code);
        Assert.Same(opened, store.Current);
    }

    // =====================================================================
    // Task 4: boundary exception handling
    // =====================================================================

    [Fact]
    public void Boundary_ValidFile_LoadsAllApplications()
    {
        var path = WriteFile("ok.csv", ReadData("applications.csv"));

        var loaded = Success(ApplicationLoader.Load(path));

        Assert.Equal(7, loaded.Length);
        Assert.Equal(0, loaded.Single(a => a.Id == "S004").VolunteerHours);
    }

    [Fact]
    public void Boundary_MissingFile_ReportsFileAndKeepsPreviousData()
    {
        var store = new ApplicationStore();
        store.Reload(WriteFile("ok.csv", ReadData("applications.csv")));
        var before = store.Current;
        var missing = Path.Combine(_dir, "nope.csv");

        var error = Failure(store.Reload(missing));

        Assert.Equal(ErrorKind.Operational, error.Kind);
        Assert.Equal(ErrorCodes.FileNotFound, error.Code);
        Assert.Contains(missing, error.Message);
        Assert.Equal(before, store.Current);                          // same immutable array, no partial update
        Assert.Equal(7, store.Current.Length);
    }

    [Fact]
    public void Boundary_MissingDirectoryAndBlankPath_AreFileNotFound()
    {
        Assert.Equal(ErrorCodes.FileNotFound, Failure(ApplicationLoader.Load(Path.Combine(_dir, "no-dir", "x.csv"))).Code);
        Assert.Equal(ErrorCodes.FileNotFound, Failure(ApplicationLoader.Load("  ")).Code);
    }

    [Fact]
    public void Boundary_PathIsADirectory_IsFileUnreadable_NotSuccess()
    {
        var error = Failure(ApplicationLoader.Load(_dir));

        Assert.Equal(ErrorKind.Operational, error.Kind);
        Assert.Equal(ErrorCodes.FileUnreadable, error.Code);
        Assert.Contains(_dir, error.Message);
    }

    [Fact]
    public void Boundary_MalformedFile_BecomesClearFailure_WithoutPartialUpdate()
    {
        var store = new ApplicationStore();
        store.Reload(WriteFile("ok.csv", ReadData("applications.csv")));
        var before = store.Current;
        // the first data row is valid, the second is broken: nothing of this file may leak into the store
        var bad = WriteFile("bad.csv", Header + "\nX001,Valid,3.0,100,10,false\nX002,Broken,abc,100,10,false\n");

        var error = Failure(store.Reload(bad));

        Assert.Equal(ErrorKind.Operational, error.Kind);
        Assert.Equal(ErrorCodes.MalformedContent, error.Code);
        Assert.Contains(bad, error.Message);
        Assert.Contains("Line 3", error.Message);
        Assert.Contains("gpa 'abc'", error.Message);
        Assert.Equal(before, store.Current);
        Assert.False(ApplicantLookup.Find(store.Current, "X001").IsSome);     // valid first row was NOT kept
    }

    [Fact]
    public void Boundary_SampleInvalidFile_ReportsEveryProblem()
    {
        var path = WriteFile("invalid.csv", ReadData("invalid.csv"));

        var error = Failure(ApplicationLoader.Load(path));

        Assert.Equal(ErrorCodes.MalformedContent, error.Code);
        Assert.Contains("7 problem(s)", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("id,name\nS1,Alpha\n")]
    public void Boundary_EmptyFileOrWrongHeader_IsMalformedContent(string content)
    {
        var path = WriteFile("x.csv", content);

        Assert.Equal(ErrorCodes.MalformedContent, Failure(ApplicationLoader.Load(path)).Code);
    }

    [Fact]
    public void Boundary_HeaderOnlyFile_IsAValidEmptyDataset_NotAnError()
    {
        var loaded = Success(ApplicationLoader.Load(WriteFile("header.csv", Header + "\n")));

        Assert.Empty(loaded);
    }

    [Fact]
    public void Boundary_SuccessfulReload_ReplacesDataCompletely()
    {
        var store = new ApplicationStore();
        store.Reload(WriteFile("ok.csv", ReadData("applications.csv")));

        var count = Success(store.Reload(WriteFile("two.csv", Header + "\nA1,Alpha,3.0,100,10,false\nA2,Beta,3.1,100,10,false\n")));

        Assert.Equal(2, count);
        Assert.False(ApplicantLookup.Find(store.Current, "S001").IsSome);
        Assert.True(ApplicantLookup.Find(store.Current, "A2").IsSome);
    }

    [Fact]
    public void Integration_FileToReport_RunsLoaderThenChain()
    {
        var store = new ApplicationStore();
        var report = store.Reload(WriteFile("ok.csv", ReadData("applications.csv")))
            .Bind(_ => ScholarshipService.BuildReport(store.Current, Settings, "merit-based", BonusPolicies.MeritBased));

        Assert.Contains("Average score: 83.60", Success(report));
    }
}
