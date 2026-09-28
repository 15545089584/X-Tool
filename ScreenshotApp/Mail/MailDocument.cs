using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using MimeKit.Text;

namespace ScreenshotApp.Mail;
/// <summary>白名单文本排版，不使用浏览器，不加载 HTML 中的图片、样式、脚本或链接资源。</summary>
public static class MailDocument
{
    public static FlowDocument Create(string text, bool html = false)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(47, 61, 80)), PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity };
        Paragraph paragraph = NewParagraph();
        doc.Blocks.Add(paragraph);
        void Break()
        {
            if (paragraph.Inlines.Count == 0) return;
            paragraph = NewParagraph(); doc.Blocks.Add(paragraph);
        }
        if (!html)
        {
            foreach (var line in text[..Math.Min(text.Length, 200000)].Replace("\r", "").Split('\n'))
            { paragraph.Inlines.Add(new Run(line)); Break(); }
            HighlightCodes(doc);
            HighlightLinks(doc);
            return doc;
        }
        using var input = new StringReader(text);
        var parser = new HtmlTokenizer(input) { DecodeCharacterReferences = true };
        string? hidden = null;
        Uri? link = null;
        int count = 0, bold = 0, code = 0, italic = 0;
        while (parser.ReadNextToken(out var token) && count < 200000)
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (hidden is not null) { if (tag.IsEndTag && name == hidden) hidden = null; continue; }
                if (!tag.IsEndTag && name is "script" or "style" or "head") { hidden = name; continue; }
                if (name == "a") link = tag.IsEndTag ? null : SafeLink(tag.Attributes.FirstOrDefault(a => a.Name.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value);
                if (name is "b" or "strong") bold = Math.Max(0, bold + (tag.IsEndTag ? -1 : 1));
                if (name is "em" or "i") italic = Math.Max(0, italic + (tag.IsEndTag ? -1 : 1));
                if (name is "code" or "pre") code = Math.Max(0, code + (tag.IsEndTag ? -1 : 1));
                if (name is "p" or "div" or "br" or "tr" or "li" or "blockquote" or "pre" or "h1" or "h2" or "h3")
                {
                    Break();
                    if (!tag.IsEndTag)
                    {
                        if (name is "h1" or "h2" or "h3") { paragraph.FontSize = name == "h1" ? 23 : 18; paragraph.FontWeight = FontWeights.SemiBold; }
                        if (name == "li") paragraph.Inlines.Add(new Run("•  "));
                        if (name is "pre" or "blockquote")
                        { paragraph.Background = new SolidColorBrush(Color.FromRgb(244,247,253)); paragraph.Padding = new Thickness(12); }
                    }
                }
            }
            else if (hidden is null && token is HtmlDataToken data)
            {
                var value = data.Data[..Math.Min(data.Data.Length, 200000 - count)];
                count += value.Length;
                if (code == 0) value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ");
                if (string.IsNullOrWhiteSpace(value) && paragraph.Inlines.Count == 0) continue;
                var run = new Run(value);
                if (bold > 0) run.FontWeight = FontWeights.SemiBold;
                if (italic > 0) run.FontStyle = FontStyles.Italic;
                if (code > 0) { run.FontFamily = new FontFamily("Consolas"); run.Background = new SolidColorBrush(Color.FromRgb(238,242,249)); }
                if (link is not null) paragraph.Inlines.Add(CreateLink(run, link));
                else paragraph.Inlines.Add(run);
            }
        }
        HighlightCodes(doc);
        HighlightLinks(doc);
        return doc;
    }
    public static Uri? SafeLink(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    private static Hyperlink CreateLink(Run run, Uri uri)
    {
        var link = new Hyperlink(run) { NavigateUri = uri, Foreground = new SolidColorBrush(Color.FromRgb(37,99,235)),
            TextDecorations = TextDecorations.Underline, ToolTip = uri.AbsoluteUri };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            if (SafeLink(e.Uri.AbsoluteUri) is not { } target) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
            catch { MessageBox.Show("无法打开默认浏览器，请复制链接后打开。", "打开邮件链接"); }
        };
        return link;
    }
    private static void HighlightLinks(FlowDocument doc)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"https?://[^\s<>""\u3000]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        foreach (var paragraph in doc.Blocks.OfType<Paragraph>().ToArray())
            foreach (var run in paragraph.Inlines.OfType<Run>().ToArray())
            {
                System.Text.RegularExpressions.MatchCollection matches;
                try { matches = pattern.Matches(run.Text); _ = matches.Count; } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { continue; }
                if (matches.Count == 0) continue;
                var cursor = 0;
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var value = match.Value.TrimEnd('.', ',', ';', ')', ']', '。', '，', '；', '）');
                    if (SafeLink(value) is not { } uri) continue;
                    paragraph.Inlines.InsertBefore(run, new Run(run.Text[cursor..match.Index]));
                    paragraph.Inlines.InsertBefore(run, CreateLink(new Run(value), uri));
                    cursor = match.Index + value.Length;
                }
                if (cursor == 0) continue;
                paragraph.Inlines.InsertBefore(run, new Run(run.Text[cursor..])); paragraph.Inlines.Remove(run);
            }
    }
    private static void HighlightCodes(FlowDocument doc)
    {
        var runs = new List<(Paragraph Parent, Run Run, int Start)>();
        var text = new System.Text.StringBuilder();
        foreach (var paragraph in doc.Blocks.OfType<Paragraph>().ToArray())
        {
            foreach (var run in paragraph.Inlines.OfType<Run>())
            { runs.Add((paragraph, run, text.Length)); text.Append(run.Text); }
            text.Append('\n');
        }
        var matches = MailCodeHighlighter.Find(text.ToString());
        foreach (var item in runs)
        {
            var value = item.Run.Text;
            var ranges = matches.Where(m => m.Start < item.Start + value.Length && m.Start + m.Length > item.Start).ToArray();
            if (ranges.Length == 0) continue;
            void Insert(string part, bool highlight)
            {
                if (part.Length == 0) return;
                var replacement = new Run(part) { FontFamily = item.Run.FontFamily, FontSize = item.Run.FontSize,
                    FontWeight = item.Run.FontWeight, FontStyle = item.Run.FontStyle,
                    Foreground = item.Run.Foreground, Background = item.Run.Background };
                if (highlight)
                {
                    replacement.Background = new SolidColorBrush(Color.FromRgb(254, 240, 173));
                    replacement.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                    replacement.FontWeight = FontWeights.Bold;
                    replacement.ToolTip = "可能是验证码或授权码，请核对邮件内容";
                }
                item.Parent.Inlines.InsertBefore(item.Run, replacement);
            }
            var cursor = 0;
            foreach (var range in ranges)
            {
                var start = Math.Max(0, range.Start - item.Start);
                var end = Math.Min(value.Length, range.Start + range.Length - item.Start);
                Insert(value[cursor..start], false); Insert(value[start..end], true); cursor = end;
            }
            Insert(value[cursor..], false);
            item.Parent.Inlines.Remove(item.Run);
        }
    }
    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0,0,0,12), LineHeight = 24 };
}
