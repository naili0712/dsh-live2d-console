using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace FishPet;

// Starts her once per user: a second start (the MCP bridge starting her while she is already up) just ends.
//   --model <大肥鱼.model3.json>  the model somewhere else than model\
//   --test --pipe <name> --settings <file> --api <file>  out of sight, on a pipe, settings and HTTP port of its own (FishPet.Mcp --check)
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name) { int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        bool test = args.Contains("--test");
        if (Option("--settings") is { } file) Places.SettingsFile = file;
        if (Option("--api") is { } apiFile) Places.ApiFile = apiFile;
        using var single = new Mutex(true, "FishPet.Single." + Tools.PipeName, out bool first);
        // local patch (托盘): 已经有一个在（多半收在托盘里）——叫它出来，然后这次启动自己退出。
        if (!first && !test)
        {
            try { EventWaitHandle.OpenExisting("FishPet.Show." + Tools.PipeName).Set(); } catch { }
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Places.Log("出错了：" + e.Exception); e.Handled = true; };
        // local patch (模型页): 设置里选过的模型也要用上
        var settings = Settings.Load();
        // local patch (扩展包): 先读 extensions 目录再让她长出来——动作词和表情在构造时就定型。
        Extensions.Reload();
        Body.UseExtraWords(Extensions.Words, Extensions.Expansions);
        Behavior.UseExtraMoods(Extensions.Moods);
        new PetWindow(settings, Places.Model(Option("--model"), settings.ModelFile), test, Option("--pipe")).Show();
        return app.Run();
    }
}
