using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

internal class MathematicsExtension : IMarkdownExtension
{
	public void Setup(MarkdownPipelineBuilder pipeline)
	{
	}

	public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
	{
		if (renderer is HtmlRenderer htmlRenderer)
		{
			htmlRenderer.ObjectRenderers.Replace<HtmlMathInlineRenderer>(new InlineRenderer());
			htmlRenderer.ObjectRenderers.Replace<HtmlMathBlockRenderer>(new BlockRenderer());
		}
	}

	public static async Task<string?> RenderAsync(MarkdownDocument document, string srcFilePath)
	{
		var nodes = document.Descendants().Where(node => node is MathInline or MathBlock).ToArray();
		if (nodes.Length == 0)
			return null;

		var formulas = nodes.Select(node => new
		{
			tex = node switch
			{
				MathInline inline => inline.Content.ToString(),
				MathBlock block => block.Lines.ToString(),
				_ => throw new UnreachableException()
			},
			displayMode = node is MathBlock or MathInline { DelimiterCount: 2 },
			line = node.Line + 1
		});

		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("node")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			}
		};
		process.StartInfo.ArgumentList.Add(Path.Join("scripts", "render-math.cjs"));
		try
		{
			process.Start();
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException($"[{srcFilePath}]: Rendering math requires Node.js on PATH and npm install.", ex);
		}

		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		await process.StandardInput.WriteAsync(JsonSerializer.Serialize(formulas));
		process.StandardInput.Close();
		await process.WaitForExitAsync();
		var error = await stderr;
		if (process.ExitCode != 0)
			throw new ArgumentException($"[{srcFilePath}]: {error.Trim()}");

		var result = JsonSerializer.Deserialize<RenderedMath>(await stdout, JsonSerializerOptions.Web)!;
		for (var i = 0; i < nodes.Length; i++)
			nodes[i].SetData(typeof(MathematicsExtension), result.Html[i]);
		return result.StyleSheet;
	}

	private record RenderedMath(string StyleSheet, string[] Html);

	private class InlineRenderer : HtmlObjectRenderer<MathInline>
	{
		protected override void Write(HtmlRenderer renderer, MathInline obj)
		{
			renderer.Write((string)obj.GetData(typeof(MathematicsExtension))!);
		}
	}

	private class BlockRenderer : HtmlObjectRenderer<MathBlock>
	{
		protected override void Write(HtmlRenderer renderer, MathBlock obj)
		{
			renderer.EnsureLine();
			renderer.WriteLine((string)obj.GetData(typeof(MathematicsExtension))!);
		}
	}
}
