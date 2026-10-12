using System.Text.RegularExpressions;

namespace Hoist.Core.Utils
{
    public static class StringUtils
    {
        private static readonly Regex trailingWord = new Regex(@"\w+$");
        private static readonly Regex trailingSpace = new Regex(@"\s+$");

        /// <summary>
        /// Port of hoist-core StringUtils.elide: shortens to at most len chars, backing off to the
        /// previous word boundary and appending '...'.
        /// </summary>
        public static string Elide(string str, int len)
        {
            if (string.IsNullOrEmpty(str) || str.Length < len) return str;
            if (str.Length <= 3) return "...";

            str = str.Substring(0, Math.Max(0, len - 3));
            str = trailingWord.Replace(str, "");
            str = trailingSpace.Replace(str, "");
            return str + "...";
        }
    }

    /// <summary>Port of hoist-core browser/Utils, Browser and Device: display names as stored in xh_track_log.</summary>
    public static class BrowserUtils
    {
        // Order matters - see the comments in hoist-core browser/Utils.groovy.
        private static readonly (string Key, string Name)[] BrowserMatchers =
        {
            ("Island", "Island"),
            ("GoodAccess", "Good"),
            ("Firefox", "Firefox"),
            ("Edge", "Microsoft Edge"),
            ("EdgiOS", "Microsoft Edge"),
            ("EdgA", "Microsoft Edge"),
            ("Edg", "Microsoft Edge"),
            ("; rv", "Internet Explorer"),
            ("MSIE", "Internet Explorer"),
            ("Opera", "Opera"),
            ("OPR", "Opera"),
            ("Chrome", "Chrome"),
            ("CriOS", "Chrome"),
            ("Safari", "Safari")
        };

        private static readonly (string Key, string Name)[] DeviceMatchers =
        {
            ("Windows", "Windows"),
            ("Android", "Android"),
            ("Linux", "Linux"),
            ("iPhone", "iPhone"),
            ("iPad", "iPad"),
            ("iPod", "iPod"),
            ("Mac OS X", "Mac"),
            ("Macintosh", "Mac"),
            ("macOS", "Mac")
        };

        /// <summary>From the Sec-Ch-UA client hint, else the User-Agent; "Unknown" if neither matches; null with no request.</summary>
        public static string GetBrowser(string userAgent, string secChUa, bool hasRequest = true)
        {
            if (!hasRequest) return null;
            return FindMatch(secChUa, BrowserMatchers) ?? FindMatch(userAgent, BrowserMatchers) ?? "Unknown";
        }

        /// <summary>From the Sec-Ch-UA-Platform client hint, else the User-Agent; "Unknown" if neither matches; null with no request.</summary>
        public static string GetDevice(string userAgent, string secChUaPlatform, bool hasRequest = true)
        {
            if (!hasRequest) return null;
            return FindMatch(secChUaPlatform, DeviceMatchers) ?? FindMatch(userAgent, DeviceMatchers) ?? "Unknown";
        }

        private static string FindMatch(string header, (string Key, string Name)[] matchers)
        {
            if (string.IsNullOrEmpty(header)) return null;
            foreach (var (key, name) in matchers)
            {
                if (header.Contains(key, StringComparison.Ordinal)) return name;
            }
            return null;
        }
    }
}
