using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FishPet;

// The pet: a transparent topmost window with her in it, her speech bubble above her head, her reflexes when
// clicked, dragged or left alone (Behavior), her own body language from what the agent has her say (Body,
// Line), and her mirror. Everything the agent does goes through Execute (Tools.cs for what each tool means).
internal sealed class PetWindow : Window
{
    private readonly Settings settings;
    private string? model; // local patch (模型页): 设置里选过的模型可以当场换
    private readonly bool test;
    private readonly SpriteImage image = new() { Stretch = Stretch.Fill };
    private readonly BubbleWindow bubble;
    private readonly Speech speech;
    private readonly Behavior behavior;
    private readonly Body body;
    private readonly Updates updates;
    private readonly DispatcherTimer timer;
    // Out of the way while another program fills the screen (settings: hide_on_fullscreen), looked at twice a second.
    private bool hiddenForFullscreen;
    private DateTime nextFullscreenLook;
    private Player? player;
    private ControlServer? server;
    private HttpApi? api;
    private string? trouble;
    private bool exiting, exitReady;
    // local patch (托盘): 收起来不等于退出——进程和托盘留着，再启动一次或双击图标就回来。
    private PetTray? tray;
    private bool trayHidden, leavingForGood;
    private SettingsWindow? settingsWindow;
    // local patch (模型适配): 每个模型一份适配档（model-adapt.json）；内置那套反应只对匹配的模型默认适用。
    private readonly AdaptFile adapt = new();
    private bool? fitsBuiltins;
    private long idleReturn;
    // local patch (模型适配): 用户点过「让 AI 调」的时间，和已经提醒过 agent 的时间。
    private DateTime? adaptAskedAt, adaptToldAt;
    // local patch (组合动作): 正在演的组合，演完之前空闲不插进来；组合里的差分要算「穿着」，
    // 不然组合里放个内置反应，它结束时会把同一张差分顺手关掉（抢触发）。
    private long comboToken;
    private bool comboBusy;
    private readonly HashSet<string> comboExpressions = [];
    // 手写或 agent 写 JSON 常用小写键，读组合的时候不挑大小写。
    private static readonly JsonSerializerOptions RelaxedJson = new() { PropertyNameCaseInsensitive = true };
    private string appliedShape = ""; // local patch (设置面板): 现在画的是哪个体型
    private long shapeChange;
    private Shape Shape => Shape.Find(settings.Shape);

    // Dragging her about.
    private bool dragging, dragged;
    private bool lockedNow; // local patch (锁定位置): 这次按下时她是不是被锁着
    private Point dragStart, windowStart;

    // What the window does to her: moving pushes her body, the taskbar hides what reaches it.
    private double lastLeft, lastTop, lookX = 9, lookY = 9;
    private Rect open;

    // Her lines: one at a time, in order; a line of nothing but 〔〕 plays with no bubble.
    private Task lines = Task.CompletedTask;
    private Line? silent;
    private readonly Stopwatch silentClock = new();
    private DateTime? relaxAt;
    private const double LingerSeconds = 3.5;

    // Her recorded voice, and her mouth.
    private readonly MediaPlayer voicePlayer = new();
    private float[]? voice;
    private DateTime voiceUntil;
    private double mouth;
    private const int VoiceStepMilliseconds = 33;

    // Every line she says is filmed, for looking back at afterwards; kept half an hour.
    private sealed record Photo(DateTime At, string Said, string Picture);
    private readonly List<Photo> photos = [];
    private List<BitmapSource>? film;
    private string filmSaid = "";
    private DateTime filmShot;
    private DateTime? filmEnded;
    private const int PhotosKept = 20, FilmFrames = 120;
    private static readonly TimeSpan PhotoLife = TimeSpan.FromMinutes(30), FilmEvery = TimeSpan.FromMilliseconds(150), FilmAfter = TimeSpan.FromMilliseconds(600);
    private const int MirrorHeight = 520, MirrorGridHeight = 360, MirrorShots = 6, MirrorColumns = 3;
    private const double MirrorSpan = .5;

    // What the user did to her, for the agent (recent_events).
    private sealed record Happening(long Sequence, DateTime At, string Kind, object Detail);
    private readonly List<Happening> happenings = [];
    private long sequence;

    internal PetWindow(Settings settings, string? model, bool test, string? pipe = null)
    {
        this.settings = settings; this.model = model; this.test = test;
        // local patch (模型适配): 上一版把所有模型塞一个文件，先拆成每个模型一份。
        AdaptFile.MigrateLegacy();
        // local patch (模型适配): 旧版把空闲与点击写在 settings.json 里，第一次跑到新版就搬进当前模型的档。
        if (model != null && (settings.Idle.Count > 0 || settings.ClickTriggers.Count > 0))
        {
            var profile = adapt.Current(model);
            if (profile.Idle.Count == 0) foreach (var (key, value) in settings.Idle) profile.Idle[key] = value;
            if (profile.Clicks.Count == 0) foreach (var (key, value) in settings.ClickTriggers) profile.Clicks[key] = value;
            settings.Idle.Clear();
            settings.ClickTriggers.Clear();
            adapt.Save();
            settings.Save();
        }
        Title = "dshlive2d控制台";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Opacity = 0;
        Content = new Grid { Children = { image } };
        bubble = new BubbleWindow(this, () => Height * Shape.HeadTop);
        bubble.Quiet = test;
        bubble.Scale = settings.BubbleScale;
        speech = new Speech(bubble) { Seconds = settings.BubbleSeconds };
        body = new Body(Send);
        updates = new Updates(Places.Root, Places.Log);
        behavior = new Behavior(Send, () => [.. settings.Outfit, .. body.Overlays, .. comboExpressions], new Random())
        {
            HandBusy = () => body.HandUp, Ended = body.Restore,
            // local patch (模型页): 空闲池按设置页的启停和权重来，模型自带的差分与动作也在里面
            MoodWeight = IdleMoodWeight, IdleExtras = IdleExtras,
            PlayExtra = (kind, name) => SendPart(kind, name),
            ClickChoice = ClickTriggerFor, // local patch (模型适配)
            PlayCombo = zone => _ = RunCombo(zone), // local patch (组合动作)
        };
        body.TakeHand = behavior.GiveUpHand;
        ApplySize();
        Place();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Never takes the focus from what the user is doing, not in Alt+Tab.
            SetWindowLongPtr(handle, -20, (nint)((long)GetWindowLongPtr(handle, -20) | 0x8000080));
        };
        Loaded += (_, _) =>
        {
            Start();
            server = new ControlServer(Execute, pipe);
            try { api = HttpApi.Start(Places.ApiFile, test ? 0 : HttpApi.DefaultPort, (name, args, token) => { lastHttp = DateTime.UtcNow; return Execute(name, args, token); }); }
            catch (Exception error) { Places.Log("网页接口开不起来：" + error.Message); }
            // A newer plugin on GitHub, looked for now and twice a day; the agent hears of it with its next tool (Execute).
            if (!test) _ = Task.Run(async () =>
            {
                while (true)
                {
                    if (settings.CheckUpdates) await updates.Check();
                    await Task.Delay(TimeSpan.FromHours(12));
                }
            });
        };
        PreviewMouseDown += MiddleDown; // local patch (桌上对话): 中键单开一路，左键那个只收左键
        PreviewMouseLeftButtonDown += Down;
        PreviewMouseMove += Move;
        PreviewMouseLeftButtonUp += Up;
        // local patch (右键菜单/滚轮): 右键出菜单，滚轮改大小；换屏时按新屏重排。
        PreviewMouseRightButtonUp += RightUp;
        PreviewMouseWheel += Wheel;
        DpiChanged += (_, _) => { ApplySize(); UpdateCut(); };
        LostMouseCapture += (_, _) => { if (dragging) { dragging = false; if (dragged) Dropped(); } };
        Closing += Leaving;
        Closed += (_, _) => { tray?.Dispose(); server?.Dispose(); api?.Dispose(); player?.Dispose(); bubble.Close(); };
        // local patch (托盘): 再启动一次 exe（快捷方式/开始菜单）时，把已经收起来的她叫出来。
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "FishPet.Show." + Tools.PipeName);
        new Thread(() => { while (wake.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowFromTray)); }) { IsBackground = true, Name = "FishPet.Show" }.Start();
        tray = new PetTray(ShowFromTray, OpenSettings, QuitForGood);
        tray.Hint("dshlive2d控制台");
    }

    private void Send(string command) => player?.Send(command);

    // local patch (模型页): 空闲池里模型自带的差分与动作，按设置页的启停和权重挑。
    private IReadOnlyList<IdleItem> IdleExtras()
    {
        if (model == null) return [];
        var profile = adapt.Current(model);
        var items = new List<IdleItem>();
        foreach (var name in ModelParts.Expressions(model))
        {
            int weight = PartWeight(profile, "exp:" + name);
            if (weight > 0) items.Add(new("exp", name, weight));
        }
        foreach (var name in ModelParts.Motions(model))
        {
            int weight = PartWeight(profile, "motion:" + name);
            if (weight > 0) items.Add(new("motion", name, weight));
        }
        return items;
    }

    private static int PartWeight(ModelProfile? profile, string key) =>
        profile != null && profile.Idle.TryGetValue(key, out var found) && found.On ? Math.Clamp(found.Weight, 0, 10) : 0;

    private int? IdleMoodWeight(string name)
    {
        // local patch (模型适配): 内置小动作是照着大肥鱼那套写的，换别的模型就不参与空闲。
        if (!FitsBuiltins) return 0;
        var profile = adapt.Current(model);
        if (!profile.Idle.TryGetValue("mood:" + name, out var found)) return null;
        return found.On ? Math.Clamp(found.Weight, 0, 10) : 0;
    }

    // local patch (模型页): 动作交给渲染器放（它自己播完回 IDLE）；万一停住，按动作时长再收一次。
    private void SendPart(string kind, string name)
    {
        if (kind != "motion") return;
        Send($"motion {name}");
        long token = ++idleReturn;
        _ = BackToIdle(token, model == null ? 3 : ModelParts.Duration(model, name));
    }

    private async Task BackToIdle(long token, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds + .3));
        if (token != idleReturn) return;
        Send("gesturestop");
        Send("motion IDLE");
    }

    // local patch (组合动作): 点某个部位演一组动作——并行就一起开始，连续就一个演完再下一个；
    // 差分自己到点关，动作按文件时长回 IDLE。
    private async Task RunCombo(string zone)
    {
        if (!adapt.Current(model).Combos.TryGetValue(zone, out var combo) || combo.Parts.Count == 0) return;
        long token = ++comboToken;
        comboBusy = true;
        comboExpressions.Clear();
        foreach (var part in combo.Parts) if (part.Kind == "exp") comboExpressions.Add(part.Name);
        try
        {
            // local patch (组合动作): 每条按自己的「开始」排时间线——跟随模式（并行一起 / 连续等上一步）、
            // 立即、等上一步、或组合开始后延迟几秒。几个动作同时请求会卡，就用延迟错开。
            double lastEnd = 0, end = 0;
            var plan = new List<(ClickPart Part, double Start, double Length)>();
            foreach (var part in combo.Parts)
            {
                double length = ComboLength(part);
                double start = part.Start switch
                {
                    "now" => 0,
                    "after" => lastEnd + Math.Max(0, part.Delay),
                    "delay" => Math.Max(0, part.Delay),
                    _ => (combo.Mode == "seq" ? lastEnd : 0) + Math.Max(0, part.Delay),
                };
                plan.Add((part, start, length));
                lastEnd = start + length;
                end = Math.Max(end, lastEnd);
            }
            var clock = Stopwatch.StartNew();
            foreach (var (part, start, _) in plan.OrderBy(item => item.Start))
            {
                double wait = start - clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                if (token != comboToken || player == null) return;
                PlayComboPart(part, token);
            }
            double remain = end - clock.Elapsed.TotalSeconds;
            if (remain > 0) await Task.Delay(TimeSpan.FromSeconds(remain));
        }
        finally
        {
            if (token == comboToken) comboBusy = false;
        }
    }

    private double ComboLength(ClickPart part) =>
        part.Kind == "motion" && model != null ? ModelParts.Duration(model, part.Name) : part.Seconds;

    private void PlayComboPart(ClickPart part, long token)
    {
        switch (part.Kind)
        {
            case "motion":
                SendPart("motion", part.Name);
                break;
            case "mood":
                behavior.Preview("mood", part.Name, DateTime.UtcNow);
                break;
            default:
                Send($"exp {part.Name} 1");
                _ = FadeComboExpression(token, part.Name, part.Seconds);
                break;
        }
    }

    private async Task FadeComboExpression(long token, string name, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, .2, 30)));
        if (token != comboToken) return;
        if (settings.Outfit.Contains(name) || body.Overlays.Contains(name)) return;
        Send($"exp {name} 0");
    }

    // local patch (模型适配): 点她哪个位置演什么。适配档里配了就用配的；没配——原生模型还是原来那套，
    // 别的模型就是不动（她那套表情和手势在那边没有，等 AI 适配或自己在这里指）。
    private (string Kind, string Name)? ClickTriggerFor(string zone)
    {
        var profile = adapt.Current(model);
        // local patch (组合动作): 配了组合的部位先走组合。
        if (profile.Combos.TryGetValue(zone, out var combo) && combo.Parts.Count > 0) return ("combo", zone);
        if (profile.Clicks.TryGetValue(zone, out var trigger) && !string.IsNullOrWhiteSpace(trigger) && ParseTrigger(trigger) is { } choice)
        {
            if (choice.Kind == "mood" && !Behavior.Moods.ContainsKey(choice.Name)) return null;
            return choice;
        }
        return FitsBuiltins ? null : ("none", "");
    }

    private bool FitsBuiltins
    {
        get
        {
            fitsBuiltins ??= model != null && ModelParts.Expressions(model).Contains("黑框眼镜");
            return fitsBuiltins.Value;
        }
    }

    private static (string Kind, string Name)? ParseTrigger(string value)
    {
        value = value.Trim();
        if (value == "none") return ("none", "");
        int at = value.IndexOf(':');
        if (at <= 0) return null;
        string kind = value[..at], name = value[(at + 1)..];
        if (name.Length == 0 || kind is not ("mood" or "exp" or "motion")) return null;
        return (kind, name);
    }

    // local patch (外观差分): 外观页勾/取消一个差分，立刻生效；取消时她穿着的内置配饰（黑框眼镜那些）不动。
    internal void WearDiff(string name, bool on)
    {
        if (on) Send($"exp {name} 1");
        else if (!settings.Outfit.Contains(name)) Send($"exp {name} 0");
    }

    // local patch (模型适配): 设置里点了「让 AI 调」——把请求落盘、记成事件，并在 agent 下一次调工具时告诉它。
    internal void RequestAdapt()
    {
        if (model == null) return;
        adaptAskedAt = DateTime.UtcNow;
        adaptToldAt = null;
        try
        {
            var folder = Path.Combine(Places.Root, "cache");
            Directory.CreateDirectory(folder);
            var profile = adapt.Current(model);
            var request = new
            {
                requestedAt = DateTime.Now.ToString("s"),
                model,
                label = Path.GetFileName(model),
                expressions = ModelParts.Expressions(model),
                motions = ModelParts.Motions(model),
                zones = Settings.Zones,
                clicks = profile.Clicks,
                combos = profile.Combos,
                missingZones = Settings.Zones.Where(zone => !profile.Clicks.ContainsKey(zone) && !profile.Combos.ContainsKey(zone)).ToArray(),
                profile = AdaptFile.Location(model),
                howTo = "adapt_model：read 看差分/动作和现在的点击触发，write 用 clicks 给六个部位挑「exp:名字」或「motion:名字」，reset 清掉；工具没更新时直接改这份 JSON。",
            };
            File.WriteAllText(Path.Combine(folder, "adapt-request.json"), JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { Places.Log("适配请求没能落盘：" + error.Message); }
        Happened("adapt_request", new { model });
        bubble.Say("好，我去叫 AI 来配一下这个模型点她身上的反应");
    }

    // local patch (扩展包): 设置里点了某个包的「扩展适配」——包是按发布时的模型写的，换个模型
    // 它用的参数可能这里根本没有。请 agent 读模型本身，写一份本地适配层（adapt.json），原包不动。
    private DateTime? packAskedAt, packToldAt;
    private string? packAskedId;

    internal void RequestPackAdapt(string id)
    {
        var pack = Extensions.All.FirstOrDefault(item => item.Id == id);
        if (pack == null) return;
        if (model == null) { bubble.Say("先选个模型吧，不然不知道要照着谁适配"); return; }
        packAskedAt = DateTime.UtcNow; packToldAt = null; packAskedId = id;
        try
        {
            var folder = Path.Combine(Places.Root, "cache");
            Directory.CreateDirectory(folder);
            var request = new
            {
                requestedAt = DateTime.Now.ToString("s"),
                model,
                label = Path.GetFileName(model),
                cdi = Path.ChangeExtension(model, ".cdi3.json"),
                pack = new { pack.Id, pack.Name, pack.Version, pack.Author, pack.Folder, pack.Words, pack.Moods },
                adaptFile = Extensions.AdaptLocation(pack),
                hasAdapt = pack.Adapt != null,
                adaptedForThisModel = pack.AdaptedFor,
                expressions = ModelParts.Expressions(model),
                motions = ModelParts.Motions(model),
                howTo = "adapt_pack：read 看这个包和模型实际有的差分/动作；write 往 adapt.json 写这个模型做得到的版本——同名覆盖 pack.json，做不了的列进 disabled；reset 清掉。" +
                        "参数名不确定就去读模型自己的 cdi 文件（上面 cdi 那个路径）。",
            };
            File.WriteAllText(Path.Combine(folder, "pack-adapt-request.json"), JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { Places.Log("扩展适配请求没能落盘：" + error.Message); }
        Happened("pack_adapt_request", new { id });
        bubble.Say("好，让 AI 去看看这个包在这个模型上做不做得了，做不了的换成本体能做出来的");
    }

    private string PackAdaptNotice()
    {
        var pack = Extensions.All.FirstOrDefault(item => item.Id == packAskedId);
        return "【用户要你适配扩展包】他刚在设置里点了「扩展适配」，包是「" + (pack?.Title ?? packAskedId) + "」。现在的模型是 " + model + "。" +
            "包里的动作词是按别的模型（或别的参数名）写的，不一定这台机器能做——先用 adapt_pack 的 read 看包内容和模型实际有哪些差分/动作，" +
            "再 write 一份 adapt.json：做得到的原样或改成本模型的参数，做不到的放进 disabled，别硬写。" +
            "请求详情在 " + Path.Combine(Places.Root, "cache", "pack-adapt-request.json") + "；工具列表里还没有 adapt_pack（MCP 没重启）时直接改 " +
            (pack == null ? "adapt.json" : Extensions.AdaptLocation(pack)) + "。做完用一两句话告诉用户你改了什么、哪些做不了。";
    }

    // local patch (扩展包): 用户把视频/素材拖进「获取扩展」窗口——记下投递箱，请 agent 去认里面的动作。
    private DateTime? grabAskedAt, grabToldAt;
    private string? grabInbox;

    internal void RequestPackGrab(string inbox, string[] files)
    {
        if (model == null) { bubble.Say("先选个模型吧，扩展包要照着它有的动作写"); return; }
        grabAskedAt = DateTime.UtcNow; grabToldAt = null; grabInbox = inbox;
        try
        {
            var folder = Path.Combine(Places.Root, "cache");
            Directory.CreateDirectory(folder);
            var request = new
            {
                requestedAt = DateTime.Now.ToString("s"),
                inbox,
                files,
                model,
                label = Path.GetFileName(model),
                cdi = Path.ChangeExtension(model, ".cdi3.json"),
                expressions = ModelParts.Expressions(model),
                motions = ModelParts.Motions(model),
                packs = Extensions.All.Select(pack => pack.Id).ToArray(),
                howTo = "照素材认动作，写成新的扩展包放进 extensions 文件夹（pack.json + 可选 adapt.json）。" +
                        "看视频用 video_storyboard / video_zoom；只提取这台模型做得到的通道与差分，做不到但在素材里确实有的，写进包并在 describe 里注明需要哪类模型。" +
                        "写完让人在设置里点「重新扫描」。",
            };
            File.WriteAllText(Path.Combine(folder, "pack-grab-request.json"), JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { Places.Log("扩展获取请求没能落盘：" + error.Message); }
        Happened("pack_grab_request", new { inbox, files });
        bubble.Say("收到，" + files.Length + " 个素材，我让 AI 去认里面的动作");
    }

    private string GrabNotice() =>
        "【用户要你从素材里做扩展包】他把 " + grabInbox + " 里的素材拖进了「获取扩展」。现在的模型是 " + model + "。" +
        "先用 video_storyboard（视频）或直接看图，认里面的动作/表情，再写成 extensions/<id>/pack.json。" +
        "只写这台模型做得出来的（参数名、差分名在 cdi 和 Exp3 里能找到），做不到的写进包并在 describe 里注明。写完让人在设置里点「重新扫描」，并告诉用户新包叫什么。";

    // local patch (模型适配): 用户点了让 AI 调，下一个工具调用里把这件事告诉 agent。
    private string AdaptNotice() =>
        "【用户要你适配模型】他刚在设置里点了「让 AI 调」。现在的模型是 " + model + "（" + Path.GetFileName(model ?? "") + "）。" +
        "这个模型还没有配好「点她不同位置时演什么」（head 头、face 脸、hand 手、skirt 裙、tail 尾巴、body 身上）。" +
        "先用 adapt_model 的 read 看它有哪些差分和动作，再用 write 的 clicks 给每个部位挑一个（\"exp:名字\"、\"motion:名字\"，或者 \"none\"）；" +
        "工具列表里还没有 adapt_model（新版 MCP 没重启）时，直接改 " + AdaptFile.Location(model ?? "") + " 这份 JSON，clicks 里写 {\"head\":\"exp:名字\"} 这种。" +
        "空闲权重（idle）想调也可以一起写。配完用一两句话告诉用户你怎么配的。";

    // local patch (扩展包): agent 通过这个工具给一个包写「本地适配层」——不动原包，只覆盖、停用、补充。
    private object AdaptPackTool(JsonElement args)
    {
        if (model == null) throw new InvalidOperationException("还没有模型，先在设置里选一个。");
        string action = args.TryGetProperty("action", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : "read";
        string? wanted = Tools.OptionalText(args, "id");
        var pack = wanted != null
            ? Extensions.All.FirstOrDefault(item => string.Equals(item.Id, wanted, StringComparison.OrdinalIgnoreCase))
            : Extensions.All.FirstOrDefault(item => item.Id == packAskedId);
        if (pack == null)
            throw new ArgumentException("没有这个扩展包。现在装着的是：" + string.Join("、", Extensions.All.Select(item => item.Id)));
        string path = Extensions.AdaptLocation(pack);
        if (action == "read")
            return new
            {
                model, label = Path.GetFileName(model),
                cdi = Path.ChangeExtension(model, ".cdi3.json"),
                pack = new { pack.Id, pack.Name, pack.Version, pack.Author, pack.Folder },
                adaptFile = path, adapted = pack.Adapt, adaptedForThisModel = pack.AdaptedFor,
                words = pack.Words, moods = pack.Moods,
                expressions = ModelParts.Expressions(model), motions = ModelParts.Motions(model),
                howTo = "write 时 words 是这个词的完整定义（和 pack.json 里同一种结构）：做得到的原样写回、改成这台模型的参数；" +
                        "做不到的把名字放进 disabled。参数名在模型自己的 cdi 文件里。",
            };
        if (action == "reset")
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception error) { throw new InvalidOperationException("删不掉 adapt.json：" + error.Message); }
            packToldAt = packAskedAt;
            ReloadPacks();
            return new { reset = true, adaptFile = path };
        }
        if (action != "write") throw new ArgumentException("action 只能是 read、write 或 reset。");
        var adapt = new ExtensionAdapt { Model = model, AdaptedAt = DateTime.Now.ToString("s"), AdaptedBy = "ai" };
        var warnings = new List<string>();
        if (args.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            adapt.Words = [];
            foreach (var item in words.EnumerateArray())
            {
                ExtensionWord? word = null;
                try { word = JsonSerializer.Deserialize<ExtensionWord>(item.GetRawText(), RelaxedJson); } catch { }
                if (word == null || string.IsNullOrWhiteSpace(word.Word)) { warnings.Add("有一个词没写名字，跳过了"); continue; }
                adapt.Words.Add(word);
            }
        }
        if (args.TryGetProperty("moods", out var moods) && moods.ValueKind == JsonValueKind.Array)
        {
            adapt.Moods = [];
            foreach (var item in moods.EnumerateArray())
            {
                ExtensionMood? mood = null;
                try { mood = JsonSerializer.Deserialize<ExtensionMood>(item.GetRawText(), RelaxedJson); } catch { }
                if (mood == null || string.IsNullOrWhiteSpace(mood.Name)) { warnings.Add("有一个表情没写名字，跳过了"); continue; }
                adapt.Moods.Add(mood);
            }
        }
        if (args.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.Array)
            adapt.Disabled = disabled.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim()).Where(name => name.Length > 0).ToList();
        if (args.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String) adapt.Notes = notes.GetString();
        try
        {
            Directory.CreateDirectory(pack.Folder);
            File.WriteAllText(path, JsonSerializer.Serialize(adapt, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception error) { throw new InvalidOperationException("写不进 adapt.json：" + error.Message); }
        packToldAt = packAskedAt;
        ReloadPacks();
        return new
        {
            written = true, adaptFile = path,
            words = adapt.Words?.Count ?? 0, moods = adapt.Moods?.Count ?? 0, disabled = adapt.Disabled?.Count ?? 0,
            warnings,
        };
    }

    // local patch (扩展包): 适配层或包改了以后，动作词和表情表要跟着重算，设置页也要刷新。
    internal void ReloadPacks()
    {
        Extensions.Reload(model);
        Body.UseExtraWords(Extensions.Words, Extensions.Expansions);
        Behavior.UseExtraMoods(Extensions.Moods);
        settingsWindow?.RefreshPacks();
    }

    // local patch (模型适配): agent 通过这个工具看/写/清当前模型的适配档。
    private object AdaptTool(JsonElement args)
    {
        if (model == null) throw new InvalidOperationException("还没有模型，先在设置里选一个。");
        string action = args.TryGetProperty("action", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : "read";
        var profile = adapt.Current(model);
        if (action == "read")
            return new
            {
                model, label = Path.GetFileName(model), native = FitsBuiltins,
                adapted = profile.Adapted, partly = profile.PartlyAdapted, covered = profile.Covered,
                adaptedAt = profile.AdaptedAt, adaptedBy = profile.AdaptedBy,
                profile = AdaptFile.Location(model),
                expressions = ModelParts.Expressions(model), motions = ModelParts.Motions(model),
                zones = Settings.Zones,
                missingZones = Settings.Zones.Where(zone => !profile.Clicks.ContainsKey(zone) && !profile.Combos.ContainsKey(zone)).ToArray(),
                current = new { clicks = profile.Clicks, combos = profile.Combos, worn = profile.Worn, idle = profile.Idle },
            };
        if (action == "reset")
        {
            adapt.ResetCurrent();
            adaptToldAt = adaptAskedAt;
            return new { reset = true, model, profile = AdaptFile.Location(model) };
        }
        if (action != "write") throw new ArgumentException("action 只能是 read、write 或 reset。");
        int changed = 0;
        var warnings = new List<string>();
        // 点击部位 → 这个模型上的差分/动作（写进去之前确认模型里真有这个差分或动作）
        if (args.TryGetProperty("clicks", out var clicks) && clicks.ValueKind == JsonValueKind.Object)
            foreach (var item in clicks.EnumerateObject())
            {
                if (!Settings.Zones.Contains(item.Name) || item.Value.ValueKind != JsonValueKind.String) continue;
                string value = item.Value.GetString()!.Trim();
                if (value.Length == 0) { profile.Clicks.Remove(item.Name); changed++; continue; }
                if (!PartExists(value)) { warnings.Add(item.Name + " → " + value + "（这个模型里没有）"); continue; }
                profile.Clicks[item.Name] = value;
                changed++;
            }
        // local patch (组合动作): 点击部位 → 一组动作（mode: par 一起演 / seq 一个接一个）
        if (args.TryGetProperty("combos", out var combos) && combos.ValueKind == JsonValueKind.Object)
            foreach (var item in combos.EnumerateObject())
            {
                if (!Settings.Zones.Contains(item.Name)) continue;
                if (item.Value.ValueKind == JsonValueKind.Null) { profile.Combos.Remove(item.Name); changed++; continue; }
                ClickCombo? combo = null;
                try { combo = JsonSerializer.Deserialize<ClickCombo>(item.Value.GetRawText(), RelaxedJson); } catch { }
                if (combo?.Parts is not { Count: > 0 }) { warnings.Add(item.Name + " → 组合格式不对"); continue; }
                if (combo.Mode is not ("par" or "seq")) combo.Mode = "par";
                var usable = combo.Parts.Where(part => part != null && PartExists(part.Kind + ":" + part.Name)).ToList();
                if (usable.Count == 0) { warnings.Add(item.Name + " → 组合里的名字这个模型都没有"); continue; }
                combo.Parts = usable;
                foreach (var part in combo.Parts)
                {
                    part.Seconds = Math.Clamp(double.IsFinite(part.Seconds) ? part.Seconds : 3.5, .2, 30);
                    if (part.Start is not ("" or "now" or "after" or "delay")) part.Start = "";
                    part.Delay = Math.Clamp(double.IsFinite(part.Delay) ? part.Delay : 0, 0, 30);
                }
                profile.Combos[item.Name] = combo;
                changed++;
            }
        // local patch (外观差分): 常驻差分（外观页勾的那些）
        if (args.TryGetProperty("worn", out var worn) && worn.ValueKind == JsonValueKind.Array)
        {
            profile.Worn.Clear();
            foreach (var item in worn.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && PartExists("exp:" + item.GetString()))
                    profile.Worn.Add(item.GetString()!.Trim());
            changed++;
        }
        // 空闲权重
        if (args.TryGetProperty("idle", out var idle) && idle.ValueKind == JsonValueKind.Object)
            foreach (var item in idle.EnumerateObject())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(item.Name, "^(mood|exp|motion):.+$")) continue;
                bool on = true; int weight = 1;
                if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out int number)) weight = number;
                else if (item.Value.ValueKind == JsonValueKind.Object)
                {
                    if (item.Value.TryGetProperty("on", out var onValue) && onValue.ValueKind is JsonValueKind.True or JsonValueKind.False) on = onValue.GetBoolean();
                    if (item.Value.TryGetProperty("weight", out var weightValue) && weightValue.TryGetInt32(out int inner)) weight = inner;
                }
                profile.Idle[item.Name] = new IdleSetting { On = on, Weight = Math.Clamp(weight, 0, 10) };
                changed++;
            }
        profile.AdaptedAt = DateTime.Now;
        profile.AdaptedBy = "ai";
        adapt.Save();
        adaptToldAt = adaptAskedAt; // 配完了就不再每回都提醒
        return new { saved = true, model, profile = AdaptFile.Location(model), changed, covered = profile.Covered, adapted = profile.Adapted, warnings };
    }

    // local patch (模型适配): 适配里写的名字在这个模型上是不是真的存在。
    private bool PartExists(string value)
    {
        if (value == "none") return true;
        if (ParseTrigger(value) is not { } choice) return false;
        return choice.Kind switch
        {
            "mood" => Behavior.Moods.ContainsKey(choice.Name),
            "exp" => model != null && ModelParts.Expressions(model).Contains(choice.Name),
            "motion" => model != null && ModelParts.Motions(model).Contains(choice.Name),
            _ => false,
        };
    }

    // local patch (模型适配): 一键回平常的样子——差分、参数、手势、单放的动作一起收，再补回穿戴和体型。
    internal void RestoreDefault()
    {
        idleReturn++;
        body.Relax();
        behavior.Reset();
        foreach (var worn in settings.Outfit) Send($"exp {worn} 1");
        if (player != null)
        {
            player.SetShape(Shape);
            appliedShape = settings.Shape;
        }
    }

    // local patch (模型页): 设置页里点「试」放一次。
    private void PreviewPart(string kind, string name)
    {
        if (player != null) behavior.Preview(kind, name, DateTime.UtcNow);
    }

    // local patch (模型页): 换模型——她先隐去，旧渲染器关掉，新模型按同样的位置和设置出来。
    private void SwitchModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        path = Path.GetFullPath(path);
        if (path == model) return;
        model = path;
        fitsBuiltins = null; // 模型换了，内置那套还能不能用要重新看
        settings.ModelFile = path;
        settings.Save();
        Restart();
        // local patch (模型适配): 换上来先告诉她适配没有（大肥鱼那套原生模型不用配），需要就去设置里叫 AI 调。
        bool native = ModelParts.Expressions(path).Contains("黑框眼镜");
        if (!native && !adapt.Current(path).Adapted)
            bubble.Say("这个模型点她不同位置还没适配，要叫 AI 来调吗？右键「设置…」→「模型与适配」里有「让 AI 调」");
    }

    private void Restart()
    {
        player?.Dispose();
        player = null;
        trouble = null;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        image.Source = null;
        body.Forget();
        Start();
    }

    // ---- Her renderer ----

    private void Start()
    {
        if (model == null) { Trouble("没找到模型：把模型文件夹放进插件的 model 文件夹里，再在设置 → 模型与空闲里选一个。"); return; }
        if (!File.Exists(Places.Renderer)) { Trouble("没找到渲染程序：先运行插件里的 setup.ps1 编译它。"); return; }
        var started = new Player(image);
        started.Ready += Ready;
        started.Failed += message => Trouble("渲染出问题了：" + message);
        player = started;
        lastLeft = Left; lastTop = Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        started.Start(model, Shape, settings.Outfit, (int)(Width * dpi.DpiScaleX), (int)(Height * dpi.DpiScaleY), settings.Fps);
        // local patch (外观差分): 这个模型上常驻的差分（外观页勾的）也补上。
        foreach (var diff in adapt.Current(model).Worn) Send($"exp {diff} 1");
        appliedShape = settings.Shape; // local patch (设置面板)
        body.Forget(); silent = null; relaxAt = null;
        behavior.Resume(DateTime.UtcNow, settings.IdleSeconds);
        timer.Start();
        open = default;
        UpdateCut();
    }

    private void Trouble(string message)
    {
        trouble = message;
        Places.Log(message);
        bubble.Say(message);
    }

    // She comes in and greets once her body type has settled.
    private void Ready()
    {
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        behavior.Show("问候", DateTime.UtcNow);
    }

    private void ApplySize()
    {
        var shape = Shape;
        // Her feet stay where they are, not the window's corner, so resizing does not lift her out of the taskbar.
        double middle = Left + Width / 2, feet = Top + Height * shape.FootAt;
        Width = Math.Round(shape.Width * settings.Scale);
        Height = Math.Round(shape.Height * settings.Scale);
        if (double.IsFinite(middle) && double.IsFinite(feet)) { Left = middle - Width / 2; Top = feet - Height * shape.FootAt; }
        if (PresentationSource.FromVisual(this) != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Send($"resize {(int)(Width * dpi.DpiScaleX)} {(int)(Height * dpi.DpiScaleY)}");
        }
        UpdateCut();
    }

    // Where she was, or the bottom right corner half sunk behind the taskbar. In a test, out of sight.
    private void Place()
    {
        if (test) { Left = -6000; Top = 100; return; }
        var area = Screen(); // local patch (多屏): 她可以在任何一块屏幕上，不再只认主屏
        Left = settings.Left ?? area.Right - Width - 8;
        Top = settings.Top ?? area.Bottom - Height * .5;
        Clamp();
    }

    // She may sink behind the taskbar, but her face stays out.
    private void Clamp()
    {
        if (test) return;
        var area = Screen(); // local patch (多屏)
        var shape = Shape;
        var bounds = new Rect(Width * shape.Side, Height * shape.HeadTop, Width * (1 - 2 * shape.Side), Height * (shape.HeadAt + .12 - shape.HeadTop));
        Left = Math.Clamp(Left, area.Left - bounds.Left, Math.Max(area.Left - bounds.Left, area.Right - bounds.Right));
        Top = Math.Clamp(Top, area.Top - bounds.Top, Math.Max(area.Top - bounds.Top, area.Bottom - bounds.Bottom));
    }

    private void SavePlace()
    {
        if (test) return;
        settings.Left = Left; settings.Top = Top;
        settings.Save();
    }

    // A topmost window draws over the taskbar: whatever of her reaches it is not drawn, and clicks there
    // pass through, so she stands behind it.
    private void UpdateCut()
    {
        if (!IsVisible || test) return;
        // local patch (多屏): 按她所在那块屏幕算任务栏遮挡，副屏上没有任务栏就不会被裁。
        var work = Screen();
        var monitor = Screen(false);
        var window = new Rect(Left, Top, Width, Height);
        double left = work.Left > monitor.Left ? Math.Clamp(work.Left - window.Left, 0, window.Width) : 0;
        double top = work.Top > monitor.Top ? Math.Clamp(work.Top - window.Top, 0, window.Height) : 0;
        double right = work.Right < monitor.Right ? Math.Clamp(work.Right - window.Left, left, window.Width) : window.Width;
        double bottom = work.Bottom < monitor.Bottom ? Math.Clamp(work.Bottom - window.Top, top, window.Height) : window.Height;
        var uncovered = new Rect(left, top, right - left, bottom - top);
        if (uncovered == open) return;
        open = uncovered;
        image.Clip = uncovered == new Rect(0, 0, Width, Height) ? null : new RectangleGeometry(uncovered);
    }

    // Her body takes a moment to change while the window changes at once: she goes out, changes unseen, and comes back.
    private async void SetShape(string id)
    {
        settings.Shape = Shape.Find(id).Id;
        settings.Save();
        if (player is not { } current) return;
        appliedShape = id; // local patch (设置面板)
        long change = ++shapeChange;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        await Task.Delay(130);
        if (change != shapeChange) return;
        ApplySize();
        current.SetShape(Shape);
        Clamp();
        long seen = current.Frames;
        for (int wait = 0; wait < 40 && current.Frames < seen + 3; wait++) await Task.Delay(20);
        if (change == shapeChange) image.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Tick()
    {
        GoWithAgent();
        if (player is not { } current) return;
        if (trayHidden) return; // local patch (托盘): 收起来了就不动
        CheckSayFile(); // local patch (桥): 桥接插件让她说的话
        if (DateTime.UtcNow >= nextFullscreenLook)
        {
            nextFullscreenLook = DateTime.UtcNow.AddMilliseconds(500);
            UpdateFullscreen(current);
        }
        if (hiddenForFullscreen) return;
        // Moving the window pushes her body; physics carries that into her hair, skirt and tail.
        double swayX = (Left - lastLeft) * 30, swayY = (Top - lastTop) * 30;
        lastLeft = Left; lastTop = Top;
        if (swayX != 0 || swayY != 0) current.Send(FormattableString.Invariant($"sway {swayX:0} {swayY:0}"));
        UpdateCut();
        UpdateMouth(current);
        // When the last agent goes, nobody is left to say its work ended. A program on the HTTP interface has no
        // connection to lose: what it starts stays until it says it is done.
        int agents = server?.Connections ?? 0;
        if (agents == 0 && agentsBefore > 0) behavior.ClearActivities();
        agentsBefore = agents;
        UpdateBody();
        UpdateFilm();
        speech.Tick();
        // Nothing of her own while she says something or still holds the look she said it with.
        behavior.Tick(DateTime.UtcNow, voice != null || speech.IsSaying || silent != null || body.Posing || comboBusy, settings.IdleSeconds);
        if (!settings.FollowMouse || test)
        {
            if (lookX == 0 && lookY == 0) return;
            lookX = lookY = 0;
            current.Send("look 0 0");
            return;
        }
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        double headX = (Left + Width / 2) * dpi.DpiScaleX, headY = (Top + Height * Shape.HeadAt) * dpi.DpiScaleY;
        double x = Math.Clamp((cursor.X - headX) / 500, -1, 1), y = Math.Clamp((headY - cursor.Y) / 400, -1, 1);
        if (Math.Abs(x - lookX) + Math.Abs(y - lookY) < .01) return;
        lookX = x; lookY = y;
        current.Send(FormattableString.Invariant($"look {x:0.00} {y:0.00}"));
    }

    // She comes and goes with the agent (the owner, 2026-11-03: the agent closed, she was still there). Once an agent
    // has been connected, she bows out after a while with none, long enough for one that reconnects or restarts its
    // servers. A program on the HTTP interface keeps her while it uses her; started by hand with no agent, she stays.
    private static readonly TimeSpan AloneFor = TimeSpan.FromSeconds(15), HttpUse = TimeSpan.FromMinutes(10);
    private bool hadAgent;
    private int agentsBefore;
    private DateTime? aloneSince;
    private DateTime lastHttp = DateTime.MinValue;

    private void GoWithAgent()
    {
        if (test || exiting) return;
        if ((server?.Connections ?? 0) > 0) { hadAgent = true; aloneSince = null; return; }
        if (!hadAgent || DateTime.UtcNow - lastHttp < HttpUse) { aloneSince = null; return; }
        aloneSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - aloneSince < AloneFor) return;
        Places.Log("接她的 Agent 都断开了，她先收起来");
        HideToTray(); // local patch (托盘): 不退出进程，留个入口
    }

    // While another program fills the screen she is hidden and her renderer draws nothing; her words still play on,
    // unseen, and she is back on top once it is gone. Not in a test, which is out of sight anyway.
    private void UpdateFullscreen(Player current)
    {
        // local patch (全屏): 只有盖住她所在那块屏的全屏窗口才让她让开
        bool covered = settings.HideOnFullscreen && !test && Fullscreen.Covers(new WindowInteropHelper(this).Handle);
        if (covered == hiddenForFullscreen) return;
        hiddenForFullscreen = covered;
        current.Send($"pause {(covered ? 1 : 0)}");
        bubble.Quiet = covered || test;
        if (covered) { bubble.Hide(); Hide(); return; }
        if (trayHidden) return; // local patch (托盘): 她本来就被收起来了，别自己冒出来
        Show();
        Topmost = false; Topmost = true;
    }

    // ---- The user's hands on her ----

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (exiting || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        dragging = true; dragged = false;
        lockedNow = settings.LockPosition; // local patch (锁定位置)
        GetCursorPos(out var p);
        dragStart = new Point(p.X, p.Y);
        windowStart = new Point(Left, Top);
        CaptureMouse();
    }

    // local patch (桌上对话): 中键点她打开输入框——左键连点有它自己的彩蛋，不去抢。
    private void MiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (exiting || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        OpenChat();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!dragging || lockedNow || e.LeftButton != MouseButtonState.Pressed) return; // local patch (锁定位置)
        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        double dx = (p.X - dragStart.X) / dpi.DpiScaleX, dy = (p.Y - dragStart.Y) / dpi.DpiScaleY;
        // local patch (锁定位置): 起拖的阈值从 4 抬到 6，手一抖不会变成拖动
        if (Math.Abs(dx) + Math.Abs(dy) > 6 && !dragged)
        {
            dragged = true;
            behavior.DragStart();
            Happened("drag_start", new { });
        }
        if (dragged) { Left = windowStart.X + dx; Top = windowStart.Y + dy; }
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        e.Handled = true;
        dragging = false;
        ReleaseMouseCapture();
        if (dragged) { Dropped(); return; }
        // The window also gets clicks on her faint edges, which count as missing her.
        var at = e.GetPosition(this);
        if (VisualTreeHelper.HitTest(image, at) == null) return;
        var zone = Behavior.Zone(Shape.Id, at.X / Width, at.Y / Height, settings.Outfit);

        behavior.Click(zone, DateTime.UtcNow, at.X / Width < .5 ? 1 : -1);
        Happened("click", new { zone, reaction = behavior.Showing });
    }

    private void Dropped()
    {
        Clamp();
        SavePlace();
        behavior.DragEnd(DateTime.UtcNow);
        Happened("drag_end", new { });
    }

    // ---- local patch (多屏 / 右键菜单 / 桌上对话) ----
    // 她所在的那块屏幕（WPF 单位）。还没建出窗口时，看她保存的位置落在哪块屏；位置也没有就跟鼠标。

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    internal Rect Screen(bool work = true)
    {
        nint monitor = 0;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) monitor = MonitorFromWindow(handle, 2);
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var point = default(NativePoint);
            if (double.IsFinite(Left) && double.IsFinite(Top))
                point = new NativePoint { X = (int)((Left + Width / 2) * dpi.DpiScaleX), Y = (int)((Top + Height / 2) * dpi.DpiScaleY) };
            else GetCursorPos(out point);
            monitor = MonitorFromPoint(point, 2);
        }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rect = work ? info.Work : info.Monitor;
            return new Rect(rect.Left / dpi.DpiScaleX, rect.Top / dpi.DpiScaleY,
                (rect.Right - rect.Left) / dpi.DpiScaleX, (rect.Bottom - rect.Top) / dpi.DpiScaleY);
        }
        return work ? SystemParameters.WorkArea : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    // 右键菜单：体型、大小、跟她说句话、先歇着。
    private void RightUp(object sender, MouseButtonEventArgs e)
    {
        if (exiting) return;
        e.Handled = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        var physical = PointToScreen(e.GetPosition(this));
        var at = new Point(physical.X / dpi.DpiScaleX, physical.Y / dpi.DpiScaleY);
        new PetMenu(MenuItems()).ShowAt(at, Screen());
    }

    private List<(string Label, Action? Run)> MenuItems()
    {
        var items = new List<(string Label, Action? Run)> { ("跟她说句话…", OpenChat), ("-", null) };
        foreach (var shape in Shape.All)
        {
            var id = shape.Id;
            items.Add(((shape.Id == Shape.Id ? "✓ " : "   ") + shape.Label, () => SetShape(id)));
        }
        items.Add(("-", null));
        items.Add(("大一点", () => Zoom(.05)));
        items.Add(("小一点", () => Zoom(-.05)));
        items.Add(("-", null));
        items.Add(((settings.LockPosition ? "✓ " : "   ") + "锁定位置（点击不会变成拖动）", ToggleLock));
        items.Add(("设置…", OpenSettings));
        items.Add(("先歇着", HideToTray));
        items.Add(("退出", QuitForGood));
        return items;
    }

    // local patch (锁定位置): 右键菜单里一键锁/解锁
    private void ToggleLock()
    {
        settings.LockPosition = !settings.LockPosition;
        settings.Save();
        tray?.Hint(settings.LockPosition ? "dshlive2d控制台（位置已锁）" : "dshlive2d控制台");
    }

    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (exiting) return;
        e.Handled = true;
        Zoom(e.Delta > 0 ? .05 : -.05);
    }

    // 滚轮和菜单都走这里：大小限制在 0.5–2，改完立刻存下来（跟 update_settings 的 scale 一样）。
    private void Zoom(double step)
    {
        settings.Scale = Math.Clamp(settings.Scale + step, .5, 2);
        ApplySize();
        Clamp();
        settings.Save();
    }

    private ChatWindow? chat;
    private void OpenChat()
    {
        if (exiting) return;
        chat ??= new ChatWindow(this, () => Height * Shape.HeadTop) { Scale = settings.BubbleScale };
        chat.Send = SaidToHer;
        chat.Display();
    }

    // 用户在她头上打的那句话：进事件、落盘（进程退了也不丢），她先应一声。
    private void SaidToHer(string text)
    {
        Happened("user_says", new { text });
        behavior.Show("点头", DateTime.UtcNow);
        KeepChat(text);
    }

    // local patch (不丢字): 用户打的字除了内存队列，再往 cache\chat.log 追加一行；托盘上记着还有几句没被读。
    private int unreadChat;
    private void KeepChat(string text)
    {
        try
        {
            var folder = Path.Combine(Places.Root, "cache");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "chat.log"), DateTime.Now.ToString("s") + "\t" + text.ReplaceLineEndings(" ") + Environment.NewLine);
        }
        catch (Exception error) { Places.Log("聊天记录没能落盘：" + error.Message); }
        unreadChat++;
        tray?.Hint("dshlive2d控制台（有 " + unreadChat + " 句新话）");
    }

    private void Happened(string kind, object detail)
    {
        var happening = new Happening(++sequence, DateTime.UtcNow, kind, detail);
        happenings.Add(happening);
        if (happenings.Count > 100) happenings.RemoveAt(0);
        server?.Publish(kind, detail);
    }

    // ---- What she says ----

    // A new line waits for the one before to be played out, or her body would be cut off mid-move. At most as long as
    // its words take and 20 s more, so a long line or recording plays out but nothing stuck holds her up for good.
    private async Task WaitForLine(CancellationToken token)
    {
        var waited = Stopwatch.StartNew();
        while ((silent != null || speech.IsSaying) && waited.Elapsed.TotalSeconds < 20 + speech.Spoken) await Task.Delay(50, token);
        // local patch (气泡排队): 上一句演完了，气泡还要悬浮一会儿；等它停够再让下一条开口，免得新话把旧气泡的字硬顶掉。
        // bubble_seconds = -1 是「一直留着」，那时最多等 20 秒，不让队列卡死；点掉气泡也会立刻放行。
        var hold = speech.HoldUntil;
        if (hold != DateTime.MaxValue && hold > DateTime.UtcNow)
        {
            var left = (hold - DateTime.UtcNow).TotalSeconds;
            if (settings.BubbleSeconds < 0) left = Math.Min(left, 20);
            left = Math.Min(left, 300);
            if (left > 0) await Task.Delay(TimeSpan.FromSeconds(left), token);
        }
    }

    // local patch (气泡排队): 气泡被点掉，这一句就算停够了。
    internal void BubbleClicked() => speech.Dismiss();

    // In the order they came, each once the one before has been acted out.
    private async Task InTurn(Func<Task> act, CancellationToken token)
    {
        var turn = new TaskCompletionSource();
        var before = lines;
        lines = turn.Task;
        try { await before; await WaitForLine(token); await act(); }
        finally { turn.SetResult(); }
    }

    private void SayLine(string text, double? voiceSeconds)
    {
        if (trayHidden) return; // local patch (托盘): 收起来了就先别冒气泡
        relaxAt = null;
        var line = new Line(text, body.Apply, voiceSeconds);
        if (line.Plain.Trim().Length == 0) { PlaySilently(text); return; }
        StartFilming(line.Plain);
        speech.Say(line);
    }

    // film: false for what she tries in the mirror, which she sees there already.
    private void PlaySilently(string text, bool filmed = true)
    {
        relaxAt = null;
        silent = new Line(text, body.Apply);
        if (filmed) StartFilming(silent.Plain);
        silentClock.Restart();
        silent.Advance(0);
    }

    // Her own look stays a while after her words, then she relaxes; not in the middle of one of her reflexes.
    private void UpdateBody()
    {
        if (silent is { } line)
        {
            line.Advance(silentClock.Elapsed.TotalSeconds);
            if (line.Done) silent = null;
        }
        if (!body.Posing || silent != null || speech.IsSaying) { relaxAt = null; return; }
        relaxAt ??= DateTime.UtcNow.AddSeconds(LingerSeconds);
        if (DateTime.UtcNow < relaxAt || behavior.Showing != null) return;
        body.Relax();
        relaxAt = null;
    }

    // Returns how long the recording is, for spreading what she does over it.
    private double? PlayVoice(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathRooted(file) || !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new ArgumentException("audio 要是本机 wav 文件的完整路径，而且文件要在。");
        voicePlayer.Stop();
        voicePlayer.Open(new Uri(file));
        voicePlayer.Play();
        try { voice = Behavior.VoiceLevels(File.ReadAllBytes(file), VoiceStepMilliseconds); }
        catch (IOException) { voice = null; }
        // A voice that never starts playing must not hold her mouth for ever.
        if (voice != null) voiceUntil = DateTime.UtcNow.AddMilliseconds(voice.Length * VoiceStepMilliseconds + 2000);
        return voice?.Length * VoiceStepMilliseconds / 1000.0;
    }

    // Her recorded voice when there is one; otherwise her mouth moves while the words come out.
    private void UpdateMouth(Player current)
    {
        double level = 0;
        if (voice is { } levels)
        {
            int step = (int)(voicePlayer.Position.TotalMilliseconds / VoiceStepMilliseconds);
            if (step < levels.Length && DateTime.UtcNow < voiceUntil) level = levels[step];
            else voice = null;
        }
        if (voice == null && speech.IsTalking) level = .2 + .35 * Math.Abs(Math.Sin(Environment.TickCount64 / 70.0));
        if (level == mouth) return;
        mouth = level;
        current.Send(FormattableString.Invariant($"mouth {level:0.00}"));
    }

    // ---- Her mirror and her photos ----

    // What she looks like right now, head to waist so her face is big enough to read, on white.
    private BitmapSource? Snapshot(int most = MirrorHeight)
    {
        if (image.Source is not BitmapSource frame || frame.PixelWidth == 0) return null;
        int top = (int)(frame.PixelHeight * Math.Max(0, Shape.HeadTop - .03)), span = Math.Min(frame.PixelHeight - top, (int)(frame.PixelHeight * MirrorSpan));
        var upper = new CroppedBitmap(frame, new Int32Rect(0, top, frame.PixelWidth, span));
        double scale = Math.Min(1, most / (double)span);
        int width = (int)Math.Round(frame.PixelWidth * scale), height = (int)Math.Round(span * scale);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw.DrawImage(upper, new Rect(0, 0, width, height));
        }
        var flat = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        flat.Render(visual);
        flat.Freeze();
        return flat;
    }

    // Looks in rows of three, left to right and down in the order they were seen, as a PNG data URL.
    private static string Picture(IReadOnlyList<BitmapSource> shots)
    {
        int columns = Math.Min(MirrorColumns, shots.Count), rows = (shots.Count + columns - 1) / columns;
        int cellWidth = shots.Max(shot => shot.PixelWidth), cellHeight = shots.Max(shot => shot.PixelHeight);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, cellWidth * columns, cellHeight * rows));
            for (int i = 0; i < shots.Count; i++)
                draw.DrawImage(shots[i], new Rect(i % columns * cellWidth, i / columns * cellHeight, shots[i].PixelWidth, shots[i].PixelHeight));
        }
        var strip = new RenderTargetBitmap(cellWidth * columns, cellHeight * rows, 96, 96, PixelFormats.Pbgra32);
        strip.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(strip));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }

    private static IReadOnlyList<BitmapSource> Moments(IReadOnlyList<BitmapSource> seen) => seen.Count <= MirrorShots ? seen
        : Enumerable.Range(0, MirrorShots).Select(i => seen[(int)Math.Round(i * (seen.Count - 1) / (double)(MirrorShots - 1))]).ToList();

    // She tries something in front of the mirror: done for real, watched all through, six moments given back.
    private async Task<IReadOnlyList<BitmapSource>> TryInMirror(string pose, CancellationToken token)
    {
        var seen = new List<BitmapSource>();
        await InTurn(async () =>
        {
            PlaySilently(pose.Contains('〔') ? pose : "〔" + pose + "〕", filmed: false);
            var line = silent!;
            var clock = Stopwatch.StartNew();
            // Until it has all played and settled a moment, never longer than six seconds.
            while (clock.Elapsed.TotalSeconds < 6 && !(line.Done && clock.Elapsed.TotalSeconds > line.Until + .3))
            {
                if (Snapshot(MirrorGridHeight) is { } shot) seen.Add(shot);
                await Task.Delay(120, token);
            }
        }, token);
        return Moments(seen);
    }

    private void StartFilming(string said)
    {
        FinishFilming();
        film = []; filmSaid = said; filmEnded = null; filmShot = default;
    }

    // A look every FilmEvery while she says it and a moment after; too long a line keeps every other one.
    private void UpdateFilm()
    {
        if (film is not { } shots) return;
        var now = DateTime.UtcNow;
        if (speech.IsSaying || silent != null) filmEnded = null; else filmEnded ??= now;
        if (now - filmShot >= FilmEvery && Snapshot(MirrorGridHeight) is { } shot)
        {
            shots.Add(shot); filmShot = now;
            if (shots.Count >= FilmFrames) { var kept = shots.Where((_, i) => i % 2 == 0).ToList(); shots.Clear(); shots.AddRange(kept); }
        }
        if (filmEnded is { } ended && now - ended >= FilmAfter) FinishFilming();
    }

    private void FinishFilming()
    {
        var shots = film;
        film = null;
        if (shots is not { Count: > 0 }) return;
        photos.Add(new(DateTime.UtcNow, filmSaid, Picture(Moments(shots))));
        TrimPhotos();
    }

    private void TrimPhotos()
    {
        photos.RemoveAll(photo => DateTime.UtcNow - photo.At > PhotoLife);
        if (photos.Count > PhotosKept) photos.RemoveRange(0, photos.Count - PhotosKept);
    }

    // ---- local patch (桥): 会话桥把回复写进 cache\say.txt，她读到就说 ----

    private readonly DateTime startedAt = DateTime.UtcNow;
    private DateTime sayFileAt, nextSayLook;

    private void CheckSayFile()
    {
        if (DateTime.UtcNow < nextSayLook) return;
        nextSayLook = DateTime.UtcNow.AddMilliseconds(600);
        var path = Path.Combine(Places.Root, "cache", "say.txt");
        try
        {
            if (!File.Exists(path)) return;
            var at = File.GetLastWriteTimeUtc(path);
            if (at == sayFileAt) return;
            sayFileAt = at;
            if (at < startedAt) return; // 启动前留下的旧话不补说
            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0 || text.Length > 2000) return;
            _ = InTurn(() => { SayLine(text, null); return Task.CompletedTask; }, CancellationToken.None);
        }
        catch (Exception error) { Places.Log("读 say.txt 出问题：" + error.Message); }
    }

    // ---- local patch (设置面板) ----

    // local patch (扩展包): 打开「获取扩展」——拖素材进来，交给 agent 认动作、写新包。
    internal void OpenGrab() => new GrabWindow(RequestPackGrab).Show();

    private void OpenSettings()
    {
        if (settingsWindow is { IsVisible: true }) { settingsWindow.Activate(); return; }
        // local patch (模型页): 设置窗还要能读现在用的模型、换模型、试放一个差分或动作
        settingsWindow = new SettingsWindow(settings, adapt, ApplySettingsFromWindow, () => model, SwitchModel, PreviewPart, RestoreDefault, RequestAdapt, WearDiff, RequestPackAdapt, OpenGrab);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    // 设置窗点了保存：能当场生效的都当场做，体型换了才重建。
    internal void ApplySettingsFromWindow()
    {
        Send($"fps {settings.Fps}");
        ApplySize();
        Clamp();
        bubble.Scale = settings.BubbleScale;
        speech.Seconds = settings.BubbleSeconds;
        nextFullscreenLook = default;
        if (player != null && appliedShape != Shape.Find(settings.Shape).Id) SetShape(settings.Shape);
        settings.Save();
        UpdateCut();
    }

    // ---- local patch (托盘): 收起来 / 叫她出来 / 真退出 ----

    // 收起来：窗口藏了、渲染器暂停，进程和托盘留着。
    private void HideToTray()
    {
        if (exiting) return;
        speech.Stop();
        trayHidden = true;
        if (IsVisible)
        {
            player?.Send("pause 1");
            Hide();
        }
        tray?.Hint("dshlive2d控制台（收起来了，双击叫我出来）");
    }

    private void ShowFromTray()
    {
        if (exiting) return;
        trayHidden = false;
        Show();
        player?.Send("pause 0");
        Topmost = false; Topmost = true;
        Clamp();
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        tray?.Hint("dshlive2d控制台");
    }

    // 托盘的「退出」：这才是真的关掉，告别演完进程结束。
    private void QuitForGood()
    {
        if (leavingForGood) return;
        leavingForGood = true;
        Close();
    }

    // ---- Leaving ----

    // She bows and fades out while still bowed, rather than vanishing halfway down.
    private async void Leaving(object? sender, CancelEventArgs e)
    {
        if (exitReady) return;
        e.Cancel = true;
        // local patch (托盘): 只有托盘的「退出」才真退，其余一律收起来。
        if (!leavingForGood) return;
        if (exiting) return;
        exiting = true;
        speech.Stop();
        if (player != null && image.Opacity > 0)
        {
            behavior.Show("告别", DateTime.UtcNow);
            await Task.Delay(1600);
            image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
            await Task.Delay(600);
        }
        exitReady = true;
        Close();
        Application.Current.Shutdown();
    }

    // ---- The agent's tools ----

    // A newer plugin found on GitHub rides along with whatever the agent asks next, once, for it to tell the user.
    private async Task<object> Execute(string name, JsonElement args, CancellationToken token)
    {
        var result = await Dispatcher.InvokeAsync(() => Run(name, args, token)).Task.Unwrap();
        // local patch (模型适配): 用户点了「让 AI 调」，下一次工具调用就把这件事告诉 agent。
        if (adaptAskedAt is { } asked && adaptToldAt != asked && DateTime.UtcNow - asked < TimeSpan.FromHours(2))
        {
            adaptToldAt = asked;
            result = Tools.WithNotice(result, AdaptNotice());
        }
        // local patch (扩展包): 点了「扩展适配」/ 拖了素材，下一次工具调用把这件事告诉 agent。
        if (packAskedAt is { } packAsked && packToldAt != packAsked && DateTime.UtcNow - packAsked < TimeSpan.FromHours(2))
        {
            packToldAt = packAsked;
            result = Tools.WithNotice(result, PackAdaptNotice());
        }
        if (grabAskedAt is { } grabAsked && grabToldAt != grabAsked && DateTime.UtcNow - grabAsked < TimeSpan.FromHours(2))
        {
            grabToldAt = grabAsked;
            result = Tools.WithNotice(result, GrabNotice());
        }
        return updates.Unannounced() is { } found ? Tools.WithNotice(result, UpdateNotice(found)) : result;
    }

    // What her body could not read in what she said, and while her lines move only before their words, a reminder to
    // think of her body as she speaks (the owner, 2026-11-05). Her last ten lines since she started.
    private readonly List<bool> acting = [];
    private string Heard(IReadOnlyList<string> lines)
    {
        acting.AddRange(lines.Select(Body.ActsWithin));
        if (acting.Count > 10) acting.RemoveRange(0, acting.Count - 10);
        var unread = Body.Unread(string.Join("\n", lines));
        var missed = unread.Count == 0 ? "" : "身体没跟上：" + string.Join("、", unread.Select(item => "〔" + item.Word + "〕" + (item.Like != null ? "（想写的是〔" + item.Like + "〕？）" : ""))) +
            "，" + (unread.Count > 1 ? "这几个" : "这个") + "没做出来。";
        return missed + (Body.NeedsActing(acting) ? "下次说之前，在心里把这几句演一遍：说到哪儿脸和身子怎么变，就在哪儿写〔〕。" : "");
    }

    private static string UpdateNotice(Updates.Found found) =>
        "【插件有新版本】GitHub 上比现在这份多了 " + found.Behind + " 处改动（新的在前）：" + string.Join("；", found.Changes) +
        "。" + (found.Guide ? "这次身体说明也改了，更新完要把你人设里【你的身体】到【你的身体·完】那段换成新版（“更新插件”第 6 步）。" : "") +
        "找个合适的时候，用一两句话告诉用户有更新、大概改了什么，问他要不要更新；他说要，就照插件 AGENTS.md 最后的“更新插件”做。" +
        "还没更新的话，过一天会再提醒一次；get_state 的 update 里随时能看到。";

    private async Task<object> Run(string name, JsonElement args, CancellationToken token)
    {
        if (exiting) throw new InvalidOperationException("你正在告别，收起之后再叫你出来。");
        if (name is not ("get_state" or "quit" or "recent_events") && trouble != null && player == null) throw new InvalidOperationException(trouble);
        switch (name)
        {
            case "say":
                var text = Tools.RequiredText(args, "text");
                var audio = Tools.OptionalText(args, "audio");
                // local patch (说话页): 超过设置里的上限就排队给桥——桥用模型压短后再让她说，不截半句。
                if (settings.SayLimit > 0 && Talk.Count(text) > settings.SayLimit)
                {
                    var chars = Talk.Count(text);
                    Talk.Queue(text, settings.SayLimit);
                    return Tools.Text(new
                    {
                        queued = true,
                        chars,
                        limit = settings.SayLimit,
                        note = "这句 " + chars + " 字，超过上限 " + settings.SayLimit + "；已经交给桥用模型压短，压好她会自己说。这次不用重说。",
                    });
                }
                // A line each, in turn; the call returns once the first has begun, the rest wait their turn whoever
                // asked. A recording is one utterance, so with one it is a single bubble.
                var bubbles = audio != null ? [text] : Line.Split(text);
                Talk.Note(text, bubbles.Count);
                await InTurn(() => { SayLine(bubbles[0], audio != null ? PlayVoice(audio) : null); return Task.CompletedTask; }, token);
                foreach (var next in bubbles.Skip(1))
                    _ = InTurn(() => { if (!exiting) SayLine(next, null); return Task.CompletedTask; }, CancellationToken.None);
                var note = Heard(bubbles);
                return Tools.Text(note.Length == 0 ? new { started = true, bubbles = bubbles.Count, said = Body.Strip(text) }
                    : (object)new { started = true, bubbles = bubbles.Count, said = Body.Strip(text), note });
            case "look_in_mirror":
                var pose = Tools.OptionalText(args, "try");
                IReadOnlyList<BitmapSource> shots = pose != null ? await TryInMirror(pose, token) : Snapshot() is { } now ? [now] : [];
                if (shots.Count == 0) throw new InvalidOperationException("镜子里还什么都看不到，等你出现了再照。");
                return Tools.Image(Picture(shots), new { shots = shots.Count, tried = pose });
            case "look_back":
                int which = args.TryGetProperty("which", out var value) && value.TryGetInt32(out var number) ? number : 1;
                if (film != null && !speech.IsSaying && silent == null) FinishFilming();
                TrimPhotos();
                if (photos.Count == 0) throw new InvalidOperationException("这半小时里还没有拍下你说话的样子。");
                if (which < 1 || which > photos.Count) throw new ArgumentException("只留着最近 " + photos.Count + " 句的样子。");
                var photo = photos[^which];
                int Ago(DateTime at) => (int)(DateTime.UtcNow - at).TotalSeconds;
                return Tools.Image(photo.Picture, new { said = photo.Said, secondsAgo = Ago(photo.At),
                    recent = Enumerable.Range(1, photos.Count).Select(n => new { which = n, said = photos[^n].Said, secondsAgo = Ago(photos[^n].At) }).ToArray() });
            case "set_activity":
                var kind = Tools.RequiredText(args, "kind", 40);
                if (!args.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("要给 active（true 或 false）。");
                behavior.Activity(kind, active.GetBoolean());
                return Tools.Text(new { kind, active = active.GetBoolean() });
            case "dress":
                foreach (var label in Tools.Texts(args, "wear").Concat(Tools.Texts(args, "remove")))
                    if (Outfit.Expression(label) == null) throw new ArgumentException("没有“" + label + "”。衣服和配饰见 dress 的说明。");
                foreach (var label in Tools.Texts(args, "wear")) Dress(Outfit.Expression(label)!, true);
                foreach (var label in Tools.Texts(args, "remove")) Dress(Outfit.Expression(label)!, false);
                return Tools.Text(new { wearing = Wearing() });
            case "get_state":
                return Tools.Text(State());
            case "update_settings":
                if (args.TryGetProperty("fps", out var fps) && fps.TryGetInt32(out var rate))
                {
                    if (!Settings.FpsChoices.Contains(rate)) throw new ArgumentException("fps 只能是 30、60、90、120。");
                    settings.Fps = rate; Send($"fps {rate}");
                }
                if (args.TryGetProperty("scale", out var scale) && scale.TryGetDouble(out var size))
                {
                    settings.Scale = Math.Clamp(size, .5, 2); ApplySize(); Clamp();
                }
                if (args.TryGetProperty("bubble_scale", out var bubbleScale) && bubbleScale.TryGetDouble(out var bubbleSize))
                {
                    settings.BubbleScale = Math.Clamp(bubbleSize, .5, 2); bubble.Scale = settings.BubbleScale;
                }
                if (args.TryGetProperty("bubble_seconds", out var stay) && stay.TryGetInt32(out var stays))
                {
                    settings.BubbleSeconds = Settings.StayFor(stays); speech.Seconds = settings.BubbleSeconds;
                }
                // Taken up at once: hidden behind a fullscreen program she comes back if it is turned off.
                if (args.TryGetProperty("hide_on_fullscreen", out var hide) && hide.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.HideOnFullscreen = hide.GetBoolean(); nextFullscreenLook = default;
                }
                if (args.TryGetProperty("check_updates", out var check) && check.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.CheckUpdates = check.GetBoolean();
                if (args.TryGetProperty("follow_mouse", out var follow) && follow.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.FollowMouse = follow.GetBoolean();
                if (args.TryGetProperty("lock_position", out var lockPos) && lockPos.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.LockPosition = lockPos.GetBoolean(); // local patch (锁定位置)
                if (args.TryGetProperty("idle_seconds", out var idle) && idle.TryGetInt32(out var seconds)) settings.IdleSeconds = Math.Clamp(seconds, 15, 600);
                settings.Save();
                if (args.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.String) SetShape(shape.GetString()!);
                return Tools.Text(State());
            case "open_settings": // local patch (设置面板)
                OpenSettings();
                return Tools.Text(new { opened = true });
            case "adapt_model": // local patch (模型适配)
                return Tools.Text(AdaptTool(args));
            case "adapt_pack": // local patch (扩展包)
                return Tools.Text(AdaptPackTool(args));
            case "recent_events":
                long after = args.TryGetProperty("after", out var since) && since.TryGetInt64(out var mark) ? mark : 0;
                if (after == 0) { unreadChat = 0; tray?.Hint("dshlive2d控制台"); } // local patch (不丢字): 读过就清掉未读
                return Tools.Text(happenings.Where(item => item.Sequence > after)
                    .Select(item => new { sequence = item.Sequence, secondsAgo = (int)(DateTime.UtcNow - item.At).TotalSeconds, kind = item.Kind, detail = item.Detail }).ToArray());
            case "quit":
                _ = Dispatcher.BeginInvoke(new Action(HideToTray)); // local patch (托盘): 收进托盘，随时能叫回来
                return Tools.Text(new { leaving = true, hidden = true, howBack = "托盘的「叫她出来」、再启动一次 FishPet.exe，或任何一次工具调用" });
            default:
                throw new ArgumentException("没有这个工具。");
        }
    }

    private void Dress(string expression, bool worn)
    {
        settings.Outfit = worn ? settings.Outfit.Append(expression).Distinct().ToArray() : settings.Outfit.Where(item => item != expression).ToArray();
        Send($"exp {expression} {(worn ? 1 : 0)}");
        behavior.UpdateGlasses();
        settings.Save();
    }

    private string[] Wearing() => Outfit.Clothes.Concat(Outfit.Accessories).Where(item => settings.Outfit.Contains(item.Expression)).Select(item => item.Label).ToArray();

    private object State() => new
    {
        shape = settings.Shape, shapeLabel = Shape.Label, wearing = Wearing(), fps = settings.Fps, scale = settings.Scale, bubble_scale = settings.BubbleScale, bubble_seconds = settings.BubbleSeconds,
        hide_on_fullscreen = settings.HideOnFullscreen, hidden_for_fullscreen = hiddenForFullscreen, follow_mouse = settings.FollowMouse,
        check_updates = settings.CheckUpdates,
        update = updates.Latest is { } found ? new { current = found.Current, latest = found.Latest, behind = found.Behind, changes = found.Changes, guide = found.Guide } : null,
        idle_seconds = settings.IdleSeconds, position = new { Left, Top }, saying = speech.IsSaying, reaction = behavior.Showing, trouble,
        model = model, // local patch (模型页)
        // local patch (不丢字): 还有几句用户打的字没被读过；chat_log 是落盘的那份，进程重启也读得到
        unread_chat = unreadChat, chat_log = Path.Combine(Places.Root, "cache", "chat.log"), session_target = settings.SessionTarget,
        lock_position = settings.LockPosition,
        clothes = Outfit.Clothes.Select(item => item.Label).ToArray(), accessories = Outfit.Accessories.Select(item => item.Label).ToArray(),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    // local patch (多屏)
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
