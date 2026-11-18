using System;
using System.IO;
using System.Linq;
using System.Text.Json;

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
