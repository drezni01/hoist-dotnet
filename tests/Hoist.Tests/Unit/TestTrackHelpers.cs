using Hoist.Core.Utils;

namespace Hoist.Tests.Unit
{
    /// <summary>Ports of hoist-core StringUtils.elide and browser/Utils (browser and device detection).</summary>
    [TestClass]
    public class TestTrackHelpers
    {
        [TestMethod]
        public void TestElide()
        {
            Assert.AreEqual("short", StringUtils.Elide("short", 10));
            Assert.AreEqual(null, StringUtils.Elide(null, 10));
            Assert.AreEqual("hello...", StringUtils.Elide("hello wonderful world", 12));
            Assert.AreEqual("...", StringUtils.Elide("abcdef", 3));
        }

        [TestMethod]
        public void TestBrowserDetection()
        {
            Assert.AreEqual("Chrome", BrowserUtils.GetBrowser("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/129.0 Safari/537.36", null));
            Assert.AreEqual("Microsoft Edge", BrowserUtils.GetBrowser("Mozilla/5.0 (Windows NT 10.0) Chrome/129.0 Safari/537.36 Edg/129.0", null));
            Assert.AreEqual("Firefox", BrowserUtils.GetBrowser("Mozilla/5.0 (Windows NT 10.0; rv:131.0) Gecko/20100101 Firefox/131.0", null));
            Assert.AreEqual("Safari", BrowserUtils.GetBrowser("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15", null));
            Assert.AreEqual("Unknown", BrowserUtils.GetBrowser("curl/8.0", null));
            // Client hints win over the User-Agent.
            Assert.AreEqual("Microsoft Edge", BrowserUtils.GetBrowser("Chrome/129.0", "\"Microsoft Edge\";v=\"129\", \"Chromium\";v=\"129\""));
            Assert.IsNull(BrowserUtils.GetBrowser(null, null, hasRequest: false));
        }

        [TestMethod]
        public void TestDeviceDetection()
        {
            Assert.AreEqual("Windows", BrowserUtils.GetDevice("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", null));
            Assert.AreEqual("Android", BrowserUtils.GetDevice("Mozilla/5.0 (Linux; Android 14)", null));
            Assert.AreEqual("iPhone", BrowserUtils.GetDevice("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", null));
            Assert.AreEqual("Mac", BrowserUtils.GetDevice("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6)", null));
            Assert.AreEqual("Mac", BrowserUtils.GetDevice("whatever", "\"macOS\""));
            Assert.AreEqual("Unknown", BrowserUtils.GetDevice("curl/8.0", null));
        }
    }
}
