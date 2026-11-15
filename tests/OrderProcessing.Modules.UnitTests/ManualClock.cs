namespace OrderProcessing.Modules.UnitTests;

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
