using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NovaLink.Lumen.Services;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>文档块类型。Markdown 解析后只保留渲染需要的几种。</summary>
public enum DocBlockKind
{
    Heading1,
    Heading2,
    Heading3,
    Paragraph,
    Bullet,
    Numbered,
    Code,
    Note,
    Table,
    Rule
}

/// <summary>
/// 一个文档块。Code 的 Text 是代码原文，Language 是围栏语言；
/// Table 的 Text 每行一条记录，单元格之间用制表符分隔（界面上降级成逐行文本）。
/// </summary>
public sealed record DocBlock(DocBlockKind Kind, string Text, string Language = "");

/// <summary>
/// 文档正文：两档文档都来自随程序嵌入的 Markdown 文件，界面里不再手写文案。
/// 开发文档嵌入的是仓库里的 docs/PLUGINS.md 原文，见 NovaLink.Lumen.csproj。
/// </summary>
public static class DocsContent
{
    private const string UsageResource = "NovaLink.Lumen.Assets.docs.usage.md";
    private const string DevelopmentResource = "NovaLink.Lumen.Assets.docs.development.md";
    private const string Fence = "\u0060\u0060\u0060";

    private static IReadOnlyList<DocBlock>? _usage;
    private static IReadOnlyList<DocBlock>? _development;

    private static readonly Regex OrderedPattern = new(@"^\d+[.)]\s+", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorPattern = new(@"^\|?[\s:\-|]+\|?$", RegexOptions.Compiled);

    public static IReadOnlyList<DocBlock> Usage => _usage ??= Load(UsageResource);

    public static IReadOnlyList<DocBlock> Development => _development ??= Load(DevelopmentResource);

    private static IReadOnlyList<DocBlock> Load(string resourceName)
    {
        try
        {
            using var stream = typeof(DocsContent).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                AppLog.Warn("docs", "内嵌文档缺失：" + resourceName);
                return new[] { new DocBlock(DocBlockKind.Paragraph, "文档没有随程序打包，请重新安装启动器。") };
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "读取内嵌文档失败：" + resourceName + " " + ex.Message);
            return new[] { new DocBlock(DocBlockKind.Paragraph, "文档读取失败，请稍后再试。") };
        }
    }

    /// <summary>把 Markdown 解析成渲染块。只支持文档实际用到的语法：标题、段落、列表、围栏代码、引用、表格、分隔线。</summary>
    public static IReadOnlyList<DocBlock> Parse(string markdown)
    {
        var blocks = new List<DocBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // 围栏代码：整段原样保留，语言标签交给界面显示。
            if (trimmed.StartsWith(Fence, StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // 跳过结束围栏
                blocks.Add(new DocBlock(DocBlockKind.Code, code.ToString(), language));
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                blocks.Add(new DocBlock(DocBlockKind.Rule, string.Empty));
                i++;
                continue;
            }

            // 引用块：合并成一条提示。
            if (lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var note = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    var text = lines[i].TrimStart()[1..].Trim();
                    if (text.Length > 0)
                    {
                        if (note.Length > 0) note.Append(' ');
                        note.Append(text);
                    }
                    i++;
                }
                blocks.Add(new DocBlock(DocBlockKind.Note, note.ToString()));
                continue;
            }

            var level = HeadingLevel(trimmed);
            if (level > 0)
            {
                blocks.Add(new DocBlock(level switch
                {
                    1 => DocBlockKind.Heading1,
                    2 => DocBlockKind.Heading2,
                    _ => DocBlockKind.Heading3
                }, trimmed[(level + 1)..].Trim()));
                i++;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                blocks.Add(new DocBlock(DocBlockKind.Bullet, trimmed[2..].Trim()));
                i++;
                continue;
            }

            if (OrderedPattern.IsMatch(trimmed))
            {
                blocks.Add(new DocBlock(DocBlockKind.Numbered, trimmed));
                i++;
                continue;
            }

            // 表格：表头 + 分隔行 + 若干行；单元格里的行内标记在渲染时去掉。
            if (trimmed.StartsWith("|", StringComparison.Ordinal)
                && i + 1 < lines.Length
                && TableSeparatorPattern.IsMatch(lines[i + 1].Trim()))
            {
                var rows = new List<string> { NormalizeRow(trimmed) };
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal))
                {
                    rows.Add(NormalizeRow(lines[i].Trim()));
                    i++;
                }
                blocks.Add(new DocBlock(DocBlockKind.Table, string.Join("\n", rows)));
                continue;
            }

            // 普通段落：连续的非空、非结构行合并成一段。
            var paragraph = new StringBuilder(trimmed);
            i++;
            while (i < lines.Length)
            {
                var next = lines[i].Trim();
                if (next.Length == 0 || IsBlockStart(lines[i])) break;
                paragraph.Append(' ').Append(next);
                i++;
            }
            blocks.Add(new DocBlock(DocBlockKind.Paragraph, paragraph.ToString()));
        }

        return blocks;
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && level < 4 && line[level] == '#') level++;
        if (level is < 1 or > 3) return 0;
        return level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static bool IsBlockStart(string line)
    {
        var text = line.TrimStart();
        if (text.StartsWith(Fence, StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal)) return true;
        if (text.StartsWith(">", StringComparison.Ordinal)) return true;
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)) return true;
        if (OrderedPattern.IsMatch(text)) return true;
        if (text.StartsWith("|", StringComparison.Ordinal)) return true;
        return HeadingLevel(text) > 0;
    }

    private static string NormalizeRow(string row)
    {
        var text = row.Trim();
        if (text.StartsWith("|", StringComparison.Ordinal)) text = text[1..];
        if (text.EndsWith("|", StringComparison.Ordinal)) text = text[..^1];

        var cells = text.Split('|');
        var result = new List<string>(cells.Length);
        foreach (var cell in cells)
        {
            result.Add(cell.Trim());
        }
        return string.Join("\t", result);
    }
}
