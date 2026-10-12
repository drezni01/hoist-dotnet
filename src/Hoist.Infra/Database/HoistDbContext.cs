using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// Maps the xh_* tables exactly as GORM / Hibernate created them for hoist-core (see docs/schema).
    /// Column names are snake_case of the property; every table has bigint id + version except xh_role,
    /// whose primary key is the name column.
    /// </summary>
    public class HoistDbContext : DbContext
    {
        private readonly TimeZoneInfo dbTimeZone;

        public HoistDbContext(DbContextOptions<HoistDbContext> options, HoistDbTimeZone dbTimeZone) : base(options)
        {
            this.dbTimeZone = dbTimeZone?.TimeZone ?? TimeZoneInfo.Utc;
        }

        public DbSet<AppConfig> Configs { get; set; }
        public DbSet<Preference> Preferences { get; set; }
        public DbSet<UserPreference> UserPreferences { get; set; }
        public DbSet<JsonBlob> JsonBlobs { get; set; }
        public DbSet<TrackLog> TrackLogs { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<RoleMember> RoleMembers { get; set; }

        private bool IsMySql => Database.ProviderName?.Contains("MySql") == true;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppConfig>(e =>
            {
                e.ToTable("xh_config");
                MapIdAndVersion(e);
                e.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
                e.Property(x => x.Value).HasColumnName("value").HasColumnType(TextType).IsRequired();
                e.Property(x => x.ValueType).HasColumnName("value_type").HasMaxLength(255).IsRequired();
                e.Property(x => x.Note).HasColumnName("note").HasMaxLength(1200);
                e.Property(x => x.ClientVisible).HasColumnName("client_visible").HasColumnType(BoolType).IsRequired();
                e.Property(x => x.LastUpdatedBy).HasColumnName("last_updated_by").HasMaxLength(50);
                e.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType(DateType).IsRequired();
                e.Property(x => x.GroupName).HasColumnName("group_name").HasMaxLength(255).IsRequired();
                e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_xh_config_name");
            });

            modelBuilder.Entity<Preference>(e =>
            {
                e.ToTable("xh_preference");
                MapIdAndVersion(e);
                e.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
                e.Property(x => x.Type).HasColumnName("type").HasMaxLength(20).IsRequired();
                e.Property(x => x.DefaultValue).HasColumnName("default_value").HasColumnType(TextType).IsRequired();
                e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(1200);
                e.Property(x => x.LastUpdatedBy).HasColumnName("last_updated_by").HasMaxLength(50);
                e.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType(DateType).IsRequired();
                e.Property(x => x.GroupName).HasColumnName("group_name").HasMaxLength(255).IsRequired();
                e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_xh_preference_name");
                e.HasMany(x => x.UserPreferences).WithOne(x => x.Preference).HasForeignKey(x => x.PreferenceId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserPreference>(e =>
            {
                e.ToTable("xh_user_preference");
                MapIdAndVersion(e);
                e.Property(x => x.Username).HasColumnName("username").HasMaxLength(50).IsRequired();
                e.Property(x => x.UserValue).HasColumnName("user_value").HasColumnType(TextType).IsRequired();
                e.Property(x => x.LastUpdatedBy).HasColumnName("last_updated_by").HasMaxLength(50);
                e.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType(DateType).IsRequired();
                e.Property(x => x.PreferenceId).HasColumnName("preference_id").IsRequired();
                e.HasIndex(x => x.Username).HasDatabaseName("idx_xh_user_preference_username");
                e.HasIndex(x => new { x.PreferenceId, x.Username }).IsUnique().HasDatabaseName("uk_xh_user_preference_pref_username");
            });

            modelBuilder.Entity<JsonBlob>(e =>
            {
                e.ToTable("xh_json_blob");
                MapIdAndVersion(e);
                e.Property(x => x.Token).HasColumnName("token").HasMaxLength(255).IsRequired();
                e.Property(x => x.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
                e.Property(x => x.Owner).HasColumnName("owner").HasMaxLength(50);
                e.Property(x => x.Acl).HasColumnName("acl").HasColumnType(TextType);
                e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
                e.Property(x => x.Value).HasColumnName("value").HasColumnType(TextType).IsRequired();
                e.Property(x => x.Meta).HasColumnName("meta").HasColumnType(TextType);
                e.Property(x => x.Description).HasColumnName("description").HasColumnType(TextType);
                e.Property(x => x.DateCreated).HasColumnName("date_created").HasColumnType(DateType).IsRequired();
                e.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType(DateType).IsRequired();
                e.Property(x => x.LastUpdatedBy).HasColumnName("last_updated_by").HasMaxLength(255);
                e.Property(x => x.ArchivedDate).HasColumnName("archived_date").IsRequired();
                e.Ignore(x => x.Archived);
                e.HasIndex(x => x.Token).IsUnique().HasDatabaseName("idx_xh_json_blob_token");
            });

            modelBuilder.Entity<TrackLog>(e =>
            {
                e.ToTable("xh_track_log");
                MapIdAndVersion(e);
                e.Property(x => x.Username).HasColumnName("username").HasMaxLength(50).IsRequired();
                e.Property(x => x.Impersonating).HasColumnName("impersonating").HasMaxLength(50);
                e.Property(x => x.DateCreated).HasColumnName("date_created").HasColumnType(DateType).IsRequired();
                e.Property(x => x.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
                e.Property(x => x.Msg).HasColumnName("msg").HasMaxLength(255).IsRequired();
                e.Property(x => x.Data).HasColumnName("data").HasColumnType(TextType);
                e.Property(x => x.Elapsed).HasColumnName("elapsed");
                e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(255).IsRequired();
                e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
                e.Property(x => x.LoadId).HasColumnName("load_id").HasMaxLength(8);
                e.Property(x => x.TabId).HasColumnName("tab_id").HasMaxLength(8);
                e.Property(x => x.Instance).HasColumnName("instance").HasMaxLength(50);
                e.Property(x => x.ClientAppCode).HasColumnName("client_app_code").HasMaxLength(50);
                e.Property(x => x.Browser).HasColumnName("browser").HasMaxLength(100);
                e.Property(x => x.Device).HasColumnName("device").HasMaxLength(100);
                e.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(255);
                e.Property(x => x.AppVersion).HasColumnName("app_version").HasMaxLength(100);
                e.Property(x => x.AppEnvironment).HasColumnName("app_environment").HasMaxLength(100);
                e.Property(x => x.Url).HasColumnName("url").HasMaxLength(500);
                e.HasIndex(x => x.Username).HasDatabaseName("idx_xh_track_log_username");
                e.HasIndex(x => x.DateCreated).HasDatabaseName("idx_xh_track_log_date_created");
                e.HasIndex(x => x.Category).HasDatabaseName("idx_xh_track_log_category");
                e.HasIndex(x => x.TabId).HasDatabaseName("idx_xh_track_log_tab_id");
                e.HasIndex(x => x.ClientAppCode).HasDatabaseName("idx_xh_track_log_client_app_code");
            });

            modelBuilder.Entity<Role>(e =>
            {
                e.ToTable("xh_role");
                e.HasKey(x => x.Name);
                e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).ValueGeneratedNever();
                e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
                e.Property(x => x.Category).HasColumnName("category").HasMaxLength(100);
                e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(1200);
                e.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType(DateType).IsRequired();
                e.Property(x => x.LastUpdatedBy).HasColumnName("last_updated_by").HasMaxLength(50).IsRequired();
                e.Ignore(x => x.Users);
                e.Ignore(x => x.DirectoryGroups);
                e.Ignore(x => x.Roles);
                e.HasMany(x => x.Members).WithOne(x => x.Role).HasForeignKey(x => x.RoleName).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RoleMember>(e =>
            {
                e.ToTable("xh_role_member");
                MapIdAndVersion(e);
                e.Property(x => x.Type).HasColumnName("type").HasMaxLength(255).HasConversion<string>().IsRequired();
                e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
                e.Property(x => x.DateCreated).HasColumnName("date_created").HasColumnType(DateType).IsRequired();
                e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(255).IsRequired();
                e.Property(x => x.RoleName).HasColumnName("role_id").HasMaxLength(255).IsRequired();
                e.HasIndex(x => new { x.Name, x.Type, x.RoleName }).IsUnique().HasDatabaseName("uk_xh_role_member_name_type_role");
            });

            // DATETIME columns hold wall-clock time in the database's zone; Hoist works in UTC.
            var toUtc = new ValueConverter<DateTime, DateTime>(
                v => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(v, DateTimeKind.Utc), dbTimeZone),
                v => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(v, DateTimeKind.Unspecified), dbTimeZone), DateTimeKind.Utc));
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var prop in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
                {
                    prop.SetValueConverter(toUtc);
                }
            }
        }

        private void MapIdAndVersion<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : class, IVersioned
        {
            e.Property<long>("Id").HasColumnName("id").ValueGeneratedOnAdd();
            e.HasKey("Id");
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
        }

        private string TextType => IsMySql ? "longtext" : "TEXT";
        private string BoolType => IsMySql ? "bit(1)" : "INTEGER";
        private string DateType => IsMySql ? "datetime(6)" : "TEXT";

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplyGormConventions();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplyGormConventions();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// GORM behaviour: version starts at 0 and increments on each update (optimistic locking against
        /// the original value); dateCreated / lastUpdated are auto-timestamped (TrackLog sets its own).
        /// </summary>
        private void ApplyGormConventions()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State == EntityState.Added)
                {
                    if (entry.Entity is IVersioned added) added.Version = 0;
                    SetIfPresent(entry, "LastUpdated", now, onlyIfDefault: false);
                    SetIfPresent(entry, "DateCreated", now, onlyIfDefault: true);
                }
                else if (entry.State == EntityState.Modified)
                {
                    if (entry.Entity is IVersioned modified)
                    {
                        var original = (long)entry.Property("Version").OriginalValue;
                        modified.Version = original + 1;
                    }
                    SetIfPresent(entry, "LastUpdated", now, onlyIfDefault: false);
                }
            }
        }

        private static void SetIfPresent(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string name, DateTime value, bool onlyIfDefault)
        {
            var prop = entry.Metadata.FindProperty(name);
            if (prop == null) return;
            var p = entry.Property(name);
            if (onlyIfDefault && p.CurrentValue is DateTime current && current != default) return;
            p.CurrentValue = value;
        }
    }

    /// <summary>The zone DATETIME columns are stored in (Hibernate wrote JVM-local time).</summary>
    public class HoistDbTimeZone
    {
        public HoistDbTimeZone(TimeZoneInfo timeZone)
        {
            TimeZone = timeZone;
        }

        public TimeZoneInfo TimeZone { get; }
    }
}
