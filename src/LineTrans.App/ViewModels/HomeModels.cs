using System;
using System.Globalization;

using LineTrans.Core;

namespace LineTrans.App.ViewModels;

/// <summary>文档列表筛选（全部 / 未完成 / 收藏）。</summary>
public enum DocFilter
{
    ALL,
    UNFINISHED,
    STARRED,
}

/// <summary>文件夹分组标题行（文档列表在代码里按文件夹拼装，不用模板选择器）。</summary>
public sealed class FolderRow
{
    public FolderRow(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    public int Count { get; }

    public string Summary => Name + "　·　" + Count + " 篇";
}

/// <summary>文档列表的一行。</summary>
public sealed class DocRow
{
    public DocRow(TranslationDoc doc)
    {
        Doc = doc;
    }

    public TranslationDoc Doc { get; }

    public string Name => Doc.Name;

    public string Folder => Doc.Folder;

    public string ModeLabel => Doc.UnitMode == UnitMode.LINE ? "逐行" : "逐句";

    public string ProgressText => Doc.TranslatedCount + " / " + Doc.TotalCount;

    public double ProgressPercent => Math.Round(Doc.Progress * 100.0, 1);

    public bool IsFinished => Doc.IsFinished;

    public string UpdatedText
    {
        get
        {
            try
            {
                var local = DateTimeOffset.FromUnixTimeMilliseconds(Doc.UpdatedAt).ToLocalTime();
                return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>卡片右上角的一行小字：置顶 / 收藏 / 模式 / 更新时间。</summary>
    public string MetaText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (Doc.Pinned) parts.Add("★ 已置顶");
            parts.Add(ModeLabel);
            parts.Add("收藏 " + Doc.StarredCount);
            parts.Add("更新 " + UpdatedText);
            return string.Join("　·　", parts);
        }
    }

    /// <summary>是否命中搜索 / 筛选。</summary>
    public bool Matches(string keyword, DocFilter filter)
    {
        if (filter == DocFilter.UNFINISHED && Doc.IsFinished) return false;
        if (filter == DocFilter.STARRED && Doc.StarredCount == 0) return false;

        if (string.IsNullOrWhiteSpace(keyword)) return true;
        string k = keyword.Trim();
        return Doc.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
            || Doc.Folder.Contains(k, StringComparison.OrdinalIgnoreCase)
            || Doc.SourceText.Contains(k, StringComparison.OrdinalIgnoreCase);
    }
}
