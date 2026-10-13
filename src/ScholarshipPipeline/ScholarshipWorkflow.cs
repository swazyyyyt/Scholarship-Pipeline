namespace ScholarshipPipeline;

/// <summary>The visible application workflow: stage 1 -> stage 2 -> stage 3. Pure.</summary>
public static class ScholarshipWorkflow
{
    public static ScholarshipResult Run(
        IReadOnlyList<ScholarshipApplication> applications,
        PipelineSettings settings,
        Func<ScholarshipApplication, int> bonusPolicy)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bonusPolicy);
        if (settings.MinGpa < 0m || settings.MinGpa > 4m)
            throw new ArgumentOutOfRangeException(nameof(settings), "MinGpa must be between 0 and 4.");
        ArgumentOutOfRangeException.ThrowIfNegative(settings.AwardSlots);

        var eligible = Stages.SelectEligible(applications, settings.MinGpa);   // stage 1
        var scored = Stages.ScoreApplications(eligible, bonusPolicy);          // stage 2
        return Stages.BuildResult(scored, settings.AwardSlots);                // stage 3
    }
}
