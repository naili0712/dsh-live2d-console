using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FishPet;

// local patch (说话页): 一次 say 的字数上限，和她说过的每一句——设置面板「说话与交流」里看。
internal static class Talk
{
    // 数一句里有多少可见字：〔〕里的神情动作不算。
    internal static int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '〔')
            {
                var end = text.IndexOf('〕', i);
                if (end > i) { i = end; continue; }
            }
            seen++;
        }
        return seen;
    }

    // 太长的一句排队给桥：由它用模型压短，再写回 say.txt 让她说。
    internal static void Queue(string text, int limit)
    {
        try
        {
            Directory.CreateDirectory(Places.Cache);
            File.WriteAllText(Path.Combine(Places.Cache, "say-queue.json"),
                JsonSerializer.Serialize(new { limit, text, at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));
        }
        catch (Exception error) { Places.Log("排队台词失败：" + error.Message); }
    }

    // 记一句：什么时候、说了什么、几个气泡。
    internal static void Note(string text, int bubbles)
    {
        try
        {
            Directory.CreateDirectory(Places.Cache);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                text = Body.Strip(text),
                bubbles,
            });
            File.AppendAllText(Places.SayHistory, line + Environment.NewLine);
            Trim();
        }
        catch (Exception error) { Places.Log("记台词失败：" + error.Message); }
    }

    // 只留最近两千行，免得文件一直长。
    private static void Trim()
    {
        try
        {
            var lines = File.ReadAllLines(Places.SayHistory);
            if (lines.Length <= 2000) return;
            File.WriteAllLines(Places.SayHistory, lines[^2000..]);
        }
        catch { }
    }

    internal sealed record Entry(string At, string Text, int Bubbles);

    // 最近 count 条，新的在前。
    internal static IReadOnlyList<Entry> Recent(int count)
    {
        try
        {
            if (!File.Exists(Places.SayHistory)) return [];
            var lines = File.ReadAllLines(Places.SayHistory);
            var list = new List<Entry>();
            foreach (var line in lines.Reverse())
            {
                if (list.Count >= count) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonNode.Parse(line) is not { } node) continue;
                    list.Add(new Entry(
                        node["at"]?.GetValue<string>() ?? "",
                        node["text"]?.GetValue<string>() ?? "",
                        node["bubbles"]?.GetValue<int>() ?? 1));
                }
                catch { }
            }
            return list;
        }
        catch { return []; }
    }
}
