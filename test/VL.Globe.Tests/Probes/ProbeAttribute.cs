namespace VL.Globe.Tests.Probes;

/// <summary>A measurement run by hand, not a fact: skipped unless VLGLOBE_PROBE=1.</summary>
/// <remarks>
/// A probe used to return early when the variable was unset, and so counted as passed in every run.
/// Skipped, it shows in the summary as what it is.
/// </remarks>
public sealed class ProbeAttribute : FactAttribute
{
    public ProbeAttribute()
    {
        if (Environment.GetEnvironmentVariable("VLGLOBE_PROBE") != "1")
            Skip = "a probe: run by hand with VLGLOBE_PROBE=1";
    }
}
