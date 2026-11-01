using System;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 程序化高颜值卡牌组件（184×260）。
///
/// 零外部贴图依赖：职业色渐变插画窗 + 几何卡种水印 + 费用宝珠 +
/// 语法高亮的简中效果区 + 攻防徽章 + 投入张数指示灯。
/// 将来往 res://assets/cards/{id}.png 放图即可自动替换插画窗。
/// </summary>
public partial class CardVisualWidget : PanelContainer
{
    public CardDef Card { get; private set; }
    public event Action<CardDef> OnLeftClick;
    public event Action<CardDef> OnRightClick;

    private Label _pipsLabel;
    private Panel _maxOverlay;
    private bool _locked;

    public void Setup(CardDef def, ChineseCardPrinter printer, int inDeckCount = 0, int maxCopyLimit = 3)
    {
        Card = def ?? throw new ArgumentNullException(nameof(def));
        CustomMinimumSize = new Vector2(184, 260);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;

        Color clanCol = SvWbTheme.ClanColor(def.Clan.IsValid ? def.Clan.Name : null);
        AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color("131A2A"), clanCol, 10, 2, 8));

        var rootVBox = new VBoxContainer();
        rootVBox.AddThemeConstantOverride("separation", 4);
        AddChild(rootVBox);

        // ---- 1. 顶部：费用宝珠 + 卡种/构筑修正标签 ----
        var topRow = new HBoxContainer();
        rootVBox.AddChild(topRow);

        var costBadge = new PanelContainer { CustomMinimumSize = new Vector2(32, 32) };
        costBadge.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color("0284C7"), SvWbTheme.AccentCyan, 16, 2, 4));
        var costLbl = new Label
        {
            Text = def.BaseCost.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        costLbl.AddThemeFontSizeOverride("font_size", 16);
        costLbl.AddThemeColorOverride("font_color", Colors.White);
        costBadge.AddChild(costLbl);
        topRow.AddChild(costBadge);

        string kindText = def.Kind switch
        {
            CardKind.Follower => "⚔ 随从",
            CardKind.Spell    => "✦ 法术",
            CardKind.Amulet   => "⛩ 护符",
            CardKind.Crest    => "❖ 纹章",
            _                 => "卡牌",
        };
        bool hasMod = def.DeckModifiers != null && def.DeckModifiers.Count > 0;
        var kindTag = new Label
        {
            Text = (hasMod ? "★规则 · " : "") + kindText,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        kindTag.AddThemeFontSizeOverride("font_size", 12);
        kindTag.AddThemeColorOverride("font_color",
            hasMod ? SvWbTheme.AccentPurple : SvWbTheme.TextSecondary);
        topRow.AddChild(kindTag);

        // ---- 2. 插画窗 ----
        var artBox = new PanelContainer { CustomMinimumSize = new Vector2(0, 68) };
        artBox.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(clanCol.R * 0.25f, clanCol.G * 0.25f, clanCol.B * 0.30f, 0.85f),
            new Color(clanCol.R, clanCol.G, clanCol.B, 0.5f), 6, 1, 0));

        var artPath = $"res://assets/cards/{def.CardId}.png";
        if (ResourceLoader.Exists(artPath))
        {
            var tex = GD.Load<Texture2D>(artPath);
            if (tex != null)
                artBox.AddChild(new TextureRect
                {
                    Texture = tex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                });
        }
        else
        {
            bool hasZh = !string.IsNullOrWhiteSpace(def.NameChs);
            var watermark = new Label
            {
                // 没有中文名时明确回落英文，并在下方副标题里标出来。
                Text = hasZh ? def.NameChs : def.Name,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            watermark.AddThemeFontSizeOverride("font_size", 15);
            watermark.AddThemeColorOverride("font_color", Colors.White);
            artBox.AddChild(watermark);
        }
        rootVBox.AddChild(artBox);

        // ---- 3. 职业 + 兵种副标题 ----
        string traitsText = def.Traits != null && def.Traits.Count > 0
            ? string.Join("·", def.Traits.Select(t => printer.TraitZh(t.Name)))
            : "无兵种";
        var subTitle = new Label
        {
            Text = $"{SvWbTheme.ClanDisplayZh(def.Clan.IsValid ? def.Clan.Name : null)} | {traitsText}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subTitle.AddThemeFontSizeOverride("font_size", 11);
        subTitle.AddThemeColorOverride("font_color", clanCol.Lerp(Colors.White, 0.35f));
        rootVBox.AddChild(subTitle);

        // ---- 4. 简中效果正文（BBCode 染色） ----
        var textScroll = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = SvWbTheme.FormatRichCardText(printer.PrintCard(def)),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 76),
            MouseFilter = MouseFilterEnum.Pass,
            ScrollActive = false,
        };
        textScroll.AddThemeFontSizeOverride("normal_font_size", 11);
        textScroll.AddThemeFontSizeOverride("bold_font_size", 11);
        rootVBox.AddChild(textScroll);

        // ---- 5. 底部：攻 / 张数点 / 血 ----
        var bottomRow = new HBoxContainer();
        rootVBox.AddChild(bottomRow);

        var atkLbl = new Label
        {
            Text = def.Kind == CardKind.Follower ? $"⚔ {def.BaseAttack}" : "",
            CustomMinimumSize = new Vector2(42, 0),
        };
        atkLbl.AddThemeFontSizeOverride("font_size", 14);
        atkLbl.AddThemeColorOverride("font_color", new Color("60A5FA"));
        bottomRow.AddChild(atkLbl);

        _pipsLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill,
                                HorizontalAlignment = HorizontalAlignment.Center };
        _pipsLabel.AddThemeFontSizeOverride("font_size", 12);
        bottomRow.AddChild(_pipsLabel);

        var lifeLbl = new Label
        {
            Text = def.Kind == CardKind.Follower ? $"🛡 {def.BaseLife}" : "",
            CustomMinimumSize = new Vector2(42, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        lifeLbl.AddThemeFontSizeOverride("font_size", 14);
        lifeLbl.AddThemeColorOverride("font_color", new Color("F87171"));
        bottomRow.AddChild(lifeLbl);

        SetLocked(false);
        UpdateDeckCopyState(inDeckCount, maxCopyLimit);
    }

    /// <summary>刷新底部投入张数指示器（●●○ 2/3）。</summary>
    public void UpdateDeckCopyState(int inDeckCount, int maxCopyLimit)
    {
        if (_pipsLabel == null) return;
        int cap = Math.Max(1, maxCopyLimit);
        if (cap <= 5)
        {
            // 超出上限时也要显示出来（多画一枚实心点），否则「已达上限」看不出来。
            int filled = Math.Clamp(inDeckCount, 0, cap + 1);
            _pipsLabel.Text = new string('●', filled)
                            + new string('○', Math.Max(0, cap - inDeckCount));
        }
        else
        {
            _pipsLabel.Text = $"{Math.Min(inDeckCount, cap)} / {cap}";
        }
        _pipsLabel.AddThemeColorOverride("font_color",
            inDeckCount >= cap ? SvWbTheme.AccentGold
          : inDeckCount > 0    ? SvWbTheme.AccentCyan
          : SvWbTheme.TextMuted);
    }

    /// <summary>非允许职业的卡整体压暗并加锁标记（仍可查看，不可投入）。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (_maxOverlay == null)
        {
            _maxOverlay = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            _maxOverlay.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                new Color(0, 0, 0, 0.55f), new Color(0, 0, 0, 0), 10, 0, 0));
            AddChild(_maxOverlay);
            _maxOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        }
        _maxOverlay.Visible = locked;
        Modulate = locked ? new Color(1, 1, 1, 0.55f) : Colors.White;
    }

    public bool IsLocked => _locked;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            OnLeftClick?.Invoke(Card);
            AcceptEvent();
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            OnRightClick?.Invoke(Card);
            AcceptEvent();
        }
    }
}
