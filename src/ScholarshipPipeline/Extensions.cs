using System.Collections.Immutable;

namespace ScholarshipPipeline;

/// <summary>
/// Assignment 8, Task 1: custom extension methods. All are PURE: they never change their receiver or any shared state.
/// </summary>
public static class ScoringQueryExtensions
{
    /// <summary>
    /// Receiver: IEnumerable&lt;ScoredApplication&gt;  ->  IOrderedEnumerable&lt;ScoredApplication&gt;.
    /// The ONE ranking rule of the project: score (desc), then GPA (desc), then Id (ordinal) as the tie-breaker.
    /// It is a query operation on scored applications, so it is written as an extension and used by the ranking stage
    /// (<see cref="Stages.BuildResult"/>) and by the shortlist report - both rankings can never drift apart.
    /// Returns an ordered query (lazy); the input is not modified.
    /// </summary>
    public static IOrderedEnumerable<ScoredApplication> OrderForRanking(this IEnumerable<ScoredApplication> scored)
    {
        ArgumentNullException.ThrowIfNull(scored);
        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Candidate.Gpa)
            .ThenBy(s => s.Candidate.Id, StringComparer.Ordinal);
    }
}

public static class ResultExtensions
{
    /// <summary>
    /// Receiver: Result&lt;T,E&gt;  ->  Result&lt;T,E&gt;.
    /// "Keep the value only if it satisfies a business condition, otherwise turn it into a failure."
    /// It is a chain step like Map/Bind, so it belongs on Result: <c>Load(..).Ensure(notEmpty, ...).Bind(...)</c>.
    /// A failure passes through untouched (the predicate is not called); a success is returned as the same value.
    /// </summary>
    public static Result<T, E> Ensure<T, E>(this Result<T, E> result, Func<T, bool> isValid, Func<T, E> onInvalid)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(isValid);
        ArgumentNullException.ThrowIfNull(onInvalid);
        return result.Bind(value => isValid(value) ? Result<T, E>.Ok(value) : Result<T, E>.Fail(onInvalid(value)));
    }

    /// <summary>
    /// Receiver: IEnumerable&lt;T&gt;  ->  Result&lt;ImmutableArray&lt;U&gt;, E&gt;.
    /// Applies a Result-returning function to every item; the FIRST failure stops the processing (the remaining items are
    /// never visited - important when the step is a native call) and is returned. All successes -> all values, in order.
    /// An empty sequence is a success with an empty array.
    /// </summary>
    public static Result<ImmutableArray<U>, E> Traverse<T, U, E>(this IEnumerable<T> items, Func<T, Result<U, E>> step)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(step);
        var values = ImmutableArray.CreateBuilder<U>();
        foreach (var item in items)
        {
            switch (step(item))
            {
                case Result<U, E>.Success s: values.Add(s.Value); break;
                case Result<U, E>.Failure f: return Result<ImmutableArray<U>, E>.Fail(f.Error);
            }
        }
        return Result<ImmutableArray<U>, E>.Ok(values.ToImmutable());
    }
}
