namespace Hoist.Core.Models
{
    /// <summary>
    /// Port of the hoist-core HoistUser trait. Usernames are lowercase (HoistUser.validateUsername).
    /// Host apps can subclass and override FormatForJson to send extra fields to the client.
    /// Roles are not held here - IdentityService resolves them through IRoleService.
    /// </summary>
    public class HoistUser
    {
        public HoistUser(string username, string email = null, string displayName = null, bool active = true)
        {
            Username = username;
            Email = email;
            DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName;
            Active = active;
        }

        public string Username { get; }
        public string Email { get; }
        public string DisplayName { get; }
        public bool Active { get; }

        public virtual Dictionary<string, object> FormatForJson()
        {
            return new Dictionary<string, object>
            {
                ["username"] = Username,
                ["email"] = Email,
                ["displayName"] = DisplayName,
                ["active"] = Active
            };
        }

        public override bool Equals(object obj) => obj is HoistUser other && other.Username == Username;
        public override int GetHashCode() => Username?.GetHashCode() ?? 0;
        public override string ToString() => Username;
    }

    public static class HoistRoles
    {
        public const string HOIST_ADMIN = "HOIST_ADMIN";
        public const string HOIST_ADMIN_READER = "HOIST_ADMIN_READER";
        public const string HOIST_IMPERSONATOR = "HOIST_IMPERSONATOR";
        public const string HOIST_ROLE_MANAGER = "HOIST_ROLE_MANAGER";
    }
}
