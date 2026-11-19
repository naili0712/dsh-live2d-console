using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishPet;

// One continuous outline joins the rounded body and its small pointer.
internal sealed class Bubble : Border
{
    internal static readonly Color HairBlue = Color.FromRgb(80, 96, 152);
    internal const double PointerHeight = 9;

    internal Bubble()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 251, 244));
        BorderBrush = new SolidColorBrush(HairBlue);
        BorderThickness = new Thickness(1.5);
        CornerRadius = new CornerRadius(24);
        Padding = new Thickness(18, 14, 14, 14 + PointerHeight);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double inset = BorderThickness.Left / 2;
        double width = ActualWidth - inset * 2;
        double height = ActualHeight - PointerHeight - inset * 2;
        if (width <= 0 || height <= 0) return;
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(width, height) / 2);
        var body = new RectangleGeometry(new Rect(inset, inset, width, height), radius, radius);
        double x = ActualWidth / 2, seam = inset + height - 1, tip = ActualHeight - inset;
        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(x - 8, seam), true, true);
            context.LineTo(new Point(x, tip), true, false);
            context.LineTo(new Point(x + 8, seam), true, false);
        }
        drawing.DrawGeometry(Background, new Pen(BorderBrush, BorderThickness.Left) { LineJoin = PenLineJoin.Round },
            new CombinedGeometry(GeometryCombineMode.Union, body, pointer));
    }
}

// Her speech bubbles: one small window each over her head, kept inside the screen, never taking the focus.
// local patch (气泡叠加): 一句一个气泡，说完的不被下一句顶掉，留在头顶往上叠，各自到时间消失。
internal sealed class BubbleWindow : Window
{
    private readonly PetWindow pet; // local patch (多屏): 跟着她所在的那块屏幕
    private readonly Func<double> headTop;
    internal TextBlock Text { get; } = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(50, 56, 84)),
        FontFamily = new FontFamily("Microsoft YaHei UI"), LineHeight = 22,
    };

    internal BubbleWindow(PetWindow pet, Func<double> headTop)
    {
        this.pet = pet; this.headTop = headTop;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Bubble { MaxWidth = 300, Child = Text };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Never activated, not in Alt+Tab.
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000080));
        };
        SizeChanged += (_, _) => { Place(); pet.BubbleResized(this); };
        pet.LocationChanged += (_, _) => Place();
        pet.SizeChanged += (_, _) => Place();
        // A click on the bubble closes it, and only it. local patch (气泡叠加): 点哪个收哪个，不连累别的句子。
        MouseLeftButtonUp += (_, _) => { Hide(); pet.BubbleClicked(this); };
    }

    // In a test the bubble only keeps its words, out of the user's sight.
    internal bool Quiet { get; set; }

    // local patch (气泡叠加): 这一句的气泡该留到什么时候；到点由头顶那一叠收走，不挡下一句开口。
    internal DateTime HideAt { get; set; } = DateTime.MaxValue;

    // local patch (气泡叠加): 她在头顶那一叠里往上让出的高度，最新的贴着头顶，旧的往上排。
    internal double StackOffset { get; set; }

    internal bool Expired => DateTime.UtcNow >= HideAt;

    // How big the bubble is (settings: bubble_scale): the words, the outline and how wide it may grow, together.
    internal double Scale
    {
        set { ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(value, value); Place(); }
    }

    internal void Say(string text)
    {
        Text.Text = text;
        if (!IsVisible && !Quiet) Show();
        Place();
    }

    internal void Place()
    {
        if (!IsVisible) return;
        var area = pet.Screen(); // local patch (多屏): 气泡跟到她所在的那块屏幕
        double left = pet.Left + pet.Width / 2 - ActualWidth / 2;
        // local patch (气泡叠加): 往上让出这一叠里前面几个气泡的位置。
        double top = pet.Top + headTop() - ActualHeight + 6 - StackOffset;
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}

// local patch (气泡叠加): 头顶那一叠气泡。新的贴着头顶，旧的往上排；各自留到自己的时间，最多几个，
// 多了把最旧的收走。点哪个收哪个。她自己的提示语也走这里。
internal sealed class BubbleStack
{
    private const int Most = 4;
    private const double Gap = 8;
    private readonly PetWindow pet;
    private readonly Func<double> headTop;
    private readonly List<BubbleWindow> windows = [];
    private double scale = 1;

    internal BubbleStack(PetWindow pet, Func<double> headTop) { this.pet = pet; this.headTop = headTop; }

    internal bool Quiet { get; set; }

    internal double Scale
    {
        set { scale = value; foreach (var window in windows) window.Scale = value; }
    }

    // 说一句就走，不参与说话队列：她的提示语用。
    internal void Say(string text)
    {
        var window = Open();
        window.Say(text);
        window.HideAt = DateTime.UtcNow + Speech.StaysFor(text);
        Reflow();
    }

    // 给她说的一句话开一个气泡，位置由 Reflow 排。
    internal BubbleWindow Open()
    {
        var window = new BubbleWindow(pet, headTop) { Quiet = Quiet };
        window.Scale = scale;
        windows.Add(window);
        while (windows.Count > Most) Drop(0);
        return window;
    }

    internal void Closed(BubbleWindow window)
    {
        if (windows.Remove(window)) Reflow();
    }

    internal void HideAll()
    {
        foreach (var window in windows) window.Hide();
        windows.Clear();
    }

    // 到时间的气泡收走，剩下的往上让位。
    internal void Tick()
    {
        bool gone = windows.RemoveAll(window => window.Expired) > 0;
        if (gone) Reflow();
    }

    // 从最新（贴着头顶）往上排，越旧越靠上。
    internal void Reflow()
    {
        double offset = 0;
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            var window = windows[i];
            window.StackOffset = offset;
            window.Place();
            offset += window.ActualHeight + Gap;
        }
    }

    private void Drop(int index)
    {
        var window = windows[index];
        windows.RemoveAt(index);
        window.Hide();
    }
}

// What she says, word by word at a speaking pace (Line): the bubble only changes with her first word,
// after whatever she does before speaking, and her mouth moves while words come out.
internal sealed class Speech
{
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly System.Diagnostics.Stopwatch elapsed = new();
    private Line? line;
    // local patch (气泡叠加): 这一句自己的气泡；说完只把气泡留给它自己，不拖住下一句。
    private BubbleWindow? bubble;
    private int shown;
    // How long the bubble stays after the last word (settings: bubble_seconds): by default a little longer for more
    // words; -1 keeps it until her next line or a click.
    internal static TimeSpan StaysFor(string words) => TimeSpan.FromSeconds(Math.Clamp(4 + words.Length * .15, 5, 20));
    internal int Seconds { get; set; }

    internal Speech()
    {
        timer.Tick += (_, _) => Tick();
    }

    internal bool IsSaying => timer.IsEnabled;
    internal bool IsTalking => timer.IsEnabled && line?.Talking == true;
    internal string Words => line?.Plain ?? "";
    internal double Spoken => line?.Spoken ?? 0;
    internal BubbleWindow? Bubble => bubble;

    internal void Say(Line said, BubbleWindow target)
    {
        line = said; bubble = target; shown = 0;
        elapsed.Restart();
        timer.Start();
        Tick();
    }

    // Called by the pet's own clock too, so a bubble left alone goes away.
    internal void Tick()
    {
        if (timer.IsEnabled && line is { } current)
        {
            current.Advance(elapsed.Elapsed.TotalSeconds);
            if (current.Shown != shown && current.Shown > 0)
            {
                shown = current.Shown;
                bubble?.Say(current.Plain[..shown]);
            }
            if (current.Done)
            {
                timer.Stop();
                // local patch (气泡叠加): 说完把整句放上去，这个气泡自己停一会儿；下一句立刻开口。
                if (current.Plain.Length > 0 && bubble is { } done)
                {
                    done.Say(current.Plain);
                    done.HideAt = Seconds switch
                    {
                        -1 => DateTime.MaxValue,
                        0 => DateTime.UtcNow + StaysFor(current.Plain),
                        var seconds => DateTime.UtcNow.AddSeconds(seconds),
                    };
                }
                bubble = null;
            }
        }
    }

    internal void Stop()
    {
        timer.Stop();
        if (bubble is { } window) { window.Hide(); window.HideAt = DateTime.MinValue; }
        bubble = null;
    }
}
