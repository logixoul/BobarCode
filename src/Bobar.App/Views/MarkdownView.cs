using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Bobar.App.Views;

/// <summary>
/// Model-written Markdown as native controls: paragraphs, headings, bold/italic, inline code, fenced code blocks
/// and lists. Anything else shows as its source text. One SelectableTextBlock per block, so selection stops at
/// block boundaries.
/// </summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    // Precise locations, so the source-text fallbacks below slice the right span (without it inline spans start at 0).
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,Courier New,monospace");

    private string _text = "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == FontSizeProperty) Rebuild();
    }

    private void Rebuild()
    {
        _text = Markdown ?? "";
        var panel = new StackPanel { Spacing = 6 };
        if (_text.Length > 0)
        {
            MarkdownDocument doc;
            try { doc = Markdig.Markdown.Parse(_text, Pipeline); }
            catch (Exception)
            {
                // Markdig rejects input nested past its depth limit: show it raw.
                Content = TextBlock(_text);
                return;
            }
            // Only paragraphs (most explanations and reasoning): one text block, so selection spans paragraphs as it
            // did before Markdown. Anything structured gets a control per block, and selection stops between them.
            if (doc.Count > 0 && doc.All(b => b is ParagraphBlock))
            {
                Content = Paragraphs(doc.Cast<ParagraphBlock>());
                return;
            }
            AddBlocks(panel, doc);
        }
        Content = panel;
    }

    private SelectableTextBlock Paragraphs(IEnumerable<ParagraphBlock> paragraphs)
    {
        var tb = TextBlock(null);
        var first = true;
        foreach (var p in paragraphs)
        {
            if (!first) { tb.Inlines!.Add(new LineBreak()); tb.Inlines.Add(new LineBreak()); }
            first = false;
            if (p.Inline is not null) AddInlines(tb.Inlines!, p.Inline);
        }
        return tb;
    }

    private void AddBlocks(Panel panel, ContainerBlock container)
    {
        foreach (var block in container)
            if (Render(block) is { } c) panel.Children.Add(c);
    }

    private Control? Render(Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var tb = Inlines(h.Inline);
                tb.FontWeight = FontWeight.SemiBold;
                tb.FontSize = FontSize * (h.Level switch { 1 => 1.45, 2 => 1.25, 3 => 1.1, _ => 1.0 });
                tb.Margin = new Thickness(0, 4, 0, 0);
                return tb;
            }
            case ParagraphBlock p:
                return Inlines(p.Inline);
            case CodeBlock code: // fenced and indented
            {
                var tb = TextBlock(code.Lines.ToString().TrimEnd('\n', '\r'));
                tb.Classes.Add("mono");
                var border = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6), Child = tb };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("OutcomeBg"));
                return border;
            }
            case ListBlock list:
                return List(list);
            case ContainerBlock c: // quotes and anything else that holds blocks: their children, plain
            {
                var panel = new StackPanel { Spacing = 6 };
                AddBlocks(panel, c);
                return panel;
            }
            case LeafBlock leaf when leaf.Inline is { } inline:
                return Inlines(inline);
            default:
            {
                var src = Source(block.Span).TrimEnd('\n', '\r');
                return src.Length > 0 ? TextBlock(src) : null;
            }
        }
    }

    private StackPanel List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2 };
        var n = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{n++}{list.OrderedDelimiter}" : "•",
                Margin = new Thickness(2, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var body = new StackPanel { Spacing = 4 };
            AddBlocks(body, item);
            Grid.SetColumn(body, 1);
            panel.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { marker, body } });
        }
        return panel;
    }

    private SelectableTextBlock Inlines(ContainerInline? container)
    {
        var tb = TextBlock(null);
        if (container is not null) AddInlines(tb.Inlines!, container);
        return tb;
    }

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
            if (Render(inline) is { } i) target.Add(i);
    }

    private Avalonia.Controls.Documents.Inline? Render(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case EmphasisInline em:
            {
                Span span = em.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, em);
                return span;
            }
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = Mono };
                run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("OutcomeBg"));
                return run;
            }
            case LineBreakInline:
                // Soft breaks too: the raw text showed the model's line breaks, and collapsing them changes how it reads.
                return new LineBreak();
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case LinkInline or AutolinkInline or HtmlInline:
                // Not rendered: their source, so the URL is kept and an image with an empty label doesn't vanish.
                return new Run(Source(inline.Span));
            case ContainerInline c:
            {
                var span = new Span();
                AddInlines(span.Inlines, c);
                return span;
            }
            default:
                return new Run(Source(inline.Span));
        }
    }

    private string Source(SourceSpan span) =>
        span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= _text.Length ? _text.Substring(span.Start, span.Length) : "";

    private static SelectableTextBlock TextBlock(string? text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
