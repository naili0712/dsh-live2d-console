using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input; // local patch (置顶): 快捷键用 Key/ModifierKeys 表示

namespace FishPet;

// What is kept between starts, in settings.json beside the plugin. Only the agent changes it (the tools
// in Control.cs); there is no settings window.
internal sealed class Settings
{
    public string Shape { get; set; } = "small";
    public string[] Outfit { get; set; } = [];
    public int Fps { get; set; } = 60;
    public double Scale { get; set; } = 1;
    // Her speech bubble, words and all, apart from her own size.
    public double BubbleScale { get; set; } = 1;
    // How long the bubble stays after her last word: 0 by how much she said (5 to 20 s), -1 until her next line or a click.
    public int BubbleSeconds { get; set; }
    // Out of the way while something else fills the screen, drawing nothing meanwhile (as the BigBlueFish pet does).
    public bool HideOnFullscreen { get; set; } = true;
    // local patch (置顶): 右键菜单的「固定屏幕顶部」——一直待在别的窗口（包括全屏的游戏、视频）上面，不因为全屏而躲开。
    public bool PinTop { get; set; }
    // local patch (置顶): 开关「固定屏幕顶部」的全局热键，写法如 F6、Ctrl+Alt+F7；设置页里可以改。
    public string PinHotkey { get; set; } = "F6";
    // Whether she looks on GitHub for a newer plugin when she starts and twice a day (Updates).
    public bool CheckUpdates { get; set; } = true;
    public bool FollowMouse { get; set; } = true;
    // Seconds between the small things she does by herself when nobody talks to her.
    public int IdleSeconds { get; set; } = 40;
    public double? Left { get; set; }
    public double? Top { get; set; }
    // local patch (设置面板): 登录 Windows 时自己出来；桌宠的话往哪个会话送（留给桥接用）。
    public bool AutoStart { get; set; }
    public string? SessionTarget { get; set; }
    // local patch (锁定位置): 锁住时她不会被拖动，点她就只是点她。
    public bool LockPosition { get; set; }
    // local patch (说话页): 一次 say 最多说多少可见字（〔〕里的神情动作不计数）；0 = 不限。
    public int SayLimit { get; set; } = 90;
    // local patch (模型页): 用 model\ 里的哪个模型；null = 找到的第一个。换模型会重开渲染器。
    public string? ModelFile { get; set; }
    // local patch (模型页): 空闲触发的差分与动作。键是 "mood:张望" / "exp:脸红" / "motion:脱捕"；
    // 没有记录的项用默认（内置小动作开、模型自带的关）。
    public Dictionary<string, IdleSetting> Idle { get; set; } = [];
    // local patch (模型页): 点她不同位置时演什么。键是 head/face/hand/skirt/tail/body，
    // 值是 "mood:摸头" / "exp:脸红" / "motion:脱捕"；没有记录就是原来的样子，"none" 是不动。
    public Dictionary<string, string> ClickTriggers { get; set; } = [];

    internal static readonly int[] FpsChoices = [30, 60, 90, 120];
    // local patch (模型页): 能单独配点击触发的部位，跟 Behavior.Zone 给的那几个一样。
    internal static readonly string[] Zones = ["head", "face", "hand", "skirt", "tail", "body"];
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal static Settings Load()
    {
        try
        {
            if (File.Exists(Places.SettingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(Places.SettingsFile)) is { } loaded)
                return loaded.Checked();
        }
        catch (Exception error) { Places.Log("设置读不出来，用默认的：" + error.Message); }
        return new Settings();
    }

    internal Settings Checked()
    {
        Shape = FishPet.Shape.Find(Shape).Id;
        Outfit = (Outfit ?? []).Where(item => FishPet.Outfit.Expression(item) != null).Select(item => FishPet.Outfit.Expression(item)!).Distinct().ToArray();
        if (!FpsChoices.Contains(Fps)) Fps = 60;
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, .5, 2);
        BubbleScale = Math.Clamp(double.IsFinite(BubbleScale) && BubbleScale > 0 ? BubbleScale : 1, .5, 2);
        BubbleSeconds = StayFor(BubbleSeconds);
        IdleSeconds = Math.Clamp(IdleSeconds, 15, 600);
        // local patch (设置面板): 只认 session-<uuid> 这种完整 id，其他当没填。
        // local patch (说话页): 上限 0–2000，负数当不限。
        SayLimit = SayLimit <= 0 ? 0 : Math.Clamp(SayLimit, 20, 2000);
        // local patch (置顶): 快捷键写法不认就退回 F6，别让它悄悄失效。
        PinHotkey = !string.IsNullOrWhiteSpace(PinHotkey) && Hotkey.TryParse(PinHotkey, out _, out _) ? PinHotkey.Trim() : "F6";
        SessionTarget = string.IsNullOrWhiteSpace(SessionTarget) || !System.Text.RegularExpressions.Regex.IsMatch(SessionTarget.Trim(), "^session-[0-9a-fA-F-]{8,}$")
            ? null : SessionTarget.Trim();
        // local patch (模型页): 记着的模型不在了就当没记；权重限制在 0–10。
        if (!string.IsNullOrWhiteSpace(ModelFile) && !File.Exists(ModelFile)) ModelFile = null;
        Idle ??= [];
        foreach (var (key, item) in Idle.ToArray())
        {
            if (item == null) { Idle.Remove(key); continue; }
            item.Weight = Math.Clamp(item.Weight, 0, 10);
        }
        // local patch (模型页): 只留认得的部位和像样的触发；别的当没设置。
        ClickTriggers ??= [];
        foreach (var (zone, trigger) in ClickTriggers.ToArray())
        {
            var clean = (trigger ?? "").Trim();
            bool usable = clean == "none" || System.Text.RegularExpressions.Regex.IsMatch(clean, "^(mood|exp|motion):.+$");
            if (!Zones.Contains(zone) || !usable) ClickTriggers.Remove(zone);
        }
        return this;
    }

    internal static int StayFor(int seconds) => seconds is -1 or 0 ? seconds : Math.Clamp(seconds, 5, 180);

    internal void Save()
    {
        try { File.WriteAllText(Places.SettingsFile, JsonSerializer.Serialize(this, Format)); }
        catch (Exception error) { Places.Log("设置存不下来：" + error.Message); }
    }
}

// local patch (置顶): 「固定屏幕顶部」的全局快捷键写法与解析。
// 不带修饰键时只认 F1–F24——单字母做全局热键会把平常打字的一个键白白吃掉。
internal static class Hotkey
{
    internal static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= 0x2; break;
                case "alt": modifiers |= 0x1; break;
                case "shift": modifiers |= 0x4; break;
                case "win":
                case "windows": modifiers |= 0x8; break;
                default: return false;
            }
        }
        if (Virtual(parts[^1]) is not { } vk) return false;
        if (modifiers == 0 && (vk < 0x70 || vk > 0x87)) return false;
        key = vk;
        return true;
    }

    internal static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        int code = (int)key;
        string main;
        if (code >= (int)Key.F1 && code <= (int)Key.F24) main = "F" + (code - (int)Key.F1 + 1);
        else if (code >= (int)Key.A && code <= (int)Key.Z) main = key.ToString();
        else if (code >= (int)Key.D0 && code <= (int)Key.D9) main = (code - (int)Key.D0).ToString();
        else main = key.ToString();
        parts.Add(main);
        return string.Join("+", parts);
    }

    private static uint? Virtual(string text)
    {
        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z') return 0x41 + (uint)(c - 'A');
            if (c is >= '0' and <= '9') return 0x30 + (uint)(c - '0');
            return null;
        }
        if (text.Length is 2 or 3 && char.ToUpperInvariant(text[0]) == 'F' && int.TryParse(text[1..], out var number) && number is >= 1 and <= 24)
            return 0x70 + (uint)(number - 1);
        return null;
    }
}
