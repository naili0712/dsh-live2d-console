using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FishPet;

// local patch (模型适配): 每个模型一份独立的适配档，存在插件目录 adapt\<模型名>-<hash>.json：空闲池
// 每一项的启停与权重，以及点她六个部位（头/脸/手/裙/尾巴/身上）分别演什么。内置的那套点击反应是
// 照着大肥鱼的表情和手势写的，换模型后要重新适配——设置里点「让 AI 调」，agent 用 adapt_model
// 写这份档；换回某个模型时自动套用它自己那份。
internal sealed class AdaptFile
{
    private ModelProfile? loaded;
    private string? forModel;

    // 当前模型的档；没有就是一张空档，改了由调用的人 Save。
    internal ModelProfile Current(string? model)
    {
        if (!string.Equals(forModel, model, StringComparison.OrdinalIgnoreCase))
        {
            forModel = model;
            loaded = Load(model);
        }
        return loaded ??= new ModelProfile { Model = model, Label = model == null ? null : Path.GetFileName(model) };
    }

    internal void Save()
    {
        if (forModel != null && loaded != null) Save(forModel, loaded);
    }

    // 文件被外面的程序（AI 或手改）动过时，让下一次 Current 重新读。
    internal void Invalidate() { forModel = null; loaded = null; }

    // 丢掉现在这个模型的档，回到没适配过的样子。
    internal void ResetCurrent()
    {
        if (forModel != null) Reset(forModel);
        forModel = null; loaded = null;
    }

    internal static string Folder => Path.Combine(Places.Root, "adapt");

    internal static string Location(string model) => Path.Combine(Folder, FileName(model));

    internal static bool Exists(string? model) => model != null && File.Exists(Location(model));

    internal static ModelProfile Load(string? model)
    {
        if (model == null) return new ModelProfile();
        try
        {
            string path = Location(model);
            if (File.Exists(path) && JsonSerializer.Deserialize<ModelProfile>(File.ReadAllText(path)) is { } profile)
            {
                profile.Model = model;
                profile.Clean();
                return profile;
            }
        }
        catch (Exception error) { Places.Log("模型适配读不出来，先当没有：" + error.Message); }
        return new ModelProfile { Model = model, Label = Path.GetFileName(model) };
    }

    internal static void Save(string model, ModelProfile profile)
    {
        try
        {
            profile.Model = model;
            profile.Label = Path.GetFileName(model);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Location(model), JsonSerializer.Serialize(profile, Format));
        }
        catch (Exception error) { Places.Log("模型适配存不下来：" + error.Message); }
    }

    internal static void Reset(string? model)
    {
        if (model == null) return;
        try { if (File.Exists(Location(model))) File.Delete(Location(model)); }
        catch (Exception error) { Places.Log("模型适配删不掉：" + error.Message); }
    }

    private static string FileName(string model)
    {
        string full = Path.GetFullPath(model);
        string name = Path.GetFileName(full);
        foreach (var suffix in new[] { ".model3.json", ".json" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length > 40) clean = clean[..40];
        if (clean.Length == 0) clean = "model";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
        return clean + "-" + hash + ".json";
    }

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    // 上一版把所有模型放在一个 model-adapt.json 里；第一次跑到新版时拆成每个模型一份。
    internal static void MigrateLegacy()
    {
        try
        {
            string legacy = Path.Combine(Places.Root, "model-adapt.json");
            if (!File.Exists(legacy)) return;
            if (JsonSerializer.Deserialize<LegacyFile>(File.ReadAllText(legacy)) is not { Models: { } models }) return;
            foreach (var (key, profile) in models)
            {
                string model = string.IsNullOrWhiteSpace(profile.Model) ? key : profile.Model;
                if (string.IsNullOrWhiteSpace(model) || File.Exists(Location(model))) continue;
                profile.Clean();
                Save(model, profile);
            }
            File.Move(legacy, legacy + ".migrated", true);
        }
        catch (Exception error) { Places.Log("旧的模型适配没能拆开：" + error.Message); }
    }

    private sealed class LegacyFile
    {
        public Dictionary<string, ModelProfile> Models { get; set; } = [];
    }
}

internal sealed class ModelProfile
{
    public int Version { get; set; } = 1;
    public string? Model { get; set; }
    public string? Label { get; set; }
    public DateTime? AdaptedAt { get; set; }
    public string? AdaptedBy { get; set; }
    // 空闲池：键 "mood:张望" / "exp:脸红" / "motion:脱捕"。
    public Dictionary<string, IdleSetting> Idle { get; set; } = [];
    // 点击部位 → "mood:摸头" / "exp:…" / "motion:…"；不设 = 原来的样子（原生模型）或不动（别的模型）。
    public Dictionary<string, string> Clicks { get; set; } = [];
    // local patch (组合动作): 点击部位 → 一组动作，可以并行（一起演）或连续（一个接一个）；
    // 配了组合的部位优先用组合，没配的用上面的单个触发。
    public Dictionary<string, ClickCombo> Combos { get; set; } = [];
    // local patch (外观差分): 这个模型上常驻的差分（外观页勾的那些），换模型各用各的。
    public List<string> Worn { get; set; } = [];

    // 六个部位都指到了才算真的适配完；只配了一部分就是「部分」。
    internal bool Adapted => Settings.Zones.All(zone => Clicks.ContainsKey(zone) || Combos.ContainsKey(zone));
    internal bool PartlyAdapted => !Adapted && Covered > 0;
    internal int Covered => Settings.Zones.Count(zone => Clicks.ContainsKey(zone) || Combos.ContainsKey(zone));

    internal void Clean()
    {
        Idle ??= []; Clicks ??= [];
        foreach (var (key, item) in Idle.ToArray())
        {
            if (item == null) { Idle.Remove(key); continue; }
            item.Weight = Math.Clamp(item.Weight, 0, 10);
        }
        foreach (var (key, value) in Clicks.ToArray())
            if (string.IsNullOrWhiteSpace(value) || !Settings.Zones.Contains(key)) Clicks.Remove(key);
        // local patch (外观差分): 去重、去空。
        Worn ??= [];
        Worn = Worn.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct().ToList();
        // local patch (组合动作): 组合里只留像样的条目，空的整组去掉。
        Combos ??= [];
        foreach (var (key, combo) in Combos.ToArray())
        {
            if (combo == null || !Settings.Zones.Contains(key)) { Combos.Remove(key); continue; }
            if (combo.Mode is not ("par" or "seq")) combo.Mode = "par";
            combo.Parts = (combo.Parts ?? []).Where(part => part != null && !string.IsNullOrWhiteSpace(part.Name)).ToList();
            foreach (var part in combo.Parts)
            {
                if (part.Kind is not ("exp" or "motion" or "mood")) part.Kind = "exp";
                part.Seconds = Math.Clamp(double.IsFinite(part.Seconds) ? part.Seconds : 3.5, .2, 30);
                // local patch (组合动作): 开始方式：跟随模式 / 立即 / 等上一步 / 延迟几秒
                if (part.Start is not ("" or "now" or "after" or "delay")) part.Start = "";
                part.Delay = Math.Clamp(double.IsFinite(part.Delay) ? part.Delay : 0, 0, 30);
            }
            if (combo.Parts.Count == 0) Combos.Remove(key);
        }
    }
}

// local patch (组合动作): 一个点击部位的一组动作。Mode 是 "par"（并行，一起演）或 "seq"（连续，
// 一个接一个）；Parts 按顺序演，Seconds 是这一步占多久（连续时的等待，并行时差分亮多久）。
internal sealed class ClickCombo
{
    public string Mode { get; set; } = "par";
    public List<ClickPart> Parts { get; set; } = [];

    internal ClickCombo Copy() => new() { Mode = Mode, Parts = Parts.Select(part => new ClickPart { Kind = part.Kind, Name = part.Name, Seconds = part.Seconds, Start = part.Start, Delay = part.Delay }).ToList() };
}

internal sealed class ClickPart
{
    public string Kind { get; set; } = "exp";
    public string Name { get; set; } = "";
    public double Seconds { get; set; } = 3.5;
    // local patch (组合动作): 这一条什么时候开始——"" 跟随模式（并行就一起、连续就等上一步），
    // "now" 立即，"after" 等上一步演完，"delay" 组合开始后 Delay 秒。
    public string Start { get; set; } = "";
    public double Delay { get; set; }
}
