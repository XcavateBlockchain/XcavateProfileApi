namespace XcavateProfileApi.Services;

/// <summary>
/// The clock the stored <c>createdAt</c> / <c>updatedAt</c> values come from.
/// </summary>
public static class Timestamps
{
    /// <summary>
    /// UTC now, truncated to the resolution the database keeps.
    /// </summary>
    /// <remarks>
    /// PostgreSQL's <c>timestamp with time zone</c> stores microseconds, while
    /// <see cref="DateTime"/> counts 100-nanosecond ticks. Writing an untruncated value means the
    /// timestamp a create or update returns carries three digits the next read cannot — so a client
    /// comparing the two, or holding one as a version marker, sees them differ for a record nobody
    /// touched. Truncating here makes the value handed back the value that was stored.
    /// </remarks>
    public static DateTime UtcNow()
    {
        var utc = DateTime.UtcNow;

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
