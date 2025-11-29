namespace Anaglyphohol.Services
{
    public class RecommendedSite
    {
        public string Title { get; set; }
        public string URL { get; set; }
        public string Image { get; set; }
        public string ContentType { get; set; }
        public RecommendedSite() { }
        public RecommendedSite(string name, string icon, string url)
        {
            Title = name;
            Image = icon;
            URL = url;
        }
    }
}
