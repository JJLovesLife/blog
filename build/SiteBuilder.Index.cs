internal partial class SiteBuilder
{
	private const int ArticlePerPage = 5;

	private static Task<(string abbr, string full)> HeadHash = GetHeadHash();

	public async Task BuildIndexPages()
	{
		// ThenBy is just to make sure the order is consistence
		var orderedArticles = articles.OrderByDescending(article => article.PostTime).ThenBy(article => article.UrlPath).ToArray();
		if (orderedArticles.Length > ArticlePerPage)
			Directory.CreateDirectory(Path.Join(OutputFolder, "page"));

		var pages = (orderedArticles.Length + ArticlePerPage - 1) / ArticlePerPage;
		if (pages == 0) pages = 1;

		await Parallel.ForEachAsync(
			Enumerable.Range(1, pages),
			new ParallelOptions(){ MaxDegreeOfParallelism = 1 },
			async (i, _) =>
			{
				// i is 1-based
				var cnt = Math.Min(ArticlePerPage, orderedArticles.Length - (i - 1) * ArticlePerPage);
				var href = i == 1 ? "index.html" : Path.Join("page", $"{i}.html");
				await using var output = new StreamWriter(Path.Join(OutputFolder, href));
				await BuildIndexPage(
					new(orderedArticles, (i - 1) * ArticlePerPage, cnt),
					output,
					i,
					i == pages
				);
			}
		);
	}

	private async Task BuildIndexPage(ReadOnlyMemory<Article> articles, StreamWriter output, int pageNo, bool isLast)
	{
		var title = pageNo == 1 ? MainTitle : $"{MainTitle} - Page {pageNo}";
		var urlPath = pageNo == 1 ? "/" : $"/page/{pageNo}";
		await WriteHeader(output, title, IndexQuote, urlPath);

		await output.WriteAsync(
$"""
<header><h1><a href="/">{H1Title}</a></h1></header>
<main>

""");

		for (var i = 0; i < articles.Length; i++)
		{
			var article = articles.Span[i];
			await output.WriteAsync(
$"""
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
			await output.WriteAsync(article.ReadLessText);
			await output.WriteAsync(
"""
		</div>
	</article>

""");
		}

		var (headAbbr, headFull) = await HeadHash;
		await output.WriteAsync(
"""
	<hr>
	<nav>

""");

		if (pageNo != 1)
		{
			var href = pageNo == 2 ? "/" : $"/page/{pageNo - 1}";
			await output.WriteAsync(
$"""
		<div style="float: left"><a href="{href}">NEWER</a></div>

""");
		}
		if (!isLast)
		{
			await output.WriteAsync(
$"""
		<div style="float: right"><a href="/page/{pageNo + 1}">OLDER</a></div>

""");
		}

		await output.WriteAsync(
$"""
	</nav>
</main>
<footer>
	<div>
		<span>Commit <a href="{repoUrl}/commits/{headFull}" target="_blank">{headAbbr}</a> on <a href="{repoUrl}" target="_blank">GitHub</a></span> | <span><a href="https://creativecommons.org/licenses/by-nc-sa/4.0/" target="_blank">CC BY-NC-SA 4.0</a></span><br>
		<span>Theme based on <a href="http://www.matrix67.com/blog/wp-content/themes/matrix67/style.css" target="_blank">Mathix67</a></span>
	</div>
</footer>

""");

		await WriteFooter(output);
	}

	private static async Task<(string abbr, string full)> GetHeadHash()
	{
		var hashes = (await Utils.RunCommandAndGetOutput("git", "log -1 --pretty=\"format:%h %H\"")).Split(' ');
		if (hashes.Length != 2)
			throw new ApplicationException("Invalid git head.");
		return (hashes[0], hashes[1]);
	}
}
