namespace Anaglyphohol.Services
{
    public class RecommendedSite
    {
        public string Title { get; set; }
        public string URL { get; set; }
        public string Image { get; set; }
        public string ContentType { get; set; }
        /// <summary>
        /// The sites TJ tested Anaglyphohol on (TJ 2026-10-05: "those are the sites I had tested the extension on"),
        /// shown in the toolbar and on the get-started page. Their images and videos are on by default
        /// (<see cref="IsRecommendedHost"/>). Odysee and Rumble come from the store version 3.0.14 (re-tested 2026-10-05).
        /// </summary>
        public static IReadOnlyList<RecommendedSite> All { get; } = new List<RecommendedSite>
        {
            new RecommendedSite("Yahoo.com Images", "sites/yahoo.png", "https://images.search.yahoo.com/search/images?p=nature"),
            new RecommendedSite("Bing.com Images", "sites/bing.png", "https://www.bing.com/images"),
            new RecommendedSite("Google.com Images", "sites/google.png", "https://www.google.com/search?udm=2&q=nature"),
            new RecommendedSite("YouTube.com", "sites/youtube.png", "https://www.youtube.com/"),
            new RecommendedSite("Odysee.com", "sites/odysee.png", "https://odysee.com/"),
            new RecommendedSite("Rumble.com", "sites/rumble.png", "https://rumble.com/"),
            new RecommendedSite("Twitch.tv", "sites/twitch.png", "https://www.twitch.tv/"),
            new RecommendedSite("Pluto.tv Live Video", "sites/plutotv.png", "https://pluto.tv/us/live-tv/656535fc2c46f30008870fae"),   // a LIVE channel: Pluto/Tubi VOD is DRM (MEASURED 2026-10-05)
            new RecommendedSite("TubiTV.com Live Video", "sites/tubi.png", "https://tubitv.com/live"),
        };

        /// <summary>Whether <paramref name="host"/> is one of <see cref="All"/>, matched on the EXACT hostname as the store
        /// version did (www.youtube.com, images.search.yahoo.com...).</summary>
        public static bool IsRecommendedHost(string host) =>
            All.Any(s => string.Equals(new Uri(s.URL).Host, host, StringComparison.OrdinalIgnoreCase));

        public RecommendedSite() { }
        public RecommendedSite(string name, string icon, string url)
        {
            Title = name;
            Image = icon;
            URL = url;
        }
    }
}
