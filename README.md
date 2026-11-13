# Scholarship Pipeline

Final project for **Introduction to Functional Programming** (academic year 2026-2027).

A C# console application for a university scholarship committee. It loads applications from a CSV file, checks them
against the eligibility rules, scores and ranks them with a configurable policy, finalizes the award round, builds two
reports, calculates stipends and exports the payout list.

| | |
|---|---|
| **Author** | Yerkinbek Alikhan, group IT-2505 (individual project) |
| **Repository** | https://github.com/swazyyyyt/Scholarship-Pipeline |
| **Branch** | `main` |
| **Final commit** | the commit tagged `v1.0-final` (`git rev-parse v1.0-final`); the full identifier is written in the project report |

## Requirements and commands

- **.NET SDK 8.0 or newer** (`dotnet --version`). The projects target `net8.0` with `RollForward=Major`, so they also run
  on a newer runtime. The final version was built and tested with SDK **10.0.401** on Windows 11 x64.
- Internet access is needed for the first build (NuGet restores the xUnit packages).
- Run every command from the folder that contains `ScholarshipPipeline.sln`.

```
dotnet build
dotnet test
dotnet run --project src/ScholarshipPipeline -- data/applications.csv rank
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S003
dotnet run --project src/ScholarshipPipeline -- data/applications.csv payout academic
```

All commands of the application:

| Command | What it does |
|---|---|
| `<csv> rank [need\|merit\|both]` | Workflow 1: screen, rank and finalize the round with the chosen bonus policy (default: both) |
| `<csv> explain <id> [need\|merit]` | Workflow 2: what happened to one applicant (rank and award, or the reason of the rejection) |
| `<csv> payout [academic\|service] [output-dir]` | Workflow 3: shortlist report, income band report, stipends, export to `<output-dir>/payouts-<config>.csv` (default folder: `output`) |
| `<csv> sequence` | Demonstration: operations in a wrong order are refused |
| `<csv> errors` | Demonstration: domain failures, present and missing lookups, file failures |

The command names used during the assignments (`need`, `merit`, `both`, `demo`, `a7`, `a8`) still work.

**Exit codes:** `0` success, `1` expected domain failure, `2` operational problem (file) or wrong usage.

Expected failures to try:

```
dotnet run --project src/ScholarshipPipeline -- data/nope.csv rank              # FILE_NOT_FOUND, exit code 2
dotnet run --project src/ScholarshipPipeline -- data/invalid.csv rank           # MALFORMED_CONTENT, exit code 2
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S005   # NOT_ELIGIBLE, exit code 1
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S999   # APPLICANT_NOT_FOUND, exit code 1
```

## Domain and rules

Input file (CSV): `id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord`.

- **Eligible:** `gpa >= 2.5` (inclusive), `0 <= gpa <= 4`, income and hours not negative, id and name not blank, no
  disciplinary record.
- **Score (rank command):** `round(gpa * 20) + min(volunteerHours / 10, 10) + bonus`.
  - need-based bonus: income `<= 500` gives 20, `<= 1000` gives 10, otherwise 0;
  - merit-based bonus: gpa `>= 3.8` gives 20, `>= 3.5` gives 10, otherwise 0.
- **Ranking:** score descending, then GPA descending, then id (ordinal) as the tie-breaker. The first 3 are awarded.
- **Score (payout command):** one configured function, `academic` (25 points per GPA unit, merit bonus) or `service`
  (volunteering counts up to 20 points, need bonus). Only scores `>= 60` are shortlisted.
- **Stipend:** `score * 100`, plus 25 % for income `<= 500` or 10 % for income `<= 1000`.

Domain models: `ScholarshipApplication`, `EligibleApplication`, `ScoredApplication`, `Recommendation`,
`ScholarshipResult`, `RoundState`, `ShortlistRow`, `IncomeBandRow`, `PayoutLine`, `PayoutReport`.

## Sample data

| File | Purpose |
|---|---|
| `data/applications.csv` | 7 valid applications: 5 eligible, 1 below the minimum GPA, 1 with a disciplinary record |
| `data/invalid.csv` | 7 different content problems (bad number, value out of range, missing field, duplicate id, ...) |
| `data/empty.csv` | a valid header without rows |

## Sample output

`dotnet run --project src/ScholarshipPipeline -- data/applications.csv rank need`

```
Parsed 7 valid application(s).

NOT ELIGIBLE: S005 - gpa 2.4 is below the minimum 2.5
NOT ELIGIBLE: S006 - has a disciplinary record

=== Policy: need-based ===
Eligible: 5 | Awarded: 3 | Average score: 85.60
Rank  Id    Name        Score  Awarded
1     S001  Aigerim     108    yes
2     S007  Hanna       96     yes
3     S003  Dana        89     yes
4     S002  Bekzat      75     no
5     S004  Erlan       60     no
```

`dotnet run --project src/ScholarshipPipeline -- data/applications.csv payout academic` (part 3 of the output)

```
--- Report 1: award shortlist (academic) ---
Rank  Id    Name        Score  Gpa   Awarded
1     S001  Aigerim     123    3.9   yes
2     S007  Hanna       120    3.8   yes
3     S002  Bekzat      101    3.6   yes
4     S003  Dana        82     3.2   no
5     S004  Erlan       63     2.5   no

--- Report 2: income band summary ---
Band    Applicants  TotalScore  AvgScore  VolunteerHours
low     2           205         102.50    170
middle  2           183         91.50     100
high    1           101         101.00    30

--- Payouts (stipend from the native C function sp_stipend_amount) ---
S001  score 123  income 400    -> stipend 15375
S007  score 120  income 1000   -> stipend 13200
S002  score 101  income 1500   -> stipend 10100
Total stipend: 38675
Exported to: output\payouts-academic.csv
```

## Project structure

```
ScholarshipPipeline.sln
data/                         sample CSV files
native/                       C source of the stipend function, build scripts, prebuilt win-x64 and linux-x64 binaries
src/ScholarshipPipeline/
  Models.cs                   immutable records of the domain
  Eligibility.cs              EligibleApplication (validated factory), EligibilityOutcome
  Stages.cs                   the three pure pipeline stages
  ScholarshipWorkflow.cs      stage 1 -> stage 2 -> stage 3
  BonusPolicies.cs            need-based and merit-based bonus (passed as Func)
  Scoring.cs                  curried, configurable scoring function
  AwardRound.cs               round states and safe transitions
  Result.cs, Option.cs        explicit success / failure and present / missing
  AppError.cs                 error kinds and error codes
  RoundOperations.cs          transitions as Result
  Applicants.cs               lookups (Option) and the Map / Bind chains
  Extensions.cs               custom extension methods
  Reports.cs                  two LINQ reports and their text formatting
  Streaming.cs                lazy line reading and lazy scoring (yield return)
  PayoutWorkflow.cs           load -> reports -> stipends -> export
  ApplicationCsvParser.cs     pure CSV text -> applications + errors
  ApplicationLoader.cs        file boundary (load), ApplicationStore
  ReportExporter.cs           file boundary (export)
  NativeStipend.cs            native boundary (P/Invoke adapter)
  ResultFormatter.cs          pure text formatting
  Program.cs                  console shell: arguments, printing, exit codes
tests/ScholarshipPipeline.Tests/
  WorkflowTests.cs            pipeline, validation, factory, states
  ErrorHandlingTests.cs       Result, Option, chains, file failures
  Assignment8Tests.cs         extensions, reports, laziness, native call, export
docs/assignments-5-8.md       detailed notes written during Assignments 5-8
```

Pure code (no console, no files, no native calls): everything except `Program.cs`, `ApplicationLoader.cs`,
`ReportExporter.cs`, `NativeStipend.cs`, the file part of `Streaming.cs` and `PayoutWorkflow.Run`.

## Requirement map

| Requirement | Where it is implemented |
|---|---|
| Immutable core models | `Models.cs` (sealed records, `ImmutableArray`), `Eligibility.cs` |
| Pure logic separated from I/O | `Stages.cs`, `Reports.cs`, `Scoring.cs`, `Applicants.cs`; console only in `Program.cs` |
| Pattern matching for a domain decision | `AwardRound.Rank` / `Finalize` (switch on `RoundState`), `BonusPolicies`, `Reports.BandOf` |
| Pipeline with 3 named stages | `Stages.Screen` -> `Stages.ScoreApplications` -> `Stages.BuildResult`, composed in `ScholarshipWorkflow.Run` |
| Higher-order function that changes a rule | `Stages.ScoreApplications(eligible, bonusPolicy)`, `Scoring.CreateScore(weights)` |
| Safe operation sequence | `EligibleApplication.Create` (private constructor), `RoundState` + `AwardRound` transitions, `RoundStore.Apply` |
| Result with 3+ domain failures | `Result.cs`, `AppError.cs`: `NOT_ELIGIBLE`, `APPLICANT_NOT_FOUND`, `INVALID_AWARD_SLOTS`, `ROUND_NOT_RANKED`, `ALREADY_RANKED`, `NO_APPLICATIONS` |
| Map and Bind in a real workflow | `ScholarshipService.RunRound`, `BuildReport`, `ExplainApplicant`; `PayoutWorkflow.Run` |
| Missing lookups | `Option.cs`, `ApplicantLookup.Find` / `FindRecommendation`, `Scoring.ByName` |
| Exceptions caught at the boundaries | `ApplicationLoader.Load`, `ReportExporter.ExportPayouts`, `NativeStipendCalculator.Calculate` |
| 2+ custom extension methods | `Extensions.cs`: `OrderForRanking`, `Ensure`, `Traverse` |
| 2 LINQ reports | `Reports.Shortlist` (Where, order with tie-breaker, Select), `Reports.IncomeBands` (GroupBy, Count, Sum, Aggregate, OrderBy) |
| yield return, deferred execution, materialisation | `LazyScoring.ScoreEligible`, `LineSource.ReadLines`; snapshot with `ToImmutableArray()` in `PayoutWorkflow.BuildReports` |
| Load from CSV, export a report | `ApplicationLoader.Load`, `ReportExporter.ExportPayouts` (paths come from the command line) |
| Missing file and malformed content | `FILE_NOT_FOUND`, `MALFORMED_CONTENT` in `ApplicationLoader.Load` |
| Automated tests | `tests/ScholarshipPipeline.Tests` (xUnit), see below |

## Tests

`dotnet test` runs **126 test cases** (102 test methods; theory rows are counted separately). Result of the final
version: **126 passed, 0 failed**.

| File | Methods | Main topics |
|---|---|---|
| `WorkflowTests.cs` | 35 | scoring rules, boundary GPA 2.5, ranking and tie-breaker, bonus policies, empty input, factory, state transitions, invalid order |
| `ErrorHandlingTests.cs` | 31 | `Result` / `Option` laws, Map and Bind, failure propagation, lookups, missing file, malformed file, data kept after a failed reload |
| `Assignment8Tests.cs` | 36 | extension methods, both reports, configured scoring, deferred execution, resource disposal, native call, complete payout workflow, export failure |

## Development history

The project was developed step by step through Assignments 5-8 and then integrated. Each stage is one commit:

| Commit | Stage |
|---|---|
| Assignment 5 | three-stage pure pipeline, bonus policy as a function |
| Assignment 6 | validated factory, explicit round states, safe transitions |
| Assignment 7 | `Result`, `Option`, Map / Bind chains, boundary error handling |
| Assignment 8 | extension methods, LINQ reports, lazy processing, native stipend, export |
| Final | readable command names, configurable output folder, final README |

The detailed notes of every assignment are in [docs/assignments-5-8.md](docs/assignments-5-8.md).

## Limitations

- Data is stored in CSV files only; there is no database and no user accounts.
- The settings (minimum GPA 2.5, 3 award slots, shortlist threshold 60) are constants in `Program.cs`, not command
  line options.
- The CSV parser does not support quoted fields, so a name must not contain a comma.
- The native stipend library is included as a prebuilt binary for Windows x64 and Linux x64 only. On another platform
  the `payout` command reports `NATIVE_LOAD_FAILED` until the library is rebuilt with `native/build.sh`.
- The finalized round is not saved between runs; only the payout list is exported.

## References

- Course lectures and the briefs of Assignments 5-8, Introduction to Functional Programming.
- Microsoft .NET documentation: LINQ, records, pattern matching, `System.Collections.Immutable`, P/Invoke.
- xUnit documentation (https://xunit.net).
