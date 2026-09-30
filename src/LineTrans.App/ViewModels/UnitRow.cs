using System.ComponentModel;

using LineTrans.Core;

namespace LineTrans.App.ViewModels;

/// <summary>
/// 翻译工作台左侧列表的一行（一条翻译单元）。
/// 用经典 Binding 而不是 x:Bind：列表会随着编辑不断刷新，经典 Binding 更省心。
/// </summary>
public sealed class UnitRow : INotifyPropertyChanged
{
    public UnitRow(TranslationUnit unit, int index)
    {
        Unit = unit;
        Index = index;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranslationUnit Unit { get; }

    /// <summary>0 基下标。</summary>
    public int Index { get; }

    /// <summary>列表里显示的序号（1 基）。</summary>
    public string Number => (Index + 1).ToString();

    /// <summary>原文摘要（一行）。</summary>
    public string Preview
    {
        get
        {
            string s = (Unit.Source ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) s = "（空行）";
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }
    }

    /// <summary>译文摘要（没有译文时显示占位）。</summary>
    public string TranslationPreview
    {
        get
        {
            string t = (Unit.Translation ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length == 0 ? "尚未翻译" : (t.Length > 60 ? t.Substring(0, 60) + "…" : t);
        }
    }

    /// <summary>状态文案：已完成 / 已收藏 / 待翻译。</summary>
    public string StatusText
    {
        get
        {
            if (Unit.Done) return Unit.Starred ? "已标记完成 · 已收藏" : "已标记完成";
            if (Unit.IsTranslated) return Unit.Starred ? "已翻译 · 已收藏" : "已翻译";
            return Unit.Starred ? "待翻译 · 已收藏" : "待翻译";
        }
    }

    /// <summary>状态标记（Segoe Fluent Icons 字形）。</summary>
    public string StatusGlyph => Unit.Done ? "\uE73E" : Unit.IsTranslated ? "\uE8C8" : "\uE738";

    /// <summary>刷新绑定。</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
