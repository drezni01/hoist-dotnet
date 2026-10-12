using System.Text.Json.Nodes;
using Hoist.Core.Configuration;

namespace Hoist.Tests.Unit
{
    /// <summary>Port of TypedConfigMap binding: declared fields only, defaults applied, strict types.</summary>
    [TestClass]
    public class TestTypedConfig
    {
        private readonly TypedConfigRegistry registry = TypedConfigRegistry.BuiltIn;

        [TestMethod]
        public void TestDefaultsForEmptyInput()
        {
            var resolved = registry.Resolve("ActivityTrackingConfig", new JsonObject());
            var expected = JsonNode.Parse("""
                {"enabled":true,"logData":false,"maxDataLength":2000,"maxElapsedMins":2,"maxElapsedMinsByCategory":{},
                 "maxEntriesPerMin":1000,"levels":[{"username":"*","category":"*","severity":"INFO"}],
                 "clientHealthReport":{"intervalMins":-1},"maxRows":{"default":10000,"limit":25000,"options":[1000,5000,10000,25000]}}
                """);
            Assert.IsTrue(JsonNode.DeepEquals(expected, resolved), resolved.ToJsonString());
        }

        [TestMethod]
        public void TestDeclarationOrderIsPreserved()
        {
            var resolved = registry.Resolve("ActivityTrackingConfig", new JsonObject { ["maxRows"] = new JsonObject { ["limit"] = 5 }, ["enabled"] = false });
            CollectionAssert.AreEqual(
                new[] { "enabled", "logData", "maxDataLength", "maxElapsedMins", "maxElapsedMinsByCategory", "maxEntriesPerMin", "levels", "clientHealthReport", "maxRows" },
                resolved.Select(kv => kv.Key).ToArray());
        }

        [TestMethod]
        public void TestNestedObjectsMergeOntoDefaults()
        {
            var resolved = registry.Resolve("ActivityTrackingConfig", JsonNode.Parse("""{"maxRows":{"limit":5}}""").AsObject());
            Assert.AreEqual(10000, resolved["maxRows"]["default"].GetValue<int>());
            Assert.AreEqual(5, resolved["maxRows"]["limit"].GetValue<int>());
        }

        [TestMethod]
        public void TestListItemsAreTyped()
        {
            var resolved = registry.Resolve("ActivityTrackingConfig", JsonNode.Parse("""{"levels":[{"username":"bob"}]}""").AsObject());
            var level = resolved["levels"][0];
            Assert.AreEqual("bob", level["username"].GetValue<string>());
            Assert.AreEqual("*", level["category"].GetValue<string>());
            Assert.AreEqual("INFO", level["severity"].GetValue<string>());
        }

        [TestMethod]
        public void TestUnknownKeysAreDropped()
        {
            var resolved = registry.Resolve("AlertBannerConfig", JsonNode.Parse("""{"enabled":false,"bogus":1}""").AsObject());
            Assert.AreEqual("""{"enabled":false}""", resolved.ToJsonString());
        }

        [TestMethod]
        public void TestMaxRowsAcceptsDefaultValueAlias()
        {
            var resolved = registry.Resolve("MaxRows", JsonNode.Parse("""{"defaultValue":7}""").AsObject());
            Assert.AreEqual(7, resolved["default"].GetValue<int>());
        }

        [TestMethod]
        public void TestWrongTypeThrows()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => registry.Resolve("AlertBannerConfig", JsonNode.Parse("""{"enabled":"yes"}""").AsObject()));
            Assert.AreEqual("Field 'enabled' on AlertBannerConfig expects Boolean but got String", ex.Message);
        }

        [TestMethod]
        public void TestNumbersWidenButNotToBool()
        {
            var resolved = registry.Resolve("TraceConfig", JsonNode.Parse("""{"sampleRate":1}""").AsObject());
            Assert.AreEqual(1d, resolved["sampleRate"].GetValue<double>());
            Assert.ThrowsException<ArgumentException>(() => registry.Resolve("AlertBannerConfig", JsonNode.Parse("""{"enabled":5}""").AsObject()));
        }

        [TestMethod]
        public void TestAllBuiltInConfigSpecsAreValid()
        {
            var specs = BuiltInSpecs.Configs;
            Assert.AreEqual(39, specs.Count);
            foreach (var spec in specs.Where(s => s.TypedClass != null))
            {
                Assert.IsTrue(registry.Has(spec.TypedClass), spec.TypedClass);
                registry.Resolve(spec.TypedClass, new JsonObject());
            }
            Assert.AreEqual(6, BuiltInSpecs.Prefs.Count);
        }
    }
}
