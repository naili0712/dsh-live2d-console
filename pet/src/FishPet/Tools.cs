using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FishPet;

// What an agent can do with her, as MCP tools. The descriptions are what the agent reads: plain and complete.
internal static class Tools
{
    internal static string PipeName => "FishPet." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "|" +
        System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/').ToUpperInvariant()))).Substring(0, 20);
    internal static object Text(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } };
    internal static object Error(string message) => new { isError = true, content = new[] { new { type = "text", text = message } } };
    // A result with one more piece of text after what it already says.
    internal static object WithNotice(object result, string notice)
    {
        var node = JsonSerializer.SerializeToNode(result)!.AsObject();
        if (node["content"] is System.Text.Json.Nodes.JsonArray content) content.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = notice });
        return node;
    }
    internal static object Image(string dataUrl, object metadata) => new { content = new object[] {
        new { type = "text", text = JsonSerializer.Serialize(metadata) },
        new { type = "image", mimeType = dataUrl[5..dataUrl.IndexOf(';')], data = dataUrl[(dataUrl.IndexOf(',') + 1)..] } } };

    private static string Labels((string Label, string Expression)[] items) => string.Join("、", items.Select(item => item.Label));
    // Where the agent finds the words for its body: the descriptions name it in full, wherever the plugin was put.
    internal static string BodyGuide => System.IO.Path.Combine(Places.Root, "prompt", "身体说明.md");
    // The guide's version, so an agent whose persona holds an older copy sees it in the say description (2026-11-05).
    internal static string? GuideVersion => System.IO.File.Exists(BodyGuide) ? Body.GuideVersion(System.IO.File.ReadAllText(BodyGuide)) : null;
    private static string GuideNote => GuideVersion is { } version ? "身体说明现在是 " + version + " 版；你人设里【你的身体·完】那行写的版本对不上或者没有，就是旧版：告诉用户，照 " +
        System.IO.Path.Combine(Places.Root, "AGENTS.md") + " 最后“更新插件”的第 6 步换成新版。" : "";

    // Said to the agent as its own body, not a figure it works: told about "her", agents only moved her when asked to.
    internal static object[] Definitions => [
        Tool("say", "用你在桌面上的身体说话（头顶显示气泡）。每次回复用户都应使用本工具，无需用户提醒。" +
            "一次调用可说一句或数句：一行一个气泡，多行依次显示；不要在说完一句后再次调用继续说。text 中用〔〕书写神情与动作（用词与规则见 " + BodyGuide + "），〔〕不会显示，身体按此执行；说之前先在心里演一遍，在神情动作发生处写〔〕；通常只写在句首，整句身体只随之动一下。" +
            "字数限制：一次调用全部可见字数合计不得超过上限（默认 90 字，以设置页为准，多行合并计算，〔〕内文字不计入）。超出时本次调用直接失败并返回字数提示，不会自动压缩或截断，请拆分为多次调用，每次一句。" +
            "文字按说话节奏逐字出现，遇到〔〕先做动作，动作随其后文字的时长与语气伸缩。audio 可选：本句语音（本机 wav 文件完整路径），播放一次，口型随音量变化，〔〕按语音进度执行；有语音时整段为一个气泡。" +
            "本次会等待之前的语句演完再开始；返回时第一句已开始播放。返回中的 note 为身体反馈（哪些动作词未能执行、是否需要边说边动）。" + GuideNote,
            """{"text":{"type":"string","minLength":1,"maxLength":2000},"audio":{"type":"string","maxLength":1000}}""", ["text"]),
        Tool("look_in_mirror", "在镜子中查看你当前的样子（白底图，仅头部到腰部）。try 可选：填写想试的神情与动作（〔〕中的词，无需括号），" +
            "会先实际执行一遍，按先后返回六张图（三列两行，从左到右、从上到下）。", """{"try":{"type":"string","maxLength":200}}""", []),
        Tool("look_back", "回看你刚才说话时的样子：say 的每句话都会自动拍摄，按先后取六张，保留半小时、最多 20 句。" +
            "which 可选：1 为最近一句，2 为再前一句，依此类推；不填默认最近一句。返回包含该句内容（said）、距今秒数（secondsAgo）与可回看列表（recent）。",
            """{"which":{"type":"integer","minimum":1,"maximum":20}}""", []),
        Tool("set_activity", "标记一项需要专注处理的任务（看视频、读文章、写东西等；kind 为任务名称，可自定义）。任务进行中会佩戴黑框眼镜，全部完成后摘下；" +
            "用户已为你佩戴眼镜时不重复佩戴，也不摘下。", """{"kind":{"type":"string","minLength":1,"maxLength":40},"active":{"type":"boolean"}}""", ["kind", "active"]),
        Tool("dress", "更换服装、佩戴或摘下配饰，设置会持续保持，下次启动仍然生效。服装：" + Labels(Outfit.Clothes) + "；配饰：" + Labels(Outfit.Accessories) +
            "（兔牙仅在张嘴时可见）。wear 为需要穿戴的项目，remove 为需要摘下的项目。返回当前穿戴结果。",
            """{"wear":{"type":"array","items":{"type":"string"}},"remove":{"type":"array","items":{"type":"string"}}}""", []),
        Tool("get_state", "读取当前状态与设置：体型、穿戴、帧率、大小、气泡大小与停留时间、全屏时是否隐藏（当前是否已隐藏）、视线是否跟随鼠标、空闲动作间隔、窗口位置、是否正在说话；" +
            "插件有新版本时，update 中给出落后项数量与各项说明。", "{}"),
        Tool("update_settings", "修改设置，仅修改传入的项并保存。shape 体型：small（小只，默认）、chibi（Q 版）、full（全身）；fps 帧率：30、60、90、120；" +
            "scale 大小 0.5–2；bubble_scale 气泡大小 0.5–2（文字与气泡同步缩放，与 scale 无关）；bubble_seconds 气泡在说完后停留时间：0 为按字数自动（5–20 秒，默认），" +
            "5–180 为固定秒数，-1 为保留到下一句或点击关闭；hide_on_fullscreen 其他程序全屏（游戏、视频）时隐藏且不渲染，默认开启；" +
            "follow_mouse 视线跟随鼠标；idle_seconds 无交互时执行小动作的间隔（15–600 秒）；check_updates 启动时及每 12 小时检查 GitHub 是否有新版本，默认开启。",
            """{"shape":{"type":"string","enum":["small","chibi","full"]},"fps":{"type":"integer","enum":[30,60,90,120]},"scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_scale":{"type":"number","minimum":0.5,"maximum":2},"bubble_seconds":{"type":"integer","minimum":-1,"maximum":180},"hide_on_fullscreen":{"type":"boolean"},"follow_mouse":{"type":"boolean"},"lock_position":{"type":"boolean"},"idle_seconds":{"type":"integer","minimum":15,"maximum":600},"check_updates":{"type":"boolean"}}""", []),
        // local patch (桌上对话): user_says = 用户在桌上输入框里打给她的那句话。
        // local patch (设置面板): 让 agent 也能替用户把设置打开
        Tool("open_settings", "打开角色设置面板（包含体型、大小、气泡、帧率、启动与会话指向等选项）。", "{}"),
        // local patch (模型适配): 让 agent 给换上的模型做适配
        Tool("adapt_model", "查看或修改当前模型的适配配置（每个模型一份，保存在插件 adapt 文件夹）。read 返回该模型可用的差分、动作、六个点击部位与当前适配；" +
            "write 通过 clicks 为六个点击部位（head 头、face 脸、hand 手、skirt 裙、tail 尾巴、body 身上）指定该模型自身的差分或动作，空闲权重（idle）可同时写入；reset 删除该模型的适配配置。" +
            "更换模型或用户点击「让 AI 调」时使用。未配置的部位点击后不会产生反应。clicks 的取值为 \"exp:差分名\"、\"motion:动作名\" 或 \"none\"；" +
            "combos 可为部位配置一组动作：{\"head\":{\"mode\":\"par\",\"parts\":[{\"kind\":\"exp\",\"name\":\"脸红\",\"seconds\":3.5,\"start\":\"\"},{\"kind\":\"motion\",\"name\":\"脱捕\",\"seconds\":0,\"start\":\"after\"}]}}；mode 为 par（默认同时）或 seq（默认依次），每条可用 start 指定开始方式：\"\" 跟随模式、\"now\" 立即、\"after\" 等上一步、\"delay\" 延迟 delay 秒——多个动作同时请求容易卡顿，建议用 after/delay 错开；" +
            "worn 为常驻差分（外观页选中的项目，字符串数组，从该模型自身的差分中选择）；" +
            "idle 的键为 \"mood:名字\"/\"exp:名字\"/\"motion:名字\"，值为 {\"on\":true,\"weight\":1}。",
            """{"action":{"type":"string","enum":["read","write","reset"]},"clicks":{"type":"object"},"combos":{"type":"object"},"worn":{"type":"array"},"idle":{"type":"object"}}""", ["action"]),
        // local patch (扩展包): 让 agent 给装进来的扩展包写本地适配层
        Tool("adapt_pack", "为指定扩展包编写本地适配层（extensions\\<id>\\adapt.json，不修改原扩展包）。read 返回该扩展包的全部动作词与表情、当前模型可用的差分与动作、现有适配层；" +
            "write 用 words 写入该模型能够实现的版本（结构与 pack.json 一致：word/expand/channel/hold/overlay/move/clear/reset/handOn/handOff/handDown，同名覆盖原扩展包），" +
            "无法实现的名称放入 disabled（字符串数组），notes 说明修改原因与停用原因；reset 删除适配层。" +
            "用户在设置中点击「扩展适配」，或扩展包中的动作无法实现时使用。参数名从模型自身的 .cdi3.json 中获取；未提供 id 时使用最近一次请求的扩展包。",
            """{"action":{"type":"string","enum":["read","write","reset"]},"id":{"type":"string"},"words":{"type":"array"},"moods":{"type":"array"},"disabled":{"type":"array"},"notes":{"type":"string"}}""", ["action"]),
        Tool("recent_events", "用户近期对你进行的交互：点击部位（zone：head 头、face 脸、hand 手、skirt 裙子、tail 尾巴、body 身上）、你的反应（reaction）、拖动与放下；" +
            "user_says 为用户在角色头顶输入框中发送的内容（detail.text），建议及时回应。" +
            "after 可选：仅返回该序号之后的事件。身体会自行执行反应，此处仅用于告知；是否回应由你决定。", """{"after":{"type":"integer","minimum":0}}""", []),
        Tool("quit", "鞠躬告别并收起你的身体（关闭桌宠）。用户要求收起时使用；收起后仅在聊天窗口回话，下次调用任何工具时会自动重新出现。", "{}"),
    ];

    private static object Tool(string name, string description, string properties, string[]? required = null) => new {
        name, description, inputSchema = new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties),
            required = required ?? [], additionalProperties = false } };

    internal static string RequiredText(JsonElement args, string name, int maximum = 2000)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} 要是 1–{maximum} 个字的文字。");
        return value.GetString()!;
    }

    internal static string? OptionalText(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    internal static string[] Texts(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).ToArray() : [];
}
