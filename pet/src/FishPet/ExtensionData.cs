using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FishPet;

// local patch (扩展包): 一份从外面装进来的东西——动作词（〔〕里能写的）、表情（点她和闲着时演的）、
// 台词（内容库）。一个包就是一个文件夹 extensions\<id>\pack.json。
// 原则：内置那套永远优先，扩展只补它没占的名字；两个扩展抢同一个名字，按 id 排序先装上的赢。

/// <summary>一个通道值：参数名 + 数值。</summary>
internal sealed class ExtensionHold
{
    public string Id { get; set; } = "";
    public double Value { get; set; }
}

/// <summary>一条动作轨：参数或 View* / FaceAngle* 的名字 + 时间轴键。</summary>
internal sealed class ExtensionTrack
{
    public string Id { get; set; } = "";
    public string Keys { get; set; } = "";
}

/// <summary>〔〕里的一个词。</summary>
internal sealed class ExtensionWord
{
    public string Word { get; set; } = "";
    /// <summary>组合词：这个词展开成这几个基础词一起做（「高兴」＝张嘴 眯眼笑 晃头），和下面那些二选一。</summary>
    public string? Expand { get; set; }
    public string? Channel { get; set; }
    public List<ExtensionHold>? Hold { get; set; }
    public string? Overlay { get; set; }
    public List<ExtensionTrack>? Move { get; set; }
    public List<string>? Clear { get; set; }
    public bool Reset { get; set; }
    public string? HandOn { get; set; }
    public string? HandOff { get; set; }
    public bool HandDown { get; set; }
}

/// <summary>点她/闲着时演的一个表情小动作。</summary>
internal sealed class ExtensionMood
{
    public string Name { get; set; } = "";
    public List<string>? Expressions { get; set; }
    public List<ExtensionHold>? Parameters { get; set; }
    public double Seconds { get; set; } = 3;
    public List<ExtensionTrack>? Motion { get; set; }
    public string? GestureOn { get; set; }
    public string? GestureOff { get; set; }
    public bool Pushed { get; set; }
}

/// <summary>内容库里的一份台词。</summary>
internal sealed class ExtensionLines
{
    public string Kind { get; set; } = "mutter";
    public string Label { get; set; } = "";
    public string File { get; set; } = "";
    [JsonIgnore] public List<string> Items { get; } = [];
    [JsonIgnore] public int Count => Items.Count;
}

/// <summary>本地适配层（extensions\<id>\adapt.json）：包按发布时间写死了参数，换了模型就得有人把
/// 它翻成这个模型做得到的。这一层不改原包，只覆盖、停用、补充。</summary>
internal sealed class ExtensionAdapt
{
    /// <summary>适配的是哪个模型（完整路径），换了模型这份就作废，重新请 AI 适配。</summary>
    public string? Model { get; set; }
    public string? AdaptedAt { get; set; }
    public string? AdaptedBy { get; set; }
    public string? Notes { get; set; }
    /// <summary>覆盖或补充的词：同名覆盖原包，新的直接加上。</summary>
    public List<ExtensionWord>? Words { get; set; }
    public List<ExtensionMood>? Moods { get; set; }
    /// <summary>这个模型做不了的词，列在这里就不装（原包不被改）。</summary>
    public List<string>? Disabled { get; set; }
}

/// <summary>一个扩展包。</summary>
internal sealed class ExtensionPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string? Source { get; set; }
    public string? Describe { get; set; }
    public List<ExtensionWord>? Words { get; set; }
    public List<ExtensionMood>? Moods { get; set; }
    public List<ExtensionLines>? Lines { get; set; }

    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public bool Enabled { get; set; } = true;
    [JsonIgnore] public string? Error { get; set; }
    /// <summary>本地适配层，没适配过就是 null。</summary>
    [JsonIgnore] public ExtensionAdapt? Adapt { get; set; }
    /// <summary>这份适配是不是给现在这个模型写的（换了模型就不算）。</summary>
    [JsonIgnore] public bool AdaptedFor { get; set; }
    [JsonIgnore] public int AdaptedWords => Adapt?.Words?.Count ?? 0;
    [JsonIgnore] public int DisabledWords => Adapt?.Disabled?.Count ?? 0;
    [JsonIgnore] public int WordCount => Words?.Count(word => string.IsNullOrWhiteSpace(word.Expand)) ?? 0;
    [JsonIgnore] public int ComboCount => Words?.Count(word => !string.IsNullOrWhiteSpace(word.Expand)) ?? 0;
    [JsonIgnore] public int MoodCount => Moods?.Count ?? 0;
    [JsonIgnore] public int LineCount => Lines?.Sum(line => line.Count) ?? 0;

    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
