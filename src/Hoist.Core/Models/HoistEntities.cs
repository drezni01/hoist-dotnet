namespace Hoist.Core.Models
{
    /// <summary>
    /// GORM domain objects get a bigint id and a bigint version (optimistic lock, incremented on every
    /// update). Infra increments Version on save so EF can share a database with a running Grails app.
    /// </summary>
    public interface IVersioned
    {
        long Version { get; set; }
    }

    /// <summary>hoist-core config/AppConfig -> xh_config</summary>
    public class AppConfig : IVersioned
    {
        public static readonly string[] Types = { "string", "int", "long", "double", "bool", "json", "pwd" };
        public const string None = "none";

        public long Id { get; set; }
        public long Version { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string ValueType { get; set; } = "string";
        public string Note { get; set; }
        public bool ClientVisible { get; set; }
        public string LastUpdatedBy { get; set; }
        public DateTime LastUpdated { get; set; }
        public string GroupName { get; set; } = "Default";
    }

    /// <summary>hoist-core pref/Preference -> xh_preference</summary>
    public class Preference : IVersioned
    {
        public static readonly string[] Types = { "string", "int", "long", "double", "bool", "json" };

        public Preference()
        {
            UserPreferences = new List<UserPreference>();
        }

        public long Id { get; set; }
        public long Version { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } = "string";
        public string DefaultValue { get; set; }
        public string Notes { get; set; }
        public string LastUpdatedBy { get; set; }
        public DateTime LastUpdated { get; set; }
        public string GroupName { get; set; } = "Default";
        public List<UserPreference> UserPreferences { get; set; }
    }

    /// <summary>hoist-core pref/UserPreference -> xh_user_preference</summary>
    public class UserPreference : IVersioned
    {
        public long Id { get; set; }
        public long Version { get; set; }
        public string Username { get; set; }
        public string UserValue { get; set; }
        public string LastUpdatedBy { get; set; }
        public DateTime LastUpdated { get; set; }
        public long PreferenceId { get; set; }
        public Preference Preference { get; set; }
    }

    /// <summary>hoist-core jsonblob/JsonBlob -> xh_json_blob</summary>
    public class JsonBlob : IVersioned
    {
        public long Id { get; set; }
        public long Version { get; set; }
        public string Token { get; set; } = NewToken();
        public string Type { get; set; }
        public string Owner { get; set; }
        public string Acl { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Meta { get; set; }
        public string Description { get; set; }
        public DateTime DateCreated { get; set; }
        public DateTime LastUpdated { get; set; }
        public string LastUpdatedBy { get; set; }

        /// <summary>Epoch millis when archived, 0 when active.</summary>
        public long ArchivedDate { get; set; }

        public bool Archived => ArchivedDate > 0;

        public static string NewToken() => Guid.NewGuid().ToString().Substring(0, 8);
    }

    /// <summary>hoist-core track/TrackLog -> xh_track_log (no lastUpdated; dateCreated set explicitly)</summary>
    public class TrackLog : IVersioned
    {
        public const int MaxMsgLength = 255;
        public const int MaxUrlLength = 500;

        public long Id { get; set; }
        public long Version { get; set; }
        public string Username { get; set; }
        public string Impersonating { get; set; }
        public DateTime DateCreated { get; set; }
        public string Category { get; set; }
        public string Msg { get; set; }
        public string Data { get; set; }
        public int? Elapsed { get; set; }
        public string Severity { get; set; }
        public string CorrelationId { get; set; }
        public string LoadId { get; set; }
        public string TabId { get; set; }
        public string Instance { get; set; }
        public string ClientAppCode { get; set; }
        public string Browser { get; set; }
        public string Device { get; set; }
        public string UserAgent { get; set; }
        public string AppVersion { get; set; }
        public string AppEnvironment { get; set; }
        public string Url { get; set; }
    }

    /// <summary>hoist-core role/provided/Role -> xh_role (primary key is the name column)</summary>
    public class Role : IVersioned
    {
        public Role()
        {
            Members = new List<RoleMember>();
        }

        public string Name { get; set; }
        public long Version { get; set; }
        public string Category { get; set; }
        public string Notes { get; set; }
        public DateTime LastUpdated { get; set; }
        public string LastUpdatedBy { get; set; }
        public List<RoleMember> Members { get; set; }

        public IEnumerable<string> Users => Members.Where(m => m.Type == RoleMemberType.USER).Select(m => m.Name.ToLowerInvariant());
        public IEnumerable<string> DirectoryGroups => Members.Where(m => m.Type == RoleMemberType.DIRECTORY_GROUP).Select(m => m.Name);
        public IEnumerable<string> Roles => Members.Where(m => m.Type == RoleMemberType.ROLE).Select(m => m.Name);
    }

    public enum RoleMemberType
    {
        USER,
        DIRECTORY_GROUP,
        ROLE
    }

    /// <summary>hoist-core role/provided/RoleMember -> xh_role_member</summary>
    public class RoleMember : IVersioned
    {
        public long Id { get; set; }
        public long Version { get; set; }
        public RoleMemberType Type { get; set; }
        public string Name { get; set; }
        public DateTime DateCreated { get; set; }
        public string CreatedBy { get; set; }
        public string RoleName { get; set; }
        public Role Role { get; set; }
    }
}
