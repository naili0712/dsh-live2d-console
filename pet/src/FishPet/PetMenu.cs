using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FishPet;

// local patch (菜单): 自绘右键菜单——原生的 ContextMenu 项一多就被屏幕边裁掉，后面的「设置…」点不到。
internal sealed class PetMenu : Window
{
    internal PetMenu(IEnumerable<(string Label, Action? Run)> items)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var stack = new StackPanel { Margin = new Thickness(6) };
        foreach (var (label, run) in items)
        {
            if (label == "-")
            {
                stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(214, 218, 232)), Margin = new Thickness(6, 4, 6, 4) });
                continue;
            }
            var background = Brushes.Transparent;
            var row = new Border
            {
                Padding = new Thickness(10, 6, 26, 6),
                Background = background,
                Child = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)) },
            };
            var highlight = new SolidColorBrush(Color.FromRgb(232, 238, 252));
            row.MouseEnter += (_, _) => row.Background = highlight;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { Close(); run?.Invoke(); };
            stack.Children.Add(row);
        }
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(252, 250, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 96, 152)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = stack,
        };
        // local patch (菜单): 失活/已经关掉的时候再 Close 会抛 InvalidOperationException，先看还开着没有。
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && IsVisible) Close(); };
    }

    // 从鼠标点向下展开，碰到屏幕边就翻到另一侧。
    internal void ShowAt(Point point, Rect area)
    {
        Show();
        UpdateLayout();
        double left = point.X, top = point.Y;
        if (left + ActualWidth > area.Right - 4) left = point.X - ActualWidth;
        if (top + ActualHeight > area.Bottom - 4) top = area.Bottom - ActualHeight - 4;
        Left = Math.Max(area.Left + 4, left);
        Top = Math.Max(area.Top + 4, top);
        Activate();
    }
}
