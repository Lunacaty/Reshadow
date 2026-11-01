using Godot;

namespace ShadowverseFan.Ui;

/// <summary>
/// 开发期截图辅助：延迟若干帧后把当前视口存成 PNG 并退出。
///
/// 只在命令行带 --shot=&lt;path&gt; 时生效，正常运行完全不受影响。
/// 存在的理由：这个仓库没有 Godot 编辑器的手动截图流程，
/// 而「UI 到底长什么样」必须眼见为实，不能只靠编译通过。
/// </summary>
public partial class DevShot : Node
{
    private string _path;
    private int _frames;
    private bool _openBuilder;
    private bool _openWizard;
    private bool _openHover;
    private bool _openBattle;
    private bool _battleExpanded;
    private bool _battleBadges;
    private bool _battleMulligan;
    private bool _openDiy;
    private bool _openPackMgr;

    public override void _Ready()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--shot=")) _path = a.Substring(7);
            if (a.StartsWith("--frames=")) int.TryParse(a.Substring(9), out _frames);
            if (a == "--builder") _openBuilder = true;
            if (a == "--wizard") _openWizard = true;
            if (a == "--hover") _openHover = true;
            if (a == "--battle") _openBattle = true;
            if (a == "--battle-expanded") { _openBattle = true; _battleExpanded = true; }
            if (a == "--battle-badges") { _openBattle = true; _battleBadges = true; }
            if (a == "--mulligan") { _openBattle = true; _battleMulligan = true; }
            if (a == "--diy") _openDiy = true;
            if (a == "--packmgr") _openPackMgr = true;
        }
        if (_frames <= 0) _frames = 30;
        SetProcess(true);

        if (_openBuilder)
            CallDeferred(nameof(OpenBuilder));
        else if (_openWizard)
            CallDeferred(nameof(OpenWizard));
        else if (_openHover)
            CallDeferred(nameof(OpenHover));
        else if (_openBattle)
            CallDeferred(nameof(OpenBattle));
        else if (_openDiy)
            CallDeferred(nameof(OpenDiy));
        else if (_openPackMgr)
            CallDeferred(nameof(OpenPackMgr));
    }

    private void OpenDiy()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenDiyForShot();
    }

    private void OpenPackMgr()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenPackMgrForShot();
    }

    private void OpenBattle()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenBattleForShot(_battleExpanded, _battleBadges, _battleMulligan);
    }

    private void OpenWizard()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenWizardForShot();
    }

    private void OpenHover()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenHoverForShot();
    }

    private void OpenBuilder()
    {
        var root = GetParent().GetNodeOrNull<Decks.DeckManagementRoot>("DeckManagementRoot");
        root?.OpenFirstDeckForShot();
    }

    public override void _Process(double delta)
    {
        if (string.IsNullOrEmpty(_path)) return;
        if (_frames-- > 0) return;

        // 打印节点树尺寸：截图只能看到「结果」，布局塌了要看是哪个节点没撑开。
        DumpTree(GetTree().Root, 0);

        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_path);
        GD.Print("[DevShot] saved: " + _path);
        GetTree().Quit();
    }

    private static void DumpTree(Node n, int depth)
    {
        if (depth > 6) return;
        string size = n is Control c ? $"{c.Size.X}x{c.Size.Y} min={c.CustomMinimumSize.X}x{c.CustomMinimumSize.Y} pos={c.Position.X},{c.Position.Y}" : "";
        GD.Print(new string(' ', depth * 2) + n.Name + " [" + n.GetType().Name + "] " + size);
        foreach (var child in n.GetChildren()) DumpTree(child, depth + 1);
    }
}
