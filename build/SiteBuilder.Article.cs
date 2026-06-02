using System.Globalization;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

internal partial class SiteBuilder
{
	private static DateTime buildTime = DateTime.UtcNow;

	private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
		.UseYamlFrontMatter()
		.Use<ReadMoreExtension>()
		.UseSoftlineBreakAsHardlineBreak()
		.UseAutoLinks()
		.Build();
	public async Task BuildArticle(string urlPath, string destFilePath, string srcFilePath)
	{
		var content = await File.ReadAllTextAsync(srcFilePath);
		using var output = new StreamWriter(destFilePath);

		var document = Markdown.Parse(content, pipeline);

		var title = GetTitle(document);
		if (title.IsEmpty)
			throw new ArgumentException($"[{urlPath}]: do not have title");

		var article = new Article() {
			SrcPath = srcFilePath.Replace('\\', '/'),
			UrlPath = urlPath,
			Title = title.ToString()
		};

		// Perf: this git log command might not be efficient as it goes through commit history multiple times.
		// PostTime
		var time = await Utils.RunCommandAndGetOutput("git", $"log --diff-filter=A --follow -1 --pretty=format:%cI -- {srcFilePath}");
		if (Utils.IsCI || !string.IsNullOrEmpty(time))
			article.PostTime = DateTimeOffset.ParseExact(time, "yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture).UtcDateTime;
		else // not committed yet
			article.PostTime = buildTime;

		// EditTime
		time = await Utils.RunCommandAndGetOutput("git", $"log --diff-filter=M --follow -1 --pretty=format:%cI -- {srcFilePath}");
		article.EditTime = article.PostTime;
		if (!string.IsNullOrEmpty(time))
			article.EditTime = DateTimeOffset.ParseExact(time, "yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture).UtcDateTime;

		// Output HTML
		await WriteHeader(output, title.Span, ArticleTitleSuffix, urlPath);
		await output.WriteAsync(
$"""
<header><h1><a href="/">{H1Title}</a></h1></header>
<main>
	<article>
		<header>
			<h2 class="title"><a href="{article.UrlPath}" rel="bookmark">{article.Title}</a></h2>
			<div class="time">
				<time datetime="{article.PostTime:yyyy-MM-ddTHH:mm:ssK}">{article.PostTime:yyyy/MM/dd}</time>

""");
		if (article.EditTime != article.PostTime)
			// fix to live branch now, no support for PR review now.
			await output.WriteAsync(
$"""
				<span><a href="{repoUrl}/commits/{branch}/{article.SrcPath}">• edited</a></span>

""");
		await output.WriteAsync(
"""
			</div>
		</header>
		<div class="content">

""");
		Markdown.ToHtml(document, output, pipeline);
		await output.WriteAsync(
"""
		</div>
	</article>

""");

		// TODO: If we want to show the latest commit of the article instead of whole blog? But that also effect the css etc
		// TODO: could optimize the interpolation
		var (headAbbr, headFull) = await HeadHash;
		await output.WriteAsync(
$"""
	<hr>
</main>
<footer>
	<div>
		<span>Commit <a href="{repoUrl}/commits/{headFull}" target="_blank">{headAbbr}</a> on <a href="{repoUrl}" target="_blank">GitHub</a></span> | <span><a href="https://creativecommons.org/licenses/by-nc-sa/4.0/" target="_blank">CC BY-NC-SA 4.0</a></span><br>
		<span>Theme based on <a href="http://www.matrix67.com/blog/wp-content/themes/matrix67/style.css" target="_blank">Mathix67</a></span>
	</div>
</footer>

""");
		await WriteFooter(output);

		var trimmed = document.TrimReadMore();
		article.ReadLessText = Markdown.ToHtml(document, pipeline);
		if (trimmed)
			article.ReadLessText += $"""<p><a href="{urlPath}#read-more">Read more...</a></p>""";

		articles.Add(article);
	}

	private static ReadOnlyMemory<char> GetTitle(MarkdownDocument document)
	{
		var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
		if (yamlBlock is null)
			return default;
		var yaml = Utils.ParseYaml(yamlBlock.Lines);
		return yaml.First(kv => kv.Item1.AsSpan().SequenceEqual("title")).Item2.AsMemory();
	}
}
