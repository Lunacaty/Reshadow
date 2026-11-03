using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 画面左下角（纹章/信仰区正上方）的效果发动实时日志栈。
///
/// 背景为该卡立绘中心填充，标题为卡名，内容为发动的那一句能力描述；
/// 3s 后自动消散，新条把旧条往上推。
///</summary>
public partial class AbilityActivationToastStack : Control
{
    private BattleSessionController _ctrl;
    private VBoxContainer _stack;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 63;

        _stack = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        _stack.AddThemeConstantOverride("separation", 6);
        AddChild(_stack);
    }

    /// <summary>锚定在左下角纹章圆槽的正上方。</summary>
    public void UpdateAnchor(Vector2 viewportSize)
    {
        float h = viewportSize.Y > 0f ? viewportSize.Y : 720f;
        Position = new Vector2(18f, h - 188f - 280f);
        Size = new Vector2(304f, 280f);
        _stack.Position = Vector2.Zero;
        _stack.Size = Size;
    }

    public void PushToast(CardDef card, AbilityDef ability, int playerId)
    {
        if (card == null || ability == null || _ctrl?.Store?.CardPrinter == null) return;

        string line = _ctrl.Store.CardPrinter.PrintAbility(ability);
        if (string.IsNullOrWhiteSpace(line)) return;

        // 最多同时保留 4 条，超出时立刻移除最旧的一条。
        while (_stack.GetChildCount() >= 4)
        {
            var oldest = _stack.GetChild(0);
            _stack.RemoveChild(oldest);
            oldest.QueueFree();
        }

        var item = new ToastItem();
        item.Setup(card, line, playerId == _ctrl.MyPlayerId);
        _stack.AddChild(item);
    }

    private sealed partial class ToastItem : ArtCenterBannerBox
    {
        private float _age;
        private const float TotalLife = 3.0f;

        public void Setup(CardDef card, string effectSentence, bool isAlly)
        {
            CardId = card.CardId;
            ArtCardId = card.ArtCardId;
            DimAlpha = 0.72f;
            Selected = isAlly;
            CustomMinimumSize = new Vector2(304f, 56f);
            MouseFilter = MouseFilterEnum.Ignore;
            AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

            var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_top", 5);
            margin.AddThemeConstantOverride("margin_bottom", 5);
            AddChild(margin);

            var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 2);
            margin.AddChild(col);

            string sideTag = isAlly ? "" : "【敌方】";
            var title = new Label
            {
                Text = sideTag + DeckEditorViewModel.DisplayName(card),
                ClipText = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            title.AddThemeFontSizeOverride("font_size", 13);
            title.AddThemeColorOverride("font_color", isAlly ? new Color("FEF08A") : new Color("FCA5A5"));
            title.AddThemeConstantOverride("outline_size", 4);
            title.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.92f));
            col.AddChild(title);

            var body = new RichTextLabel
            {
                BbcodeEnabled = true,
                Text = SvWbTheme.FormatRichCardText(effectSentence),
                FitContent = true,
                ScrollActive = false,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            body.AddThemeFontSizeOverride("normal_font_size", 11);
            body.AddThemeFontSizeOverride("bold_font_size", 11);
            body.AddThemeConstantOverride("outline_size", 3);
            body.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.90f));
            col.AddChild(body);
        }

        public override void _Process(double delta)
        {
            _age += (float)delta;
            if (_age >= TotalLife)
            {
                QueueFree();
                return;
            }
            // 前 0.15s 淡入，最后 0.35s 淡出。
            float alpha = _age < 0.15f ? (_age / 0.15f)
                        : _age > TotalLife - 0.35f ? ((TotalLife - _age) / 0.35f)
                        : 1f;
            Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha, 0f, 1f));
        }
    }
}
