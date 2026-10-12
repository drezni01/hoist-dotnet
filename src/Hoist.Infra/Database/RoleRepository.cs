using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Hoist.Infra.Database
{
    /// <summary>xh_role / xh_role_member access. Members are owned by their role (cascade all-delete-orphan).</summary>
    public class RoleRepository : IRoleRepository
    {
        private readonly IDbContextFactory<HoistDbContext> factory;

        public RoleRepository(IDbContextFactory<HoistDbContext> factory)
        {
            this.factory = factory;
        }

        public async Task<List<Role>> ListAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Roles.AsNoTracking().Include(r => r.Members).OrderBy(r => r.Name).ToListAsync();
        }

        public async Task<Role> FindAsync(string name)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Roles.AsNoTracking().Include(r => r.Members).FirstOrDefaultAsync(r => r.Name == name);
        }

        public async Task<Role> SaveAsync(Role role)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = await ctx.Roles.Include(r => r.Members).FirstOrDefaultAsync(r => r.Name == role.Name);
            if (existing == null)
            {
                foreach (var m in role.Members) m.RoleName = role.Name;
                ctx.Roles.Add(role);
                await ctx.SaveChangesAsync();
                return role;
            }

            // Optimistic lock against the version the caller loaded.
            ctx.Entry(existing).Property(r => r.Version).OriginalValue = role.Version;
            existing.Category = role.Category;
            existing.Notes = role.Notes;
            existing.LastUpdatedBy = role.LastUpdatedBy;

            var incoming = role.Members.Select(m => (m.Type, m.Name)).ToHashSet();
            foreach (var removed in existing.Members.Where(m => !incoming.Contains((m.Type, m.Name))).ToList())
            {
                existing.Members.Remove(removed);
                ctx.RoleMembers.Remove(removed);
            }
            var current = existing.Members.Select(m => (m.Type, m.Name)).ToHashSet();
            foreach (var added in role.Members.Where(m => !current.Contains((m.Type, m.Name))))
            {
                existing.Members.Add(new RoleMember { Type = added.Type, Name = added.Name, CreatedBy = added.CreatedBy, RoleName = existing.Name });
            }

            // Member changes alone still bump the role's version and lastUpdated, as GORM does for a dirty collection.
            ctx.Entry(existing).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
            return existing;
        }

        public async Task DeleteAsync(string name)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var role = await ctx.Roles.Include(r => r.Members).FirstOrDefaultAsync(r => r.Name == name);
            if (role == null) return;
            ctx.Roles.Remove(role);
            await ctx.SaveChangesAsync();
        }
    }
}
