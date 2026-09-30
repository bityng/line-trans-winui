using System;
using System.Collections.Generic;

using LineTrans.Core.Dictionary;

namespace LineTrans.App.Services;

/// <summary>查词弹窗要显示的全部内容（由 <see cref="GlobalCaptureService"/> 组装）。</summary>
public sealed class LookupPopupContent
{
    /// <summary>捕获到的原文。</summary>
    public string Captured { get; init; } = string.Empty;

    /// <summary>标题行：单词本身 / 「整句翻译」/ 「未检测到选中文字」。</summary>
    public string Headline { get; init; } = string.Empty;

    /// <summary>音标（只有单词才有）。</summary>
    public string Phonetic { get; init; } = string.Empty;

    /// <summary>来源徽章：本地词库 / AI 翻译 / AI 释义 / 提示。</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>正文：释义或译文。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>底部状态行（剪贴板是否已还原、错误原因……）。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>是否允许「加入我的词库」。</summary>
    public bool CanAddToWordbook { get; init; }

    /// <summary>正文是否还在加载（加载时不显示复制按钮的可用态）。</summary>
    public bool IsLoading { get; init; }

    public static LookupPopupContent NoSelection(string status) => new()
    {
        Headline = "未检测到选中文字",
        Badge = "提示",
        Body = "没有从当前程序里复制到任何文字。\n\n" +
               "常见原因：\n" +
               "· 目标程序里确实没有选中内容；\n" +
               "· 该程序不接受模拟的 Ctrl+C（部分全屏游戏、管理员权限程序）；\n" +
               "· 焦点在系统安全界面上（UAC 弹窗、登录界面）。",
        Status = status,
    };

    public static LookupPopupContent Loading(string captured) => new()
    {
        Captured = captured,
        Headline = captured,
        Badge = "查询中",
        Body = "正在查询…",
        IsLoading = true,
    };

    /// <summary>把词库结果拼成多行释义（与翻译页的浮层保持同一种排版）。</summary>
    public static string FormatSenses(DictEntry entry)
    {
        var lines = new List<string>();
        foreach (var sense in entry.Senses)
        {
            string pos = sense.PartOfSpeech ?? string.Empty;
            string definition = sense.Definition ?? string.Empty;
            if (definition.Length == 0) continue;
            lines.Add(pos.Length == 0 ? definition : pos + ". " + definition);
        }
        if (!string.IsNullOrWhiteSpace(entry.Translation)) lines.Add(entry.Translation!.Trim());
        return string.Join("\n", lines);
    }
}
