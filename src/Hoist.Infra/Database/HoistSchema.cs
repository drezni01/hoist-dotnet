using System.Data.Common;
using Hoist.Core.Configuration;
using Hoist.Core.Exceptions;
using Hoist.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// The xh_* tables live in their own schema with their own connection string, so EnsureCreated is
    /// enough: it creates the tables in an empty schema and is a no-op on an existing hoist-core schema.
    /// The schema is then checked against the EF model, so drift fails startup instead of failing a query
    /// later. See docs/decisions/0002-hoist-schema.md and 0003-validate-schema-at-startup.md.
    /// </summary>
    public class HoistSchema : IHoistSchema
    {
        private readonly IDbContextFactory<HoistDbContext> factory;
        private readonly HoistOptions options;
        private readonly ILogger<HoistSchema> logger;

        public HoistSchema(IDbContextFactory<HoistDbContext> factory, HoistOptions options, ILogger<HoistSchema> logger)
        {
            this.factory = factory;
            this.options = options;
            this.logger = logger;
        }

        public async Task EnsureSchemaAsync()
        {
            if (string.Equals(options.Database.Migrate, "EnsureCreated", StringComparison.OrdinalIgnoreCase))
            {
                await using var ctx = await factory.CreateDbContextAsync();
                if (await ctx.Database.EnsureCreatedAsync())
                {
                    logger.LogWarning("Created Hoist schema (xh_* tables) in an empty database");
                }
            }

            if (!options.Database.ValidateSchema) return;

            var problems = await FindProblemsAsync();
            if (problems.Count > 0)
            {
                var message = $"Hoist schema does not match the hoist-core v{HoistOptions.HoistCoreVersion} model: {string.Join("; ", problems)}. " +
                    "Apply the DDL from docs/schema/README.md, or set Hoist:Database:ValidateSchema=false to start anyway.";
                logger.LogError("{Message}", message);
                throw new HoistSchemaException(message);
            }
            logger.LogInformation("Validated Hoist schema");
        }

        public async Task<IReadOnlyList<string>> FindProblemsAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var sql = ctx.GetService<ISqlGenerationHelper>();
            var conn = ctx.Database.GetDbConnection();
            await ctx.Database.OpenConnectionAsync();  // connection errors propagate as themselves, not as missing tables
            try
            {
                var problems = new List<string>();
                foreach (var entity in ctx.Model.GetEntityTypes().OrderBy(e => e.GetTableName()))
                {
                    var table = entity.GetTableName();
                    var actual = await ReadColumnsAsync(conn, sql.DelimitIdentifier(table));
                    if (actual == null)
                    {
                        problems.Add($"missing table {table}");
                        continue;
                    }

                    foreach (var column in entity.GetProperties().Select(p => p.GetColumnName()))
                    {
                        if (!actual.Contains(column)) problems.Add($"missing column {table}.{column}");
                    }
                    var extra = actual.Except(entity.GetProperties().Select(p => p.GetColumnName()), StringComparer.OrdinalIgnoreCase).ToList();
                    if (extra.Count > 0)
                    {
                        logger.LogDebug("Unmapped columns in {Table}: {Columns}", table, string.Join(", ", extra));
                    }
                }
                return problems;
            }
            finally
            {
                await ctx.Database.CloseConnectionAsync();
            }
        }

        /// <summary>Column names of a table via an empty SELECT (works on any provider), or null if the table is missing.</summary>
        private static async Task<HashSet<string>> ReadColumnsAsync(DbConnection conn, string quotedTable)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {quotedTable} WHERE 1 = 0";
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
                return columns;
            }
            catch (DbException)
            {
                return null;
            }
        }
    }
}
