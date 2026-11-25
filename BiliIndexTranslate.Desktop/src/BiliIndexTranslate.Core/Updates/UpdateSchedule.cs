namespace BiliIndexTranslate.Core.Updates;

public enum UpdateCheckAction
{
    /// <summary>Do not check (portable build, development run, or automatic checking is off).</summary>
    None,

    /// <summary>A check is due.</summary>
    Now,

    /// <summary>Check once <see cref="UpdateCheckPlan.Delay"/> has passed.</summary>
    After,
}

public readonly record struct UpdateCheckPlan(UpdateCheckAction Action, TimeSpan Delay)
{
    public static UpdateCheckPlan None => new(UpdateCheckAction.None, TimeSpan.Zero);
    public static UpdateCheckPlan Now => new(UpdateCheckAction.Now, TimeSpan.Zero);
    public static UpdateCheckPlan After(TimeSpan delay) => new(UpdateCheckAction.After, delay);
}

/// <summary>When the next automatic update check happens. Pure, so the timing rules can be tested.</summary>
public static class UpdateSchedule
{
    /// <summary>Wait after start before the first check, so the check does not compete with start-up.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>Time between two checks while the app keeps running.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <param name="isInstalled">The app was installed by the Velopack installer.</param>
    /// <param name="enabled">The "check for updates automatically" option.</param>
    /// <param name="lastCheck">When the last check finished in this run, or null if none has yet.</param>
    public static UpdateCheckPlan Next(bool isInstalled, bool enabled, DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (!isInstalled || !enabled) return UpdateCheckPlan.None;
        if (lastCheck is null) return UpdateCheckPlan.After(StartupDelay);

        var due = lastCheck.Value + Interval;
        if (due <= now) return UpdateCheckPlan.Now;
        // A clock that was set back must not postpone the check by more than one interval.
        var remaining = due - now;
        return UpdateCheckPlan.After(remaining > Interval ? Interval : remaining);
    }
}
