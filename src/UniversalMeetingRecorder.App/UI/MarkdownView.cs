using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Рендер митинг-ноутов (notes.md от Speakr) в FlowDocument. Markdig — только парсер; рендер свой
/// и минимальный: заголовки, абзацы, списки, жирный/курсив/код, цитаты, ссылки, разделители.
/// Текст выделяется и копируется (как .textSelection на Mac).
/// </summary>
internal static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UseAutoLinks().Build();

    public static FlowDocumentScrollViewer Create(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        foreach (var block in Markdown.Parse(markdown, Pipeline)) Add(doc.Blocks, block);
        return new FlowDocumentScrollViewer
        {
            Document = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Focusable = true,
        };
    }

    private static void Add(BlockCollection into, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
                var hp = new Paragraph { FontSize = h.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 }, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
                AddInlines(hp.Inlines, h.Inline);
                into.Add(hp);
                break;
            case ParagraphBlock p:
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(para.Inlines, p.Inline);
                into.Add(para);
                break;
            case ListBlock l:
                var list = new List { MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(20, 0, 0, 0) };
                foreach (var item in l.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item) Add(li.Blocks, child);
                    foreach (var b in li.Blocks) b.Margin = new Thickness(0, 0, 0, 2);
                    list.ListItems.Add(li);
                }
                into.Add(list);
                break;
            case QuoteBlock q:
                var section = new Section { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Foreground = Brushes.DimGray };
                foreach (var child in q) Add(section.Blocks, child);
                into.Add(section);
                break;
            case FencedCodeBlock or CodeBlock:
                var code = (LeafBlock)block;
                into.Add(new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), Padding = new Thickness(6) });
                break;
            case ThematicBreakBlock:
                into.Add(new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6) }));
                break;
            case ContainerBlock container:
                foreach (var child in container) Add(into, child);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                var lp = new Paragraph();
                AddInlines(lp.Inlines, leaf.Inline);
                into.Add(lp);
                break;
        }
    }

    private static void AddInlines(InlineCollection into, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) into.Add(Convert(inline));
    }

    private static WpfInline Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case EmphasisInline em:
                var span = em.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : em.DelimiterCount >= 2 ? new Bold() : (Span)new Italic();
                foreach (var child in em) span.Inlines.Add(Convert(child));
                return span;
            case CodeInline c:
                return new Run(c.Content) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)) };
            case LinkInline link:
                var h = new Hyperlink();
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                {
                    h.NavigateUri = uri;
                    h.RequestNavigate += (_, e) => Platform.Shell.Open(e.Uri.AbsoluteUri);
                }
                foreach (var child in link) h.Inlines.Add(Convert(child));
                if (!h.Inlines.Any()) h.Inlines.Add(new Run(link.Url));
                return h;
            case AutolinkInline auto:
                var a = new Hyperlink(new Run(auto.Url));
                if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var au))
                {
                    a.NavigateUri = au;
                    a.RequestNavigate += (_, e) => Platform.Shell.Open(e.Uri.AbsoluteUri);
                }
                return a;
            case LineBreakInline:
                return new LineBreak();
            case HtmlInline html:
                return new Run(html.Tag);
            case ContainerInline container:
                var s = new Span();
                foreach (var child in container) s.Inlines.Add(Convert(child));
                return s;
            default:
                return new Run(inline.ToString());
        }
    }
}
