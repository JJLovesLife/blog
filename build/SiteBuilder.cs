using System.Collections.Concurrent;
using System.Xml;

internal partial class SiteBuilder
{
	public const string ArticlesFolder = "articles";
	public const string OutputFolder = "output";

	public const string MainTitle = "JJ";
	public const string IndexQuote = "Easy to Say, Hard to Do";
	public const string H1Title = "JJ's blog";
	public const string ArticleTitleSuffix = H1Title;

	private readonly Uri repoUrl;
	private readonly string? siteDomain;
	private readonly string branch;
	private readonly bool force;
	private readonly ConcurrentBag<Article> articles = new();

	public SiteBuilder(Uri repo, string branch, bool force, string? siteDomain)
	{
		this.repoUrl = repo;
		this.siteDomain = siteDomain;
		this.branch = branch;
		this.force = force;
	}

	public Task PostArticlesBuild()
	{
		var tasks = new List<Task>()
		{
			BuildIndexPages(),
			BuildSitemap(),
			BuildRobotsTxt(),
			CopyAssets(),
			BuildRedirects(),
		};

		return Task.WhenAll(tasks);
	}

	private async Task CopyAssets()
	{
		var cssRelPath = Path.Join("assets", "style.css");
		var cssInputInfo = new FileInfo(cssRelPath);
		if (!cssInputInfo.Exists)
			throw new ArgumentException("Missing style.css CSS file.");
		
		var cssOutputPath = Path.Join(OutputFolder, cssRelPath);
		var cssOutputInfo = new FileInfo(cssOutputPath);
		if (!force && cssOutputInfo.Exists && cssOutputInfo.LastWriteTimeUtc >= cssInputInfo.LastWriteTimeUtc)
		{
			return;
		}
		else if (force & Utils.IsCI)
		{
			// Not elegant, but easy to do now.
			// Only run in CI to avoid slow down local building.
			var data = await new HttpClient().GetStringAsync("https://necolas.github.io/normalize.css/latest/normalize.css");
			if (!data.Contains("v8.0.1"))
				throw new ArgumentException("normalize.css is out of date");
		}

		if (!Directory.Exists(Path.Join(OutputFolder, "assets")))
			Directory.CreateDirectory(Path.Join(OutputFolder, "assets"));

		cssInputInfo.CopyTo(cssOutputPath, true);
	}

	private static Task BuildRedirects()
	{
		var configFilePath = Path.Join(OutputFolder, "_redirects");
		return File.WriteAllTextAsync(
			configFilePath,
"""
/page/1 / 301
""");
	}

	private async Task BuildSitemap()
	{
		if (string.IsNullOrWhiteSpace(siteDomain))
			return;

		var orderedArticles = articles.OrderByDescending(article => article.PostTime).ThenBy(article => article.UrlPath).ToArray();
		var pages = (orderedArticles.Length + ArticlePerPage - 1) / ArticlePerPage;
		if (pages == 0) pages = 1;

		using var output = XmlWriter.Create(
			Path.Join(OutputFolder, "sitemap.xml"),
			new XmlWriterSettings()
			{
				Async = true,
				Indent = true
			});

		await WriteSitemap(output, orderedArticles, pages);
	}

	private Task BuildRobotsTxt()
	{
		if (string.IsNullOrWhiteSpace(siteDomain))
			return Task.CompletedTask;

		return File.WriteAllTextAsync(
			Path.Join(OutputFolder, "robots.txt"),
$"""
User-agent: *
Allow: /
Sitemap: {GetAbsoluteUrl("/sitemap.xml")}
""");
	}

	private async Task WriteSitemap(XmlWriter output, Article[] orderedArticles, int pages)
	{
		await output.WriteStartDocumentAsync();
		await output.WriteStartElementAsync(null, "urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");

		for (var pageNo = 1; pageNo <= pages; pageNo++)
		{
			var urlPath = pageNo == 1 ? "/" : $"/page/{pageNo}";
			await WriteSitemapUrl(output, urlPath);
		}

		foreach (var article in orderedArticles)
		{
			await WriteSitemapUrl(output, article.UrlPath, article.EditTime);
		}

		await output.WriteEndElementAsync();
		await output.WriteEndDocumentAsync();
	}

	private async Task WriteSitemapUrl(XmlWriter output, string urlPath, DateTime? lastModified = null)
	{
		await output.WriteStartElementAsync(null, "url", null);
		await output.WriteElementStringAsync(null, "loc", null, GetAbsoluteUrl(urlPath));
		if (lastModified is not null)
			await output.WriteElementStringAsync(null, "lastmod", null, lastModified.Value.ToString("yyyy-MM-dd"));
		await output.WriteEndElementAsync();
	}

	private Task WriteHeader(TextWriter output, ReadOnlySpan<char> title, string suffix, string urlPath)
	{
		var canonicalUrl = GetCanonicalUrl(urlPath);
		return output.WriteAsync($"""
		<!DOCTYPE html>
		<html lang="zh-CN">
		<head>
			<meta charset="utf-8">
			<title>{title} | {suffix}</title>
			{canonicalUrl}
			<link rel="stylesheet" href="/assets/style.css">
		</head>
		<body>

		""");
	}

	private string GetCanonicalUrl(string urlPath)
	{
		if (string.IsNullOrWhiteSpace(siteDomain))
			return string.Empty;

		return $"<link rel=\"canonical\" href=\"{GetAbsoluteUrl(urlPath)}\">";
	}

	private string GetAbsoluteUrl(string urlPath)
	{
		var builder = new UriBuilder("https", siteDomain!)
		{
			Path = urlPath
		};

		return builder.Uri.AbsoluteUri;
	}

	private static Task WriteFooter(TextWriter output)
	{
		return output.WriteAsync(
"""
<script>
const thisYearFormatter = new Intl.DateTimeFormat(navigator.language, { day: 'numeric', month: 'short' });
const pastYearFormatter = new Intl.DateTimeFormat(navigator.language, { day: 'numeric', month: 'short', year:'numeric'});
const titleFormatter = new Intl.DateTimeFormat(navigator.language, { day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short' });
const thisYear = new Date().getUTCFullYear();
document.querySelectorAll('time').forEach(t => {
	const parsed = Date.parse(t.dateTime);
	if (Number.isNaN(parsed)) return;
	const date = new Date(parsed);
	t.textContent = (date.getUTCFullYear() === thisYear ? thisYearFormatter : pastYearFormatter).format(date);
	t.setAttribute('title', titleFormatter.format(date));
	t.setAttribute('lang', navigator.language);
});
</script>
</body>
</html>
""");
	}

	private struct Article
	{
		public string SrcPath;
		public string UrlPath;
		public string Title;
		public DateTime PostTime;
		public DateTime EditTime;
		public string ReadLessText;
	}
}
