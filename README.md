# Scholarship Application Scoring Pipeline

Assignment 6 - Handling Temporal Coupling (Introduction to Functional Programming, 2026-2027).
The project is the same runnable project as Assignment 5, extended with a validated factory, explicit states and safe transitions.

- **Team:** individual work. Yerkinbek Alikhan, IT-2505 - all logic, tests and documentation.
- **Workflow:** scholarship committee screens applications, ranks them with a bonus policy and finalizes the awards.
- **Input:** CSV `id, name, gpa, monthlyIncome, volunteerHours, hasDisciplinaryRecord` (`data/applications.csv`).
- **Domain rules:** eligible = `gpa >= 2.5` (inclusive), `0 <= gpa <= 4`, income >= 0, hours >= 0, id/name not blank, no disciplinary record. Score = `round(gpa*20) + min(hours/10, 10) + bonus` (need-based or merit-based). Top 3 are awarded.
- **Output:** ineligible applications with reasons, then ranking, awards and average score per policy.

## Task 1 - Hidden dependency (UNSAFE example, documentation only - not in the project)

Operation: **finalize the awards**. Hidden prerequisites: applications must be screened (eligibility), then scored/ranked, and only then finalized.

```csharp
// UNSAFE "before" version (NOT part of the application)
class Round {
    public List<ScholarshipApplication> Applications = new();   // anyone can add raw data
    public List<Recommendation> Ranking = new();                // empty until Rank() is called
    public bool Finalized;
    public void Screen()   { Applications.RemoveAll(a => a.Gpa < 2.5m || a.HasDisciplinaryRecord); }
    public void Rank()     { /* fills Ranking from Applications */ }
    public void Finalize() { Finalized = true; }                // never checks anything
}
var r = new Round { Applications = { new("S006","Galym",3.8m,600,15,true) } };
r.Finalize();               // order wrong: Rank() and Screen() were never called
// r.Finalized == true, Ranking is empty, the disciplined student is still in Applications
r.Rank();                   // called after Finalize or without Screen(): S006 is ranked and can be awarded
r.Rank(); r.Rank();         // repeated call silently recomputes / duplicates work
```

**Order:** Screen -> Rank -> Finalize. **What goes wrong:** Finalize succeeds with no ranking (an "invalid confirmation": nobody is awarded but the round is closed); skipping Screen lets a student with a disciplinary record or gpa 2.4 get a scholarship; calling in another order or repeating changes the result.

**Safe version (this project):** the order is carried by types. `Rank` needs a `RoundState.Collected` built from `EligibleApplication` values; `Finalize` on a state that is not ranked is refused; unchecked applications cannot reach scoring at all.

## Task 2 - Validated factory (`Eligibility.cs`)

- `EligibleApplication.Create(ScholarshipApplication, decimal minGpa)` returns `EligibilityOutcome` (Eligible with the value, or Ineligible with ALL reasons). Checks: gpa range, gpa >= minimum (inclusive), no disciplinary record, income >= 0, hours >= 0, id and name not blank. Invalid `minGpa` throws `ArgumentOutOfRangeException` (programmer error, not data error).
- **Access restriction:** `EligibleApplication` is a `sealed class` with a **private constructor** and a **get-only** property. It is deliberately not a `record`, so there is no public `with` copy that could skip the checks. The only way to get one is `Create`. A reflection test enforces this.
- **Used by the real workflow:** `Stages.Screen` (stage 1) calls the factory for every application; `Stages.ScoreApplications` and `ScoredApplication` accept only `EligibleApplication`; `AwardRound.Open` uses `Screen`.
- Parsing (`ApplicationCsvParser`) still checks the text format (numbers, flags, duplicates); the factory checks the domain rules.

## Task 3 - Distinct cases (closed record hierarchies; constructors are private so no extra case can be added)

| Type | Cases | Meaning / payload |
|------|-------|-------------------|
| `EligibilityOutcome` | `Eligible(EligibleApplication Value)` | passed all rules; carries the validated value |
| | `Ineligible(ImmutableArray<string> Reasons)` | failed; carries only the reasons |
| `RoundState` | `Collected(Eligible, Rejected)` | screened, not ranked |
| | `Ranked(Result, Rejected)` | ranking computed |
| | `Finalized(Result, Rejected)` | awards are final |
| `TransitionOutcome` | `Accepted(RoundState Next)` / `Refused(string Reason)` | next state or a clear refusal |

## Task 4 - Pattern matching in the real path

- `Stages.Screen`: `switch` over `EligibilityOutcome` (Eligible -> eligible list, Ineligible -> rejection with reasons, default throws).
- `AwardRound.Rank` / `Finalize` / `RejectionsOf`: `switch` expressions over `RoundState` with every case handled explicitly; the `_` branch only refuses.
- `RoundStore.Apply` and `Program.cs`: `switch` over `TransitionOutcome` / the current state. No casts, no `!`.

## Task 5 - Safe operations (`AwardRound.cs`)

| Call | Result |
|------|--------|
| `Open` -> `Rank` -> `Finalize` | success: `Collected` -> `Ranked` -> `Finalized` |
| `Finalize` on `Collected` (missing prerequisite / wrong order) | `Refused("must be ranked before ...")` |
| `Rank` with negative award slots (invalid input) | `Refused("Award slots must not be negative")` |
| `Rank` again (repeat) | `Refused("already ranked")`; ranking is not replaced |
| `Finalize` again (repeat) | `Accepted` with the **same** state object (documented idempotent rule) |

Transitions are pure: they take a state and return a new state; nothing is mutated. `RoundStore` is the only mutable holder (boundary): it computes the outcome first and stores the new state only when it is `Accepted`; a refusal or an exception leaves the stored state untouched.
Compile-time prevention: `Stages.ScoreApplications` cannot be called with an unchecked `ScholarshipApplication` (it needs `EligibleApplication`); such a call is not included in the code because it does not compile.

## Requirement map

| Requirement | Files / methods |
|-------------|-----------------|
| Hidden dependency before/after | this README (Task 1) |
| Validated factory, used by the app | `Eligibility.cs` `EligibleApplication.Create`; `Stages.Screen`; `AwardRound.Open` |
| Prevent unchecked construction | private constructor, sealed class, get-only property |
| Distinct cases | `EligibilityOutcome`, `RoundState`, `TransitionOutcome` |
| Pattern matching | `Stages.Screen`, `AwardRound.*`, `RoundStore.Apply`, `Program.cs` |
| Safe operations / repeated rule | `AwardRound.Rank`, `AwardRound.Finalize`, `RoundStore.Apply` |
| Functional separation | pure `Stages`/`AwardRound`; I/O only in `Program.cs` |
| Tests | `tests/ScholarshipPipeline.Tests/WorkflowTests.cs` |

## How to build, run and test

- **.NET SDK 8.0** (`dotnet --version`; the projects use `RollForward=Major`). Internet is needed on the first build to restore xUnit from NuGet.
- **Working directory:** the folder containing `ScholarshipPipeline.sln`.

```
dotnet build
dotnet run --project src/ScholarshipPipeline -- data/applications.csv both     # normal run
dotnet run --project src/ScholarshipPipeline -- data/applications.csv demo     # invalid sequence, invalid input, repeated call
dotnet run --project src/ScholarshipPipeline -- data/invalid.csv both          # parse failures
dotnet test
```

Expected normal run: `NOT ELIGIBLE: S005 - gpa 2.4 is below the minimum 2.5`, `NOT ELIGIBLE: S006 - has a disciplinary record`, then the need-based and merit-based tables from Assignment 5 (S001, S007, S003 / S001, S007, S002 awarded; averages 85.60 / 83.60).

Expected `demo` run:

```
1. Finalize before Rank   : REFUSED: The round must be ranked before it can be finalized.
2. Rank with -1 slots     : REFUSED: Award slots must not be negative (got -1).
   state still unchanged  : True
3. Rank (valid)           : ACCEPTED -> Ranked
4. Rank again             : REFUSED: The round is already ranked; finalize it.
5. Finalize               : ACCEPTED -> Finalized
6. Finalize again         : ACCEPTED -> Finalized (same state: True)
```

## Tests (xUnit, `dotnet test`)

| Required case | Tests |
|---------------|-------|
| Factory success | `Factory_ValidInput_CreatesUsableValidatedValue` |
| Factory rejection | `Factory_EachRule_RejectsInvalidInputAndReportsReason` (5 rules), `Factory_BlankId_...`, `Factory_InvalidMinGpaOrNullInput_Throws`, `Factory_NoPublicConstructors_AndGetOnlyProperties` |
| Distinct cases | `DistinctCases_BothCasesReachable_...`, `Screening_SplitsApplications...`, `RoundStates_AllThreeCasesAreReachable` |
| Normal operation | `NormalOperation_OpenRankFinalize_GivesDocumentedAwards` + A5 workflow tests |
| Missing prerequisite | `MissingPrerequisite_FinalizeBeforeRank_IsRefused`, `InvalidInput_NegativeAwardSlots_IsRefused` |
| Repeated operation | `RepeatedOperation_RankTwiceIsRefused_FinalizeTwiceReturnsSameState` |
| Boundary value | `Boundary_Factory_...` (2.5 / 4.0 / 2.49), `Boundary_Transition_ZeroAwardSlots...` + A5 boundary tests |
| State preservation | `StatePreservation_FailedTransitionsLeaveStateAndStoreUnchanged`, `StatePreservation_FailedConstruction_DoesNotChangeInputList` |

**Latest test outcome:** all 55 test cases (A5 + A6) passed, 0 failed. Note: this was checked in an offline environment with a small assertion runner because NuGet was unreachable; please run `dotnet test` once on your machine and update this line with the real xUnit count.

## Contributions

- Yerkinbek Alikhan, IT-2505 - individual project: factory, states, transitions, tests, documentation.

---

# Background: Assignment 5 documentation (still valid)

### A5 Task 1 - Workflow definition

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

### A5 Task 2 - Models and validation

- Immutable `record`s in `src/ScholarshipPipeline/Models.cs`: `ScholarshipApplication`, `ScoredApplication`, `Recommendation`, `ScholarshipResult`, `PipelineSettings`, `ParseReport`.
- Results expose `ImmutableArray<T>` (a real immutable copy, not a mutable list behind `IReadOnlyList`).
- `ApplicationCsvParser.Parse` (the application boundary) uses `TryParse`, validates ranges and rejects bad rows with a message such as `Line 3: gpa 'abc' is not a number.` Invalid values are never replaced by defaults and `!` is never used to silence null warnings.
- `null` input -> `ArgumentNullException` (missing); header only -> valid empty dataset; no header at all -> error; `0` and `false` are valid values.
- Limitation: CSV fields cannot contain commas or quotes.

### A5 Task 3 - Stages (`Stages.cs`, composed in `ScholarshipWorkflow.Run`)

| Stage | Input -> Output | Rule | Example |
|-------|-----------------|------|---------|
| 1 `SelectEligible` | `IReadOnlyList<ScholarshipApplication>`, `decimal minGpa` -> `ImmutableArray<ScholarshipApplication>` | gpa >= minGpa and no disciplinary record | 7 records -> S001, S002, S003, S004, S007 |
| 2 `ScoreApplications` | `IReadOnlyList<ScholarshipApplication>`, `Func<ScholarshipApplication,int>` -> `ImmutableArray<ScoredApplication>` | base + volunteer + bonusPolicy | S003 need-based -> 89 |
| 3 `BuildResult` | `IReadOnlyList<ScoredApplication>`, `int awardSlots` -> `ScholarshipResult` | sort, rank, mark awarded, summarise | scores 108,96,89,75,60 -> 3 awarded, avg 85.60 |

Trace of S003 (Dana, gpa 3.2, income 300, 50 h), need-based: parsed -> `ScholarshipApplication` -> passes stage 1 (3.2 >= 2.5) -> stage 2: 64 + 5 + 20 = `ScoredApplication(S003, 89)` -> stage 3: rank 3, `Recommendation(3, "S003", "Dana", 89, Awarded: true)`.

### A5 Task 4 - Configurable rule

- Higher-order function: `Stages.ScoreApplications(eligible, Func<ScholarshipApplication,int> bonusPolicy)`; the delegate is invoked in `.Select(a => ... + bonusPolicy(a))`.
- Policies: `BonusPolicies.NeedBased` and `BonusPolicies.MeritBased`.
- The policy is chosen in `Program.cs` (`SelectPolicies`) and passed as an argument to `ScholarshipWorkflow.Run`. No global mutable setting exists.

### A5 Task 5 - Purity

Stages and `ScholarshipWorkflow.Run` never read the console, write files, use a clock/random value or touch static mutable state, and never modify the caller's collection. File reading and printing live only in `Program.cs`; `ResultFormatter` is a pure function that returns a string.

### A5 Requirement map

| Requirement | Where |
|-------------|-------|
| Typed immutable models | `Models.cs` |
| Parsing / validation | `ApplicationCsvParser.cs` |
| >= 3 pure stages | `Stages.cs` (`SelectEligible`, `ScoreApplications`, `BuildResult`) |
| Visible composition | `ScholarshipWorkflow.Run` |
| Higher-order function + two policies | `Stages.ScoreApplications`, `BonusPolicies.cs` |
| I/O separated from core | `Program.cs`, `ResultFormatter.cs` |
| Tests | `tests/ScholarshipPipeline.Tests/WorkflowTests.cs` |

### A5 build notes

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

### A5 tests (xUnit, `dotnet test`)

| Required case | Test(s) | Expected |
|---------------|---------|----------|
| Normal workflow | `NormalWorkflow_NeedBased_...`, `NormalWorkflow_MeritBased_...` | rankings and scores from the tables above |
| Empty dataset | `EmptyDataset_HeaderOnly_...`, `EmptyList_AllRejectedByStage1_...` | empty ranking, counts 0, average 0 |
| Boundary | `Boundary_GpaExactlyMinimum_...`, `Boundary_NeedBasedBonus_...`, `Boundary_MeritBasedBonus_...`, `Boundary_AwardSlots_...`, `Boundary_VolunteerPoints_...` | exact thresholds are inclusive |
| Invalid / missing | `InvalidInput_File_...`, `InvalidInput_SingleRow_...` (10 cases), `MissingInput_NullAndEmptyAreRejected`, `LegitimateZeroAndFalse_AreAccepted` | rejected with message, no fabricated result |
| Two policies | `TwoPolicies_SameInput_DifferInAwardedSet` | S003 vs S002 |
| Purity and stages | `Purity_...`, `Stage1_...`, `Stage2_...`, `Stage3_...` | caller data unchanged |

**Latest test outcome:** Passed: 35, Failed: 0

### A5 team

- Yerkinbek Alikhan, IT-2505 - individual project: all logic, tests and documentation.
