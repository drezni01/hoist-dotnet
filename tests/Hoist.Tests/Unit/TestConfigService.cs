using System.Text.Json.Nodes;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;

namespace Hoist.Tests.Unit
{
    /// <summary>Port of ConfigService / AppConfig behaviour - docs/api/config.md.</summary>
    [TestClass]
    public class TestConfigService
    {
        [TestMethod]
        public async Task TestBuiltInConfigsCreatedOnInit()
        {
            using var ts = await TestServices.CreateAsync();
            var repo = ts.Get<IConfigRepository>();
            var all = await repo.ListAsync();
            Assert.AreEqual(40, all.Count); // 39 from BootStrap + xhRoleModuleConfig from DefaultRoleService

            var tz = all.Single(c => c.Name == "xhAppTimeZone");
            Assert.AreEqual("UTC", tz.Value);
            Assert.AreEqual("string", tz.ValueType);
            Assert.IsTrue(tz.ClientVisible);
            Assert.AreEqual("xh.io", tz.GroupName);
            Assert.AreEqual("hoist-bootstrap", tz.LastUpdatedBy);

            // json defaults are Jackson pretty-printed; typed configs seed an empty object.
            Assert.AreEqual("{\n  \"app\" : -1\n}", all.Single(c => c.Name == "xhAutoRefreshIntervals").Value);
            Assert.AreEqual("[ ]", all.Single(c => c.Name == "xhAppInstances").Value);
            Assert.AreEqual("{ }", all.Single(c => c.Name == "xhActivityTrackingConfig").Value);
        }

        [TestMethod]
        public async Task TestEnsureRequiredDoesNotOverwriteExisting()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            await svc.SetValueAsync("xhAppTimeZone", "America/New_York", "admin");
            await svc.EnsureRequiredConfigsCreatedAsync(BuiltInSpecs.Configs);
            Assert.AreEqual("America/New_York", await svc.GetStringAsync("xhAppTimeZone"));
        }

        [TestMethod]
        public async Task TestTypedGetters()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            await svc.EnsureRequiredConfigsCreatedAsync(new[]
            {
                new ConfigSpec { Name = "tInt", ValueType = "int", DefaultValue = 42 },
                new ConfigSpec { Name = "tLong", ValueType = "long", DefaultValue = 9000000000L },
                new ConfigSpec { Name = "tDouble", ValueType = "double", DefaultValue = 1.5 },
                new ConfigSpec { Name = "tBool", ValueType = "bool", DefaultValue = true },
                new ConfigSpec { Name = "tMap", ValueType = "json", DefaultValue = new JsonObject { ["a"] = 1 } },
                new ConfigSpec { Name = "tList", ValueType = "json", DefaultValue = new JsonArray(1, 2) }
            });

            Assert.AreEqual(42, await svc.GetIntAsync("tInt"));
            Assert.AreEqual(9000000000L, await svc.GetLongAsync("tLong"));
            Assert.AreEqual(1.5, await svc.GetDoubleAsync("tDouble"));
            Assert.AreEqual(true, await svc.GetBoolAsync("tBool"));
            Assert.AreEqual(1, (await svc.GetMapAsync("tMap"))["a"].GetValue<int>());
            Assert.AreEqual(2, (await svc.GetListAsync("tList")).Count);
        }

        [TestMethod]
        public async Task TestMissingAndWrongTypeErrors()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();

            var missing = await Assert.ThrowsExceptionAsync<Core.Exceptions.HoistException>(() => svc.GetStringAsync("nope"));
            Assert.AreEqual("No config found with name: [nope]", missing.Message);
            Assert.AreEqual("fallback", await svc.GetStringAsync("nope", "fallback"));

            var wrongType = await Assert.ThrowsExceptionAsync<Core.Exceptions.HoistException>(() => svc.GetIntAsync("xhAppTimeZone"));
            Assert.AreEqual("Unexpected type for config: [xhAppTimeZone] | config is string | expected int", wrongType.Message);
        }

        [TestMethod]
        public async Task TestStringIfSetAndStringList()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            Assert.IsNull(await svc.GetStringIfSetAsync("xhEmailSupport"));

            await svc.EnsureRequiredConfigsCreatedAsync(new[]
            {
                new ConfigSpec { Name = "listA", ValueType = "string", DefaultValue = "x, y ,[listB]" },
                new ConfigSpec { Name = "listB", ValueType = "string", DefaultValue = "z" }
            });
            CollectionAssert.AreEqual(new[] { "x", "y", "z" }, (await svc.GetStringListAsync("listA")).ToArray());
        }

        [TestMethod]
        public async Task TestPwdIsEncryptedAtRestAndDecryptedOnRead()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            await svc.EnsureRequiredConfigsCreatedAsync(new[] { new ConfigSpec { Name = "tPwd", ValueType = "pwd", DefaultValue = "hunter2" } });

            var stored = (await ts.Get<IConfigRepository>().FindByNameAsync("tPwd")).Value;
            Assert.AreNotEqual("hunter2", stored);
            Assert.AreEqual("hunter2", await svc.GetPwdAsync("tPwd"));

            await svc.SetValueAsync("tPwd", "changed", "admin");
            Assert.AreEqual("changed", await svc.GetPwdAsync("tPwd"));
        }

        [TestMethod]
        public async Task TestClientConfigOnlyClientVisibleWithTypedDefaultsAndObscuredPwd()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            await svc.EnsureRequiredConfigsCreatedAsync(new[]
            {
                new ConfigSpec { Name = "visiblePwd", ValueType = "pwd", DefaultValue = "secret", ClientVisible = true },
                new ConfigSpec { Name = "hiddenStr", ValueType = "string", DefaultValue = "x" }
            });

            var client = await svc.GetClientConfigAsync();
            Assert.IsFalse(client.ContainsKey("hiddenStr"));
            Assert.IsFalse(client.ContainsKey("xhEnvPollConfig"));
            Assert.AreEqual("*********", client["visiblePwd"].GetValue<string>());
            Assert.AreEqual(false, client["xhEnableImpersonation"].GetValue<bool>());
            Assert.AreEqual(2000, client["xhActivityTrackingConfig"]["maxDataLength"].GetValue<int>());
            Assert.AreEqual(16, client.Count);
        }

        [TestMethod]
        public async Task TestInstanceConfigOverridesStoredValue()
        {
            using var ts = await TestServices.CreateAsync(o => o.InstanceConfig["xhAppTimeZone"] = "Europe/London");
            var svc = ts.Get<ConfigService>();
            Assert.AreEqual("Europe/London", await svc.GetStringAsync("xhAppTimeZone"));
            Assert.AreEqual("Europe/London", (await svc.GetClientConfigAsync())["xhAppTimeZone"].GetValue<string>());
        }

        [TestMethod]
        public async Task TestInvalidValuesRejected()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            await Assert.ThrowsExceptionAsync<Core.Exceptions.ValidationException>(() => svc.SetValueAsync("xhEnableImpersonation", "maybe", "admin"));
            await Assert.ThrowsExceptionAsync<Core.Exceptions.ValidationException>(() => svc.SetValueAsync("xhFlags", "{not json", "admin"));
            var typed = await Assert.ThrowsExceptionAsync<Core.Exceptions.ValidationException>(() => svc.SetValueAsync("xhAlertBannerConfig", "{\"enabled\":\"yes\"}", "admin"));
            StringAssert.Contains(typed.Message, "Cannot apply value to AlertBannerConfig: Field 'enabled' on AlertBannerConfig expects Boolean but got String");
        }

        [TestMethod]
        public async Task TestVersionIncrementsOnUpdate()
        {
            using var ts = await TestServices.CreateAsync();
            var svc = ts.Get<ConfigService>();
            var repo = ts.Get<IConfigRepository>();
            Assert.AreEqual(0, (await repo.FindByNameAsync("xhAppTimeZone")).Version);
            await svc.SetValueAsync("xhAppTimeZone", "Asia/Tokyo", "admin");
            var updated = await repo.FindByNameAsync("xhAppTimeZone");
            Assert.AreEqual(1, updated.Version);
            Assert.AreEqual("admin", updated.LastUpdatedBy);
        }
    }
}
