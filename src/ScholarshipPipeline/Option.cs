namespace ScholarshipPipeline;

/// <summary>
/// Explicit model of a value that may be missing: Some(value) or None.
/// Some(0), Some(false) and Some("") are LEGITIMATE data and are different from None.
/// Absence is never encoded with null or default(T).
/// </summary>
public abstract record Option<T>
{
    private Option() { }

    public sealed record Some : Option<T>
    {
        public T Value { get; }

        internal Some(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }
    }

    public sealed record None : Option<T>
    {
        internal None() { }
    }

    public static Option<T> Of(T value) => new Some(value);

    public static Option<T> Empty() => new None();

    public bool IsSome => this is Some;

    public R Match<R>(Func<T, R> onSome, Func<R> onNone)
    {
        ArgumentNullException.ThrowIfNull(onSome);
        ArgumentNullException.ThrowIfNull(onNone);
        return this switch
        {
            Some s => onSome(s.Value),
            None => onNone(),
            _ => throw new InvalidOperationException("Unreachable: Option has only two cases.")
        };
    }

    /// <summary>The item is REQUIRED by the workflow: absence becomes a meaningful failure.</summary>
    public Result<T, E> ToResult<E>(Func<E> errorIfMissing)
    {
        ArgumentNullException.ThrowIfNull(errorIfMissing);
        return Match(Result<T, E>.Ok, () => Result<T, E>.Fail(errorIfMissing()));
    }
}
