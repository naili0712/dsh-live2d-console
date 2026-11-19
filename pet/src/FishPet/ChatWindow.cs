using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FishPet;

// local patch (桌上对话): 她头顶上的小输入框——打一句，回车交给她；Esc 或点别处就收起。
internal sealed class ChatWindow : Window
{
    private readonly PetWindow pet;
    private readonly Func<double> headTop;
    private readonly TextBox box = new()
    {
        FontSize = 15, FontFamily = new FontFamily("Microsoft YaHei UI"), MaxLength = 500, Width = 260,
        Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)), Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), CaretBrush = new SolidColorBrush(Color.FromRgb(80, 96, 152)),
    };
    internal Action<string>? Send;

    internal ChatWindow(PetWindow pet, Func<double> headTop)
    {
        this.pet = pet; this.headTop = headTop;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true;
        var hint = new TextBlock
        {
            Text = "回车发送 · Esc 关闭", FontSize = 11, Margin = new Thickness(2, 5, 2, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(122, 128, 150)),
        };
        Content = new Bubble { MaxWidth = 320, Child = new StackPanel { Children = { box, hint } } };
        PreviewKeyDown += Typing;
        Deactivated += (_, _) => Hide();
        SizeChanged += (_, _) => Place();
        pet.LocationChanged += (_, _) => Place();
        pet.SizeChanged += (_, _) => Place();
        SourceInitialized += (_, _) =>
        {
            // 能打字，但不进 Alt+Tab。
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x80));
        };
    }

    // 跟气泡一样随设置缩放。
    internal double Scale
    {
        set { ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(value, value); Place(); }
    }

    internal void Display()
    {
        box.Text = "";
        Show(); Activate(); box.Focus();
        Place();
    }

    private void Place()
    {
        if (!IsVisible) return;
        var area = pet.Screen();
        double left = pet.Left + pet.Width / 2 - ActualWidth / 2, top = pet.Top + headTop() - ActualHeight - 6;
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    private void Typing(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = box.Text.Trim();
        Hide();
        box.Text = "";
        if (text.Length > 0) Send?.Invoke(text);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
