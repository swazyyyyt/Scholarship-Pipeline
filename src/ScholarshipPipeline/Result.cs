namespace ScholarshipPipeline;

/// <summary>
/// Explicit outcome of an operation that can fail in an EXPECTED way: Success (carries a value) or Failure (carries an error).
/// The constructor is private and both cases are sealed and non-null by construction, so contradictory states
/// (a success without payload, a failure that "has a value") cannot be created. The payload of a case can only be read
/// after the case is known (pattern matching / Match), never through a shared property.
/// </summary>
public abstract record Result<T, E>
{
    private Result() { }

    public sealed record Success : Result<T, E>
    {
        public T Value { get; }

        internal Success(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }
    }

    public sealed record Failure : Result<T, E>
    {
        public E Error { get; }

        internal Failure(E error)
        {
            ArgumentNullException.ThrowIfNull(error);
            Error = error;
        }
    }

    public static Result<T, E> Ok(T value) => new Success(value);

    public static Result<T, E> Fail(E error) => new Failure(error);

    public bool IsSuccess => this is Success;

    /// <summary>Explicit case handler: exactly one branch runs.</summary>
    public R Match<R>(Func<T, R> onSuccess, Func<E, R> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return this switch
        {
            Success s => onSuccess(s.Value),
            Failure f => onFailure(f.Error),
            _ => throw new InvalidOperationException("Unreachable: Result has only two cases.")
        };
    }

    /// <summary>
    /// Map : Result&lt;T,E&gt; + Func&lt;T,U&gt; -> Result&lt;U,E&gt;.
    /// Applies a PURE function to a successful value and wraps the plain result in a new Success.
    /// On failure the function is NOT called and the same error is propagated.
    /// </summary>
    public Result<U, E> Map<U>(Func<T, U> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return this switch
        {
            Success s => Result<U, E>.Ok(f(s.Value)),
            Failure x => Result<U, E>.Fail(x.Error),
            _ => throw new InvalidOperationException("Unreachable: Result has only two cases.")
        };
    }

    /// <summary>
    /// Bind : Result&lt;T,E&gt; + Func&lt;T,Result&lt;U,E&gt;&gt; -> Result&lt;U,E&gt;.
    /// Continues with an operation that itself may fail. The Result returned by f is preserved as it is
    /// (NOT wrapped again, otherwise we would get Result&lt;Result&lt;U,E&gt;,E&gt;).
    /// On failure the function is NOT called and the same error is propagated.
    /// </summary>
    public Result<U, E> Bind<U>(Func<T, Result<U, E>> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return this switch
        {
            Success s => f(s.Value),
            Failure x => Result<U, E>.Fail(x.Error),
            _ => throw new InvalidOperationException("Unreachable: Result has only two cases.")
        };
    }
}
