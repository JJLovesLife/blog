using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;

internal class CodeLanguageExtension : IMarkdownExtension
{
	public void Setup(MarkdownPipelineBuilder pipeline)
	{
	}

	public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
	{
		if (renderer is HtmlRenderer htmlRenderer)
		{
			var codeBlockRenderer = htmlRenderer.ObjectRenderers.Find<CodeBlockRenderer>();
			if (codeBlockRenderer is not null)
				codeBlockRenderer.OutputAttributesOnPre = true;
		}
	}
}
