namespace ScholarshipPipeline;

/// <summary>
/// Domain      = an expected business failure (a rule or the current state said no). No exception is involved.
/// Operational = the environment failed (file missing, malformed content). Caught only at the boundary.
/// </summary>
public enum ErrorKind
{
    Domain,
    Operational
}

/// <summary>Structured error: kind, stable code for the caller, human-readable message naming the rule / item / file.</summary>
public sealed record AppError(ErrorKind Kind, string Code, string Message)
{
    public override string ToString() => $"[{Kind}/{Code}] {Message}";
}

/// <summary>All error codes in one place (documented in the README).</summary>
public static class ErrorCodes
{
    // Domain failures
    public const string ApplicantNotFound = "APPLICANT_NOT_FOUND";   // required lookup is missing
    public const string NotEligible = "NOT_ELIGIBLE";                // validation (factory) said no
    public const string InvalidAwardSlots = "INVALID_AWARD_SLOTS";   // invalid input (like an invalid quantity)
    public const string InvalidSettings = "INVALID_SETTINGS";        // invalid min GPA
    public const string RoundNotRanked = "ROUND_NOT_RANKED";         // state does not permit finalize
    public const string AlreadyRanked = "ALREADY_RANKED";            // state does not permit rank (repeat)
    public const string RoundFinalized = "ROUND_FINALIZED";          // state does not permit rank (closed)
    public const string UnknownState = "UNKNOWN_STATE";
    public const string NotRankedResult = "RESULT_NOT_AVAILABLE";    // no ranking in the state
    public const string NoApplications = "NO_APPLICATIONS";          // A8: the data file is valid but empty
    public const string InvalidStipendInput = "INVALID_STIPEND_INPUT"; // A8: input rejected BEFORE the native call

    // Operational failures
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileUnreadable = "FILE_UNREADABLE";
    public const string MalformedContent = "MALFORMED_CONTENT";
    public const string NativeLoadFailed = "NATIVE_LOAD_FAILED";         // A8: library missing / wrong architecture
    public const string NativeBindingFailed = "NATIVE_BINDING_FAILED";   // A8: export not found in the library
    public const string NativeRejectedInput = "NATIVE_REJECTED_INPUT";   // A8: native function returned its error value
    public const string ExportFailed = "EXPORT_FAILED";                  // A8: report file could not be written
}
