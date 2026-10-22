using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Ods.Desktop.Services;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace Ods.Desktop.Controls;

/// <summary>
/// Renders a chat reply's Markdown natively: a read-only, selectable
/// RichTextBox built from Markdig's syntax tree (headings, lists, quotes,
/// tables, code blocks with a copy button, inline code, emphasis and links).
/// Mouse-wheel input goes to the transcript, not to this control.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Document = new FlowDocument();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this };
        (Parent as UIElement ?? this).RaiseEvent(forwarded);
    }

    private void Render()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = FontSize,
            LineHeight = FontSize * 1.5,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        var syntax = Markdig.Markdown.Parse(Markdown ?? "", Pipeline);
        foreach (var block in syntax) AddBlock(document.Blocks, block);
        Document = document;
    }

    private void AddBlock(BlockCollection blocks, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = FontSize + (heading.Level <= 2 ? 5 : 2), Margin = new Thickness(0, 10, 0, 4) };
                AddInlines(title.Inlines, heading.Inline);
                blocks.Add(title);
                break;
            case ParagraphBlock paragraph:
                var text = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(text.Inlines, paragraph.Inline);
                blocks.Add(text);
                break;
            case FencedCodeBlock or CodeBlock:
                blocks.Add(CodeBlockView((LeafBlock)block));
                break;
            case ListBlock list:
                var wpfList = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item) AddBlock(listItem.Blocks, child);
                    foreach (var paragraphInItem in listItem.Blocks.OfType<Paragraph>()) paragraphInItem.Margin = new Thickness(0, 0, 0, 2);
                    wpfList.ListItems.Add(listItem);
                }
                blocks.Add(wpfList);
                break;
            case QuoteBlock quote:
                var section = new Section { Padding = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
                section.SetResourceReference(Section.BorderBrushProperty, "AccentFillColorDefaultBrush");
                section.SetResourceReference(Section.ForegroundProperty, "TextFillColorSecondaryBrush");
                foreach (var child in quote) AddBlock(section.Blocks, child);
                blocks.Add(section);
                break;
            case ThematicBreakBlock:
                var rule = new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                blocks.Add(rule);
                break;
            case Markdig.Extensions.Tables.Table table:
                blocks.Add(TableView(table));
                break;
            case HtmlBlock html:
                blocks.Add(new Paragraph(new Run(html.Lines.ToString())));
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(blocks, child);
                break;
        }
    }

    private WpfBlock CodeBlockView(LeafBlock code)
    {
        var language = code is FencedCodeBlock fenced ? fenced.Info ?? "" : "";
        var source = code.Lines.ToString().TrimEnd('\n', '\r');
        var copy = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        };
        copy.Click += (_, _) => Clipboard.SetText(source);
        var header = new DockPanel { Margin = new Thickness(10, 4, 4, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var label = new System.Windows.Controls.TextBlock { Text = language, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        header.Children.Add(label);
        // Wrapped rather than scrolled: a chat transcript has no room for nested scrollbars.
        var body = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("SelectableText"),
            Text = source,
            FontFamily = (FontFamily)FindResource("OdsMonoFont"),
            FontSize = 12.5,
            Margin = new Thickness(10, 2, 10, 10),
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(body);
        var frame = new Border { CornerRadius = new CornerRadius(6), Child = panel, Margin = new Thickness(0, 2, 0, 10) };
        frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return new BlockUIContainer(frame);
    }

    private WpfBlock TableView(Markdig.Extensions.Tables.Table table)
    {
        var view = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
        var columns = table.OfType<MdTableRow>().Max(r => r.Count);
        for (var i = 0; i < columns; i++) view.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdTableRow>())
        {
            var wpfRow = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var paragraph = new Paragraph { Margin = new Thickness(6, 3, 6, 3) };
                if (cell.FirstOrDefault() is ParagraphBlock content) AddInlines(paragraph.Inlines, content.Inline);
                if (row.IsHeader) paragraph.FontWeight = FontWeights.SemiBold;
                var wpfCell = new System.Windows.Documents.TableCell(paragraph) { BorderThickness = new Thickness(0, 0, 0, 1) };
                wpfCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                wpfRow.Cells.Add(wpfCell);
            }
            group.Rows.Add(wpfRow);
        }
        view.RowGroups.Add(group);
        return view;
    }

    private void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) target.Add(Convert(inline));
    }

    private WpfInline Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = (FontFamily)FindResource("OdsMonoFont"), FontSize = FontSize - 0.5 };
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                return run;
            case EmphasisInline emphasis:
                var span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : (Span)new Italic();
                AddInlines(span.Inlines, emphasis);
                return span;
            case LinkInline link when !link.IsImage:
                var hyperlink = new Hyperlink();
                AddInlines(hyperlink.Inlines, link);
                if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url));
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    hyperlink.NavigateUri = uri;
                    hyperlink.RequestNavigate += (_, e) => AppHost.OpenUrl(e.Uri.AbsoluteUri);
                    hyperlink.ToolTip = uri.AbsoluteUri;
                }
                return hyperlink;
            case AutolinkInline autolink:
                var auto = new Hyperlink(new Run(autolink.Url));
                if (Uri.TryCreate(autolink.Url, UriKind.Absolute, out var autoUri) && autoUri.Scheme.StartsWith("http", StringComparison.Ordinal))
                {
                    auto.NavigateUri = autoUri;
                    auto.RequestNavigate += (_, e) => AppHost.OpenUrl(e.Uri.AbsoluteUri);
                }
                return auto;
            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");
            case HtmlInline html:
                return new Run(html.Tag);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case ContainerInline nested:
                var group = new Span();
                AddInlines(group.Inlines, nested);
                return group;
            default:
                return new Run(inline.ToString() ?? "");
        }
    }
}
