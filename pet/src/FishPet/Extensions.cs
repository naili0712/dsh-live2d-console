using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

/// <summary>装着的扩展包，和它们带来的名字。</summary>
internal static class Extensions
{
    private sealed class State
    {
        public Dictionary<string, bool> Off { get; set; } = [];
    }

    private const string PackFile = "pack.json";
    private const string StateFile = ".state.json";
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Folder => Path.Combine(Places.Root, "extensions");
    private static string StatePath => Path.Combine(Folder, StateFile);

    internal static IReadOnlyList<ExtensionPack> All { get; private set; } = [];
    internal static IReadOnlyDictionary<string, ExtensionWord> Words { get; private set; } = new Dictionary<string, ExtensionWord>();
    internal static IReadOnlyDictionary<string, string> Expansions { get; private set; } = new Dictionary<string, string>();
    internal static IReadOnlyDictionary<string, string> ExpansionSource { get; private set; } = new Dictionary<string, string>();
    internal static IReadOnlyDictionary<string, ExtensionMood> Moods { get; private set; } = new Dictionary<string, ExtensionMood>();
    internal static IReadOnlyDictionary<string, string> WordSource { get; private set; } = new Dictionary<string, string>();
    internal static IReadOnlyDictionary<string, string> MoodSource { get; private set; } = new Dictionary<string, string>();
    internal static IReadOnlyDictionary<string, string> Skipped { get; private set; } = new Dictionary<string, string>();
    internal static string? LastError { get; private set; }

    internal static bool Any => All.Any(pack => pack.Enabled);

    internal static string PackName(string id) => All.FirstOrDefault(pack => pack.Id == id)?.Title ?? id;

    /// <summary>现在对着哪个模型；本地适配层是按模型认的。</summary>
    internal static string? CurrentModel { get; private set; }

    internal static string AdaptLocation(ExtensionPack pack) => Path.Combine(pack.Folder, "adapt.json");

    /// <summary>重读 extensions 目录。装了什么、哪个包出事、谁的名字被谁占了，都记下来。</summary>
    internal static void Reload(string? model = null)
    {
        if (model != null) CurrentModel = model;
        var packs = new List<ExtensionPack>();
        var words = new Dictionary<string, ExtensionWord>();
        var expansions = new Dictionary<string, string>();
        var expansionFrom = new Dictionary<string, string>();
        var moods = new Dictionary<string, ExtensionMood>();
        var wordFrom = new Dictionary<string, string>();
        var moodFrom = new Dictionary<string, string>();
        var skipped = new Dictionary<string, string>();
        string? trouble = null;
        Dictionary<string, bool> off;
        try
        {
            off = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Read)?.Off ?? [] : [];
        }
        catch (Exception error) { trouble = "读扩展状态失败：" + error.Message; off = []; }

        try
        {
            if (Directory.Exists(Folder))
            {
                foreach (var folder in Directory.EnumerateDirectories(Folder).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string file = Path.Combine(folder, PackFile);
                    if (!File.Exists(file)) continue;
                    ExtensionPack? pack = null;
                    try { pack = JsonSerializer.Deserialize<ExtensionPack>(File.ReadAllText(file), Read); }
                    catch (Exception error) { pack = new ExtensionPack { Id = Path.GetFileName(folder), Folder = folder, Error = "读 pack.json 失败：" + error.Message }; }
                    if (pack == null) continue;
                    pack.Folder = folder;
                    if (string.IsNullOrWhiteSpace(pack.Id)) pack.Id = Path.GetFileName(folder);
                    pack.Enabled = !(off.TryGetValue(pack.Id, out var isOff) && isOff);
                    if (pack.Error == null) LoadLines(pack);
                    LoadAdapt(pack, CurrentModel);
                    packs.Add(pack);
                    if (pack.Error != null || !pack.Enabled) continue;
                    // 适配层说这个模型做不了的就跳过了；没列出来的照原样装。
                    var disabled = new HashSet<string>(pack.Adapt?.Disabled ?? [], StringComparer.Ordinal);
                    foreach (var word in pack.Words ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(word.Word) || disabled.Contains(word.Word)) continue;
                        if (!string.IsNullOrWhiteSpace(word.Expand))
                        {
                            if (expansions.ContainsKey(word.Word)) { skipped[word.Word] = pack.Id; continue; }
                            expansions[word.Word] = word.Expand; expansionFrom[word.Word] = pack.Id;
                            continue;
                        }
                        if (words.ContainsKey(word.Word)) { skipped[word.Word] = pack.Id; continue; }
                        words[word.Word] = word; wordFrom[word.Word] = pack.Id;
                    }
                    foreach (var mood in pack.Moods ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(mood.Name) || disabled.Contains(mood.Name)) continue;
                        if (moods.ContainsKey(mood.Name)) { skipped[mood.Name] = pack.Id; continue; }
                        moods[mood.Name] = mood; moodFrom[mood.Name] = pack.Id;
                    }
                    // 本地适配层：只对着这个模型时生效，覆盖原包的词、补上它没有的。
                    if (pack.AdaptedFor && pack.Adapt is { } adapted)
                    {
                        foreach (var word in adapted.Words ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(word.Word)) continue;
                            if (!string.IsNullOrWhiteSpace(word.Expand)) { expansions[word.Word] = word.Expand; expansionFrom[word.Word] = pack.Id; continue; }
                            words[word.Word] = word; wordFrom[word.Word] = pack.Id;
                        }
                        foreach (var mood in adapted.Moods ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(mood.Name)) continue;
                            moods[mood.Name] = mood; moodFrom[mood.Name] = pack.Id;
                        }
                    }
                }
            }
        }
        catch (Exception error) { trouble = "扫扩展目录失败：" + error.Message; }

        All = packs;
        Words = words; Moods = moods;
        Expansions = expansions; ExpansionSource = expansionFrom;
        WordSource = wordFrom; MoodSource = moodFrom;
        Skipped = skipped;
        LastError = trouble;
        if (trouble != null) Places.Log("扩展包：" + trouble);
    }

    // 本地适配层：extensions\<id>\adapt.json，按模型认——换了模型这份就不算，要重新请 AI 适配。
    private static void LoadAdapt(ExtensionPack pack, string? model)
    {
        try
        {
            string path = AdaptLocation(pack);
            if (!File.Exists(path)) return;
            pack.Adapt = JsonSerializer.Deserialize<ExtensionAdapt>(File.ReadAllText(path), Read);
            pack.AdaptedFor = pack.Adapt?.Model is { Length: > 0 } intended && model is { Length: > 0 } &&
                string.Equals(Path.GetFullPath(intended), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) { pack.Error ??= "读 adapt.json 失败：" + error.Message; }
    }

    private static void LoadLines(ExtensionPack pack)
    {
        foreach (var lines in pack.Lines ?? [])
        {
            if (string.IsNullOrWhiteSpace(lines.File)) continue;
            if (string.IsNullOrWhiteSpace(lines.Label)) lines.Label = lines.File;
            try
            {
                string path = Path.Combine(pack.Folder, lines.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { pack.Error ??= "缺台词文件：" + lines.File; continue; }
                foreach (var raw in File.ReadAllLines(path))
                {
                    var text = raw.Trim();
                    if (text.Length == 0 || text.StartsWith('#')) continue;
                    lines.Items.Add(text);
                }
            }
            catch (Exception error) { pack.Error ??= "读台词文件失败：" + lines.File + "：" + error.Message; }
        }
    }

    /// <summary>装/停一个包（不改文件，只记状态）。</summary>
    internal static void SetEnabled(string id, bool on)
    {
        var state = new State();
        try
        {
            if (File.Exists(StatePath)) state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Read) ?? new State();
        }
        catch { state = new State(); }
        if (on) state.Off.Remove(id); else state.Off[id] = true;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Write));
        }
        catch (Exception error) { Places.Log("扩展状态写不进去：" + error.Message); }
        Reload(CurrentModel);
    }

    /// <summary>扩展带来的台词，按种类合并。</summary>
    internal static List<string> LinesOf(string kind) =>
        All.Where(pack => pack.Enabled && pack.Error == null)
           .SelectMany(pack => pack.Lines ?? [])
           .Where(lines => string.Equals(lines.Kind, kind, StringComparison.OrdinalIgnoreCase))
           .SelectMany(lines => lines.Items).ToList();
}
