using System;
using System.IO;
using System.Linq;

namespace FishPet;

// The model's three body types. Fractions are of the frame: HeadTop is the top of her hair, HeadAt her
// eyes, FootAt her soles, Side the margin left and right of her body. FeetY is where her soles are on the
// model and Reach how far she travels when she jumps. The frame leaves her room to jump.
internal sealed record Shape(string Id, string Label, string? Body, float Zoom, float CenterY,
    int Width, int Height, double HeadTop, double HeadAt, double FootAt, double Side, float FeetY, float Reach)
{
    internal static readonly Shape[] All =
    {
        new("small", "小只", "体型1", 1.626f, .319f, 340, 620, .106, .241, .991, .15, -.2845f, 1),
        new("chibi", "Q 版", "体型2", 3.4f, .53f, 300, 300, .09, .42, .87, .22, .3124f, .45f),
        new("full", "全身", null, 1, 0, 340, 680, .11, .2, .96, .15, -.92f, 1.7f),
    };
    internal static Shape Find(string? id) => All.FirstOrDefault(shape => shape.Id == id) ?? All[0];
}

// What she wears stays on until it is changed, unlike her faces: label, then the model's expression.
internal static class Outfit
{
    internal static readonly (string Label, string Expression)[] Clothes =
    {
        ("长裙", "裙子切换"), ("胸口衣服", "胸口衣服"), ("脱鞋", "脱鞋"), ("收起尾巴", "尾巴开关"),
    };
    internal static readonly (string Label, string Expression)[] Accessories =
    {
        ("黑框眼镜", "黑框眼镜"), ("单片眼镜", "单片眼镜"), ("钢盆", "钢盆"), ("兔牙", "兔牙"),
    };
    internal static string? Expression(string label) =>
        Clothes.Concat(Accessories).FirstOrDefault(item => item.Label == label || item.Expression == label).Expression;
}

// local patch (模型页): 一项空闲触发在设置里的样子——开不开、权重多大（0 等于不触发）。
internal sealed class IdleSetting
{
    public bool On { get; set; } = true;
    public int Weight { get; set; } = 1;
}

// local patch (模型页): 模型自带的差分和动作，按渲染器认的文件夹读（renderer/main.cpp 只读 Exp3 和 Animation）。
internal static class ModelParts
{
    internal static string[] Expressions(string modelFile) => Names(modelFile, "Exp3", ".exp3.json");
    // IDLE 是渲染器自己循环的待机动作，不放进空闲触发里。
    internal static string[] Motions(string modelFile) => Names(modelFile, "Animation", ".motion3.json").Where(name => name != "IDLE").ToArray();

    // local patch (模型适配): 动作播多久——试看或空闲放完以后按它对时间回 IDLE。
    internal static double Duration(string modelFile, string name)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(modelFile) ?? "", "Animation", name + ".motion3.json");
            if (!File.Exists(path)) return 3;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("Meta", out var meta) && meta.TryGetProperty("Duration", out var duration) && duration.TryGetDouble(out var seconds))
                return Math.Clamp(seconds, 1, 60);
        }
        catch { }
        return 3;
    }

    private static string[] Names(string modelFile, string folder, string suffix)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(modelFile) ?? "", folder);
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*" + suffix)
                .Select(Path.GetFileName).OfType<string>()
                .Select(name => name[..^suffix.Length])
                .Order().ToArray();
        }
        catch { return []; }
    }
}

// Where things are. The plugin folder holds bin\ (this program), renderer\build\Release (the renderer each
// user builds against their own Cubism SDK) and model\ (the model each user downloads from its author).
internal static class Places
{
    // local patch (模型页): 默认找的模型；找不到它就找 model\ 里的第一个 .model3.json。
    internal const string ModelFile = "大肥鱼.model3.json";
    internal static string Root { get; } = FindRoot();
    internal static string Renderer => Path.Combine(Root, "renderer", "build", "Release", "l2d-render.exe");
    internal static string Cache => Path.Combine(Root, "cache");
    // A test keeps its settings apart (--settings).
    internal static string SettingsFile { get; set; } = Path.Combine(Root, "settings.json");
    internal static string ErrorLog => Path.Combine(Root, "errors.log");
    // local patch (说话页): 她说过的每一句，一行一条 JSON。
    internal static string SayHistory => Path.Combine(Cache, "say-history.jsonl");
    // Where the HTTP interface listens and its token (HttpApi); a test keeps its own (--api).
    internal static string ApiFile { get; set; } = Path.Combine(Root, "api.json");

    // The model wherever in model\ it was unpacked, or where --model says. local patch (模型页): 设
    // 置里选过的模型优先于默认找的那个。
    internal static string? Model(string? given, string? saved = null)
    {
        if (given != null) return File.Exists(given) ? Path.GetFullPath(given) : null;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return Path.GetFullPath(saved);
        var folder = Path.Combine(Root, "model");
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, ModelFile, SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(folder, "*.model3.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    // local patch (模型页): 设置页里能挑的模型——model\ 里每一个 .model3.json。
    internal static string[] Models()
    {
        var folder = Path.Combine(Root, "model");
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateFiles(folder, "*.model3.json", SearchOption.AllDirectories).Order().ToArray(); }
        catch { return []; }
    }

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "renderer")) && Directory.Exists(Path.Combine(folder.FullName, "model"))) return folder.FullName;
        return AppContext.BaseDirectory;
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(ErrorLog, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
    }
}
