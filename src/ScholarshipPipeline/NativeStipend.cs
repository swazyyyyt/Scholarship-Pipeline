using System.Runtime.InteropServices;

namespace ScholarshipPipeline;

/// <summary>
/// Assignment 8, Tasks 7-9: P/Invoke bindings to native/scholarship_native.c.
/// C export : int32_t sp_stipend_amount(int32_t score, int32_t monthly_income)   (cdecl)
/// C# types : int32_t = int (always 32 bit), no pointers, no native memory, so nothing has to be freed.
/// The library name has no prefix/suffix: the runtime resolves it to libscholarship_native.so (Linux),
/// scholarship_native.dll (Windows) or libscholarship_native.dylib (macOS) next to the application.
/// </summary>
internal static class NativeMethods
{
    [DllImport("scholarship_native", EntryPoint = "sp_stipend_amount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int StipendAmount(int score, int monthlyIncome);

    // Deliberately points to a library that does not exist: a REAL load failure for the failure demo and the tests
    // (a missing file cannot be tested with the real library once it is loaded into the process).
    [DllImport("scholarship_native_does_not_exist", EntryPoint = "sp_stipend_amount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int StipendAmountFromMissingLibrary(int score, int monthlyIncome);
}

/// <summary>Boundary contract of the stipend calculation (lets tests and demos replace the native call).</summary>
public interface IStipendCalculator
{
    /// <summary>Monthly stipend in whole currency units, or a clear failure.</summary>
    Result<int, AppError> Calculate(int score, decimal monthlyIncome);
}

/// <summary>
/// Adapter around the native call (the ONLY class that touches the native function).
///  1. validates domain input BEFORE the call (-> INVALID_STIPEND_INPUT, native code is not reached),
///  2. translates loading / binding failures into Result.Failure (NATIVE_LOAD_FAILED, NATIVE_BINDING_FAILED),
///  3. translates the native error value -1 into NATIVE_REJECTED_INPUT.
/// Income handling: whole currency units; a fractional income is truncated toward zero (documented in the README).
/// </summary>
public sealed class NativeStipendCalculator : IStipendCalculator
{
    public const int MinScore = 0, MaxScore = 200, MinIncome = 0, MaxIncome = 1_000_000;

    private readonly Func<int, int, int> _call;

    public NativeStipendCalculator(Func<int, int, int> nativeCall)
    {
        ArgumentNullException.ThrowIfNull(nativeCall);
        _call = nativeCall;
    }

    /// <summary>The real library next to the application.</summary>
    public static NativeStipendCalculator Real() => new(NativeMethods.StipendAmount);

    /// <summary>Binding to a library that does not exist: used for the load-failure demonstration and tests.</summary>
    public static NativeStipendCalculator WithMissingLibrary() => new(NativeMethods.StipendAmountFromMissingLibrary);

    public Result<int, AppError> Calculate(int score, decimal monthlyIncome)
    {
        if (score < MinScore || score > MaxScore)
            return Fail(ErrorKind.Domain, ErrorCodes.InvalidStipendInput,
                $"Score {score} is outside the valid range {MinScore}..{MaxScore} for a stipend.");
        if (monthlyIncome < MinIncome || monthlyIncome > MaxIncome)
            return Fail(ErrorKind.Domain, ErrorCodes.InvalidStipendInput,
                $"Monthly income {monthlyIncome} is outside the valid range {MinIncome}..{MaxIncome} for a stipend.");

        var income = (int)decimal.Truncate(monthlyIncome);
        int amount;
        try
        {
            amount = _call(score, income);
        }
        catch (DllNotFoundException ex)
        {
            return Fail(ErrorKind.Operational, ErrorCodes.NativeLoadFailed,
                $"Native library could not be loaded: {FirstLine(ex.Message)}");
        }
        catch (BadImageFormatException ex)
        {
            return Fail(ErrorKind.Operational, ErrorCodes.NativeLoadFailed,
                $"Native library has the wrong format or architecture: {FirstLine(ex.Message)}");
        }
        catch (EntryPointNotFoundException ex)
        {
            return Fail(ErrorKind.Operational, ErrorCodes.NativeBindingFailed,
                $"Native function 'sp_stipend_amount' was not found in the library: {FirstLine(ex.Message)}");
        }

        return amount < 0
            ? Fail(ErrorKind.Operational, ErrorCodes.NativeRejectedInput,
                $"Native library rejected the input (score {score}, income {income}) with code {amount}.")
            : Result<int, AppError>.Ok(amount);
    }

    private static string FirstLine(string message) =>
        message.Split('\n', 2)[0].Trim();

    private static Result<int, AppError> Fail(ErrorKind kind, string code, string message) =>
        Result<int, AppError>.Fail(new AppError(kind, code, message));
}
