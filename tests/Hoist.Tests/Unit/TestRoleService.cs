using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;

namespace Hoist.Tests.Unit
{
    /// <summary>Port of DefaultRoleService (read side) - docs/api/roles.md.</summary>
    [TestClass]
    public class TestRoleService
    {
        [TestMethod]
        public async Task TestRequiredRolesAndConfigCreated()
        {
            using var ts = await TestServices.CreateAsync();
            var roles = await ts.Get<IRoleRepository>().ListAsync();
            CollectionAssert.AreEquivalent(new[] { "HOIST_ADMIN", "HOIST_ADMIN_READER", "HOIST_IMPERSONATOR", "HOIST_ROLE_MANAGER" }, roles.Select(r => r.Name).ToArray());
            Assert.IsTrue(roles.All(r => r.Category == "Hoist" && r.LastUpdatedBy == "defaultRoleService"));

            var reader = roles.Single(r => r.Name == "HOIST_ADMIN_READER");
            Assert.AreEqual(1, reader.Members.Count);
            Assert.AreEqual(RoleMemberType.ROLE, reader.Members[0].Type);
            Assert.AreEqual("HOIST_ADMIN", reader.Members[0].Name);

            Assert.AreEqual(300, (await ts.Get<ConfigService>().GetMapAsync("xhRoleModuleConfig"))["refreshIntervalSecs"].GetValue<int>());
        }

        [TestMethod]
        public async Task TestRoleInheritanceThroughRoleMembers()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<DefaultRoleService>();
            await svc.AssignRoleAsync("Alice", "HOIST_ADMIN");

            var roles = await svc.GetRolesForUserAsync("alice");
            CollectionAssert.AreEquivalent(new[] { "HOIST_ADMIN", "HOIST_ADMIN_READER", "HOIST_IMPERSONATOR" }, roles.ToArray());

            // Lookups are case-insensitive; stored USER members are lowercased.
            Assert.AreEqual(3, (await svc.GetRolesForUserAsync("ALICE")).Count);
            CollectionAssert.AreEqual(new[] { "alice" }, (await svc.GetUsersForRoleAsync("HOIST_ADMIN_READER")).ToArray());
        }

        [TestMethod]
        public async Task TestMultiLevelInheritance()
        {
            using var ts = await TestServices.CreateAsync();
            var repo = ts.Get<IRoleRepository>();
            await repo.SaveAsync(new Role { Name = "APP_READER", LastUpdatedBy = "test", Members = { new RoleMember { Type = RoleMemberType.ROLE, Name = "APP_EDITOR", CreatedBy = "test" } } });
            await repo.SaveAsync(new Role { Name = "APP_EDITOR", LastUpdatedBy = "test", Members = { new RoleMember { Type = RoleMemberType.ROLE, Name = "APP_OWNER", CreatedBy = "test" } } });
            await repo.SaveAsync(new Role { Name = "APP_OWNER", LastUpdatedBy = "test", Members = { new RoleMember { Type = RoleMemberType.USER, Name = "Olivia", CreatedBy = "test" } } });

            var svc = ts.Get<DefaultRoleService>();
            await svc.RefreshAsync();
            CollectionAssert.AreEquivalent(new[] { "APP_READER", "APP_EDITOR", "APP_OWNER" }, (await svc.GetRolesForUserAsync("olivia")).ToArray());
        }

        [TestMethod]
        public async Task TestBootstrapAdminOnlyOutsideProduction()
        {
            using (var dev = await TestServices.CreateAsync(o => o.BootstrapAdminUser = "Boss"))
            {
                var roles = await dev.Get<DefaultRoleService>().GetRolesForUserAsync("boss");
                CollectionAssert.AreEquivalent(new[] { "HOIST_ADMIN", "HOIST_ADMIN_READER", "HOIST_ROLE_MANAGER" }, roles.ToArray());
            }
            using (var prod = await TestServices.CreateAsync(o => { o.BootstrapAdminUser = "boss"; o.Environment = "Production"; }))
            {
                Assert.AreEqual(0, (await prod.Get<DefaultRoleService>().GetRolesForUserAsync("boss")).Count);
            }
        }
    }
}
