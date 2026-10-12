namespace Hoist.Core.Utils
{
    /// <summary>Java TimeZone equivalents. Zone ids sent to the client are IANA ids (e.g. "America/New_York").</summary>
    public static class TimeZones
    {
        /// <summary>The zone for a Java/IANA id, or null when unknown (Java: TimeZone.availableIDs.contains(id)).</summary>
        public static TimeZoneInfo Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (id == "UTC" || id == "GMT") return TimeZoneInfo.Utc;
            return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : null;
        }

        public static string IanaId(TimeZoneInfo tz)
        {
            if (tz == TimeZoneInfo.Utc) return "UTC";
            if (tz.HasIanaId) return tz.Id;
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : tz.Id;
        }

        /// <summary>TimeZone.getOffset(now): current offset from UTC in millis, including DST.</summary>
        public static long OffsetMillis(TimeZoneInfo tz) => (long)tz.GetUtcOffset(DateTime.UtcNow).TotalMilliseconds;
    }
}
