using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Linkbase.Core.Docs;

// A few of the repo's Markdown documents, shown inside the app, so a hosted demo can show what the product is required to do
// without a source checkout. Docs:Pages lists them by file name without the extension (such as "requirements"); with none
// listed the feature is off and the app has no docs page and no link to it. Only a listed name is ever read, so the address
// can't be used to read another file. The documents are the repo's own and are trusted, but raw HTML in them is still not passed
// through, so a document can only say what Markdown can.
public sealed partial class DocsService
{
    public sealed record Heading(string Id, string Text);

    public sealed record Page(string Slug, string Title, string Html, IReadOnlyList<Heading> Headings);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .DisableHtml()
        .Build();

    private readonly string? _directory;
    private readonly List<string> _slugs;
    private readonly Dictionary<string, Page> _rendered = [];
    private readonly object _lock = new();

    public DocsService(string? directory, IEnumerable<string> slugs)
    {
        _directory = directory;
        _slugs = slugs.Where(s => SlugPattern().IsMatch(s)).Distinct().ToList();
    }

    public bool Enabled => _directory is not null && _slugs.Count > 0;

    public IReadOnlyList<string> Slugs => _slugs;

    // Docs:Path if set, otherwise the docs folder beside the app (in the image) or the repo's (when run from source).
    public static DocsService FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var slugs = configuration.GetSection("Docs:Pages").Get<string[]>() ?? [];
        if (slugs.Length == 0)
        {
            return new DocsService(null, []);
        }

        var candidates = new List<string?> { configuration["Docs:Path"], Path.Combine(environment.ContentRootPath, "docs") };
        try
        {
            candidates.Add(RepoPaths.Resolve("docs"));
        }
        catch (DirectoryNotFoundException)
        {
            // Not run from inside the repo.
        }

        return new DocsService(candidates.FirstOrDefault(c => c is not null && Directory.Exists(c)), slugs);
    }

    // The page for a slug the app was told to serve, or null: not listed, or its file isn't there.
    public Page? Find(string? slug)
    {
        if (!Enabled || slug is null || !_slugs.Contains(slug))
        {
            return null;
        }

        lock (_lock)
        {
            if (_rendered.TryGetValue(slug, out var cached))
            {
                return cached;
            }

            var file = Path.Combine(_directory!, slug + ".md");
            if (!File.Exists(file))
            {
                return null;
            }

            return _rendered[slug] = Render(slug, File.ReadAllText(file));
        }
    }

    // Internal so a test can render text without a file.
    internal Page Render(string slug, string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        RewriteLinks(document, slug);

        var headings = document.Descendants<HeadingBlock>().ToList();
        var title = headings.FirstOrDefault(h => h.Level == 1) is { } first ? TextOf(first) : slug;
        var contents = headings.Where(h => h.Level == 2 && h.GetAttributes().Id is not null)
            .Select(h => new Heading(h.GetAttributes().Id!, TextOf(h)))
            .ToList();
        return new Page(slug, title, document.ToHtml(Pipeline), contents);
    }

    // A link to another listed document goes to its page here, and one to the same page keeps its #section (a bare #section
    // would resolve against the app's base address, not this page). Links to anything else in the repo, such as other
    // documents or source files, lead nowhere in the app, so they keep their words and lose the link. Web links are kept.
    private void RewriteLinks(MarkdownDocument document, string slug)
    {
        foreach (var link in document.Descendants<LinkInline>().Where(l => !l.IsImage).ToList())
        {
            var url = link.Url ?? "";
            if (url.Contains("://") || url.StartsWith("mailto:", StringComparison.Ordinal))
            {
                continue;
            }

            var hash = url.IndexOf('#');
            var path = hash < 0 ? url : url[..hash];
            var fragment = hash < 0 ? "" : url[hash..];
            if (path.Length == 0)
            {
                link.Url = $"/docs/{slug}{fragment}";
            }
            else if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(path) is var target && !path.Contains("history/") && _slugs.Contains(target))
            {
                link.Url = $"/docs/{target}{fragment}";
            }
            else
            {
                Unlink(link);
            }
        }
    }

    private static void Unlink(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }

        link.Remove();
    }

    private static string TextOf(HeadingBlock heading) =>
        heading.Inline is null ? "" : string.Concat(heading.Inline.Descendants().Where(i => i is LiteralInline or CodeInline).Select(i => i.ToString()));

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();
}
