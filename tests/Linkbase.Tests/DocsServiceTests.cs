using Linkbase.Core.Docs;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Linkbase.Tests;

public sealed class DocsServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("docs-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    private DocsService Serve(string markdown, params string[] slugs)
    {
        File.WriteAllText(Path.Combine(_directory, "spec.md"), markdown);
        return new DocsService(_directory, slugs.Length == 0 ? ["spec"] : slugs);
    }

    [Fact]
    public void A_listed_document_is_rendered_with_its_title_and_contents()
    {
        var page = Serve("# The spec\n\n## First part\n\ntext\n\n## Second part\n\nmore\n").Find("spec")!;

        Assert.Equal("The spec", page.Title);
        Assert.Equal(["First part", "Second part"], page.Headings.Select(h => h.Text));
        Assert.Equal(["first-part", "second-part"], page.Headings.Select(h => h.Id));
        Assert.Contains("<h2 id=\"first-part\">", page.Html);
    }

    [Fact]
    public void Tables_are_rendered()
    {
        var page = Serve("# T\n\n| a | b |\n|---|---|\n| 1 | 2 |\n").Find("spec")!;

        Assert.Contains("<table>", page.Html);
        Assert.Contains("<td>1</td>", page.Html);
    }

    [Fact]
    public void A_name_that_is_not_listed_is_not_served_even_if_the_file_exists()
    {
        var service = Serve("# T\n");
        File.WriteAllText(Path.Combine(_directory, "other.md"), "# Other\n");

        Assert.Null(service.Find("other"));
        Assert.Null(service.Find("../spec"));
        Assert.Null(service.Find(null));
    }

    [Fact]
    public void A_listed_name_with_no_file_is_not_found()
    {
        Assert.Null(Serve("# T\n", "spec", "missing").Find("missing"));
    }

    [Fact]
    public void Names_that_could_leave_the_folder_are_never_listed()
    {
        var service = Serve("# T\n", "spec", "../secret", "a/b", "A");

        Assert.Equal(["spec"], service.Slugs);
    }

    [Fact]
    public void With_nothing_listed_the_feature_is_off()
    {
        var service = new DocsService(_directory, []);

        Assert.False(service.Enabled);
        Assert.Null(service.Find("spec"));
    }

    [Fact]
    public void A_link_to_the_same_page_keeps_its_section_under_the_docs_address()
    {
        var page = Serve("# T\n\nSee [part](#first-part).\n\n## First part\n").Find("spec")!;

        Assert.Contains("href=\"/docs/spec#first-part\"", page.Html);
    }

    [Fact]
    public void A_link_to_another_served_document_goes_to_its_page_here()
    {
        var page = Serve("# T\n\nSee [more](other.md#x) and [here](./spec.md).\n", "spec", "other").Find("spec")!;

        Assert.Contains("href=\"/docs/other#x\"", page.Html);
        Assert.Contains("href=\"/docs/spec\"", page.Html);
    }

    [Fact]
    public void A_link_to_something_not_served_keeps_its_words_and_loses_the_link()
    {
        var page = Serve("# T\n\nSee [the **code**](../src/Foo.cs) and [history](history/plan.md).\n").Find("spec")!;

        Assert.DoesNotContain("<a ", page.Html);
        Assert.Contains("the <strong>code</strong>", page.Html);
        Assert.Contains("history", page.Html);
    }

    [Fact]
    public void Web_links_are_kept()
    {
        var page = Serve("# T\n\n[site](https://example.com/x)\n").Find("spec")!;

        Assert.Contains("href=\"https://example.com/x\"", page.Html);
    }

    [Fact]
    public void Raw_html_is_not_passed_through()
    {
        var page = Serve("# T\n\n<script>alert(1)</script>\n\n<b>bold</b>\n").Find("spec")!;

        Assert.DoesNotContain("<script>", page.Html);
        Assert.DoesNotContain("<b>", page.Html);
    }

    [Fact]
    public void Configuration_with_no_pages_gives_a_service_that_is_off()
    {
        var configuration = new ConfigurationBuilder().Build();

        var service = DocsService.FromConfiguration(configuration, new TestEnvironment(_directory));

        Assert.False(service.Enabled);
    }

    [Fact]
    public void Configuration_reads_the_listed_pages_from_the_configured_folder()
    {
        File.WriteAllText(Path.Combine(_directory, "spec.md"), "# From config\n");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Docs:Pages:0"] = "spec",
            ["Docs:Path"] = _directory,
        }).Build();

        var service = DocsService.FromConfiguration(configuration, new TestEnvironment("nowhere"));

        Assert.Equal("From config", service.Find("spec")!.Title);
    }

    private sealed class TestEnvironment(string contentRoot) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
