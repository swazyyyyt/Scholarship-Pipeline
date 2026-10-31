namespace ScholarshipPipeline;

/// <summary>
/// Assignment 8, Tasks 4 and 5: lazy line reading with a clearly owned resource.
/// OWNER: the iterator below (it opens the reader through the supplied factory).
/// RELEASE POINT: the <c>using</c> in the iterator - runs when the enumeration finishes, throws, or is abandoned early
/// (foreach / First / Take dispose the enumerator, which executes the compiler-generated finally block).
/// </summary>
public static class LineSource
{
    /// <summary>
    /// Deferred: nothing is opened until the first MoveNext. Every enumeration opens the source again,
    /// so a second enumeration would re-read the source - materialise (ToArray) when a snapshot is needed.
    /// </summary>
    public static IEnumerable<string> ReadLines(Func<TextReader> open)
    {
        ArgumentNullException.ThrowIfNull(open);   // eager check: this method is not an iterator itself
        return Iterate(open);
    }

    public static IEnumerable<string> ReadFileLines(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ReadLines(() => new StreamReader(path));
    }

    private static IEnumerable<string> Iterate(Func<TextReader> open)
    {
        using var reader = open();
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}

/// <summary>
/// Meaningful deferred workflow: applications -> (eligible ones, scored) one at a time with <c>yield return</c>.
/// No diagnostic code here: tests observe the timing with a counting source and a counting score function.
/// </summary>
public static class LazyScoring
{
    /// <summary>
    /// Deferred. Creating the enumerable runs none of the body (the arguments are only checked). Each iteration step pulls
    /// one application from <paramref name="applications"/>, screens it through the validated factory and, if eligible,
    /// calls <paramref name="score"/> exactly once and yields the scored value. Ineligible applications are skipped.
    /// Enumerating twice repeats the whole work (and re-reads the source) - call <c>ToArray()</c> / <c>ToImmutableArray()</c>
    /// once at the materialisation point and reuse the snapshot.
    /// </summary>
    public static IEnumerable<ScoredApplication> ScoreEligible(
        IEnumerable<ScholarshipApplication> applications,
        decimal minGpa,
        Func<ScholarshipApplication, int> score)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(score);
        if (minGpa < 0m || minGpa > 4m)
            throw new ArgumentOutOfRangeException(nameof(minGpa), "minGpa must be between 0 and 4.");
        return Iterate(applications, minGpa, score);
    }

    private static IEnumerable<ScoredApplication> Iterate(
        IEnumerable<ScholarshipApplication> applications, decimal minGpa, Func<ScholarshipApplication, int> score)
    {
        foreach (var application in applications)
        {
            if (EligibleApplication.Create(application, minGpa) is EligibilityOutcome.Eligible eligible)
                yield return new ScoredApplication(eligible.Value, score(application));
        }
    }
}
