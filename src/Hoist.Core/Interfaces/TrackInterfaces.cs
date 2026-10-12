using System.Linq.Expressions;
using Hoist.Core.Models;

namespace Hoist.Core.Interfaces
{
    /// <summary>Request headers TrackService records (User-Agent and client hints). Null outside a request.</summary>
    public interface ITrackRequestInfo
    {
        bool HasRequest { get; }
        string UserAgent { get; }
        string SecChUa { get; }
        string SecChUaPlatform { get; }
    }

    public interface ITrackLogRepository
    {
        Task SaveAsync(TrackLog log);
        Task<List<TrackLog>> ListAllAsync();

        /// <summary>Recent 'Client Error' rows for a user and load, newest first (for comment merging).</summary>
        Task<List<TrackLog>> FindRecentClientErrorsAsync(string username, string loadId, DateTime since, int max);

        Task UpdateDataAsync(long id, string data);

        /// <summary>Rows in [start, end) matching the filter, newest first, at most maxRows.</summary>
        Task<List<TrackLog>> QueryAsync(DateTime start, DateTime end, Expression<Func<TrackLog, bool>> filter, int maxRows);

        /// <summary>Sorted distinct non-null values of a column (admin filter lookups).</summary>
        Task<List<string>> DistinctValuesAsync(Expression<Func<TrackLog, string>> column);
    }
}
