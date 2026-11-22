namespace PdfNotes.TestSupport;

/// <summary>
/// A clock that moves only when told: timestamps and timers both follow Advance, and a timer that comes due
/// fires inside <see cref="Advance(TimeSpan)"/> on the calling thread (pointer and hold tests, 1.4 plan Task 4).
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly List<Timer> timers = [];

    public long Ticks { get; private set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Ticks;
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, () => callback(state));
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Timers waiting to fire.</summary>
    public int Pending => timers.Count;

    /// <summary>Moves the clock on by <paramref name="ms"/>, firing the timers that come due in the order they do.</summary>
    public void Advance(int ms) => Advance(TimeSpan.FromMilliseconds(ms));

    public void Advance(TimeSpan by)
    {
        long end = Ticks + by.Ticks;
        while (timers.Where(t => t.Due <= end).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            Ticks = Math.Max(Ticks, next.Due);
            if (next.Period > 0) next.Due += next.Period;
            else timers.Remove(next);
            next.Fire();
        }
        Ticks = end;
    }

    private sealed class Timer(ManualClock owner, Action fire) : ITimer
    {
        public long Due, Period;
        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.timers.Remove(this);
            if (dueTime == Timeout.InfiniteTimeSpan) return true;
            Due = owner.Ticks + dueTime.Ticks;
            Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            owner.timers.Add(this);
            return true;
        }

        public void Dispose() => owner.timers.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
