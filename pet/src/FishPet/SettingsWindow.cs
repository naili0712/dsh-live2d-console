using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FishPet;

// local patch (设置面板): 托盘或右键打开的独立设置窗——改完立刻生效、顺手落盘。
// local patch (模型页): 三个页签：外观与行为 / 模型与适配 / 动作与差分。
// local patch (模型适配): 空闲与点击的选择按模型分开存在 adapt 文件夹里，换模型自动各用各的；
// 六个点击触发是照着大肥鱼那套写的，换模型后要重新指到该模型的差分或动作上（可以让 AI 配）。
internal sealed class SettingsWindow : Window
{
    private readonly Settings settings;
    private readonly AdaptFile adapt;
    private readonly Action apply;
    private readonly Func<string?> currentModel;
    private readonly Action<string> switchModel;
    private readonly Action<string, string> preview;
    private readonly Action restore;
    // local patch (模型适配): 用户点「让 AI 调」时，请桌宠把请求交给 agent。
    private readonly Action requestAdapt;
    // local patch (外观差分): 勾/取消一个常驻差分，让桌宠当场发。
    private readonly Action<string, bool> wearDiff;
    private readonly ComboBox shape = new() { Width = 160 };
    private readonly Slider scale = new() { Minimum = .5, Maximum = 2, TickFrequency = .05, IsSnapToTickEnabled = true, Width = 150 };
    private readonly Slider bubble = new() { Minimum = .5, Maximum = 2, TickFrequency = .05, IsSnapToTickEnabled = true, Width = 150 };
    private readonly Slider stay = new() { Minimum = -1, Maximum = 180, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 150 };
    private readonly ComboBox fps = new() { Width = 160 };
    private readonly Slider idle = new() { Minimum = 15, Maximum = 600, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 150 };
    private readonly CheckBox follow = new() { Content = "视线跟着鼠标" };
    private readonly CheckBox hide = new() { Content = "别的程序全屏时藏起来" };
    private readonly CheckBox auto = new() { Content = "登录 Windows 时自动出现" };
    private readonly CheckBox lockPos = new() { Content = "固定位置（点她不会变成拖她）" }; // local patch (锁定位置)
    private readonly ComboBox session = new() { Width = 250, IsEditable = true };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) };
    // local patch (模型页): 模型下拉、当前模型说明
    private readonly ComboBox modelPick = new() { Width = 380, DisplayMemberPath = "Label" };
    private readonly TextBlock modelNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 0) };
    // local patch (模型页): 空闲池的逐项权重
    private readonly StackPanel parts = new();
    private readonly ObservableCollection<TriggerChoice> triggers = new();
    private readonly List<(ComboBox Box, string Zone)> triggerBoxes = [];
    // local patch (外观差分): 当前模型的差分，勾了就一直开着
    private readonly StackPanel diffs = new();
    // local patch (组合动作): 每个部位一行「组合…」和它的状态字
    private readonly Dictionary<string, TextBlock> comboStates = [];
    private readonly Dictionary<string, Button> comboClears = [];
    private readonly System.Windows.Threading.DispatcherTimer flash = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private string? flashZone;
    private static readonly (string Id, string Label)[] Zones = [("head", "头"), ("face", "脸"), ("hand", "手"), ("skirt", "裙"), ("tail", "尾巴"), ("body", "身上")];
    // local patch (模型适配): 当前模型的适配状态与两个按钮
    private readonly TextBlock adaptStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 4) };
    private readonly Button askAi = new() { Content = "让 AI 调", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 2, 8, 0) };
    private readonly Button resetAdapt = new() { Content = "恢复这个模型的默认适配", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 2, 0, 0) };
    private DateTime? askedAt;
    // local patch (模型适配): AI 配完会改文件，窗口开着也能自己跟上
    private readonly System.Windows.Threading.DispatcherTimer adaptWatch = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private DateTime adaptSeen;
    // local patch (设置面板): 改哪儿就存哪儿，不用点保存；拖滑块时等手停下来再应用。
    private bool ready;
    private readonly System.Windows.Threading.DispatcherTimer saveLater = new() { Interval = TimeSpan.FromMilliseconds(350) };
    // local patch (设置面板): 每个滑块都带一个数字，拖的时候跟着变
    private readonly TextBlock scaleText = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock bubbleText = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock stayText = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock idleText = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    // local patch (说话页): 字数上限与台词历史
    private readonly TextBox sayLimit = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock sayLimitHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) };
    private readonly StackPanel talkHistory = new() { Margin = new Thickness(0, 4, 0, 0) };

    private sealed record ModelChoice(string Path, string Label);
    private sealed record TriggerChoice(string Value, string Label);

    internal SettingsWindow(Settings settings, AdaptFile adapt, Action apply, Func<string?> currentModel, Action<string> switchModel, Action<string, string> preview, Action restore, Action requestAdapt, Action<string, bool> wearDiff)
    {
        this.settings = settings; this.adapt = adapt; this.apply = apply;
        this.currentModel = currentModel; this.switchModel = switchModel; this.preview = preview; this.restore = restore;
        this.requestAdapt = requestAdapt; this.wearDiff = wearDiff;
        settings.ClickTriggers ??= [];
        Title = "dshlive2d控制台 · 设置";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        Width = 820;
        Height = 920;
        MinWidth = 600; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false; // local patch (设置面板): 别再压着别的窗，想切走就切走
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(252, 250, 246));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        shape.ItemsSource = FishPet.Shape.All.Select(item => item.Label).ToArray();
        shape.SelectedItem = FishPet.Shape.Find(settings.Shape).Label;
        fps.ItemsSource = Settings.FpsChoices;
        fps.SelectedItem = settings.Fps;
        scale.Value = settings.Scale;
        bubble.Value = settings.BubbleScale;
        stay.Value = settings.BubbleSeconds;
        idle.Value = settings.IdleSeconds;
        follow.IsChecked = settings.FollowMouse;
        hide.IsChecked = settings.HideOnFullscreen;
        auto.IsChecked = settings.AutoStart;
        lockPos.IsChecked = settings.LockPosition;
        session.ItemsSource = RecentSessions();
        session.Text = settings.SessionTarget ?? "";
        scaleText.Text = scale.Value.ToString("0.00");
        bubbleText.Text = bubble.Value.ToString("0.00");
        stayText.Text = StayLabel((int)stay.Value);
        idleText.Text = (int)idle.Value + " 秒";
        scale.ValueChanged += (_, _) => scaleText.Text = scale.Value.ToString("0.00");
        bubble.ValueChanged += (_, _) => bubbleText.Text = bubble.Value.ToString("0.00");
        stay.ValueChanged += (_, _) => stayText.Text = StayLabel((int)stay.Value);
        idle.ValueChanged += (_, _) => idleText.Text = (int)idle.Value + " 秒";

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Group("外观"));
        panel.Children.Add(Row("体型", shape));
        panel.Children.Add(Row("大小", With(scale, scaleText)));
        panel.Children.Add(Row("气泡大小", With(bubble, bubbleText)));
        panel.Children.Add(Row("气泡停留", With(stay, stayText)));
        panel.Children.Add(Row("帧率", fps));
        // local patch (外观差分): 模型自带的差分，勾上就常驻；按模型各存各的。
        panel.Children.Add(Group("差分"));
        panel.Children.Add(new TextBlock
        {
            Text = "当前模型自带的表情/差分，勾上就一直开着；换模型时自动换成那个模型自己那份。想让她穿哪件、戴什么，在这里勾。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 4),
        });
        panel.Children.Add(diffs);
        panel.Children.Add(Group("行为"));
        panel.Children.Add(Row("", follow));
        panel.Children.Add(Row("", hide));
        panel.Children.Add(Row("", lockPos));
        panel.Children.Add(Row("空闲小动作", With(idle, idleText)));
        panel.Children.Add(Group("启动"));
        panel.Children.Add(Row("", auto));
        panel.Children.Add(Group("会话指向"));
        var pick = new StackPanel { Orientation = Orientation.Horizontal };
        pick.Children.Add(session);
        var again = new Button { Content = "刷新", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        again.Click += (_, _) => session.ItemsSource = RecentSessions();
        pick.Children.Add(again);
        panel.Children.Add(pick);
        note.Text = "留空 = 桥会自己建一个「dshlive2d控制台 · 桌面」会话专门用；也可以填 session-… 指定。填了但那个会话不在线时，会回退到最近活跃的会话（跳过 Aurabot 的）。";
        panel.Children.Add(note);

        // local patch (模型页): 三个页签；按钮钉在底部，页签和滚动条各页各的
        var tabs = new TabControl { Margin = new Thickness(8, 6, 8, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "外观与行为", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "模型与适配", Content = new ScrollViewer { Content = BuildAdaptPage(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "动作与差分", Content = new ScrollViewer { Content = BuildPartsPage(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        // local patch (说话页): 字数上限与她说过的每一句
        tabs.Items.Add(new TabItem { Header = "说话与交流", Content = new ScrollViewer { Content = BuildTalkPage(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        // local patch (扩展包): 装了扩展、或者装过但全停用了，才多这一页
        if (Extensions.All.Count > 0)
        {
            tabs.Items.Add(new TabItem { Header = "扩展包", Content = new ScrollViewer { Content = BuildPacksPage(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 10, 18, 14) };
        var close = new Button { Content = "关闭", Padding = new Thickness(22, 5, 22, 5) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;

        RefreshModels();
        RefreshParts();
        adaptSeen = AdaptStamp();
        adaptWatch.Tick += (_, _) => WatchAdapt();
        adaptWatch.Start();
        flash.Tick += (_, _) => { flash.Stop(); if (flashZone is { } zone) UpdateCombo(zone); flashZone = null; };
        Closed += (_, _) => { adaptWatch.Stop(); flash.Stop(); };

        saveLater.Tick += (_, _) => { saveLater.Stop(); Persist(); };
        void Touch(object? sender, EventArgs e) { if (!ready) return; saveLater.Stop(); saveLater.Start(); }
        shape.SelectionChanged += Touch;
        fps.SelectionChanged += Touch;
        scale.ValueChanged += Touch;
        bubble.ValueChanged += Touch;
        stay.ValueChanged += Touch;
        idle.ValueChanged += Touch;
        follow.Checked += Touch; follow.Unchecked += Touch;
        hide.Checked += Touch; hide.Unchecked += Touch;
        auto.Checked += Touch; auto.Unchecked += Touch;
        lockPos.Checked += Touch; lockPos.Unchecked += Touch;
        session.LostFocus += Touch;
        ready = true;
    }

    private ModelProfile Profile => adapt.Current(currentModel());

    private static Button RestoreButton(string text, Action run)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => run();
        return button;
    }

    // ---- local patch (扩展包): 装进来的动作与内容 ----

    private UIElement BuildPacksPage()
    {
        var page = new StackPanel { Margin = new Thickness(18) };
        page.Children.Add(Group("装着的扩展"));
        page.Children.Add(new TextBlock
        {
            Text = "每个扩展是一个文件夹，放在插件目录的 extensions 里，里面一个 pack.json 写它带了什么：动作词（〔〕里能写的）、表情（点她和闲着时演的）、台词（内容库）。内置那套永远优先，扩展只补它没占的名字；两个扩展抢同一个名字，按文件夹名排前面的赢。装好后重新扫描，动作与差分页里就会多出它们的选项，并注明是哪个包带来的。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 2, 0, 6),
        });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        bar.Children.Add(RestoreButton("打开扩展文件夹", OpenPacksFolder));
        var again = new Button { Content = "重新扫描", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        again.Click += (_, _) => ReloadExtensions();
        bar.Children.Add(again);
        page.Children.Add(bar);

        if (Extensions.All.Count == 0)
        {
            page.Children.Add(new TextBlock { Text = "还没有装扩展。把扩展包文件夹放进 extensions 里，再点「重新扫描」。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
        }
        foreach (var pack in Extensions.All) page.Children.Add(PackRow(pack));

        if (Extensions.Skipped.Count > 0)
        {
            page.Children.Add(Group("同名没装上的"));
            page.Children.Add(new TextBlock
            {
                Text = string.Join("、", Extensions.Skipped.Select(pair => pair.Key + "（被 " + Extensions.PackName(pair.Value) + " 占了）")),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 90, 40)), Margin = new Thickness(0, 2, 0, 4),
            });
        }
        if (Extensions.LastError is { } trouble)
        {
            page.Children.Add(new TextBlock { Text = "⚠ " + trouble, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 90, 40)), Margin = new Thickness(0, 6, 0, 0) });
        }

        page.Children.Add(Group("内容库"));
        page.Children.Add(new TextBlock
        {
            Text = "扩展带来的台词按种类合在一起：待机嘀咕（mutter）、小剧场（skit）、首次出场（welcome）、事件反应（event）。这一版先把它们收进来登记、能看有多少条；什么时候挑哪一句来说，留给下一版接。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 2, 0, 6),
        });
        var libraries = Extensions.All.Where(pack => pack.Enabled && pack.Lines is { Count: > 0 }).SelectMany(pack => pack.Lines!.Select(lines => (pack, lines))).ToArray();
        if (libraries.Length == 0)
        {
            page.Children.Add(new TextBlock { Text = "还没有扩展带台词。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
        }
        foreach (var (pack, lines) in libraries)
        {
            var box = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            box.Children.Add(new TextBlock
            {
                Text = lines.Label + "  ·  " + lines.Count + " 条  ·  来自 " + pack.Title,
                FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
            });
            if (lines.Count > 0)
            {
                box.Children.Add(new TextBlock
                {
                    Text = string.Join(Environment.NewLine, lines.Items.Take(3).Select(text => "· " + text)) + (lines.Count > 3 ? Environment.NewLine + "…… 还有 " + (lines.Count - 3) + " 条" : ""),
                    TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(10, 2, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)),
                });
            }
            page.Children.Add(box);
        }
        return page;
    }

    private FrameworkElement PackRow(ExtensionPack pack)
    {
        var box = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 222, 212)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(255, 253, 249)),
        };
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var on = new CheckBox { IsChecked = pack.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        on.Checked += (_, _) => SetPackEnabled(pack.Id, true);
        on.Unchecked += (_, _) => SetPackEnabled(pack.Id, false);
        head.Children.Add(on);
        head.Children.Add(new TextBlock
        {
            Text = pack.Title, FontWeight = FontWeights.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
        });
        if (!string.IsNullOrWhiteSpace(pack.Version))
            head.Children.Add(new TextBlock { Text = "  v" + pack.Version, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 168)) });
        if (!string.IsNullOrWhiteSpace(pack.Author))
            head.Children.Add(new TextBlock { Text = "  ·  " + pack.Author, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 168)) });
        stack.Children.Add(head);

        var counts = new List<string>();
        if (pack.WordCount > 0) counts.Add(pack.WordCount + " 个动作词");
        if (pack.ComboCount > 0) counts.Add(pack.ComboCount + " 个组合词");
        if (pack.MoodCount > 0) counts.Add(pack.MoodCount + " 个表情");
        if (pack.LineCount > 0) counts.Add(pack.LineCount + " 条台词");
        stack.Children.Add(new TextBlock
        {
            Text = counts.Count == 0 ? "这个包是空的（pack.json 里没有 words / moods / lines）。" : "带了 " + string.Join("、", counts),
            FontSize = 12, Margin = new Thickness(24, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 116, 138)),
        });
        if (!string.IsNullOrWhiteSpace(pack.Describe))
            stack.Children.Add(new TextBlock { Text = pack.Describe, FontSize = 11, Margin = new Thickness(24, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 168)) });
        if (!string.IsNullOrWhiteSpace(pack.Source))
            stack.Children.Add(new TextBlock { Text = pack.Source, FontSize = 11, Margin = new Thickness(24, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(120, 140, 190)) });
        if (pack.Error is { } error)
            stack.Children.Add(new TextBlock { Text = "⚠ " + error, FontSize = 11, Margin = new Thickness(24, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(180, 90, 40)) });
        var open = new Button { Content = "打开这个包", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(24, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => OpenFolder(pack.Folder);
        stack.Children.Add(open);
        box.Child = stack;
        return box;
    }

    private void SetPackEnabled(string id, bool on)
    {
        Extensions.SetEnabled(id, on);
        ReloadExtensions();
    }

    private void ReloadExtensions()
    {
        Extensions.Reload();
        Body.UseExtraWords(Extensions.Words, Extensions.Expansions);
        Behavior.UseExtraMoods(Extensions.Moods);
        RefreshParts();
    }

    private void OpenPacksFolder()
    {
        try { Directory.CreateDirectory(Extensions.Folder); } catch { }
        OpenFolder(Extensions.Folder);
    }

    private static void OpenFolder(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception error) { Places.Log("打不开文件夹：" + error.Message); }
    }

    // ---- local patch (说话页): 字数上限与台词历史 ----

    private UIElement BuildTalkPage()
    {
        var page = new StackPanel { Margin = new Thickness(18) };
        page.Children.Add(Group("说话"));
        page.Children.Add(new TextBlock
        {
            Text = "一次说话最多说多少字。超了不会硬说，退回来让模型压短重说，压出来的必须是完整句子，不会截半句；〔〕里的神情动作不计入。填 0 就是不限。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 2, 0, 4),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        row.Children.Add(new TextBlock { Text = "字数上限", Width = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(60, 66, 88)) });
        sayLimit.Text = settings.SayLimit.ToString();
        row.Children.Add(sayLimit);
        row.Children.Add(new TextBlock { Text = "字", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var applyLimit = new Button { Content = "应用", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(14, 2, 14, 2) };
        applyLimit.Click += (_, _) => ApplySayLimit();
        row.Children.Add(applyLimit);
        page.Children.Add(row);
        sayLimit.LostFocus += (_, _) => ApplySayLimit();
        page.Children.Add(sayLimitHint);
        SetSayLimitHint();

        page.Children.Add(Group("说过的"));
        page.Children.Add(new TextBlock
        {
            Text = "她每次在桌面上说的话都记在这里，最新的在最上面；最近两百句留着。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 2, 0, 4),
        });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        var refresh = new Button { Content = "刷新", Padding = new Thickness(14, 2, 14, 2) };
        refresh.Click += (_, _) => ReloadTalkHistory();
        bar.Children.Add(refresh);
        page.Children.Add(bar);
        page.Children.Add(talkHistory);
        ReloadTalkHistory();
        return page;
    }

    private void SetSayLimitHint() =>
        sayLimitHint.Text = settings.SayLimit == 0 ? "现在不限长度。" : "现在一次最多 " + settings.SayLimit + " 字；超了会被退回，不会显示半句话。";

    private void ApplySayLimit()
    {
        if (int.TryParse(sayLimit.Text.Trim(), out var value)) settings.SayLimit = Math.Clamp(value, 0, 2000);
        settings.SayLimit = settings.SayLimit <= 0 ? 0 : Math.Max(20, settings.SayLimit);
        sayLimit.Text = settings.SayLimit.ToString();
        SetSayLimitHint();
        settings.Checked().Save();
        apply();
    }

    private void ReloadTalkHistory()
    {
        talkHistory.Children.Clear();
        var entries = Talk.Recent(80);
        if (entries.Count == 0)
        {
            talkHistory.Children.Add(new TextBlock { Text = "还没有说过话。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
            return;
        }
        foreach (var entry in entries)
        {
            var box = new StackPanel { Margin = new Thickness(0, 5, 0, 7) };
            box.Children.Add(new TextBlock { Text = entry.At + "  ·  " + entry.Bubbles + " 个气泡", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
            box.Children.Add(new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 76)) });
            talkHistory.Children.Add(box);
        }
    }

    // ---- local patch (模型页): 模型加载与按模型的适配 ----

    private UIElement BuildAdaptPage()
    {
        var page = new StackPanel { Margin = new Thickness(18) };
        page.Children.Add(Group("模型"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        row.Children.Add(modelPick);
        var load = new Button { Content = "切换并重载", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 2, 12, 2) };
        load.Click += (_, _) => LoadPicked();
        row.Children.Add(load);
        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 2, 12, 2) };
        browse.Click += (_, _) => Browse();
        row.Children.Add(browse);
        var rescan = new Button { Content = "重新扫描", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 2, 12, 2) };
        rescan.Click += (_, _) => { adapt.Invalidate(); RefreshModels(); RefreshParts(); };
        row.Children.Add(rescan);
        page.Children.Add(row);
        modelNote.Text = "模型放在插件的 model 文件夹里（里面有 .model3.json 的才算）。换模型会重新开渲染器，位置、大小、穿戴和设置都不变；上面的适配各模型各一份。";
        page.Children.Add(modelNote);

        // local patch (模型适配): 换模型后先看有没有这份适配档；没有就让 agent 来配
        page.Children.Add(Group("适配状态"));
        page.Children.Add(adaptStatus);
        askAi.Click += (_, _) => AskAi();
        resetAdapt.Click += (_, _) => { adapt.ResetCurrent(); askedAt = null; RefreshParts(); };
        var adaptButtons = new StackPanel { Orientation = Orientation.Horizontal };
        adaptButtons.Children.Add(askAi);
        adaptButtons.Children.Add(resetAdapt);
        page.Children.Add(adaptButtons);

        // local patch (模型适配): 适配的是「动作与差分」页里六个点击部位，不在这里重复列
        page.Children.Add(Group("这里适配什么"));
        page.Children.Add(new TextBlock
        {
            Text = "适配的是「动作与差分」页里那六个点击触发：头、脸、手、裙、尾巴、身上。摸头、戳脸这些是照着大肥鱼的表情和手势写的，换成别的模型后要在那边给每个部位挑这个模型自己的差分或动作——点「让 AI 调」让 AI 来配，也可以自己去那页手动改；一个都没配的话，点她身上不会有反应。大肥鱼这套原生模型不用配。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 6),
        });

        page.Children.Add(Group("适配档"));
        page.Children.Add(new TextBlock
        {
            Text = "每个模型一份，存在插件文件夹的 adapt 文件夹里（<模型名>-<短码>.json），改完自动保存；换回某个模型时自动套用它自己那份。想从头来就点「恢复这个模型的默认适配」。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 6),
        });
        page.Children.Add(RestoreButton("恢复默认样子", restore));
        return page;
    }

    private UIElement BuildPartsPage()
    {
        var page = new StackPanel { Margin = new Thickness(18) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(RestoreButton("恢复默认样子", restore));
        top.Children.Add(new TextBlock
        {
            Text = "  试看完、或者她停在某个动作上时点这里",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)),
        });
        page.Children.Add(top);

        page.Children.Add(Group("点她不同位置时演什么"));
        page.Children.Add(new TextBlock
        {
            Text = "原来的样子：头 = 摸头，脸 = 戳脸，手 = 开心，裙 = 护裙，尾巴 = 震惊，身上 = 心虚（只有没配的部位才玩「连点五下挨钢盆」）。下拉改完即时保存、不用点保存；换成差分只放 3.5 秒，换成功作播一遍，选「不动」就只是被她注意到。配了组合的部位下拉会锁住，点「停用」才改回单个触发。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 2),
        });
        foreach (var (zone, label) in Zones) page.Children.Add(TriggerRow(zone, label));

        page.Children.Add(Group("空闲触发的差分与动作"));
        page.Children.Add(new TextBlock
        {
            Text = "打勾 = 她闲着的时候可以自己演；权重越大越常被挑中，0 等于不触发。内置小动作是照身体说明里的那些演的，换模型后先在「模型与适配」里指过去；模型的差分放 3.5 秒，动作播一遍再回默认。深夜打哈欠、打瞌睡会自动更频繁。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)), Margin = new Thickness(0, 4, 0, 2),
        });
        page.Children.Add(parts);
        return page;
    }

    private void RefreshModels()
    {
        var choices = ModelChoices();
        modelPick.ItemsSource = choices;
        var now = currentModel();
        modelPick.SelectedItem = choices.FirstOrDefault(item => string.Equals(item.Path, now, StringComparison.OrdinalIgnoreCase));
        if (modelPick.SelectedItem == null && choices.Count > 0) modelPick.SelectedIndex = 0;
        modelNote.Text = now == null
            ? "现在没有找到模型：把模型文件夹放进插件的 model 文件夹里，再点「重新扫描」。"
            : "现在用的是：" + now;
        RefreshAdaptStatus();
    }

    private List<ModelChoice> ModelChoices(string? extra = null)
    {
        var paths = Places.Models().ToList();
        if (extra != null && !paths.Contains(extra)) paths.Add(extra);
        var folder = Path.Combine(Places.Root, "model");
        return paths.Select(path => new ModelChoice(path,
            path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path[(folder.Length + 1)..] : path)).ToList();
    }

    private void LoadPicked()
    {
        if (modelPick.SelectedItem is not ModelChoice choice) return;
        if (string.Equals(choice.Path, currentModel(), StringComparison.OrdinalIgnoreCase))
        {
            modelNote.Text = "已经在用这个模型了：" + choice.Path;
            return;
        }
        switchModel(choice.Path);
        modelNote.Text = "正在换成：" + choice.Path + "（渲染器要重开，稍等一下）";
        RefreshParts();
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Title = "选一个 Live2D 模型", Filter = "Live2D 模型 (*.model3.json)|*.model3.json|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var choices = ModelChoices(dialog.FileName);
        modelPick.ItemsSource = choices;
        modelPick.SelectedItem = choices.FirstOrDefault(item => item.Path == dialog.FileName);
        switchModel(dialog.FileName);
        modelNote.Text = "正在换成：" + dialog.FileName + "（渲染器要重开，稍等一下）";
        RefreshParts();
    }

    // local patch (模型适配): AI 配完写的文件比窗口里的新，就把列表和状态重读一遍。
    private DateTime AdaptStamp()
    {
        string? model = currentModel();
        return model != null && AdaptFile.Exists(model) ? File.GetLastWriteTimeUtc(AdaptFile.Location(model)) : DateTime.MinValue;
    }

    private void WatchAdapt()
    {
        var at = AdaptStamp();
        if (at == adaptSeen) return;
        adaptSeen = at;
        adapt.Invalidate();
        RefreshParts();
    }

    private void AskAi()
    {
        if (currentModel() == null) return;
        requestAdapt();
        askedAt = DateTime.UtcNow;
        RefreshAdaptStatus();
    }

    // local patch (模型适配): 这份档配了几个部位、全不全，写清楚并决定两个按钮露不露。
    private void RefreshAdaptStatus()
    {
        string? model = currentModel();
        bool exists = AdaptFile.Exists(model);
        var profile = Profile;
        resetAdapt.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        if (model == null)
        {
            askAi.Visibility = Visibility.Collapsed;
            adaptStatus.Text = "还没有模型。";
            adaptStatus.Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148));
            return;
        }
        // 大肥鱼这一套模型，六个部位原来那套本来就能用，不用适配；换别的模型才要。
        bool native = Native(model);
        askAi.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
        string asked = askedAt == null ? "" : "（已经通知 AI 了，等它来调）";
        if (native)
        {
            string combos = profile.Combos.Count > 0 ? "（你自己给 " + profile.Combos.Count + " 个部位配了组合）" : "";
            adaptStatus.Text = "✓ 原生模型，点她六个位置原来那套直接能用，不用适配。" + combos +
                (exists ? Environment.NewLine + AdaptFile.Location(model) : "");
            adaptStatus.Foreground = new SolidColorBrush(Color.FromRgb(60, 120, 80));
            return;
        }
        if (!exists)
        {
            adaptStatus.Text = "⚠ 这个模型的点击触发还没适配（0/6 个部位）——点她身上不会有反应。点「让 AI 调」让 AI 来配，或者自己去「动作与差分」页手动指。" + asked;
            adaptStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 90, 40));
            return;
        }
        if (!profile.Adapted)
        {
            adaptStatus.Text = "⚠ 点击触发只配了 " + profile.Covered + "/6 个部位，没配的点下去不会有反应。点「让 AI 调」补全，或者自己去「动作与差分」页手动指。" + asked;
            adaptStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 90, 40));
            return;
        }
        string who = profile.AdaptedBy == "ai" ? "AI 配的" : "手动配的";
        adaptStatus.Text = "✓ 点击触发已适配（" + who + (profile.AdaptedAt is { } at ? " · " + at.ToString("MM-dd HH:mm") : "") + "）" +
            Environment.NewLine + AdaptFile.Location(model);
        adaptStatus.Foreground = new SolidColorBrush(Color.FromRgb(60, 120, 80));
    }

    // local patch (模型适配): 是不是大肥鱼那套（有它那批表情），是就不用适配。
    private static bool Native(string? model) => model != null && ModelParts.Expressions(model).Contains("黑框眼镜");

    // ---- local patch (模型页): 差分与动作的空闲权重 ----

    private void RefreshParts()
    {
        parts.Children.Clear();
        RefreshTriggers();
        string? model = currentModel();
        if (model == null)
        {
            parts.Children.Add(new TextBlock { Text = "还没有模型，所以没有差分和动作可列。", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
            RefreshAdaptStatus();
            return;
        }
        // local patch (模型适配): 内置小动作只有大肥鱼那套模型才有；别的模型只列它自己的差分与动作。
        if (Native(model)) AddPartGroup("内置小动作", Behavior.IdleNames.Select(name => (Kind: "mood", Name: name)));
        // local patch (扩展包): 扩展带来的表情单独一组，跟内置分开看
        var extended = Behavior.Moods.Keys.Where(name => Extensions.MoodSource.ContainsKey(name)).Order().ToArray();
        if (Native(model) && extended.Length > 0) AddPartGroup("扩展表情", extended.Select(name => (Kind: "mood", Name: name)));
        AddPartGroup("差分", ModelParts.Expressions(model).Select(name => (Kind: "exp", Name: name)));
        AddPartGroup("动作", ModelParts.Motions(model).Select(name => (Kind: "motion", Name: name)));
        foreach (var (zone, _) in Zones) UpdateCombo(zone);
        RefreshDiffs();
        RefreshAdaptStatus();
    }

    // local patch (外观差分): 列表跟着当前模型走，勾上的就是常驻的。
    private void RefreshDiffs()
    {
        diffs.Children.Clear();
        string? model = currentModel();
        if (model == null)
        {
            diffs.Children.Add(new TextBlock { Text = "还没有模型。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)) });
            return;
        }
        var profile = Profile;
        var wrap = new WrapPanel();
        foreach (var name in ModelParts.Expressions(model))
        {
            var check = new CheckBox
            {
                Content = name, Width = 170, Margin = new Thickness(0, 3, 8, 3),
                IsChecked = profile.Worn.Contains(name),
            };
            check.Checked += (_, _) => SetWorn(name, true);
            check.Unchecked += (_, _) => SetWorn(name, false);
            wrap.Children.Add(check);
        }
        diffs.Children.Add(wrap);
    }

    private void SetWorn(string name, bool on)
    {
        var profile = Profile;
        if (on) { if (!profile.Worn.Contains(name)) profile.Worn.Add(name); }
        else profile.Worn.Remove(name);
        adapt.Save();
        wearDiff(name, on);
        RefreshAdaptStatus();
    }

    // local patch (组合动作): 下拉是单个触发，旁边的「组合…」能编一组（并行/连续）。
    private UIElement TriggerRow(string zone, string label)
    {
        var box = TriggerBox(zone);
        var edit = new Button { Content = "组合…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (_, _) => EditCombo(zone, label);
        var state = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)),
        };
        comboStates[zone] = state;
        // local patch (组合动作): 组合启用后下拉不生效，想改回单个触发就点「停用」。
        var clear = new Button
        {
            Content = "停用", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        clear.Click += (_, _) => { Profile.Combos.Remove(zone); adapt.Save(); UpdateCombo(zone); RefreshAdaptStatus(); };
        comboClears[zone] = clear;
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(box);
        line.Children.Add(edit);
        line.Children.Add(clear);
        line.Children.Add(state);
        return Row(label, line);
    }

    private void EditCombo(string zone, string label)
    {
        var profile = Profile;
        var existing = profile.Combos.TryGetValue(zone, out var found) ? found : null;
        var dialog = new ComboWindow(label, existing, currentModel()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Result is { } result) profile.Combos[zone] = result;
        else profile.Combos.Remove(zone); // 空组合 = 不用组合
        adapt.Save();
        RefreshAdaptStatus();
        FlashSaved(zone);
    }

    // local patch (组合动作): 保存完给个明确回执，1.6 秒后换成启用状态。
    private void FlashSaved(string zone)
    {
        if (comboStates.TryGetValue(zone, out var state))
        {
            state.Text = "已保存 ✓";
            state.Foreground = new SolidColorBrush(Color.FromRgb(60, 120, 80));
        }
        flashZone = zone;
        flash.Stop();
        flash.Start();
    }

    private void UpdateCombo(string zone)
    {
        if (!comboStates.TryGetValue(zone, out var state)) return;
        var profile = Profile;
        bool has = profile.Combos.TryGetValue(zone, out var combo) && combo.Parts.Count > 0;
        var found = triggerBoxes.FirstOrDefault(item => item.Zone == zone);
        if (found.Box != null)
        {
            found.Box.IsEnabled = !has;
            found.Box.ToolTip = has ? "这个部位在用组合，下拉要等「停用」之后才生效" : null;
        }
        if (comboClears.TryGetValue(zone, out var clear)) clear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
        {
            string mode = combo!.Mode == "seq" ? "连续" : "并行";
            state.Text = "✓ 组合已启用（" + mode + " · " + combo.Parts.Count + " 项）";
            state.Foreground = new SolidColorBrush(Color.FromRgb(60, 120, 80));
            state.ToolTip = string.Join(" → ", combo.Parts.Select(part => part.Kind + ":" + part.Name));
        }
        else
        {
            state.Text = "";
            state.ToolTip = null;
        }
    }

    private ComboBox TriggerBox(string zone)
    {
        var box = new ComboBox
        {
            Width = 320, DisplayMemberPath = "Label", SelectedValuePath = "Value", ItemsSource = triggers,
            SelectedValue = Profile.Clicks.TryGetValue(zone, out var saved) ? saved : "",
        };
        box.SelectionChanged += (_, _) => { if (ready) SaveClick(zone, box.SelectedValue as string ?? ""); };
        triggerBoxes.Add((box, zone));
        return box;
    }

    // local patch (模型页): 下拉里的条目跟着模型走——内置反应 + 这个模型的差分和动作。
    private void RefreshTriggers()
    {
        // local patch (模型页): 同上，重建下拉的选中变化不是用户改的。
        bool was = ready;
        ready = false;
        try
        {
        triggers.Clear();
        triggers.Add(new("", "（原来的样子）"));
        triggers.Add(new("none", "不动"));
        string? model = currentModel();
        if (model != null)
        {
            // local patch (模型适配): 内置那套反应只有大肥鱼自己有；别的模型只列它自己的差分和动作。
            // local patch (扩展包): 扩展带来的表情注明是哪个包给的
            if (Native(model)) foreach (var name in Behavior.Moods.Keys.Order())
                triggers.Add(new("mood:" + name, Extensions.MoodSource.TryGetValue(name, out var from) ? "扩展 · " + Extensions.PackName(from) + "：" + name : "内置：" + name));
            foreach (var name in ModelParts.Expressions(model)) triggers.Add(new("exp:" + name, "差分：" + name));
            foreach (var name in ModelParts.Motions(model)) triggers.Add(new("motion:" + name, "动作：" + name));
        }
        var profile = Profile;
        foreach (var (box, zone) in triggerBoxes)
            box.SelectedValue = profile.Clicks.TryGetValue(zone, out var saved) ? saved : "";
        }
        finally { ready = was; }
    }

    private void SaveClick(string zone, string value)
    {
        var profile = Profile;
        if (string.IsNullOrWhiteSpace(value)) profile.Clicks.Remove(zone);
        else profile.Clicks[zone] = value;
        adapt.Save();
    }

    private void AddPartGroup(string title, IEnumerable<(string Kind, string Name)> items)
    {
        var list = items.ToArray();
        if (list.Length == 0) return;
        parts.Children.Add(new TextBlock
        {
            Text = title + "（" + list.Length + "）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(80, 96, 152)),
        });
        foreach (var (kind, name) in list) parts.Children.Add(PartRow(kind, name));
    }

    private FrameworkElement PartRow(string kind, string name)
    {
        string key = kind + ":" + name;
        var profile = Profile;
        bool known = profile.Idle.TryGetValue(key, out var saved);
        bool on = known ? saved!.On : kind == "mood";
        int weight = known ? Math.Clamp(saved!.Weight, 0, 10) : 1;

        var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var label = new TextBlock
        {
            Text = name, Width = 300, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name,
            VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
        };
        var kindText = new TextBlock
        {
            Text = kind switch { "mood" => Extensions.MoodSource.ContainsKey(name) ? "扩展" : "内置", "exp" => "差分", _ => "动作" },
            Width = 40, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 146, 168)),
        };
        var slider = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 120, Value = weight, VerticalAlignment = VerticalAlignment.Center };
        var number = new TextBlock { Text = weight.ToString(), Width = 22, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var tryIt = new Button { Content = "试", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void Write() => SaveIdle(key, check.IsChecked == true, (int)slider.Value);
        check.Checked += (_, _) => Write();
        check.Unchecked += (_, _) => Write();
        slider.ValueChanged += (_, _) => { number.Text = ((int)slider.Value).ToString(); Write(); };
        tryIt.Click += (_, _) => preview(kind, name);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(check, 0); grid.Children.Add(check);
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        Grid.SetColumn(kindText, 2); grid.Children.Add(kindText);
        Grid.SetColumn(slider, 3); grid.Children.Add(slider);
        Grid.SetColumn(number, 4); grid.Children.Add(number);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(grid);
        row.Children.Add(tryIt);
        return row;
    }

    private void SaveIdle(string key, bool on, int weight)
    {
        var profile = Profile;
        profile.Idle[key] = new IdleSetting { On = on, Weight = Math.Clamp(weight, 0, 10) };
        adapt.Save();
    }

    private static UIElement Group(string name) =>
        new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(80, 96, 152)) };

    private static UIElement Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(60, 66, 88)) };
        Grid.SetColumn(text, 0); grid.Children.Add(text);
        Grid.SetColumn(control, 1); grid.Children.Add(control);
        return grid;
    }

    private static UIElement With(Slider slider, TextBlock tail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(slider);
        row.Children.Add(tail);
        return row;
    }

    // local patch (设置面板): 不关窗也能一直改，改完就存、就应用。
    private void Persist()
    {
        if (!ready) return;
        settings.Shape = FishPet.Shape.All.First(item => item.Label == (string)shape.SelectedItem!).Id;
        settings.Scale = Math.Round(scale.Value, 2);
        settings.BubbleScale = Math.Round(bubble.Value, 2);
        settings.BubbleSeconds = Settings.StayFor((int)stay.Value);
        settings.Fps = (int)fps.SelectedItem!;
        settings.IdleSeconds = (int)idle.Value;
        settings.FollowMouse = follow.IsChecked == true;
        settings.HideOnFullscreen = hide.IsChecked == true;
        settings.AutoStart = auto.IsChecked == true;
        settings.LockPosition = lockPos.IsChecked == true;
        settings.SessionTarget = string.IsNullOrWhiteSpace(session.Text) ? null : session.Text.Trim();
        settings.Checked().Save();
        AutoStartSet(settings.AutoStart);
        apply();
    }

    private static string StayLabel(int seconds) => seconds switch { -1 => "常留", 0 => "按字数自动", var s => s + " 秒" };

    // 登录自启：HKCU\...\Run 里放一条，值就是本程序的完整路径。
    private static void AutoStartSet(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run == null) return;
            if (on)
            {
                run.SetValue("dshlive2d控制台", "\"" + Environment.ProcessPath + "\"");
                run.DeleteValue("大肥鱼", false); // 旧名换掉，免得开机拉她两次
            }
            else
            {
                run.DeleteValue("dshlive2d控制台", false);
                run.DeleteValue("大肥鱼", false);
            }
        }
        catch (Exception error) { Places.Log("自启设置失败：" + error.Message); }
    }

    // 最近的会话目录名（session-<uuid>），给会话指向一个现成的候选。
    private static string[] RecentSessions()
    {
        try
        {
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
            if (!Directory.Exists(home)) return [];
            return Directory.EnumerateDirectories(home, "session-*", SearchOption.AllDirectories)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(item => item.LastWriteTimeUtc)
                .Take(40)
                .Select(item => item.Name)
                .Distinct()
                .ToArray();
        }
        catch { return []; }
    }
}

// local patch (组合动作): 编辑一个点击部位的组合——并行（一起演）或连续（一个接一个），
// 每条是一个差分/动作/内置反应，附秒数；留空保存就是不用组合。
internal sealed class ComboWindow : Window
{
    private readonly string? model;
    private readonly StackPanel rows = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox parallel = new() { Content = "并行（一起演）", IsChecked = true };
    private readonly CheckBox series = new() { Content = "连续（一个接一个）" };

    internal ClickCombo? Result { get; private set; }

    internal ComboWindow(string label, ClickCombo? existing, string? model)
    {
        this.model = model;
        Title = "组合动作 · " + label;
        Width = 800;
        Height = 460;
        MinWidth = 700;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(252, 250, 246));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 14, 16, 2) };
        head.Children.Add(new TextBlock { Text = "模式：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        head.Children.Add(parallel);
        head.Children.Add(series);
        parallel.Checked += (_, _) => { if (parallel.IsChecked == true) series.IsChecked = false; };
        series.Checked += (_, _) => { if (series.IsChecked == true) parallel.IsChecked = false; };
        var note = new TextBlock
        {
            Text = "「模式」给个默认：并行 = 几条一起开始，连续 = 一个演完再演下一个。每条还能单独定「开始」：跟随模式 / 立即 / 等上一步 / 延迟几秒——几个动作同时请求容易卡，用「等上一步」或「延迟」错开就好。秒数是这一步占多久（差分到点关，动作按文件时长）。一条都不留、直接保存 = 这个部位不用组合。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(16, 2, 16, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)),
        };

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(note);
        body.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280, Margin = new Thickness(16, 0, 16, 0) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 14) };
        var add = new Button { Content = "添加一条", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => AddRow("exp", null, 3.5, "", 0);
        var clear = new Button { Content = "清空", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => rows.Children.Clear();
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };
        var save = new Button { Content = "保存", Padding = new Thickness(18, 3, 18, 3) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(add);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        if (existing is { Parts.Count: > 0 })
        {
            if (existing.Mode == "seq") { series.IsChecked = true; parallel.IsChecked = false; }
            foreach (var part in existing.Parts) AddRow(part.Kind, part.Name, part.Seconds, part.Start, part.Delay);
        }
        else AddRow("exp", null, 3.5, "", 0);
    }

    private void AddRow(string kind, string? name, double seconds, string start, double delay)
    {
        var kindPick = new ComboBox { Width = 96, Margin = new Thickness(0, 0, 6, 0) };
        kindPick.ItemsSource = new[] { "差分", "动作", "内置反应" };
        kindPick.SelectedIndex = kind switch { "motion" => 1, "mood" => 2, _ => 0 };
        var namePick = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 6, 0) };
        var secondsBox = new TextBox { Width = 48, Text = seconds.ToString("0.#"), Margin = new Thickness(0, 0, 6, 0) };
        // local patch (组合动作): 这条什么时候开始
        var startPick = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 6, 0) };
        startPick.ItemsSource = new[] { "跟随模式", "立即", "等上一步", "延迟" };
        startPick.SelectedIndex = start switch { "now" => 1, "after" => 2, "delay" => 3, _ => 0 };
        var delayBox = new TextBox { Width = 44, Text = delay.ToString("0.#"), Margin = new Thickness(0, 0, 6, 0) };
        string? wanted = name;
        void Fill()
        {
            var items = kindPick.SelectedIndex switch
            {
                1 => model == null ? new List<string>() : ModelParts.Motions(model).ToList(),
                2 => Behavior.Moods.Keys.Order().ToList(),
                _ => model == null ? new List<string>() : ModelParts.Expressions(model).ToList(),
            };
            namePick.ItemsSource = items;
            if (wanted != null && items.Contains(wanted)) namePick.SelectedItem = wanted;
            else if (items.Count > 0) namePick.SelectedIndex = 0;
            wanted = null;
        }
        kindPick.SelectionChanged += (_, _) => Fill();
        Fill();
        var remove = new Button { Content = "删", Padding = new Thickness(8, 1, 8, 1) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(kindPick);
        row.Children.Add(namePick);
        row.Children.Add(new TextBlock { Text = "秒 ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        row.Children.Add(secondsBox);
        row.Children.Add(new TextBlock { Text = "开始 ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        row.Children.Add(startPick);
        row.Children.Add(new TextBlock { Text = "延迟 ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        row.Children.Add(delayBox);
        row.Children.Add(remove);
        row.Tag = new object[] { kindPick, namePick, secondsBox, startPick, delayBox };
        remove.Click += (_, _) => rows.Children.Remove(row);
        rows.Children.Add(row);
    }

    private void Save()
    {
        var parts = new List<ClickPart>();
        foreach (var row in rows.Children.OfType<StackPanel>())
        {
            if (row.Tag is not object[] tag) continue;
            var kindPick = (ComboBox)tag[0];
            var namePick = (ComboBox)tag[1];
            var secondsBox = (TextBox)tag[2];
            var startPick = (ComboBox)tag[3];
            var delayBox = (TextBox)tag[4];
            if (namePick.SelectedItem is not string name || string.IsNullOrWhiteSpace(name)) continue;
            string kind = kindPick.SelectedIndex switch { 1 => "motion", 2 => "mood", _ => "exp" };
            string start = startPick.SelectedIndex switch { 1 => "now", 2 => "after", 3 => "delay", _ => "" };
            double.TryParse(secondsBox.Text, out var seconds);
            double.TryParse(delayBox.Text, out var delay);
            parts.Add(new ClickPart
            {
                Kind = kind, Name = name.Trim(), Seconds = Math.Clamp(seconds <= 0 ? 3.5 : seconds, .2, 30),
                Start = start, Delay = Math.Clamp(delay <= 0 ? 0 : delay, 0, 30),
            });
        }
        if (parts.Count == 0) { Result = null; DialogResult = true; return; }
        // local patch (组合动作): 并行/连续只决定开始时机，放几个动作、几个差分由用户自己定。
        Result = new ClickCombo { Mode = series.IsChecked == true ? "seq" : "par", Parts = parts };
        DialogResult = true;
    }
}
