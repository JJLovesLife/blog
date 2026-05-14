using System.Collections.Concurrent;

internal partial class SiteBuilder
{
	public const string ArticlesFolder = "articles";
	public const string OutputFolder = "output";

	public const string MainTitle = "JJ";
	public const string IndexQuote = "Easy to Say, Hard to Do";
	public const string H1Title = "JJ's blog";
	public const string ArticleTitleSuffix = H1Title;

	private readonly Uri repoUrl;
	private readonly string branch;
	private readonly bool force;
	private readonly ConcurrentBag<Article> articles = new();

	public SiteBuilder(Uri repo, string branch, bool force)
	{
		this.repoUrl = repo;
		this.branch = branch;
		this.force = force;
	}

	public Task PostArticlesBuild()
	{
		var tasks = new List<Task>()
		{
			BuildIndexPages(),
			CopyAssets(),
			BuildStaticWebAppConfig(),
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

	private static Task BuildStaticWebAppConfig()
	{
		var configFilePath = Path.Join(OutputFolder, "staticwebapp.config.json");
		return File.WriteAllTextAsync(
			configFilePath,
"""
{
	"routes": [
		{
			"route": "/page/1",
			"redirect": "/",
			"statusCode": 301
		}
	]
}
""");
	}

	private static Task WriteHeader(TextWriter output, ReadOnlySpan<char> title, string suffix)
	{
		return output.WriteAsync($"""
		<!DOCTYPE html>
		<html lang="en">
		<head>
			<meta charset="utf-8">
			<title>{title} | {suffix}</title>
			<link rel="stylesheet" href="/assets/style.css">
		</head>
		<body>

		""");
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
