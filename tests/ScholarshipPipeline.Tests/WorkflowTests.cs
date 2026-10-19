using System.Collections.Immutable;

namespace ScholarshipPipeline.Tests;

public class WorkflowTests
{
    private static readonly PipelineSettings Settings = new(MinGpa: 2.5m, AwardSlots: 3);
    private const string Header = "id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord";

    // ---------- helpers ----------
    private static string ReadData(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file));

    private static ImmutableArray<ScholarshipApplication> SampleApplications()
    {
        var report = ApplicationCsvParser.Parse(ReadData("applications.csv"));
        Assert.False(report.HasErrors);
        return report.Valid;
    }

    private static ScholarshipApplication App(
        string id, decimal gpa = 3.0m, decimal income = 2000m, int hours = 0, bool disciplinary = false) =>
        new(id, "Name " + id, gpa, income, hours, disciplinary);

    private static EligibleApplication Elig(
        string id, decimal gpa = 3.0m, decimal income = 2000m, int hours = 0) =>
        EligibleApplication.Create(App(id, gpa, income, hours), 0m) switch
        {
            EligibilityOutcome.Eligible e => e.Value,
            _ => throw new InvalidOperationException("test helper: application should be eligible")
        };

    private static string[] Ids(IEnumerable<Recommendation> rs) => rs.Select(r => r.Id).ToArray();
    private static int[] Scores(IEnumerable<Recommendation> rs) => rs.Select(r => r.Score).ToArray();
    private static string[] AwardedIds(ScholarshipResult r) => r.Ranking.Where(x => x.Awarded).Select(x => x.Id).ToArray();

    // ---------- NORMAL WORKFLOW ----------
    [Fact]
    public void NormalWorkflow_NeedBased_ProducesDocumentedRanking()
    {
        var result = ScholarshipWorkflow.Run(SampleApplications(), Settings, BonusPolicies.NeedBased);

        Assert.Equal(new[] { "S001", "S007", "S003", "S002", "S004" }, Ids(result.Ranking));
        Assert.Equal(new[] { 108, 96, 89, 75, 60 }, Scores(result.Ranking));
        Assert.Equal(new[] { "S001", "S007", "S003" }, AwardedIds(result));
        Assert.Equal(5, result.EligibleCount);
        Assert.Equal(3, result.AwardedCount);
        Assert.Equal(85.6m, result.AverageScore);
    }

    [Fact]
    public void NormalWorkflow_MeritBased_ProducesDocumentedRanking()
    {
        var result = ScholarshipWorkflow.Run(SampleApplications(), Settings, BonusPolicies.MeritBased);

        Assert.Equal(new[] { "S001", "S007", "S002", "S003", "S004" }, Ids(result.Ranking));
        Assert.Equal(new[] { 108, 106, 85, 69, 50 }, Scores(result.Ranking));
        Assert.Equal(new[] { "S001", "S007", "S002" }, AwardedIds(result));
        Assert.Equal(83.6m, result.AverageScore);
    }

    // ---------- TWO POLICIES ----------
    [Fact]
    public void TwoPolicies_SameInput_DifferInAwardedSet()
    {
        var input = SampleApplications();
        var need = ScholarshipWorkflow.Run(input, Settings, BonusPolicies.NeedBased);
        var merit = ScholarshipWorkflow.Run(input, Settings, BonusPolicies.MeritBased);

        Assert.Contains("S003", AwardedIds(need));
        Assert.DoesNotContain("S002", AwardedIds(need));
        Assert.Contains("S002", AwardedIds(merit));
        Assert.DoesNotContain("S003", AwardedIds(merit));
        Assert.NotEqual(AwardedIds(need), AwardedIds(merit));
    }

    // ---------- EMPTY DATASET ----------
    [Fact]
    public void EmptyDataset_HeaderOnly_IsValidAndGivesEmptyResult()
    {
        var report = ApplicationCsvParser.Parse(ReadData("empty.csv"));
        Assert.False(report.HasErrors);
        Assert.Empty(report.Valid);

        var result = ScholarshipWorkflow.Run(report.Valid, Settings, BonusPolicies.NeedBased);
        Assert.Empty(result.Ranking);
        Assert.Equal(0, result.EligibleCount);
        Assert.Equal(0, result.AwardedCount);
        Assert.Equal(0m, result.AverageScore);
    }

    [Fact]
    public void EmptyList_AllRejectedByStage1_GivesEmptyResult()
    {
        var onlyIneligible = new[] { App("X1", gpa: 1.0m), App("X2", disciplinary: true) };
        var result = ScholarshipWorkflow.Run(onlyIneligible, Settings, BonusPolicies.MeritBased);
        Assert.Empty(result.Ranking);
        Assert.Equal(0, result.AwardedCount);
    }

    // ---------- BOUNDARY VALUE ----------
    [Fact]
    public void Boundary_GpaExactlyMinimum_IsIncluded()
    {
        var apps = new[] { App("at", gpa: 2.5m), App("below", gpa: 2.49m) };
        var eligible = Stages.Screen(apps, 2.5m).Eligible;
        Assert.Equal(new[] { "at" }, eligible.Select(a => a.Id).ToArray());
    }

    [Theory]
    [InlineData(500, 20)]
    [InlineData(500.01, 10)]
    [InlineData(1000, 10)]
    [InlineData(1000.01, 0)]
    [InlineData(0, 20)]
    public void Boundary_NeedBasedBonus_FollowsIncomeThresholds(double income, int expectedBonus) =>
        Assert.Equal(expectedBonus, BonusPolicies.NeedBased(App("a", income: (decimal)income)));

    [Theory]
    [InlineData(3.8, 20)]
    [InlineData(3.79, 10)]
    [InlineData(3.5, 10)]
    [InlineData(3.49, 0)]
    public void Boundary_MeritBasedBonus_FollowsGpaThresholds(double gpa, int expectedBonus) =>
        Assert.Equal(expectedBonus, BonusPolicies.MeritBased(App("a", gpa: (decimal)gpa)));

    [Fact]
    public void Boundary_AwardSlots_ZeroAndMoreThanCandidates()
    {
        var scored = Stages.ScoreApplications(new[] { Elig("a"), Elig("b") }, _ => 0);
        Assert.Equal(0, Stages.BuildResult(scored, 0).AwardedCount);
        Assert.Equal(2, Stages.BuildResult(scored, 10).AwardedCount);
    }

    [Fact]
    public void Boundary_VolunteerPoints_AreCappedAtTen()
    {
        Assert.Equal(9, Stages.VolunteerPoints(App("a", hours: 99)));
        Assert.Equal(10, Stages.VolunteerPoints(App("a", hours: 100)));
        Assert.Equal(10, Stages.VolunteerPoints(App("a", hours: 5000)));
    }

    // ---------- INVALID AND MISSING INPUT ----------
    [Fact]
    public void InvalidInput_File_RejectsBadRowsWithClearMessages()
    {
        var report = ApplicationCsvParser.Parse(ReadData("invalid.csv"));

        Assert.Equal("S101", Assert.Single(report.Valid).Id);
        Assert.Equal(7, report.Errors.Length);
        Assert.Contains("Line 3", report.Errors[0]);
        Assert.Contains("gpa", report.Errors[0]);
        Assert.Contains("duplicate id", report.Errors[6]);
    }

    [Theory]
    [InlineData("S1,A,abc,100,0,false", "gpa")]
    [InlineData("S1,A,4.1,100,0,false", "gpa")]
    [InlineData("S1,A,3.0,-1,0,false", "monthlyIncome")]
    [InlineData("S1,A,3.0,,0,false", "monthlyIncome")]
    [InlineData("S1,A,3.0,100,1.5,false", "volunteerHours")]
    [InlineData("S1,A,3.0,100,-2,false", "volunteerHours")]
    [InlineData("S1,A,3.0,100,0,yes", "hasDisciplinaryRecord")]
    [InlineData("S1,,3.0,100,0,false", "name")]
    [InlineData(",A,3.0,100,0,false", "id")]
    [InlineData("S1,A,3.0,100", "expected 6 fields")]
    public void InvalidInput_SingleRow_IsRejectedAndNeverFabricated(string row, string expectedFragment)
    {
        var report = ApplicationCsvParser.Parse(Header + "\n" + row);

        Assert.Empty(report.Valid);
        Assert.Contains(expectedFragment, Assert.Single(report.Errors));
    }

    [Fact]
    public void MissingInput_NullAndEmptyAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ApplicationCsvParser.Parse(null));
        Assert.Throws<ArgumentNullException>(() =>
            ScholarshipWorkflow.Run(null!, Settings, BonusPolicies.NeedBased));
        Assert.Throws<ArgumentNullException>(() =>
            ScholarshipWorkflow.Run(Array.Empty<ScholarshipApplication>(), Settings, null!));
        Assert.Throws<ArgumentNullException>(() =>
            ScholarshipWorkflow.Run(Array.Empty<ScholarshipApplication>(), null!, BonusPolicies.NeedBased));

        var blank = ApplicationCsvParser.Parse("   \n  ");           // no header at all != empty dataset
        Assert.True(blank.HasErrors);
        Assert.Empty(blank.Valid);

        var wrongHeader = ApplicationCsvParser.Parse("a,b,c\nS1,A,3.0");
        Assert.True(wrongHeader.HasErrors);
        Assert.Empty(wrongHeader.Valid);
    }

    [Fact]
    public void LegitimateZeroAndFalse_AreAccepted()
    {
        var report = ApplicationCsvParser.Parse(Header + "\nZ1,Zero,0,0,0,false");

        Assert.False(report.HasErrors);
        var a = Assert.Single(report.Valid);
        Assert.Equal(0m, a.Gpa);
        Assert.Equal(0m, a.MonthlyFamilyIncome);
        Assert.Equal(0, a.VolunteerHours);
        Assert.False(a.HasDisciplinaryRecord);
    }

    // ---------- PURITY AND STAGES ----------
    [Fact]
    public void Purity_Workflow_DoesNotMutateCallerCollection_AndIsRepeatable()
    {
        var list = SampleApplications().ToList();
        var snapshot = list.ToArray();

        var first = ScholarshipWorkflow.Run(list, Settings, BonusPolicies.NeedBased);
        var second = ScholarshipWorkflow.Run(list, Settings, BonusPolicies.NeedBased);
        ScholarshipWorkflow.Run(list, Settings, BonusPolicies.MeritBased);

        Assert.True(snapshot.SequenceEqual(list));                     // same items, same order
        Assert.True(first.Ranking.SequenceEqual(second.Ranking));      // same input + policy -> same output
        Assert.Equal(first.AverageScore, second.AverageScore);
    }

    [Fact]
    public void Stage1_ReturnsIndependentCopy_AndDropsIneligible()
    {
        var list = SampleApplications().ToList();
        var eligible = Stages.Screen(list, 2.5m).Eligible;

        Assert.Equal(new[] { "S001", "S002", "S003", "S004", "S007" }, eligible.Select(a => a.Id).ToArray());
        list.Clear();                                                  // caller changes its own list later
        Assert.Equal(5, eligible.Length);                              // result is unaffected
    }

    [Fact]
    public void Stage2_CallsDelegateOncePerApplication_AndAddsItsBonus()
    {
        var calls = 0;
        var input = new[] { Elig("a", gpa: 3.0m, hours: 25), Elig("b", gpa: 3.0m, hours: 25) };

        var scored = Stages.ScoreApplications(input, _ => { calls++; return 7; });

        Assert.Equal(2, calls);
        Assert.All(scored, s => Assert.Equal(60 + 2 + 7, s.Score));    // 3.0*20 + 25/10 + bonus
        Assert.Equal("a", input[0].Id);                                // input untouched
    }

    [Fact]
    public void Stage3_TieBreaks_ByGpaThenId_AndMarksAwarded()
    {
        var scored = new[]
        {
            new ScoredApplication(Elig("B", gpa: 3.0m), 50),
            new ScoredApplication(Elig("A", gpa: 3.0m), 50),
            new ScoredApplication(Elig("C", gpa: 3.5m), 50),
        };

        var result = Stages.BuildResult(scored, 2);

        Assert.Equal(new[] { "C", "A", "B" }, Ids(result.Ranking));
        Assert.Equal(new[] { "C", "A" }, AwardedIds(result));
        Assert.Equal("B", scored[0].Candidate.Id);                   // input order unchanged
        Assert.Equal(50m, result.AverageScore);
    }

    [Fact]
    public void Formatter_ShowsPolicyNameAndRankedRows()
    {
        var result = ScholarshipWorkflow.Run(SampleApplications(), Settings, BonusPolicies.NeedBased);
        var text = ResultFormatter.Format("need-based", result);

        Assert.Contains("need-based", text);
        Assert.Contains("S001", text);
        Assert.Contains("Average score: 85.60", text);
    }

    // ================= Assignment 6: validated factory, explicit cases, safe transitions =================

    // ---------- FACTORY SUCCESS ----------
    [Fact]
    public void Factory_ValidInput_CreatesUsableValidatedValue()
    {
        var outcome = EligibleApplication.Create(App("S1", gpa: 3.2m, income: 300m, hours: 50), 2.5m);

        var eligible = Assert.IsType<EligibilityOutcome.Eligible>(outcome);
        Assert.Equal("S1", eligible.Value.Id);
        Assert.Equal(3.2m, eligible.Value.Gpa);
        Assert.Equal(50, eligible.Value.Application.VolunteerHours);
    }

    // ---------- FACTORY REJECTION (each rule) ----------
    [Theory]
    [InlineData(2.4, 500, 10, false, "below the minimum")]
    [InlineData(3.0, 500, 10, true, "disciplinary")]
    [InlineData(4.5, 500, 10, false, "between 0.0 and 4.0")]
    [InlineData(3.0, -1, 10, false, "monthlyIncome")]
    [InlineData(3.0, 500, -3, false, "volunteerHours")]
    public void Factory_EachRule_RejectsInvalidInputAndReportsReason(
        double gpa, double income, int hours, bool disciplinary, string expectedReason)
    {
        var raw = new ScholarshipApplication("S1", "A", (decimal)gpa, (decimal)income, hours, disciplinary);

        var outcome = EligibleApplication.Create(raw, 2.5m);

        var ineligible = Assert.IsType<EligibilityOutcome.Ineligible>(outcome);
        Assert.Contains(ineligible.Reasons, r => r.Contains(expectedReason));
    }

    [Fact]
    public void Factory_BlankId_IsRejected_AndAllReasonsAreCollected()
    {
        var raw = new ScholarshipApplication(" ", "A", 1.0m, 0m, 0, true);
        var ineligible = Assert.IsType<EligibilityOutcome.Ineligible>(EligibleApplication.Create(raw, 2.5m));

        Assert.Equal(3, ineligible.Reasons.Length);   // id, gpa below minimum, disciplinary record
        Assert.Contains("id is missing", ineligible.Reasons);
    }

    [Fact]
    public void Factory_InvalidMinGpaOrNullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EligibleApplication.Create(null!, 2.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EligibleApplication.Create(App("a"), 4.1m));
    }

    // ---------- UNCHECKED CONSTRUCTION IS NOT POSSIBLE ----------
    [Fact]
    public void Factory_NoPublicConstructors_AndGetOnlyProperties()
    {
        Assert.Empty(typeof(EligibleApplication).GetConstructors());                 // only Create(...) builds one
        Assert.True(typeof(EligibleApplication).IsSealed);
        Assert.All(typeof(EligibleApplication).GetProperties(),
            p => Assert.True(p.SetMethod is null, p.Name + " must be get-only"));
        // the unions are closed: nobody outside can add a case
        Assert.Empty(typeof(EligibilityOutcome).GetConstructors());
        Assert.Empty(typeof(RoundState).GetConstructors());
        Assert.Empty(typeof(TransitionOutcome).GetConstructors());
    }

    // ---------- DISTINCT CASES + PATTERN MATCHING ----------
    [Fact]
    public void DistinctCases_BothCasesReachable_AndMatchedExplicitly()
    {
        static string Match(EligibilityOutcome o) => o switch
        {
            EligibilityOutcome.Eligible e => "eligible:" + e.Value.Id,
            EligibilityOutcome.Ineligible i => "ineligible:" + string.Join("|", i.Reasons),
            _ => "unknown"
        };

        Assert.Equal("eligible:ok", Match(EligibleApplication.Create(App("ok"), 2.5m)));
        Assert.Equal("ineligible:has a disciplinary record",
            Match(EligibleApplication.Create(App("bad", disciplinary: true), 2.5m)));
    }

    [Fact]
    public void Screening_SplitsApplicationsIntoEligibleAndRejectedWithReasons()
    {
        var screening = Stages.Screen(SampleApplications(), 2.5m);

        Assert.Equal(new[] { "S001", "S002", "S003", "S004", "S007" }, screening.Eligible.Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "S005", "S006" }, screening.Rejected.Select(r => r.Id).ToArray());
        Assert.Contains("below the minimum 2.5", Assert.Single(screening.Rejected[0].Reasons));
        Assert.Contains("disciplinary", Assert.Single(screening.Rejected[1].Reasons));
    }

    [Fact]
    public void RoundStates_AllThreeCasesAreReachable()
    {
        var collected = AwardRound.Open(SampleApplications(), 2.5m);
        var ranked = Accepted(AwardRound.Rank(collected, BonusPolicies.NeedBased, 3));
        var finalized = Accepted(AwardRound.Finalize(ranked));

        Assert.IsType<RoundState.Collected>(collected);
        Assert.IsType<RoundState.Ranked>(ranked);
        Assert.IsType<RoundState.Finalized>(finalized);
        Assert.Equal(2, AwardRound.RejectionsOf(finalized).Length);   // rejections travel with every case
    }

    // ---------- NORMAL OPERATION (validated value used by the real workflow) ----------
    [Fact]
    public void NormalOperation_OpenRankFinalize_GivesDocumentedAwards()
    {
        var store = new RoundStore(AwardRound.Open(SampleApplications(), 2.5m));

        Assert.IsType<TransitionOutcome.Accepted>(store.Apply(s => AwardRound.Rank(s, BonusPolicies.NeedBased, 3)));
        Assert.IsType<TransitionOutcome.Accepted>(store.Apply(AwardRound.Finalize));

        var finalized = Assert.IsType<RoundState.Finalized>(store.Current);
        Assert.Equal(new[] { "S001", "S007", "S003" }, AwardedIds(finalized.Result));
        Assert.Equal(85.6m, finalized.Result.AverageScore);
    }

    // ---------- MISSING PREREQUISITE / INVALID SEQUENCE ----------
    [Fact]
    public void MissingPrerequisite_FinalizeBeforeRank_IsRefused()
    {
        var collected = AwardRound.Open(SampleApplications(), 2.5m);

        var refused = Assert.IsType<TransitionOutcome.Refused>(AwardRound.Finalize(collected));

        Assert.Contains("ranked before", refused.Reason);
    }

    [Fact]
    public void InvalidInput_NegativeAwardSlots_IsRefused()
    {
        var collected = AwardRound.Open(SampleApplications(), 2.5m);
        var refused = Assert.IsType<TransitionOutcome.Refused>(AwardRound.Rank(collected, BonusPolicies.NeedBased, -1));
        Assert.Contains("negative", refused.Reason);
    }

    // ---------- REPEATED OPERATION ----------
    [Fact]
    public void RepeatedOperation_RankTwiceIsRefused_FinalizeTwiceReturnsSameState()
    {
        var store = new RoundStore(AwardRound.Open(SampleApplications(), 2.5m));
        store.Apply(s => AwardRound.Rank(s, BonusPolicies.NeedBased, 3));
        var ranked = store.Current;

        Assert.IsType<TransitionOutcome.Refused>(store.Apply(s => AwardRound.Rank(s, BonusPolicies.MeritBased, 3)));
        Assert.Same(ranked, store.Current);                      // second Rank did not replace the ranking

        store.Apply(AwardRound.Finalize);
        var finalized = store.Current;
        var again = Assert.IsType<TransitionOutcome.Accepted>(store.Apply(AwardRound.Finalize));

        Assert.Same(finalized, again.Next);                      // documented safe rule: same outcome
        Assert.Same(finalized, store.Current);
        Assert.IsType<TransitionOutcome.Refused>(store.Apply(s => AwardRound.Rank(s, BonusPolicies.NeedBased, 3)));
    }

    // ---------- BOUNDARY VALUE (exact limits of factory and transition) ----------
    [Fact]
    public void Boundary_Factory_GpaAtMinimumAndAtMaximumAccepted_JustBelowRejected()
    {
        Assert.IsType<EligibilityOutcome.Eligible>(EligibleApplication.Create(App("a", gpa: 2.5m), 2.5m));
        Assert.IsType<EligibilityOutcome.Eligible>(EligibleApplication.Create(App("b", gpa: 4.0m), 2.5m));
        Assert.IsType<EligibilityOutcome.Ineligible>(EligibleApplication.Create(App("c", gpa: 2.49m), 2.5m));
        Assert.IsType<EligibilityOutcome.Eligible>(EligibleApplication.Create(App("d", hours: 0, income: 0m), 2.5m));
    }

    [Fact]
    public void Boundary_Transition_ZeroAwardSlotsIsAccepted_AndAwardsNobody()
    {
        var ranked = Assert.IsType<RoundState.Ranked>(
            Accepted(AwardRound.Rank(AwardRound.Open(SampleApplications(), 2.5m), BonusPolicies.NeedBased, 0)));
        Assert.Equal(0, ranked.Result.AwardedCount);
        Assert.Equal(5, ranked.Result.Ranking.Length);
    }

    // ---------- STATE PRESERVATION ----------
    [Fact]
    public void StatePreservation_FailedTransitionsLeaveStateAndStoreUnchanged()
    {
        var collected = Assert.IsType<RoundState.Collected>(AwardRound.Open(SampleApplications(), 2.5m));
        var eligibleBefore = collected.Eligible.Select(e => e.Id).ToArray();
        var store = new RoundStore(collected);

        store.Apply(AwardRound.Finalize);                                              // refused: not ranked
        store.Apply(s => AwardRound.Rank(s, BonusPolicies.NeedBased, -5));             // refused: bad slots
        Assert.Throws<InvalidOperationException>(() =>
            store.Apply(s => AwardRound.Rank(s, _ => throw new InvalidOperationException("policy failed"), 3)));

        Assert.Same(collected, store.Current);                                         // store not changed
        Assert.Equal(eligibleBefore, collected.Eligible.Select(e => e.Id).ToArray());  // state content not changed
        Assert.Equal(2, collected.Rejected.Length);
    }

    [Fact]
    public void StatePreservation_FailedConstruction_DoesNotChangeInputList()
    {
        var list = new List<ScholarshipApplication> { App("ok"), App("bad", gpa: 1.0m) };
        var snapshot = list.ToArray();

        var screening = Stages.Screen(list, 2.5m);

        Assert.Equal(new[] { "ok" }, screening.Eligible.Select(e => e.Id).ToArray());
        Assert.Equal("bad", Assert.Single(screening.Rejected).Id);
        Assert.True(snapshot.SequenceEqual(list));
    }

    private static RoundState Accepted(TransitionOutcome outcome) =>
        outcome switch
        {
            TransitionOutcome.Accepted a => a.Next,
            _ => throw new InvalidOperationException("test helper: transition should be accepted")
        };
}
