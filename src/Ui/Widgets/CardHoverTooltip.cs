using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 鼠标悬停在卡牌或牌组条上时跟随显示的镶金效果浮窗。
///
/// 因为卡面本体 100% 展示立绘与费攻防、绝不印效果文字，
/// 效果文本统一由本浮窗与右键详情窗承载。
/// </summary>
public partial class CardHoverTooltip : PanelContainer
{
    private Label _titleLbl;
    private Label _metaLbl;
    private RichTextLabel _bodyRich;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 90;
        CustomMinimumSize = new Vector2(290, 0);

        AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.08f, 0.10f, 0.18f, 0.96f),
            new Color("E2C07D"), 10, 2, 14));

        var vbox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        _titleLbl = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _titleLbl.AddThemeFontSizeOverride("font_size", 16);
        _titleLbl.AddThemeColorOverride("font_color", new Color("FEF08A"));
        vbox.AddChild(_titleLbl);

        _metaLbl = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _metaLbl.AddThemeFontSizeOverride("font_size", 12);
        _metaLbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        vbox.AddChild(_metaLbl);

        vbox.AddChild(new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 1),
            Color = new Color(0.89f, 0.75f, 0.49f, 0.4f),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        _bodyRich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(266, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _bodyRich.AddThemeFontSizeOverride("normal_font_size", 13);
        _bodyRich.AddThemeFontSizeOverride("bold_font_size", 13);
        vbox.AddChild(_bodyRich);
    }

    public void ShowForCard(CardDef def, ChineseCardPrinter printer, Control anchorControl)
    {
        if (def == null) { Visible = false; return; }

        string name = !string.IsNullOrWhiteSpace(def.NameChs)
            ? $"{def.NameChs} ({def.Name})" : def.Name;
        _titleLbl.Text = name;

        string statPart = def.Kind == CardKind.Follower
            ? $"费用 {def.BaseCost}  |  ⚔ 攻击 {def.BaseAttack}  |  🛡 生命 {def.BaseLife}"
            : $"费用 {def.BaseCost}  |  {(def.Kind == CardKind.Spell ? "法术" : "护符")}";
        // 兵种：卡面印刷的全部兵种（多兵种的随从如「士兵·亡者」必须都列出来）。
        string traitPart = "";
        try
        {
            var traits = def.Traits;
            if (traits != null && traits.Count > 0)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var t in traits)
                {
                    if (!t.IsValid || string.IsNullOrWhiteSpace(t.Name)) continue;
                    string zh;
                    try { zh = printer?.TraitZh(t.Name) ?? t.Name; }
                    catch { zh = t.Name; }
                    if (string.IsNullOrEmpty(zh)) continue;
                    if (!names.Contains(zh)) names.Add(zh);   // 去重：同一兵种只列一次
                }
                if (names.Count > 0) traitPart = "·" + string.Join("·", names);
            }
        }
        catch { }

        _metaLbl.Text = $"【{SvWbTheme.ClanDisplayZh(def.Clan.IsValid ? def.Clan.Name : null)}{traitPart}】 {statPart}";

        // 悬浮窗同样要带子卡，否则构筑 / 换牌时完全看不到纹章与激奏（179 号文档 D4）。
        _bodyRich.Text = SvWbTheme.FormatFullCardText(def, printer);
        Visible = true;

        // 智能定位：默认贴右，靠右边缘时翻到左侧
        Rect2 vp = GetViewportRect();
        Rect2 tr = anchorControl.GetGlobalRect();
        float x = tr.End.X + 12f;
        if (x + Size.X > vp.Size.X - 12f) x = tr.Position.X - Size.X - 12f;
        if (x < 12f) x = 12f;
        float y = Mathf.Clamp(tr.Position.Y, 12f, Mathf.Max(12f, vp.Size.Y - Size.Y - 12f));
        GlobalPosition = new Vector2(x, y);
    }

    public void HideTooltip() => Visible = false;
}
