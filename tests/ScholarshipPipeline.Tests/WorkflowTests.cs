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
        var eligible = Stages.SelectEligible(apps, 2.5m);
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
        var scored = Stages.ScoreApplications(new[] { App("a"), App("b") }, _ => 0);
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
        var eligible = Stages.SelectEligible(list, 2.5m);

        Assert.Equal(new[] { "S001", "S002", "S003", "S004", "S007" }, eligible.Select(a => a.Id).ToArray());
        list.Clear();                                                  // caller changes its own list later
        Assert.Equal(5, eligible.Length);                              // result is unaffected
    }

    [Fact]
    public void Stage2_CallsDelegateOncePerApplication_AndAddsItsBonus()
    {
        var calls = 0;
        var input = new[] { App("a", gpa: 3.0m, hours: 25), App("b", gpa: 3.0m, hours: 25) };

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
            new ScoredApplication(App("B", gpa: 3.0m), 50),
            new ScoredApplication(App("A", gpa: 3.0m), 50),
            new ScoredApplication(App("C", gpa: 3.5m), 50),
        };

        var result = Stages.BuildResult(scored, 2);

        Assert.Equal(new[] { "C", "A", "B" }, Ids(result.Ranking));
        Assert.Equal(new[] { "C", "A" }, AwardedIds(result));
        Assert.Equal("B", scored[0].Application.Id);                   // input order unchanged
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
}
