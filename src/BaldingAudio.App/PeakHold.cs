namespace BaldingAudio.App;

/// <summary>
/// Holds the largest recent value, then lets it fall away.
/// </summary>
/// <para>
/// Exists for the settings window's balance meter, and pulled out of
/// <see cref="AppHost"/> for one reason: it is the only new piece of logic on the path
/// between a measurement and the number the user sets a threshold against, and it has
/// a time constant in it. Inline in the render tick it could only be checked by
/// standing up a WinForms timer, so it would have gone in untested - which is exactly
/// where a sign error or a wrong direction of decay hides, because the meter still
/// moves and just moves wrongly.
/// </para>
///
/// <para>
/// Deliberately driven by an explicit <c>dt</c> rather than a clock, so a check can
/// step it in exact increments instead of sleeping.
/// </para>
/// </summary>
public sealed class PeakHold
{
    private readonly double _fallSeconds;
    private readonly double _resetBelow;

    /// <summary>Seconds since the last value that beat the held peak.</summary>
    private double _elapsed;

    public PeakHold(double fallSeconds = 1.6, double resetBelow = 0.005)
    {
        _fallSeconds = fallSeconds > 0 ? fallSeconds : 1.0;
        _resetBelow = resetBelow;
    }

    /// <summary>
    /// The fall time constant, in seconds: the held value drops to 1/e of itself after
    /// this long with nothing stronger arriving. Reported so the window can quote the
    /// figure that actually governs the meter rather than a separate guess at it.
    /// </summary>
    public double FallSeconds => _fallSeconds;

    /// <summary>The held value, or 0 when nothing is being held.</summary>
    public double Value { get; private set; }

    /// <summary>True while a value is being held rather than the meter being empty.</summary>
    public bool Holding => Value != 0.0;

    /// <summary>
    /// Offers a sample. A stronger one is taken immediately and restarts the decay;
    /// anything weaker leaves the peak alone and lets the fall continue.
    /// </summary>
    /// <param name="sample">The new reading, or null when there is none.</param>
    /// <param name="dt">Seconds since the last call.</param>
    public void Push(double? sample, double dt)
    {
        if (sample is double v && Math.Abs(v) > Math.Abs(Value))
        {
            Value = v;
            _elapsed = 0;
            return;
        }

        if (Value == 0.0) return;

        // exp(-dt/tau) once per step, NOT exp(-elapsed/tau) against a running total.
        // The second form compounds, because the running total is applied again on every
        // call, so the fall is quadratic in log and the meter empties much faster than
        // the time constant claims. A 0.45 peak was down to 0.15 after 400 ms where a
        // 1.6 s constant implies 0.35 - fast enough that a user would have watched the
        // meter go blank between sounds, which is the exact failure the hold exists to
        // prevent.
        _elapsed += dt;
        Value *= Math.Exp(-dt / _fallSeconds);

        if (Math.Abs(Value) < _resetBelow)
        {
            Value = 0.0;
            _elapsed = 0;
        }
    }

    public void Clear()
    {
        Value = 0.0;
        _elapsed = 0;
    }
}
