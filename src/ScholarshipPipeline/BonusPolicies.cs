namespace ScholarshipPipeline;

/// <summary>
/// Two policies for the SAME rule: "how many bonus points does an application get?".
/// Both have the shape Func&lt;ScholarshipApplication, int&gt; and are chosen outside the pipeline.
/// </summary>
public static class BonusPolicies
{
    /// <summary>Need-based: income &lt;= 500 -> 20, income &lt;= 1000 -> 10, otherwise 0.</summary>
    public static int NeedBased(ScholarshipApplication application) =>
        application.MonthlyFamilyIncome switch
        {
            <= 500m => 20,
            <= 1000m => 10,
            _ => 0
        };

    /// <summary>Merit-based: GPA &gt;= 3.8 -> 20, GPA &gt;= 3.5 -> 10, otherwise 0.</summary>
    public static int MeritBased(ScholarshipApplication application) =>
        application.Gpa switch
        {
            >= 3.8m => 20,
            >= 3.5m => 10,
            _ => 0
        };
}
