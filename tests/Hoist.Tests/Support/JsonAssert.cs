using System.Text.Json;

namespace Hoist.Tests.Support
{
    public static class JsonAssert
    {
        public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, int expectedStatus = 200)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(expectedStatus, (int)response.StatusCode, $"Unexpected status. Body: {body}");
            StringAssert.StartsWith(response.Content.Headers.ContentType?.MediaType ?? "", "application/json", $"Body: {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        /// <summary>Asserts a 204 renderSuccess() response.</summary>
        public static async Task NoContentAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(204, (int)response.StatusCode, $"Unexpected status. Body: {body}");
        }

        /// <summary>Asserts the object has exactly these keys - extra or missing keys are contract breaks.</summary>
        public static void HasExactKeys(JsonElement obj, params string[] keys)
        {
            Assert.AreEqual(JsonValueKind.Object, obj.ValueKind, $"Expected object, got {obj}");
            var actual = obj.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var expected = keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"Keys differ. Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }

        public static void HasKeys(JsonElement obj, params string[] keys)
        {
            foreach (var k in keys) Assert.IsTrue(obj.TryGetProperty(k, out _), $"Missing key '{k}' in {obj}");
        }

        /// <summary>
        /// Matches the shape of a fixture file in Fixtures/: same keys and JSON value kinds, recursively.
        /// A null in the fixture means "any value"; arrays are checked against their first fixture element.
        /// </summary>
        public static void MatchesFixture(JsonElement actual, string fixtureName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
            var fixture = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            MatchShape(fixture, actual, "$");
        }

        private static void MatchShape(JsonElement expected, JsonElement actual, string path)
        {
            if (expected.ValueKind == JsonValueKind.Null || actual.ValueKind == JsonValueKind.Null) return;

            Assert.AreEqual(Normalize(expected.ValueKind), Normalize(actual.ValueKind), $"Kind mismatch at {path}: {actual}");

            if (expected.ValueKind == JsonValueKind.Object)
            {
                HasExactKeys(actual, expected.EnumerateObject().Select(p => p.Name).ToArray());
                foreach (var p in expected.EnumerateObject()) MatchShape(p.Value, actual.GetProperty(p.Name), $"{path}.{p.Name}");
            }
            else if (expected.ValueKind == JsonValueKind.Array && expected.GetArrayLength() > 0)
            {
                foreach (var item in actual.EnumerateArray()) MatchShape(expected[0], item, $"{path}[]");
            }
        }

        private static JsonValueKind Normalize(JsonValueKind kind) => kind == JsonValueKind.False ? JsonValueKind.True : kind;
    }
}
