using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>
/// EXCEPTION BOUNDARY. File access lives only here (never in domain functions).
/// Operational exceptions are caught narrowly and converted into Result.Failure that names the file.
/// Content problems (bad header, bad rows, duplicates) are NOT silently skipped: a file with any invalid row is
/// rejected as a whole (MALFORMED_CONTENT), so no partial data is ever returned or stored.
/// </summary>
public static class ApplicationLoader
{
    public static Result<ImmutableArray<ScholarshipApplication>, AppError> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail(ErrorCodes.FileNotFound, "No data file path was given.");

        string text;
        try
        {
            // A8: the reader is owned by LineSource.ReadLines (disposed when the enumeration ends or fails); the lines are
            // materialised here, inside the try block, so every IO exception is still converted at this boundary.
            text = string.Join('\n', LineSource.ReadFileLines(path).ToArray());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(ErrorCodes.FileNotFound, $"Data file '{path}' was not found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCodes.FileUnreadable, $"Data file '{path}' could not be read: {ex.Message}");
        }

        var report = ApplicationCsvParser.Parse(text);
        if (report.HasErrors)
            return Fail(ErrorCodes.MalformedContent,
                $"Data file '{path}' is malformed ({report.Errors.Length} problem(s)): {string.Join(" | ", report.Errors)}");

        return Result<ImmutableArray<ScholarshipApplication>, AppError>.Ok(report.Valid);
    }

    private static Result<ImmutableArray<ScholarshipApplication>, AppError> Fail(string code, string message) =>
        Result<ImmutableArray<ScholarshipApplication>, AppError>.Fail(new AppError(ErrorKind.Operational, code, message));
}

/// <summary>
/// Holds the applications currently used by the program (the only mutable thing of the data layer).
/// Reload replaces the stored data ONLY when loading fully succeeded; on any failure the previous data is untouched.
/// There is intentionally NO fallback to default or old data hidden behind a success.
/// </summary>
public sealed class ApplicationStore
{
    public ImmutableArray<ScholarshipApplication> Current { get; private set; } = ImmutableArray<ScholarshipApplication>.Empty;

    /// <summary>Returns the number of loaded applications on success, or the operational error.</summary>
    public Result<int, AppError> Reload(string path) =>
        ApplicationLoader.Load(path).Match(
            applications =>
            {
                Current = applications;  // the only mutation: one assignment of a fully built immutable value
                return Result<int, AppError>.Ok(applications.Length);
            },
            error => Result<int, AppError>.Fail(error));   // failure: Current is not touched
}
