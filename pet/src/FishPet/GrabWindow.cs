using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FishPet;

// local patch (扩展包): 「获取扩展」——把视频、截图、整段素材拖进来，交给 agent 认里面的动作，
// 认完写成新的扩展包。这里只管收和投递，认动作是 agent 的事（它看视频、读模型，再写 pack.json）。
internal sealed class GrabWindow : Window
{
    private static readonly string[] Media = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".webp", ".psd", ".json"];
    private readonly Action<string, string[]> handOver;
    private readonly List<string> chosen = [];
    private readonly StackPanel listing = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly Button send;

    internal GrabWindow(Action<string, string[]> handOver)
    {
        this.handOver = handOver;
        Title = "获取扩展 · 把素材交给 AI 认动作";
        Width = 580; Height = 480;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 460; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(252, 250, 246));
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var page = new StackPanel { Margin = new Thickness(18) };
        page.Children.Add(new TextBlock
        {
            Text = "把素材拖进来——视频、录屏、截图都行。",
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
        });
        page.Children.Add(new TextBlock
        {
            Text = "AI 会看这些素材，把里面的动作、表情认出来，写成扩展包放进 extensions 里；认得出来的她当场就能用，这个模型做不了的会标成「需要别的模型」。这步要花点时间（它得逐段看视频）。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(120, 126, 148)),
        });

        var drop = new Border
        {
            Height = 150, Background = new SolidColorBrush(Color.FromRgb(255, 253, 249)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 206, 226)), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), AllowDrop = true,
        };
        var inside = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        inside.Children.Add(new TextBlock
        {
            Text = "把文件拖到这里", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 116, 138)),
        });
        inside.Children.Add(new TextBlock
        {
            Text = "视频 / 图片 / 文件夹", FontSize = 11, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 156, 178)),
        });
        var browse = new Button { Content = "或者点这里选文件", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        browse.Click += (_, _) => Pick();
        inside.Children.Add(browse);
        drop.Child = inside;
        drop.DragOver += (_, e) =>
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            drop.BorderBrush = new SolidColorBrush(ok ? Color.FromRgb(120, 150, 220) : Color.FromRgb(200, 206, 226));
        };
        drop.DragLeave += (_, _) => drop.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 206, 226));
        drop.Drop += (_, e) =>
        {
            drop.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 206, 226));
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Add(paths);
        };
        page.Children.Add(drop);
        page.Children.Add(listing);
        page.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "先不弄了", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        send = new Button { Content = "交给 AI 分析", Padding = new Thickness(18, 4, 18, 4), IsEnabled = false };
        send.Click += (_, _) => Hand();
        buttons.Children.Add(send);
        page.Children.Add(buttons);

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Pick()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选素材", Multiselect = true,
            Filter = "素材 (*.mp4;*.mov;*.mkv;*.webm;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.mp4;*.mov;*.mkv;*.webm;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) Add(dialog.FileNames);
    }

    private void Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        if (Wanted(file) && !chosen.Contains(file)) { chosen.Add(file); added++; }
                }
                else if (File.Exists(path) && Wanted(path) && !chosen.Contains(path)) { chosen.Add(path); added++; }
            }
            catch { }
        }
        Redraw(added);
    }

    private static bool Wanted(string file) => Media.Contains(Path.GetExtension(file).ToLowerInvariant());

    private void Redraw(int added)
    {
        listing.Children.Clear();
        foreach (var path in chosen.Take(20))
        {
            listing.Children.Add(new TextBlock
            {
                Text = "· " + Path.GetFileName(path) + "  （" + Size(new FileInfo(path).Length) + "）",
                FontSize = 11, Margin = new Thickness(2, 1, 0, 1),
                Foreground = new SolidColorBrush(Color.FromRgb(90, 96, 118)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (chosen.Count > 20)
            listing.Children.Add(new TextBlock { Text = "…… 还有 " + (chosen.Count - 20) + " 个", FontSize = 11, Foreground = Brushes.Gray });
        status.Text = chosen.Count == 0
            ? "还没选素材。"
            : "选了 " + chosen.Count + " 个" + (added > 0 ? "（刚加了 " + added + " 个）" : "") + "。点「交给 AI 分析」，它会复制一份到插件的 workspace\\pack-inbox 里再开始认。";
        send.IsEnabled = chosen.Count > 0;
    }

    private static string Size(long bytes) => bytes switch
    {
        > 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("0.0") + " GB",
        > 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.0") + " MB",
        _ => Math.Max(1, bytes / 1024) + " KB",
    };

    private async void Hand()
    {
        if (chosen.Count == 0) return;
        send.IsEnabled = false;
        status.Text = "正在复制素材……（大视频要一会儿）";
        string inbox = Path.Combine(Places.Root, "workspace", "pack-inbox", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var copies = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(inbox);
                foreach (var path in chosen)
                {
                    var target = Path.Combine(inbox, Path.GetFileName(path));
                    for (int n = 2; File.Exists(target); n++) target = Path.Combine(inbox, Path.GetFileNameWithoutExtension(path) + " (" + n + ")" + Path.GetExtension(path));
                    File.Copy(path, target, true);
                    copies.Add(target);
                }
            });
        }
        catch (Exception error)
        {
            status.Text = "复制失败：" + error.Message;
            send.IsEnabled = true;
            return;
        }
        handOver(inbox, copies.ToArray());
        status.Text = "交出去了。AI 认完会在「扩展包」页里出现新的包，你到时候点「重新扫描」就能看到。";
        await Task.Delay(1200);
        Close();
    }
}
