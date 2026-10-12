using System.Collections.Concurrent;
using Hoist.Infra.Database;
using Hoist.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoist.Tests.Schema
{
    /// <summary>
    /// Hoist's SQL is not logged unless Hoist:Database:LogSql is set: EF uses one log category for every
    /// DbContext, so a host logging its own SQL (e.g. Database.Command at Information) would otherwise get
    /// every Hoist query too. Command errors are still logged.
    /// </summary>
    [TestClass]
    public class TestHoistSqlLogging
    {
        private const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

        private static async Task<List<LogEntry>> RunQueryAsync(bool logSql, string sql = null)
        {
            var capture = new CapturingLoggerProvider();
            using var ts = await TestServices.CreateAsync(
                o => o.Database.LogSql = logSql,
                s => s.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture)));
            capture.Entries.Clear();

            await using var ctx = await ts.Get<IDbContextFactory<HoistDbContext>>().CreateDbContextAsync();
            if (sql == null) await ctx.Configs.AsNoTracking().ToListAsync();
            else await Assert.ThrowsExceptionAsync<Microsoft.Data.Sqlite.SqliteException>(() => ctx.Database.ExecuteSqlRawAsync(sql));
            return capture.Entries.ToList();
        }

        [TestMethod]
        public async Task TestSqlNotLoggedByDefault()
        {
            var entries = await RunQueryAsync(logSql: false);
            Assert.IsFalse(entries.Any(e => e.Category == CommandCategory), string.Join("\n", entries.Select(e => e.Message)));
        }

        [TestMethod]
        public async Task TestSqlLoggedWhenEnabled()
        {
            var entries = await RunQueryAsync(logSql: true);
            Assert.IsTrue(entries.Any(e => e.Category == CommandCategory && e.Level == LogLevel.Information && e.Message.Contains("xh_config")));
        }

        [TestMethod]
        public async Task TestFailedCommandsStillLogged()
        {
            var entries = await RunQueryAsync(logSql: false, sql: "SELECT * FROM no_such_table");
            Assert.IsTrue(entries.Any(e => e.Category == CommandCategory && e.Level == LogLevel.Error));
        }

        public record LogEntry(string Category, LogLevel Level, string Message);

        public class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<LogEntry> Entries { get; } = new ConcurrentQueue<LogEntry>();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

            public void Dispose()
            {
            }

            private class CapturingLogger : ILogger
            {
                private readonly string category;
                private readonly ConcurrentQueue<LogEntry> entries;

                public CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
                {
                    this.category = category;
                    this.entries = entries;
                }

                public IDisposable BeginScope<TState>(TState state) => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
