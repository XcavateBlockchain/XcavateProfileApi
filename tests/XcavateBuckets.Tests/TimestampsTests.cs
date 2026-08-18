using XcavateProfileApi.Services;

namespace XcavateBuckets.Tests;

/// <summary>
/// Pins the resolution of the stored timestamps. The in-memory SQLite the rest of this suite runs on
/// keeps a DateTime's full 100-nanosecond precision, so nothing else here would notice a value that
/// PostgreSQL will quietly truncate on the way in — and the mismatch only shows up as a create
/// response disagreeing with the next read.
/// </summary>
[TestFixture]
public class TimestampsTests
{
    [Test]
    public void UtcNow_is_truncated_to_the_resolution_postgres_stores()
    {
        var values = Enumerable.Range(0, 200).Select(_ => Timestamps.UtcNow()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                values.Where(v => v.Ticks % TimeSpan.TicksPerMicrosecond != 0), Is.Empty,
                "a sub-microsecond component cannot survive a round trip through timestamptz");
            Assert.That(values, Has.All.Matches<DateTime>(v => v.Kind == DateTimeKind.Utc));
        });
    }

    [Test]
    public void UtcNow_still_tells_the_time()
    {
        Assert.That(
            Timestamps.UtcNow(),
            Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
            "truncation must not shift the value");
    }
}
