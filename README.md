# Scholarship Application Scoring Pipeline

Assignment 5 - Composing Functions Using Pipelining (Introduction to Functional Programming, 2026-2027)

## Task 1 - Workflow definition

- **Topic:** scoring scholarship applications and recommending who receives a scholarship.
- **Intended users:** a university scholarship committee.
- **Team:** individual work. Name / group: `<FILL IN: your name, group>`
- **Input fields (CSV):** `id, name, gpa, monthlyIncome, volunteerHours, hasDisciplinaryRecord`
- **Business rules:**
  1. Eligible = `gpa >= 2.5` (inclusive) AND no disciplinary record.
  2. Score = `round(gpa * 20)` + `min(volunteerHours / 10, 10)` + **bonus** (bonus depends on the policy).
  3. Policy A "need-based": income <= 500 -> +20; income <= 1000 -> +10; otherwise 0.
  4. Policy B "merit-based": gpa >= 3.8 -> +20; gpa >= 3.5 -> +10; otherwise 0.
  5. Ranking: score (desc), then gpa (desc), then id (ordinal). The first 3 (`AwardSlots`) are awarded.
- **Expected result:** ordered recommendations + summary (eligible count, awarded count, average score of eligible).

### Dataset `data/applications.csv` and expected results (written BEFORE running)

| Id | gpa | income | hours | disciplinary | Why in the dataset |
|----|-----|--------|-------|--------------|--------------------|
| S001 | 3.9 | 400 | 120 | no | accepted, volunteer cap (12 -> 10) |
| S002 | 3.6 | 1500 | 30 | no | accepted, differs between policies |
| S003 | 3.2 | 300 | 50 | no | accepted, differs between policies |
| S004 | 2.5 | 800 | 0 | no | **boundary**: gpa exactly at minimum; zero hours is valid |
| S005 | 2.4 | 200 | 200 | no | **rejected**: gpa below minimum |
| S006 | 3.8 | 600 | 15 | yes | **rejected**: disciplinary record |
| S007 | 3.8 | 1000 | 100 | no | **boundary**: income exactly 1000, gpa exactly 3.8 |

| Id | need-based score | merit-based score |
|----|------------------|-------------------|
| S001 | 78+10+20 = **108** | 78+10+20 = **108** |
| S007 | 76+10+10 = **96** | 76+10+20 = **106** |
| S003 | 64+5+20 = **89** | 64+5+0 = **69** |
| S002 | 72+3+0 = **75** | 72+3+10 = **85** |
| S004 | 50+0+10 = **60** | 50+0+0 = **50** |

- Need-based ranking: S001, S007, S003, S002, S004; awarded: S001, S007, **S003**; average 85.60.
- Merit-based ranking: S001, S007, S002, S003, S004; awarded: S001, S007, **S002**; average 83.60.
- The policy changes who gets the third scholarship (S003 vs S002); the pipeline code is identical.

## Task 2 - Models and validation

- Immutable `record`s in `src/ScholarshipPipeline/Models.cs`: `ScholarshipApplication`, `ScoredApplication`, `Recommendation`, `ScholarshipResult`, `PipelineSettings`, `ParseReport`.
- Results expose `ImmutableArray<T>` (a real immutable copy, not a mutable list behind `IReadOnlyList`).
- `ApplicationCsvParser.Parse` (the application boundary) uses `TryParse`, validates ranges and rejects bad rows with a message such as `Line 3: gpa 'abc' is not a number.` Invalid values are never replaced by defaults and `!` is never used to silence null warnings.
- `null` input -> `ArgumentNullException` (missing); header only -> valid empty dataset; no header at all -> error; `0` and `false` are valid values.
- Limitation: CSV fields cannot contain commas or quotes.

## Task 3 - Stages (`Stages.cs`, composed in `ScholarshipWorkflow.Run`)

| Stage | Input -> Output | Rule | Example |
|-------|-----------------|------|---------|
| 1 `SelectEligible` | `IReadOnlyList<ScholarshipApplication>`, `decimal minGpa` -> `ImmutableArray<ScholarshipApplication>` | gpa >= minGpa and no disciplinary record | 7 records -> S001, S002, S003, S004, S007 |
| 2 `ScoreApplications` | `IReadOnlyList<ScholarshipApplication>`, `Func<ScholarshipApplication,int>` -> `ImmutableArray<ScoredApplication>` | base + volunteer + bonusPolicy | S003 need-based -> 89 |
| 3 `BuildResult` | `IReadOnlyList<ScoredApplication>`, `int awardSlots` -> `ScholarshipResult` | sort, rank, mark awarded, summarise | scores 108,96,89,75,60 -> 3 awarded, avg 85.60 |

Trace of S003 (Dana, gpa 3.2, income 300, 50 h), need-based: parsed -> `ScholarshipApplication` -> passes stage 1 (3.2 >= 2.5) -> stage 2: 64 + 5 + 20 = `ScoredApplication(S003, 89)` -> stage 3: rank 3, `Recommendation(3, "S003", "Dana", 89, Awarded: true)`.

## Task 4 - Configurable rule

- Higher-order function: `Stages.ScoreApplications(eligible, Func<ScholarshipApplication,int> bonusPolicy)`; the delegate is invoked in `.Select(a => ... + bonusPolicy(a))`.
- Policies: `BonusPolicies.NeedBased` and `BonusPolicies.MeritBased`.
- The policy is chosen in `Program.cs` (`SelectPolicies`) and passed as an argument to `ScholarshipWorkflow.Run`. No global mutable setting exists.

## Task 5 - Purity

Stages and `ScholarshipWorkflow.Run` never read the console, write files, use a clock/random value or touch static mutable state, and never modify the caller's collection. File reading and printing live only in `Program.cs`; `ResultFormatter` is a pure function that returns a string.

## Requirement map

| Requirement | Where |
|-------------|-------|
| Typed immutable models | `Models.cs` |
| Parsing / validation | `ApplicationCsvParser.cs` |
| >= 3 pure stages | `Stages.cs` (`SelectEligible`, `ScoreApplications`, `BuildResult`) |
| Visible composition | `ScholarshipWorkflow.Run` |
| Higher-order function + two policies | `Stages.ScoreApplications`, `BonusPolicies.cs` |
| I/O separated from core | `Program.cs`, `ResultFormatter.cs` |
| Tests | `tests/ScholarshipPipeline.Tests/WorkflowTests.cs` |

## How to build, run and test

- Prerequisite: .NET SDK 8.0 or newer (`dotnet --version`). Packages are restored from NuGet (internet needed on first build).
- Working directory: the folder that contains `ScholarshipPipeline.sln`.

```
dotnet build
dotnet run --project src/ScholarshipPipeline -- data/applications.csv both
dotnet run --project src/ScholarshipPipeline -- data/applications.csv need
dotnet run --project src/ScholarshipPipeline -- data/applications.csv merit
dotnet run --project src/ScholarshipPipeline -- data/invalid.csv both
dotnet run --project src/ScholarshipPipeline -- data/empty.csv both
dotnet test
```

### Expected output of the normal run (`data/applications.csv both`)

```
Parsed 7 valid application(s).

=== Policy: need-based ===
Eligible: 5 | Awarded: 3 | Average score: 85.60
Rank  Id    Name        Score  Awarded
1     S001  Aigerim     108    yes
2     S007  Hanna       96     yes
3     S003  Dana        89     yes
4     S002  Bekzat      75     no
5     S004  Erlan       60     no

=== Policy: merit-based ===
Eligible: 5 | Awarded: 3 | Average score: 83.60
Rank  Id    Name        Score  Awarded
1     S001  Aigerim     108    yes
2     S007  Hanna       106    yes
3     S002  Bekzat      85     yes
4     S003  Dana        69     no
5     S004  Erlan       50     no
```

Failure demo (`data/invalid.csv`): 7 lines starting with `REJECTED:` (lines 3-9), 1 valid application parsed, exit code 1.
Empty demo (`data/empty.csv`): `Parsed 0 valid application(s).`, then `(no eligible applications)` for each policy.

## Tests (xUnit, `dotnet test`)

| Required case | Test(s) | Expected |
|---------------|---------|----------|
| Normal workflow | `NormalWorkflow_NeedBased_...`, `NormalWorkflow_MeritBased_...` | rankings and scores from the tables above |
| Empty dataset | `EmptyDataset_HeaderOnly_...`, `EmptyList_AllRejectedByStage1_...` | empty ranking, counts 0, average 0 |
| Boundary | `Boundary_GpaExactlyMinimum_...`, `Boundary_NeedBasedBonus_...`, `Boundary_MeritBasedBonus_...`, `Boundary_AwardSlots_...`, `Boundary_VolunteerPoints_...` | exact thresholds are inclusive |
| Invalid / missing | `InvalidInput_File_...`, `InvalidInput_SingleRow_...` (10 cases), `MissingInput_NullAndEmptyAreRejected`, `LegitimateZeroAndFalse_AreAccepted` | rejected with message, no fabricated result |
| Two policies | `TwoPolicies_SameInput_DifferInAwardedSet` | S003 vs S002 |
| Purity and stages | `Purity_...`, `Stage1_...`, `Stage2_...`, `Stage3_...` | caller data unchanged |

**Latest test outcome:** Passed: 35, Failed: 0

## Team / contributions

- Yerkinbek Alikhan, IT-2505 - individual project: all logic, tests and documentation.
