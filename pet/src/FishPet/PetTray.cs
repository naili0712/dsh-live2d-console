using System;
using System.Drawing;
using System.Windows.Forms;

namespace FishPet;

// local patch (托盘): 她收起来以后也有个入口——双击图标叫她出来，右键能真退出。
internal sealed class PetTray : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Action show;

    internal PetTray(Action show, Action settings, Action quit)
    {
        this.show = show;
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("叫她出来", null, (_, _) => show());
        menu.Items.Add("设置…", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => quit());
        icon = new NotifyIcon
        {
            Icon = ProgramIcon(),
            Text = "dshlive2d控制台",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => show();
    }

    // 收起来时提示双击；别的时候显示她在做什么。
    internal void Hint(string text) => icon.Text = text.Length > 63 ? text[..63] : text;

    // 图标就是 exe 自带的那张（ApplicationIcon 编进去的），不再多带一个文件。
    private static Icon ProgramIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
