namespace TogetherServer;

// Pure retained-history projection. A missing interval is missing evidence,
// never a claim that the server was offline or available for that day.
internal static class WeeklyRuntimeProjection
{
    internal static IReadOnlyList<WeeklyRuntimeDay> ByDay(DateTimeOffset windowStart,
        DateTimeOffset windowEnd, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var result = new List<WeeklyRuntimeDay>();
        var midnight = new DateTimeOffset(windowStart.UtcDateTime.Date, TimeSpan.Zero);
        // A rolling seven-day window intersects at most eight UTC calendar days.
        while (midnight < windowEnd && result.Count < 8)
        {
            var start = midnight < windowStart ? windowStart : midnight;
            var end = midnight.AddDays(1) > windowEnd ? windowEnd : midnight.AddDays(1);
            var clipped = intervals.Where(item => item.Start < end && item.End > start)
                .Select(item => (Start: item.Start < start ? start : item.Start,
                    End: item.End > end ? end : item.End))
                .Where(item => item.Start < item.End)
                .OrderBy(item => item.Start).ThenBy(item => item.End).ToList();
            long ticks = 0;
            DateTimeOffset? activeStart = null;
            DateTimeOffset? activeEnd = null;
            foreach (var interval in clipped)
            {
                if (activeStart is null)
                {
                    activeStart = interval.Start;
                    activeEnd = interval.End;
                }
                else if (interval.Start <= activeEnd!.Value)
                {
                    if (interval.End > activeEnd.Value) activeEnd = interval.End;
                }
                else
                {
                    ticks += (activeEnd!.Value - activeStart.Value).Ticks;
                    activeStart = interval.Start;
                    activeEnd = interval.End;
                }
            }
            if (activeStart is not null) ticks += (activeEnd!.Value - activeStart.Value).Ticks;
            result.Add(new(start.ToUniversalTime(), end.ToUniversalTime(),
                clipped.Count == 0 ? null : ticks / TimeSpan.TicksPerSecond, clipped.Count));
            midnight = midnight.AddDays(1);
        }
        return result;
    }
}
