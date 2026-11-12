using Xunit;

namespace Anemo.Core.Tests
{
    public class BugReportTests
    {
        private static (string Title, string Body) Parse(string url)
        {
            var uri = new Uri(url);
            var query = uri.Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            return (query["title"], query["body"]);
        }

        [Fact]
        public void BuildUrl_TargetsNewIssuePageWithVersionInBody()
        {
            var url = BugReport.BuildUrl("Anemo Widget", "1.3.7");
            Assert.StartsWith(BugReport.NewIssueUrl + "?", url);
            var (title, body) = Parse(url);
            Assert.Equal("[Anemo Widget] ", title);
            Assert.Contains("Anemo Widget 1.3.7", body);
            Assert.DoesNotContain("Diagnostics", body);
        }

        [Fact]
        public void BuildUrl_IncludesAndTruncatesDiagnostics()
        {
            var url = BugReport.BuildUrl("Anemo Scanner", "1.3.2", new string('x', 5000));
            var (_, body) = Parse(url);
            Assert.Contains("Diagnostics", body);
            Assert.DoesNotContain(new string('x', BugReport.MaxDiagnosticsLength + 1), body);
            Assert.True(url.Length < 8000);
        }

        [Fact]
        public void BuildUrl_EscapesSpecialCharacters()
        {
            var url = BugReport.BuildUrl("Anemo Widget", "1.0.0", "a&b=c#d");
            Assert.DoesNotContain("#d", url);
            Assert.Contains("a&b=c#d", Parse(url).Body);
        }
    }
}
