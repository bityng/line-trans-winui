using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using LineTrans.App.Services;
using LineTrans.App.ViewModels;
using LineTrans.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Windows.Storage;
using Windows.Storage.Pickers;

namespace LineTrans.App.Views;

/// <summary>
/// 文档页：列出全部文档（按文件夹分组）、搜索 / 筛选、新建、从文件导入、重命名 / 移动 / 置顶 / 删除。
/// 列表在代码里拼装而不是用模板选择器：数据结构简单，代码拼装没有绑定失败的风险。
/// </summary>
public sealed partial class HomePage : Page
{
    private bool _ready;

    public HomePage()
    {
        InitializeComponent();

        FontSize = AppServices.BodyFontSize;

        FilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = (int)AppServices.Docs.SortMode;

        _ready = true;
        Refresh();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    // ------------------------------------------------------------------
    // 列表渲染
    // ------------------------------------------------------------------

    private void Refresh()
    {
        if (!_ready) return;

        var filter = FilterBox.SelectedIndex switch
        {
            1 => DocFilter.UNFINISHED,
            2 => DocFilter.STARRED,
            _ => DocFilter.ALL,
        };

        var all = AppServices.Docs.Docs;
        var shown = new List<DocRow>();
        foreach (var doc in all)
        {
            var row = new DocRow(doc);
            if (row.Matches(SearchBox.Text, filter)) shown.Add(row);
        }

        int finished = 0;
        int remaining = 0;
        foreach (var doc in all)
        {
            if (doc.IsFinished) finished++;
            remaining += doc.RemainingCount;
        }

        SummaryText.Text = "共 " + all.Count + " 篇文档　·　已完成 " + finished + " 篇　·　待翻译 "
            + remaining + " 句" + (shown.Count == all.Count ? string.Empty : "　·　当前筛选命中 " + shown.Count + " 篇");

        ListHost.Children.Clear();

        if (shown.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = all.Count == 0
                    ? "还没有任何文档。点击「新建文档」粘贴正文，或「从文件导入」选择 .txt / .md / .srt / .csv 文件。"
                    : "没有符合当前搜索与筛选条件的文档。",
            };
            empty.Style = (Style)Resources["EmptyStateTextStyle"];
            ListHost.Children.Add(empty);
            return;
        }

        // 按文件夹分组，保持仓库给出的排序（置顶的文档自然排在各组最前）
        var order = new List<string>();
        var groups = new Dictionary<string, List<DocRow>>(StringComparer.Ordinal);
        foreach (var row in shown)
        {
            string folder = string.IsNullOrWhiteSpace(row.Folder) ? TranslationDoc.DefaultFolder : row.Folder;
            if (!groups.TryGetValue(folder, out var list))
            {
                list = new List<DocRow>();
                groups[folder] = list;
                order.Add(folder);
            }
            list.Add(row);
        }

        foreach (string folder in order)
        {
            var header = new TextBlock { Text = folder + "　·　" + groups[folder].Count + " 篇" };
            header.Style = (Style)Resources["FolderHeaderTextStyle"];
            ListHost.Children.Add(header);

            foreach (var row in groups[folder])
            {
                ListHost.Children.Add(BuildCard(row));
            }
        }
    }

    private Border BuildCard(DocRow row)
    {
        var card = new Border { Style = (Style)Resources["DocCardBorderStyle"] };
        card.Tapped += (s, e) => OpenDocument(row);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 6 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var title = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center };
        title.Style = (Style)Resources["DocTitleTextStyle"];
        titleRow.Children.Add(title);
        titleRow.Children.Add(BuildBadge(row.ModeLabel));
        if (row.Doc.Pinned) titleRow.Children.Add(BuildBadge("已置顶"));
        if (row.IsFinished) titleRow.Children.Add(BuildBadge("已完成"));
        left.Children.Add(titleRow);

        var meta = new TextBlock
        {
            Text = "文件夹：" + row.Folder + "　·　收藏 " + row.Doc.StarredCount + " 条　·　更新 " + row.UpdatedText,
        };
        meta.Style = (Style)Resources["DocMetaTextStyle"];
        left.Children.Add(meta);

        var progressRow = new Grid { ColumnSpacing = 10 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = row.ProgressPercent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Style = (Style)Resources["DocProgressBarStyle"];
        progressRow.Children.Add(bar);

        var percent = new TextBlock { Text = row.ProgressText, VerticalAlignment = VerticalAlignment.Center };
        percent.Style = (Style)Resources["DocMetaTextStyle"];
        Grid.SetColumn(percent, 1);
        progressRow.Children.Add(percent);

        left.Children.Add(progressRow);
        grid.Children.Add(left);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var openButton = new Button { Content = "打开" };
        openButton.Style = (Style)Resources["RowButtonStyle"];
        openButton.Click += (s, e) => OpenDocument(row);
        actions.Children.Add(openButton);

        var moreButton = new Button { Content = "更多" };
        moreButton.Style = (Style)Resources["RowButtonStyle"];
        moreButton.Click += (s, e) => ShowDocMenu(moreButton, row);
        actions.Children.Add(moreButton);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private Border BuildBadge(string text)
    {
        var badge = new Border { Style = (Style)Resources["BadgeBorderStyle"] };
        var label = new TextBlock { Text = text };
        label.Style = (Style)Resources["BadgeTextStyle"];
        badge.Child = label;
        return badge;
    }

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------

    private void OpenDocument(DocRow row)
    {
        AppServices.Log("打开文档：" + row.Name);
        MainWindow.Instance?.OpenDocument(row.Doc.Id);
    }

    private void ShowDocMenu(FrameworkElement anchor, DocRow row)
    {
        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "打开" };
        open.Click += (s, e) => OpenDocument(row);
        flyout.Items.Add(open);

        var pin = new MenuFlyoutItem { Text = row.Doc.Pinned ? "取消置顶" : "置顶" };
        pin.Click += (s, e) =>
        {
            AppServices.Docs.SetPinned(row.Doc.Id, !row.Doc.Pinned);
            Refresh();
        };
        flyout.Items.Add(pin);

        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += async (s, e) => await RenameAsync(row);
        flyout.Items.Add(rename);

        var move = new MenuFlyoutItem { Text = "移动到文件夹" };
        move.Click += async (s, e) => await MoveAsync(row);
        flyout.Items.Add(move);

        // 「导出」二级菜单：六种格式 + 「导出到数据目录」。
        // 菜单项由 ExportService 统一造，翻译页工具栏用的是同一套。
        var export = new MenuFlyoutSubItem { Text = "导出" };
        foreach (var item in ExportService.BuildMenu(row.Doc, ShowExportResultAsync).Items)
        {
            export.Items.Add(item);
        }
        flyout.Items.Add(export);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "删除" };
        remove.Click += async (s, e) => await DeleteAsync(row);
        flyout.Items.Add(remove);

        flyout.ShowAt(anchor);
    }

    private async Task RenameAsync(DocRow row)
    {
        var input = new TextBox { Text = row.Name, PlaceholderText = "文档名称", Header = "文档名称" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名文档",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string name = (input.Text ?? string.Empty).Trim();
        if (name.Length == 0) return;

        AppServices.Docs.Rename(row.Doc.Id, name);
        Refresh();
    }

    private async Task MoveAsync(DocRow row)
    {
        var folders = AppServices.Docs.Folders();
        var input = new TextBox { Text = row.Folder, PlaceholderText = "文件夹名称", Header = "移动到文件夹" };
        var hint = new TextBlock
        {
            Text = folders.Count == 0
                ? "还没有其它文件夹，输入一个新名字即可创建。"
                : "已有文件夹：" + string.Join("、", folders),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        };

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(input);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移动到文件夹",
            Content = panel,
            PrimaryButtonText = "移动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string folder = (input.Text ?? string.Empty).Trim();
        if (folder.Length == 0) return;

        AppServices.Docs.Move(row.Doc.Id, folder);
        Refresh();
    }

    private async Task DeleteAsync(DocRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除文档",
            Content = new TextBlock
            {
                Text = "确定要删除《" + row.Name + "》吗？\n\n该文档的原文与全部译文都会被永久删除，无法撤销。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        AppServices.Docs.Delete(row.Doc.Id);
        await AppServices.Docs.FlushAsync();
        Refresh();
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = await ShowNewDocDialogAsync(
                "新建文档",
                "未命名文档",
                string.Empty,
                "正文会按所选方式切分，创建后立刻写入磁盘。");
            if (request == null) return;

            string body = request.SmartClean ? TextParser.SmartClean(request.Text) : request.Text;
            AppServices.Docs.Create(request.Name, body, request.Mode);
            await AppServices.Docs.FlushAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("新建文档失败", ex.Message);
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".srt");
            picker.FileTypeFilter.Add(".csv");

            var window = MainWindow.Instance;
            if (window != null)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string text = await FileIO.ReadTextAsync(file);
            string defaultName = Path.GetFileNameWithoutExtension(file.Name);

            var request = await ShowNewDocDialogAsync(
                "从文件导入",
                defaultName,
                text,
                "来源：" + file.Name + "（" + text.Length + " 字符）");
            if (request == null) return;

            string body = request.SmartClean ? TextParser.SmartClean(request.Text) : request.Text;
            AppServices.Docs.Create(request.Name, body, request.Mode);
            await AppServices.Docs.FlushAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导入失败", ex.Message);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppServices.Docs.FlushAsync();
            AppServices.Docs.Load();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("刷新失败", ex.Message);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        AppServices.Docs.SetSort(SortBox.SelectedIndex switch
        {
            1 => DocSort.NAME,
            2 => DocSort.PROGRESS,
            _ => DocSort.UPDATED,
        });
        Refresh();
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------

    /// <summary>
    /// 文档页的导出反馈用对话框（译文页在顶部条上，用的是 InfoBar）。
    /// 先让出一拍再弹：菜单项点击时 MenuFlyout 还在收拢，立刻弹对话框会撞上
    /// 「同一时间只能有一个 ContentDialog」的限制。
    /// </summary>
    private async Task ShowExportResultAsync(ExportOutcome? outcome)
    {
        if (outcome == null) return; // 用户取消

        await Task.Yield();

        if (!outcome.Ok)
        {
            await ShowMessageAsync("导出失败", outcome.Error ?? "未知错误");
            return;
        }

        AppServices.Log("导出完成：" + outcome.FilePath);

        string text = "格式：" + outcome.FormatLabel
            + "\n条目：" + outcome.Units + " 条　·　大小：" + outcome.Bytes + " 字节"
            + "\n位置：" + outcome.FilePath;
        if (outcome.Warning is { Length: > 0 })
        {
            text = outcome.Warning + "\n\n" + text;
        }

        string dir = Path.GetDirectoryName(outcome.FilePath) ?? outcome.FilePath;

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导出成功",
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                PrimaryButtonText = "打开所在文件夹",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                AppServices.OpenDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log("导出提示弹窗失败：" + ex.Message);
            await ShowMessageAsync("导出成功", text);
        }
    }

    // ------------------------------------------------------------------
    // 新建 / 导入对话框
    // ------------------------------------------------------------------

    private sealed record NewDocRequest(string Name, string Text, UnitMode Mode, bool SmartClean);

    private async Task<NewDocRequest?> ShowNewDocDialogAsync(
        string title,
        string defaultName,
        string initialText,
        string hint)
    {
        var nameBox = new TextBox { Text = defaultName, PlaceholderText = "文档名称", Header = "文档名称" };

        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = "在这里粘贴或输入源文本…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            Header = "源文本",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        var lineRadio = new RadioButton { Content = "逐行（一行一条，空行只用于分段）", IsChecked = true, GroupName = "unitmode" };
        var sentenceRadio = new RadioButton { Content = "逐句（按句末标点断句）", GroupName = "unitmode" };
        var cleanCheck = new CheckBox
        {
            Content = "智能清理（去字幕时间轴 / 序号行 / Markdown 标记）",
            IsChecked = true,
        };

        var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(nameBox);
        panel.Children.Add(textBox);
        panel.Children.Add(lineRadio);
        panel.Children.Add(sentenceRadio);
        panel.Children.Add(cleanCheck);
        panel.Children.Add(hintText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        string name = (nameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0) name = "未命名文档";

        var mode = sentenceRadio.IsChecked == true ? UnitMode.SENTENCE : UnitMode.LINE;
        return new NewDocRequest(name, textBox.Text ?? string.Empty, mode, cleanCheck.IsChecked == true);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
            };
            await dialog.ShowAsync();
        }
        catch
        {
            AppServices.Log(title + "：" + message);
        }
    }
}
