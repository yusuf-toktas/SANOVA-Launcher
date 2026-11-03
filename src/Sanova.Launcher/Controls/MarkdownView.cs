using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Controls;

/// <summary>
/// Renders the small Markdown subset used in release notes: headings, paragraphs, bullet and
/// numbered lists, bold, italic, inline code and links. Text only — no HTML, no images —
/// because release notes come from the network. Links open only for http(s).
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    [GeneratedRegex(@"(\*\*(?<bold>.+?)\*\*)|(`(?<code>[^`]+)`)|(\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\))|((?<![\w*])[*_](?<italic>[^*_]+)[*_](?![\w*]))", RegexOptions.CultureInvariant)]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedPattern();

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView()
    {
        Spacing = 10;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        var text = Markdown;
        if (string.IsNullOrWhiteSpace(text)) return;

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            Children.Add(CreateText(string.Join(" ", paragraph), "body"));
            paragraph.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                Children.Add(CreateText(trimmed[level..].Trim(), level <= 1 ? "h2" : "h3"));
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                FlushParagraph();
                Children.Add(CreateListItem("•", trimmed[2..].Trim()));
            }
            else if (NumberedPattern().Match(trimmed) is { Success: true } numbered)
            {
                FlushParagraph();
                Children.Add(CreateListItem(numbered.Groups[1].Value + ".", numbered.Groups[2].Value));
            }
            else
            {
                paragraph.Add(trimmed);
            }
        }
        FlushParagraph();
    }

    private static Control CreateListItem(string marker, string content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
        var bullet = new TextBlock { Text = marker, Classes = { "muted" }, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Top };
        var text = CreateText(content, "body");
        Grid.SetColumn(text, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(text);
        return grid;
    }

    private static SelectableTextBlock CreateText(string content, string style)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { style } };
        if (style == "body") block.Classes.Add("muted");
        var inlines = block.Inlines!;
        var index = 0;
        foreach (Match match in InlinePattern().Matches(content))
        {
            if (match.Index > index) inlines.Add(new Run(content[index..match.Index]));
            if (match.Groups["bold"].Success)
            {
                inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.Bold });
            }
            else if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New") });
            }
            else if (match.Groups["text"].Success)
            {
                var url = match.Groups["url"].Value;
                if (ShellIntegration.IsSafeWebLink(url))
                {
                    var link = new Button { Content = match.Groups["text"].Value, Classes = { "link" }, Padding = new Thickness(0) };
                    ToolTip.SetTip(link, url);
                    link.Click += (_, _) => ShellIntegration.OpenUrl(url);
                    inlines.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
                }
                else
                {
                    inlines.Add(new Run(match.Groups["text"].Value));
                }
            }
            else if (match.Groups["italic"].Success)
            {
                inlines.Add(new Run(match.Groups["italic"].Value) { FontStyle = FontStyle.Italic });
            }
            index = match.Index + match.Length;
        }
        if (index < content.Length) inlines.Add(new Run(content[index..]));
        return block;
    }
}
