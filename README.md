# Scholarship Application Scoring Pipeline

**Assignment 8 - Extension Methods and LINQ** (Introduction to Functional Programming, 2026-2027).
This is the **same runnable project** as Assignments 5, 6 and 7 (pipeline stages, validated factory, safe transitions, `Result`/`Option`, `Map`/`Bind`, boundary errors). Assignment 8 adds reusable queries, a curried scoring function, lazy processing with safe resource ownership, a small native C library called through P/Invoke, and an integrated workflow that runs two reports and exports a file. The earlier documentation is kept below ("Background") and is still valid.

## Team

| Name | Group | Implemented logic | Tests | Documentation |
|------|-------|-------------------|-------|---------------|
| Yerkinbek Alikhan | IT-2505 | `Extensions.cs`, `Reports.cs`, `Scoring.cs`, `Streaming.cs`, `NativeStipend.cs`, `ReportExporter.cs`, `PayoutWorkflow.cs`, `native/*.c`, `Program.cs` (`a8` mode) | `Assignment8Tests.cs` (36 test methods) + earlier tests | this README (individual project) |

## Prerequisites, working directory, commands

- **.NET SDK 8.0** (`dotnet --version` -> 8.0.x; `RollForward=Major`, so a newer SDK works too). Internet is needed on the first build to restore the xUnit packages from NuGet.
- **Native library:** a prebuilt binary for **linux-x64** and **win-x64** is included in `native/lib/`. A C compiler is only needed to rebuild it (see "Native build").
- **Working directory:** the folder that contains `ScholarshipPipeline.sln`, `data/` and `native/`.

```
dotnet build
dotnet test
dotnet run --project src/ScholarshipPipeline -- data/applications.csv a8 academic   # ALL Assignment 8 demonstrations, academic scoring
dotnet run --project src/ScholarshipPipeline -- data/applications.csv a8 service    # same, other configured scoring function
dotnet run --project src/ScholarshipPipeline -- data/nope.csv a8                    # file failure: missing file (exit code 2)
dotnet run --project src/ScholarshipPipeline -- data/invalid.csv a8                 # file failure: malformed content (exit code 2)
dotnet run --project src/ScholarshipPipeline -- data/applications.csv a7            # earlier demonstrations still work
```

The `a8` run writes `output/payouts-<configuration>.csv` (the `output/` folder is created at run time and ignored by git). Exit codes as before: **0** success, **1** domain failure, **2** operational problem or wrong usage.

## Workflow, input, rules, expected results

Input: CSV `id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord` (`data/applications.csv`, 7 applicants; S005 and S006 are screened out by the validated factory, 5 are eligible). Domain rules are unchanged (eligible = GPA >= 2.5 and no disciplinary record; first 3 by rank are awarded).

A8 workflow: `Load (A7)` -> `Ensure` not empty -> **pure** `BuildReports` (deferred scoring, one materialised snapshot, two LINQ reports) -> **native boundary** `AddStipends` (C function per awarded applicant) -> **file boundary** `ExportPayouts`. The first failure skips the remaining steps, so nothing is exported after a failure.

Expected results on `data/applications.csv` (hand calculated, checked by tests):

| Id | academic score | service score | income band |
|----|---------------:|--------------:|-------------|
| S001 | 123 | 99 | low |
| S002 | 101 | 60 | high |
| S003 | 82 | 78 | low |
| S004 | 63 | 48 | middle |
| S007 | 120 | 87 | middle |

Academic scoring awards **S001, S007, S002** (stipends 15375, 13200, 10100; total 38675). Service scoring awards **S001, S007, S003** (stipends 12375, 9570, 9750; total 31695). The different result on the same input (S002 vs S003) is the documented difference of the two configured functions.

## Requirement map

| Requirement | Files and methods |
|-------------|-------------------|
| Task 1 extension methods (used) | `Extensions.cs`: `OrderForRanking` (used in `Stages.BuildResult` and `Reports.Shortlist`), `Ensure` (used in `PayoutWorkflow.Run`), `Traverse` (used in `PayoutWorkflow.AddStipends`) |
| Task 2 two LINQ reports | `Reports.cs`: `Shortlist`, `IncomeBands`; text in `ReportFormatter`; data loaded from CSV by `ApplicationLoader` |
| Task 3 currying / partial application | `Scoring.cs`: `CreateScore`, `AcademicFocus`, `ServiceFocus`, used by `PayoutWorkflow.BuildReports` |
| Task 4 deferred execution | `Streaming.cs`: `LazyScoring.ScoreEligible` (`yield return`), materialisation in `PayoutWorkflow.BuildReports` |
| Task 5 disposal | `Streaming.cs`: `LineSource` (used by `ApplicationLoader.Load`), `ReportExporter.WriteTo` / `ExportPayouts` |
| Task 6 resource note | section "Resource note" below |
| Tasks 7-9 native C library | `native/scholarship_native.c/.h`, `native/build.sh`, `native/build.cmd`, `NativeStipend.cs` (`NativeMethods`, `NativeStipendCalculator`), csproj `Content` items |
| Integration | `PayoutWorkflow.Run`, `Program.cs` `RunA8Demo` |
| Failures as `Result` | new codes in `AppError.cs`: `NO_APPLICATIONS`, `INVALID_STIPEND_INPUT`, `NATIVE_LOAD_FAILED`, `NATIVE_BINDING_FAILED`, `NATIVE_REJECTED_INPUT`, `EXPORT_FAILED` (plus all A7 codes) |

## Task 1 - Extension methods (`Extensions.cs`)

| Method | Receiver -> return | Behaviour | Why it belongs on the receiver |
|--------|--------------------|-----------|--------------------------------|
| `OrderForRanking` | `IEnumerable<ScoredApplication>` -> `IOrderedEnumerable<ScoredApplication>` | Score desc, then GPA desc, then Id (ordinal). Lazy, does not change the input. | It is a query step on a sequence of scored applications and is chained like `Where`/`Select`. It is the ONE ranking rule: the A5 ranking stage and the shortlist report both call it, so they cannot drift apart. |
| `Ensure` | `Result<T,E>` -> `Result<T,E>` | Keeps a success only if a predicate holds, otherwise returns a failure built from the value. A failure passes through and the predicate is not called. | It is a chain step like `Map`/`Bind`; the call reads `Load(..).Ensure(notEmpty, ..).Bind(..)`. Used to turn "valid file with zero applications" into a clear `NO_APPLICATIONS` failure. |
| `Traverse` | `IEnumerable<T>` -> `Result<ImmutableArray<U>,E>` | Applies a Result-returning function to every item; the **first failure stops** the loop. Empty input is a success with an empty array. | It turns "a sequence of steps that can fail" into one Result. Used for the native stipend calls so a failing library is called once, not once per applicant. |

None of them mutates its receiver or any shared state.

## Task 2 - LINQ reports (`Reports.cs`)

| | Report 1: award shortlist | Report 2: income band summary |
|---|---|---|
| Intended user | scholarship committee chair | financial office (budget per social group) |
| Input | snapshot of scored eligible applications + `ShortlistOptions(MinScore, AwardSlots)` | the same snapshot |
| Filters | `Where Score >= MinScore` | none (all eligible applications) |
| Stages | `Where` -> `OrderForRanking` (`OrderByDescending`/`ThenByDescending`/`ThenBy`) -> `Select` (rank, awarded flag) | `GroupBy` income band -> `Select` (`Count`, `Sum`, `Aggregate`) -> `OrderBy` band order `ThenBy` band name |
| Output fields | Rank, Id, Name, Score, Gpa, MonthlyIncome, Awarded | Band, Applicants, TotalScore, AverageScore, TotalVolunteerHours |
| Expected (academic, MinScore 80, 3 slots) | S001 123 yes, S007 120 yes, S002 101 yes, S003 82 no | low: 2 / 205 / 102.50 / 170 h; middle: 2 / 183 / 91.50 / 100 h; high: 1 / 101 / 101.00 / 30 h |

Bands: low = income <= 500, middle <= 1000, high above. Both reports are pure (no console, no file, no native code), take a snapshot, and are deterministic: ties are broken by GPA and Id (report 1) and by band order (report 2). Empty input and "no matches" give empty output; ineligible or invalid applicants never reach the reports because `LazyScoring` screens through the validated factory and the loader rejects malformed files.

## Task 3 - Currying and partial application (`Scoring.cs`)

`Compute(weights, application)` is the ordinary two-argument function. `CreateScore : ScoreWeights -> (ScholarshipApplication -> int)` is its **curried** form: the first call fixes and validates the configuration, the returned function accepts the remaining input. Two **partial applications**:

- `AcademicFocus = CreateScore(25 points per GPA unit, 1 point per 20 h, max 5, merit bonus)`
- `ServiceFocus  = CreateScore(15 points per GPA unit, 1 point per 5 h, max 20, need bonus)`

Example (S001, GPA 3.9, 120 h, income 400): academic `round(3.9*25)=98 + min(6,5)=5 + 20 = 123`, service `round(3.9*15)=59 + min(24,20)=20 + 20 = 99`. The chosen function is passed into the workflow as a value (`PayoutWorkflow.Run(..., score, ...)`); no global setting is switched.

## Tasks 4 and 5 - Deferred execution and disposal

- `LazyScoring.ScoreEligible` is an iterator (`yield return`): creating it runs nothing; each iteration step pulls one application, screens it and calls the score function once for eligible ones. Tests observe the timing with a counting source and a counting score function (no diagnostics in production code).
- **Materialisation point:** `PayoutWorkflow.BuildReports` calls `ToImmutableArray()` once. Both reports reuse the snapshot. Enumerating the *unmaterialised* query again would re-read the source and recompute every score (demo section 2 shows 5 calls, then 5 more); the snapshot can be reused any number of times with no recalculation and no file access.

| Resource | Owner | Release point | Failure / early stop |
|----------|-------|---------------|----------------------|
| `StreamReader` of the input file | iterator `LineSource.ReadLines` (`using var`) | end of enumeration | disposed on exception and when enumeration stops early (`First`, `Take`), because disposing the enumerator runs the iterator's `finally` |
| `StreamWriter` of the export | `ReportExporter.WriteTo` (`using var`) | end of the method | disposed if writing throws; `ExportPayouts` writes `<file>.tmp`, moves it over the target only after the writer is closed and deletes the temp file on failure |
| native library handle | the .NET runtime (loaded once per process, never freed by us) | process exit | no native memory is allocated by our code |

`ApplicationLoader.Load` used the convenience API `File.ReadAllText` in A7 (it opens and closes the file internally, so the file is closed before it returns). It now reads through `LineSource` and materialises the lines inside the `try` block (`ToArray`), so the stream lifetime is bounded and every IO exception still becomes `FILE_NOT_FOUND` / `FILE_UNREADABLE`. Garbage collection is not relied on for the file handle; a test opens the file exclusively after reading to prove it was released.

## Resource note (Task 6)

- **Materialised datasets:** the loaded `ImmutableArray<ScholarshipApplication>` (once per run), the lines of the input file (a short-lived array inside `Load`), and the scored snapshot (once per `BuildReports`). Reports are small derived arrays of rows.
- **Retention by deferred queries / closures:** the unmaterialised `LazyScoring.ScoreEligible` query holds the source collection and the score function until it is discarded. The workflow does not store it: it is consumed by `ToImmutableArray()` inside `BuildReports` and then unreachable. The configured functions `AcademicFocus`/`ServiceFocus` capture only an immutable `ScoreWeights` record, not any data.
- **Long-lived collections, caches, event subscriptions:** none are used. `ApplicationStore.Current` holds one immutable array and is replaced, never appended to, so it cannot grow.
- **Design decision:** reports take a snapshot instead of a lazy query. This avoids re-reading the file and recomputing scores for the second report (repeated allocation / IO) and removes any chance of consuming a disposed reader.
- **Trade-off streaming vs snapshot:** streaming (`LineSource`) keeps only one line in memory and could handle a very large file, but the data can be consumed once and the reader must stay open during enumeration. A snapshot costs memory proportional to the data set but is stable, reusable and lets the resource be closed immediately. The project loads a bounded snapshot because the data set is small and both reports need the same data. No memory leak was fixed here, so none is claimed.

## Native C library (Tasks 7-9)

**Function contract** (`native/scholarship_native.h`):

```c
int32_t sp_stipend_amount(int32_t score, int32_t monthly_income);   // cdecl, exported
```

| Item | Definition |
|------|------------|
| Purpose | Monthly stipend of an awarded applicant, used for the payouts in the exported report |
| Inputs | `score` 0..200, `monthly_income` 0..1 000 000 (whole currency units) |
| Output | stipend in **whole currency units**; `base = score * 100`; supplement 25 % if income <= 500, 10 % if income <= 1000, else 0 %; `result = base + base * supplement / 100` in integer arithmetic (fraction dropped, i.e. round down) |
| Invalid input | returns `-1` (`SP_ERROR_INVALID_INPUT`) |
| Types | `int32_t` <-> C# `int` (fixed 32 bit on every platform); no pointers, no native allocation, nothing to free |
| Manual example | score 123, income 400: base 12300, +25 % = 3075, result **15375** |

**C# binding and adapter** (`NativeStipend.cs`): `[DllImport("scholarship_native", EntryPoint = "sp_stipend_amount", CallingConvention = Cdecl)]` in `NativeMethods`. The runtime maps the name to `libscholarship_native.so` / `scholarship_native.dll` / `libscholarship_native.dylib` in the application folder. `NativeStipendCalculator` (the only user of the binding) validates domain input before the call (`INVALID_STIPEND_INPUT`), converts `DllNotFoundException` / `BadImageFormatException` to `NATIVE_LOAD_FAILED`, `EntryPointNotFoundException` to `NATIVE_BINDING_FAILED` and the native `-1` to `NATIVE_REJECTED_INPUT`. A fractional income is truncated toward zero before the call. No project setting is needed for `DllImport` (it is used instead of `LibraryImport`, so `AllowUnsafeBlocks` is not required).

**Platform, compiler, build:**

| | |
|---|---|
| Tested platform | Linux x86-64 (Ubuntu 24.04), .NET SDK 8.0.133, **gcc 13.3.0** |
| Linux build | `sh native/build.sh` = `gcc -std=c11 -Wall -Wextra -Werror -O2 -shared -fPIC -o native/lib/linux-x64/libscholarship_native.so native/scholarship_native.c` |
| Windows x64 | `native\build.cmd` (MinGW-w64 gcc) or MSVC: `cl /LD /O2 native\scholarship_native.c /Fe:native\lib\win-x64\scholarship_native.dll`. The included `scholarship_native.dll` was cross-compiled on Linux with `x86_64-w64-mingw32-gcc 13-win32` (`-shared -static -static-libgcc`); it was **not executed** in this environment. |
| macOS | not built or tested; `cc -shared -fPIC -O2 -o native/lib/osx/libscholarship_native.dylib native/scholarship_native.c` would be the command |
| Binary location after a clean build | `ScholarshipPipeline.csproj` copies `native/lib/<rid>/<binary>` for the current OS (x64 only) to the output folder next to `ScholarshipPipeline.dll`; the test project receives it through the project reference |
| Architecture | x86-64 only; on another architecture the binary is not copied and the adapter reports `NATIVE_LOAD_FAILED` |

**Integration difficulties and resolutions:**
1. The runtime must find the library at run time and in the test run. Solution: a conditional `Content` item in the csproj copies the binary to the output folder, which is on the native probing path.
2. A load failure cannot be tested by deleting the file once the real library is loaded in the process. Solution: a second binding (`StipendAmountFromMissingLibrary`) points to a library that does not exist, so the test gets a genuine `DllNotFoundException` on a fresh load, plus an injectable delegate for the other failure kinds. The real native call is tested separately and is never replaced by a test double.
3. The native library must not contain business rules that differ from validation in C#. Solution: the adapter validates the same ranges first and the C function repeats the check for safety; both are tested.

## Tests (xUnit, `dotnet test`)

Cumulative: **102 test methods** (`WorkflowTests` 35 from A5/A6, `ErrorHandlingTests` 31 from A7, `Assignment8Tests` 36 new). Required coverage of A8:

| Area | Tests (all in `Assignment8Tests.cs`) |
|------|--------------------------------------|
| Reports | `Shortlist_NormalResult_...`, `Shortlist_EmptyDataset_...`, `Shortlist_NoMatchingRecords_...`, `Shortlist_TiedPrimaryValues_...`, `IncomeBands_GroupingAndTotals_...`, `IncomeBands_EmptyInput_OrderIsDeterministic_...` |
| Extensions | `OrderForRanking_...`, `Ensure_...`, `Traverse_...` (also covered through `Workflow_*`) |
| Configured functions | `ConfiguredFunctions_SameInput_GiveDocumentedDifferentResults`, `ConfiguredFunctions_ChangeTheAwardedSet_...`, `CreateScore_IsCurried_...`, `ScoringByName_...` |
| Deferred execution | `LazyScoring_CreatingTheEnumerable_DoesNotProcessAnything`, `LazyScoring_Iteration_PullsAndScoresOneItemAtATime_...`, `LazyScoring_Materialisation_...`, `LazyScoring_InvalidArguments_...` |
| Resource lifetime | `ReadLines_OpensOnlyOnEnumeration_AndDisposesAfterSuccess`, `ReadLines_DisposesOnEarlyStop`, `ReadLines_DisposesWhenReadingFails`, `ReadFileLines_ReleasesTheFileHandle_...`, `WriteTo_DisposesWriterOnSuccessAndOnFailure` |
| Native integration | `NativeCall_RealLibrary_ReturnsManuallyCalculatedValues` (15375, 13200, 10100, edges), `NativeFunction_ItselfReturnsMinusOne_...`, `Adapter_InvalidDomainInput_...`, `Adapter_LoadAndBindingFailures_BecomeClearResultFailures` |
| Complete workflows | normal: `Workflow_NormalPath_ReportsNativeStipendsAndExport`, `Workflow_NoMatchingApplicants_...`; failure: `Workflow_NativeFailure_ExportsNothing_...`, `Workflow_FirstNativeFailure_StopsRemainingNativeCalls`, `Workflow_EmptyDataFile_...`, `Workflow_InvalidSettings_...`, `Workflow_ExportFailure_...` |
| File boundaries | `Workflow_MissingFile_...`, `Workflow_MalformedContent_...`, `Loader_ReadingThroughLineSource_...` (temporary directory per test, deleted afterwards) |

The suite contains well over the required minimum (8 pure/validation tests, 2 workflow tests, 2 file tests).

**Latest test outcome:** all **126 test cases** (102 methods, theory rows counted separately; A5 + A6 + A7 + A8) passed, 0 failed. Note: NuGet was not reachable in the environment where this was prepared, so the run used the same test source files with a small re-implementation of `Fact`/`Theory`/`Assert` instead of the real xUnit packages. The C library, the P/Invoke calls and the file operations were real (Linux x64). **Please run `dotnet test` once on your machine and replace this line with the real xUnit numbers.** If you run on Windows, the included `scholarship_native.dll` was cross-compiled and not executed here; if the native tests fail there, rebuild it with `native\build.cmd`.

## Defense notes (small changes to try)

- Change the shortlist threshold: `minScore` in `Program.cs` (`RunA8Demo`) or the `minScore` argument of `PayoutWorkflow.Run`; test `Shortlist_NormalResult_...`.
- Change the tie-breaker: `ScoringQueryExtensions.OrderForRanking` (one place for both reports); test `OrderForRanking_...`.
- New configuration: `CreateScore(new ScoreWeights(...))` in `Scoring.cs`, add it to `Scoring.ByName`.
- New income band limit: `Reports.LowIncomeLimit` / `MiddleIncomeLimit`; test `IncomeBands_EmptyInput_..._BandEdges`.
- Change the stipend supplement in `scholarship_native.c`, rebuild with `native/build.sh`, update the expected values in `NativeCall_RealLibrary_...`.

## Contributions

- Yerkinbek Alikhan, IT-2505 - individual project: all A8 logic, native library, tests and documentation listed above.

---

# Background: Assignment 7 documentation (still valid)

Assignment 7 - Exception Handling in FP (Introduction to Functional Programming, 2026-2027).
This is the **same runnable project** as Assignments 5 and 6 (pipeline stages, validated factory, explicit states, safe transitions), extended with an explicit `Result`/`Option` model, `Map`/`Bind` composition and boundary exception handling. Assignment 5 and 6 documentation is kept below ("Background") and is still valid.

## Team

| Name | Group | Implemented logic | Tests | Documentation |
|------|-------|-------------------|-------|---------------|
| Yerkinbek Alikhan | IT-2505 | `Result`, `Option`, `AppError`, `RoundOperations`, `ScholarshipService` chains, `ApplicationLoader`/`ApplicationStore`, `Program` integration | `ErrorHandlingTests.cs` (35 cases) | this README (individual project) |

## Prerequisites, working directory, commands

- **.NET SDK 8.0** (`dotnet --version` -> 8.0.x; the projects use `RollForward=Major`, so a newer SDK also works). Internet is needed on the first build to restore the xUnit packages from NuGet.
- **Working directory:** the folder that contains `ScholarshipPipeline.sln` and `data/` (the commands below use relative `data/...` paths).

```
dotnet build
dotnet test
dotnet run --project src/ScholarshipPipeline -- data/applications.csv both            # normal run (A5/A6 output, now through the Result chain)
dotnet run --project src/ScholarshipPipeline -- data/applications.csv a7              # ALL Assignment 7 demonstrations
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S003    # one applicant: success
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S005    # domain failure (not eligible)
dotnet run --project src/ScholarshipPipeline -- data/applications.csv explain S999    # missing lookup
dotnet run --project src/ScholarshipPipeline -- data/nope.csv both                    # file failure: missing file
dotnet run --project src/ScholarshipPipeline -- data/invalid.csv both                 # file failure: malformed content
dotnet run --project src/ScholarshipPipeline -- data/applications.csv demo            # A6 safe-transition demo (unchanged)
```

Optional policy for `explain`: `... explain S002 merit`. Exit codes: **0** success, **1** expected domain failure, **2** operational problem (file) or wrong usage. No demonstrated case ends in an unhandled exception.

## Workflow, input, rules, output (short)

A scholarship committee loads applications from CSV (`id,name,gpa,monthlyIncome,volunteerHours,hasDisciplinaryRecord`), screens them through the validated factory, scores/ranks them with a bonus policy (need-based or merit-based) and finalizes the top 3 awards (rules and dataset: see Background A5/A6). Output: ineligible applications with reasons, the ranking table and summary per policy, or a clear error. New in A7: every step that can fail *in an expected way* returns a `Result`, and the file layer reports problems as values.

## Task 1 - Explicit domain outcomes (`Result.cs`, `AppError.cs`)

`Result<T,E>` is a closed hierarchy with a **private constructor** and exactly two sealed cases: `Success(Value)` and `Failure(Error)`. `null` payloads are rejected at construction, and the payload can only be read after the case is known (`Match` / pattern matching), so "success without payload" and "failure that has a value" cannot exist. `AppError(Kind, Code, Message)` is the structured error: `Kind` is `Domain` (expected business failure) or `Operational` (environment); the message always names the rule / applicant / file.

| # | Case | Input | Expected result | Error code and message content |
|---|------|-------|-----------------|--------------------------------|
| 0 | **Success** | whole round, need-based, 3 slots | `Finalized` state; awards S001, S007, S003; average 85.60 | - |
| 0 | **Success** | `explain S003` (need-based) | `S003 Dana: rank 3, score 89, AWARDED` | - |
| 1 | Invalid input (like an invalid quantity) | `AwardSlots = -1` | Failure, state unchanged | `INVALID_AWARD_SLOTS` - "Award slots must not be negative (got -1)." |
| 1b | Invalid input | `MinGpa = 4.5` | Failure (no exception) | `INVALID_SETTINGS` - "Minimum GPA 4.5 must be between 0 and 4." |
| 2 | Missing required item | `explain S999` | Failure, later steps skipped | `APPLICANT_NOT_FOUND` - "Applicant 'S999' does not exist in the loaded applications." |
| 3 | Validation rejects the item | `explain S005` / `explain S006` | Failure | `NOT_ELIGIBLE` - names the applicant and **every** violated rule ("gpa 2.4 is below the minimum 2.5" / "has a disciplinary record") |
| 4 | State does not permit the operation | `Finalize` before `Rank` | Failure, state unchanged | `ROUND_NOT_RANKED` - "The round must be ranked before it can be finalized." |
| 5 | State does not permit the operation | `Rank` on an already ranked round | Failure, ranking not replaced | `ALREADY_RANKED` - "The round is already ranked; finalize it." (`ROUND_FINALIZED` after finalize) |

No business failure uses an exception. `ArgumentNullException` / `ArgumentOutOfRangeException` remain only for programmer errors (a null argument), as in A6.

**Validation failure vs workflow state (what each type represents).** `NOT_ELIGIBLE` is a *validation* failure: the **data** of the applicant breaks a rule (produced by the A6 factory `EligibleApplication.Create`, which returns the `EligibilityOutcome` cases `Eligible` / `Ineligible`). `ROUND_NOT_RANKED` / `ALREADY_RANKED` / `ROUND_FINALIZED` are *workflow-state* failures: the data is fine but the **current state** of the round (`RoundState.Collected` / `Ranked` / `Finalized`) does not permit the operation. `TransitionOutcome.Refused` now carries a stable `Code`, and `RoundOperations.ToResult` turns `Accepted` -> `Success` and `Refused` -> `Failure(Domain error)`, so the A6 safe transitions are the single source of those rules and stay active.

## Task 2 - Missing values (`Option.cs`, `Applicants.cs`)

`Option<T>` = `Some(value)` / `None` (private constructor, closed, no `null`/`default(T)` as absence, no `!` operator).

| Lookup | Returns | Present | Absent | Meaning |
|--------|---------|---------|--------|---------|
| `ApplicantLookup.Find(apps, id)` | `Option<ScholarshipApplication>` | `Some(app)` | `None` -> `ToResult` -> `APPLICANT_NOT_FOUND` | **required** by `ExplainApplicant`, so absence becomes a Result failure |
| `ApplicantLookup.FindRecommendation(result, id)` | `Option<Recommendation>` | `Some(rec)` | `None` (screened out / not ranked) -> `ToResult` failure | required by the final step of `ExplainApplicant` |
| `ApplicantLookup.VolunteerHours(apps, id)` | `Option<int>` | `Some(0)` for S004 (**0 hours is legitimate data**), `Some(120)` for S001 | `None` for unknown id | primitive value: zero is **not** absence |

No lookup in this workflow is optional, so there is no "apply default" behaviour; no fabricated record is ever returned. (Ids are compared ordinally: `s003` is absent.)

## Task 3 - `Map` and `Bind` (`Result.cs`, `Applicants.cs`)

- `Map : Result<T,E> + Func<T,U> -> Result<U,E>` - the function is **pure** and returns a plain value; `Map` wraps it in `Success`.
- `Bind : Result<T,E> + Func<T,Result<U,E>> -> Result<U,E>` - the function may itself fail and already returns a `Result`; `Bind` returns it **as is** (`Same` instance), otherwise we would get `Result<Result<U,E>,E>`. That is why the return types of the supplied functions differ.
- On `Failure` both return the same error and **never call the function**. The payload is only read inside the `Success` branch.

Real chain 1 - the whole round (`ScholarshipService.RunRound` / `BuildReport`):

```
ValidateSettings(settings)                       Result<PipelineSettings>
 .Map  (valid => AwardRound.Open(apps, MinGpa))  pure: screening through the A6 factory -> Collected
 .Bind (state => RoundOperations.Rank(...))      may refuse: INVALID_AWARD_SLOTS / ALREADY_RANKED
 .Bind (RoundOperations.Finalize)                may refuse: ROUND_NOT_RANKED
 .Bind (RoundOperations.ResultOf)                extract the ranking (Result)
 .Map  (result => ResultFormatter.Format(...))   pure display text
```

Real chain 2 - one applicant (`ScholarshipService.ExplainApplicant`, the "quote-style" chain):

```
ApplicantLookup.Find(id).ToResult(...)           Option -> Result: missing applicant -> APPLICANT_NOT_FOUND
 .Bind RequireEligible                           A6 factory: NOT_ELIGIBLE with all reasons
 .Bind RunRound(...) .Bind ResultOf              the full round above (may fail)
 .Bind FindRecommendation(...).ToResult(...)     lookup in the ranking (Option -> Result)
 .Map  ToView                                    pure display model
```

A missing applicant stops the chain before the round runs (proved by a counting policy in `FailurePropagation_MissingOrIneligibleApplicant_SkipsTheWholeRound`). The **final outcome is handled once** in `Program.Report` via `Result.Match`.

## Task 4 - Boundary exception handling (`ApplicationLoader.cs`)

File access exists **only** in `ApplicationLoader.Load` (domain functions, stages and `ScholarshipService` never touch files). Exceptions are caught narrowly and converted into `Failure` with kind `Operational` and the file path in the message.

| Problem | Caught | Outcome |
|---------|--------|---------|
| File or directory missing; blank path | `FileNotFoundException`, `DirectoryNotFoundException` / explicit check | `FILE_NOT_FOUND` + path |
| Cannot read (path is a directory, permissions, I/O) | `UnauthorizedAccessException`, `IOException` | `FILE_UNREADABLE` + path + system message |
| Empty file, wrong header, bad number / flag, missing field, wrong field count, duplicate id, out-of-range value | `ApplicationCsvParser` row/format checks (no exception) | `MALFORMED_CONTENT` + path + **every** problem with its line number |

There is no empty `catch`, no catch-all and no fallback. **Behaviour change vs A5:** a file with any invalid row is now rejected **as a whole** (A5 kept the valid rows and reported the bad ones); this guarantees no partial data. `ApplicationCsvParser.Parse` itself is unchanged, so the A5 parser tests still hold. A header-only file is a valid empty dataset, not an error.

**State preservation.** `ApplicationStore.Reload(path)` replaces `Current` (an immutable array) **only** when loading fully succeeded; on any failure `Current` is not touched (demos 7 and 8 print "unchanged: True"). The A6 `RoundStore` keeps the same rule for round states.

## Task 5 - Demonstrations (`dotnet run --project src/ScholarshipPipeline -- data/applications.csv a7`)

Covers: normal path (round and applicant), three kinds of domain failure (invalid input, state, validation), present and missing lookups (incl. the legitimate zero), the two file failures with preserved data, and the A6 pipeline / safe transitions still working afterwards. Actual output:

```

=== 1. Normal path: whole round (validate -> Map open -> Bind rank -> Bind finalize -> Map format) ===
=== Policy: need-based ===
Eligible: 5 | Awarded: 3 | Average score: 85.60
Rank  Id    Name        Score  Awarded
1     S001  Aigerim     108    yes
2     S007  Hanna       96     yes
3     S003  Dana        89     yes
4     S002  Bekzat      75     no
5     S004  Erlan       60     no

=== 2. Normal path: applicant S003 (Find -> Bind eligible -> Bind round -> Bind lookup -> Map view) ===
S003 Dana: rank 3, score 89, AWARDED

=== 3. Domain failure: invalid input (negative award slots) ===
REJECTED [Domain/INVALID_AWARD_SLOTS] Award slots must not be negative (got -1).

=== 4. Domain failure: state does not permit (Finalize before Rank, then Rank twice) ===
REJECTED [Domain/ROUND_NOT_RANKED] The round must be ranked before it can be finalized.
Rank accepted -> Ranked
REJECTED [Domain/ALREADY_RANKED] The round is already ranked; finalize it.

=== 5. Domain failure: validation (applicants S005 gpa too low, S006 disciplinary record) ===
REJECTED [Domain/NOT_ELIGIBLE] Applicant 'S005' is not eligible: gpa 2.4 is below the minimum 2.5.
REJECTED [Domain/NOT_ELIGIBLE] Applicant 'S006' is not eligible: has a disciplinary record.

=== 6. Lookup: present and missing (and a legitimate zero) ===
Hours of S001 : Some(120)
Hours of S004 : Some(0)   (0 hours is real data, not absence)
Hours of S999 : None
REJECTED [Domain/APPLICANT_NOT_FOUND] Applicant 'S999' does not exist in the loaded applications.

=== 7. File failure: missing file (stored data preserved) ===
FAILED [Operational/FILE_NOT_FOUND] Data file 'data/does-not-exist.csv' was not found.
Applications before: 7, after: 7 (unchanged: True)

=== 8. File failure: malformed content (stored data preserved) ===
FAILED [Operational/MALFORMED_CONTENT] Data file 'data/invalid.csv' is malformed (7 problem(s)): Line 3: gpa 'abc' is not a number. | Line 4: gpa 4.5 must be between 0.0 and 4.0. | Line 5: monthlyIncome -5 must not be negative. | Line 6: volunteerHours is missing. | Line 7: hasDisciplinaryRecord 'maybe' is not 'true' or 'false'. | Line 8: expected 6 fields but found 4. | Line 9: duplicate id 'S101'.
Applications before: 7, after: 7 (unchanged: True)

=== 9. Pipeline and safe transitions still work after the failures ===
S001 Aigerim: rank 1, score 108, AWARDED
--- Sequence demo (need-based) ---
1. Finalize before Rank   : REFUSED: The round must be ranked before it can be finalized.
2. Rank with -1 slots     : REFUSED: Award slots must not be negative (got -1).
   state still unchanged  : True
3. Rank (valid)           : ACCEPTED -> Ranked
4. Rank again             : REFUSED: The round is already ranked; finalize it.
5. Finalize               : ACCEPTED -> Finalized
6. Finalize again         : ACCEPTED -> Finalized (same state: True)
```

Malformed file from the command line (`data/invalid.csv both`, exit code 2):

```
FAILED to load data: [Operational/MALFORMED_CONTENT] Data file 'data/invalid.csv' is malformed (7 problem(s)): Line 3: gpa 'abc' is not a number. | Line 4: gpa 4.5 must be between 0.0 and 4.0. | Line 5: monthlyIncome -5 must not be negative. | Line 6: volunteerHours is missing. | Line 7: hasDisciplinaryRecord 'maybe' is not 'true' or 'false'. | Line 8: expected 6 fields but found 4. | Line 9: duplicate id 'S101'.
```

## Requirement map

| Requirement | Files / methods |
|-------------|-----------------|
| Result with two protected cases, structured error | `Result.cs`, `AppError.cs` |
| >= 3 domain failures | `RoundOperations.ValidateSettings`/`Rank`/`Finalize`, `ScholarshipService.RequireEligible`, `ExplainApplicant` |
| Option, real lookups, zero is not absence | `Option.cs`, `ApplicantLookup.Find` / `FindRecommendation` / `VolunteerHours` |
| `Map`, `Bind`, failure propagation | `Result<T,E>.Map`, `.Bind`; chains in `ScholarshipService.RunRound`, `BuildReport`, `ExplainApplicant` |
| Final outcome handled once | `Program.Report` (`Match`) |
| Boundary exceptions, no partial update | `ApplicationLoader.Load`, `ApplicationStore.Reload` |
| A6 safe transitions kept | `AwardRound`, `RoundStore`, `RoundOperations.ToResult`; `demo` mode |
| Tests | `tests/ScholarshipPipeline.Tests/ErrorHandlingTests.cs` (+ A5/A6 `WorkflowTests.cs`) |

## Tests (xUnit, `dotnet test`)

New A7 file `ErrorHandlingTests.cs` - 35 test cases (with theory rows); A5/A6 `WorkflowTests.cs` - 55 cases that still pass. File tests use a unique temp directory per test and delete it in `Dispose`; sample files are copied from `data/` into the test output.

| Required case | Tests |
|---------------|-------|
| Complete success | `CompleteSuccess_RunRound_...`, `CompleteSuccess_BuildReport_...`, `CompleteSuccess_ExplainApplicant_...` (3 rows), `Integration_FileToReport_...` |
| Domain failures + useful error | `DomainFailure_InvalidAwardSlots_...`, `..._InvalidMinGpa_...`, `..._StateDoesNotPermit_FinalizeBeforeRank`, `..._RankTwice_...`, `..._NotEligible_...` (2 rows), `..._RequiredApplicantMissing_...` |
| Lookup present and absent | `LookupPresentAndAbsent_...`, `LookupLegitimateZero_...`, `Option_ZeroFalseAndEmptyString_...`, `Option_ToResult_...` |
| Map success | `MapSuccess_TransformationReceivesValueAndReturnsNewValue` |
| Bind success | `BindSuccess_NextOperationReceivesValue_AndItsResultIsPreserved` (`Same`), `BindSuccess_FailureReturnedByNextOperationIsPreserved` |
| Failure propagation (non-execution) | `FailurePropagation_MapAndBind_...` (counters), `FailurePropagation_MissingOrIneligibleApplicant_...` (policy double, 0 calls), `FailurePropagation_InvalidSettings_StopsBeforeOpenRankAndFinalize` (3 counters) |
| Missing file | `Boundary_MissingFile_ReportsFileAndKeepsPreviousData`, `Boundary_MissingDirectoryAndBlankPath_...`, `Boundary_PathIsADirectory_...` |
| Malformed file | `Boundary_MalformedFile_BecomesClearFailure_WithoutPartialUpdate`, `Boundary_SampleInvalidFile_...`, `Boundary_EmptyFileOrWrongHeader_...` (2 rows), `Boundary_HeaderOnlyFile_...`, `Boundary_SuccessfulReload_...` |
| Result invariants, A6 transitions still safe | `ResultAndOption_CannotRepresentContradictoryStates`, `SafeTransitions_StayActive_...` |

**Latest test outcome:** all 90 test cases (35 new A7 + 55 from A5/A6) passed, 0 failed. Note: this was checked in an offline environment with a small assertion runner (the same test source files, `Fact`/`Theory`/`Assert` re-implemented) because NuGet was unreachable. The suite was also run with a comma as the decimal separator (error messages use `InvariantCulture`, so they do not depend on the Windows regional settings). A deliberate break of the loader made 4 of the new tests fail, so the assertions are effective. **Please run `dotnet test` once on your machine and replace this line with the real xUnit numbers.**

## Defense notes

- *Why different return types:* `Map` wraps a plain value, `Bind` returns the function's own `Result` (see `BindSuccess_...` `Assert.Same`).
- *Proof that a dependent function is skipped:* `FailurePropagation_*` tests (counters stay 0).
- *Zero vs absence:* `VolunteerHours("S004")` is `Some(0)`, `VolunteerHours("S999")` is `None`.
- *Exception boundary:* `ApplicationLoader.Load` only; state preserved by `ApplicationStore.Reload` (assign only on success).
- *Live modification ideas:* add a new rule (e.g. maximum 2 awards per income band) as a new error code in `ErrorCodes` + one `Bind` step + one test; or a new file problem in `ApplicationLoader`.

## Contributions

- Yerkinbek Alikhan, IT-2505 - individual project: Result/Option, chains, boundary, integration, tests, documentation.

---

# Background: Assignment 6 documentation (still valid)

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

Failure demo (`data/invalid.csv`): 7 lines starting with `REJECTED:` (lines 3-9), 1 valid application parsed, exit code 1. **Superseded in Assignment 7:** the file is now rejected as a whole with `MALFORMED_CONTENT` (exit code 2); see Task 4 of A7. The parser itself and its A5 tests are unchanged.
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
