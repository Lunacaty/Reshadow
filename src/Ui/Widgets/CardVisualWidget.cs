using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 《影之诗：超凡世界》原版级卡牌视觉组件（基准尺寸 192×268）。
///
/// 严格还原原版卡面结构：
///   1. 卡面绝不印效果小字 —— 内部 100% 是全画幅立绘区；
///   2. 顶部拱形金属卡框 + 深酒红镶边卡名横幅；
///   3. 左上角：翡翠绿宝石 + 双层耀金圈费用宝珠（微凸出卡框）；
///   4. 左下角：蓝剑镶金攻击力徽章（仅随从，多边形立体剑身）；
///   5. 右下角：红盾镶金防御力徽章（仅随从，多边形立体骑士盾）；
///   6. 底部中央：金属雕花横梁 + 菱形宝石；
///   7. 卡框正下方独立底座：投入张数点（● ● ○）。
///
/// 效果文本不在卡面上 —— 由 CardHoverTooltip 与 CardInspectorModal 承载。
/// </summary>
public partial class CardVisualWidget : Control
{
    public CardDef Card { get; private set; }
    public event Action<CardDef> OnLeftClick;
    public event Action<CardDef> OnRightClick;
    public event Action<CardDef, Control> OnHoverEntered;
    public event Action<CardDef> OnHoverExited;

    private int _inDeckCount;
    private int _maxCopyLimit = 3;
    private bool _locked;
    private bool _hovered;
    private Texture2D _artTexture;
    private bool _showCopyPips = true;

    public bool IsLocked => _locked;

    public void Setup(
        CardDef def, ChineseCardPrinter printer,
        int inDeckCount = 0, int maxCopyLimit = 3, bool showCopyPips = true)
    {
        Card = def ?? throw new ArgumentNullException(nameof(def));
        _inDeckCount = inDeckCount;
        _maxCopyLimit = Math.Max(1, maxCopyLimit);
        _showCopyPips = showCopyPips;

        CustomMinimumSize = new Vector2(192, showCopyPips ? 276 : 254);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;

        foreach (var child in GetChildren()) child.QueueFree();

        var artPath = $"res://assets/cards/{def.CardId}.png";
        _artTexture = ResourceLoader.Exists(artPath) ? GD.Load<Texture2D>(artPath) : null;

        MouseEntered += () => { _hovered = true; QueueRedraw(); OnHoverEntered?.Invoke(Card, this); };
        MouseExited  += () => { _hovered = false; QueueRedraw(); OnHoverExited?.Invoke(Card); };

        QueueRedraw();
    }

    public void UpdateDeckCopyState(int inDeckCount, int maxCopyLimit)
    {
        _inDeckCount = inDeckCount;
        _maxCopyLimit = Math.Max(1, maxCopyLimit);
        QueueRedraw();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Card == null) return;

        float w = Size.X;
        float pipsH = _showCopyPips ? 22f : 0f;
        float h = Size.Y - pipsH;
        if (w < 60 || h < 80) return;

        var font = ThemeDB.FallbackFont;
        Color clanCol = SvWbTheme.ClanColor(Card.Clan.IsValid ? Card.Clan.Name : null);

        float frameL = 14f;
        float frameR = w - 14f;
        float frameT = 12f;
        float frameB = h - 14f;
        float frameW = frameR - frameL;
        float frameH = frameB - frameT;

        DrawCardShadowAndGlow(frameL, frameT, frameW, frameH, clanCol);
        DrawOrnateOuterFrame(frameL, frameT, frameW, frameH, clanCol);

        Rect2 artRect = new(frameL + 7f, frameT + 18f, frameW - 14f, frameH - 30f);
        DrawFullBleedArtwork(artRect, clanCol, font);

        DrawTopNameBanner(frameL, frameT, frameW, font);
        DrawBottomOrnamentAndGem(frameL, frameB, frameW, clanCol);
        DrawEmeraldCostOrb(new Vector2(frameL + 14f, frameT + 24f), 21f, Card.BaseCost, font);

        if (Card.Kind == CardKind.Follower)
        {
            DrawBlueSwordAttackBadge(new Vector2(frameL + 12f, frameB - 14f), Card.BaseAttack, font);
            DrawRedShieldLifeBadge(new Vector2(frameR - 12f, frameB - 14f), Card.BaseLife, font);
        }
        else
        {
            DrawNonFollowerKindBadge(new Vector2(w * 0.5f, frameB - 20f), font);
        }

        DrawMechanicBadges(frameR, frameT, font);

        if (_locked)
            DrawRect(new Rect2(frameL, frameT, frameW, frameH), new Color(0.03f, 0.04f, 0.08f, 0.62f));

        if (_showCopyPips)
            DrawCopyPipsBar(new Rect2(frameL + 16f, h + 2f, frameW - 32f, pipsH - 4f), font);
    }

    private void DrawCardShadowAndGlow(float l, float t, float w, float h, Color clanCol)
    {
        DrawRect(new Rect2(l + 4f, t + 6f, w, h), new Color(0, 0, 0, 0.55f));
        if (_hovered)
            DrawRect(new Rect2(l - 3f, t - 3f, w + 6f, h + 6f), new Color(1.0f, 0.84f, 0.35f, 0.45f));
    }

    private void DrawOrnateOuterFrame(float l, float t, float w, float h, Color clanCol)
    {
        Vector2[] outerPoly = BuildArchedFramePolygon(l, t, w, h, 0f);
        Vector2[] midPoly   = BuildArchedFramePolygon(l, t, w, h, 2.5f);
        Vector2[] innerPoly = BuildArchedFramePolygon(l, t, w, h, 6.5f);

        Color rimColor = _hovered ? new Color("FFF3C4") : new Color("D6C7B2");
        DrawColoredPolygon(outerPoly, rimColor);

        Color frameMetal = new Color("8E86A8").Lerp(clanCol, 0.28f);
        DrawColoredPolygon(midPoly, frameMetal);
        DrawColoredPolygon(innerPoly, new Color("141020"));

        float cx = l + w * 0.5f;
        float crownY = t + 7f;
        Vector2[] crownDiamond =
        {
            new(cx, crownY - 5f),
            new(cx + 7f, crownY),
            new(cx, crownY + 5f),
            new(cx - 7f, crownY),
        };
        DrawColoredPolygon(crownDiamond, new Color("FDE68A"));
        DrawLine(new Vector2(cx - 32f, crownY), new Vector2(cx - 9f, crownY), new Color("E2E8F0"), 1.5f);
        DrawLine(new Vector2(cx + 9f, crownY), new Vector2(cx + 32f, crownY), new Color("E2E8F0"), 1.5f);

        DrawPolyline(ClosePoly(outerPoly), _hovered ? SvWbTheme.AccentGold : new Color("F8FAFC"), 1.5f);
    }

    private static Vector2[] BuildArchedFramePolygon(float l, float t, float w, float h, float inset)
    {
        float il = l + inset;
        float ir = l + w - inset;
        float it = t + inset;
        float ib = t + h - inset;
        float cx = l + w * 0.5f;
        float archRise = 10f;

        return new Vector2[]
        {
            new(il, it + archRise),
            new(il + w * 0.18f, it + archRise * 0.35f),
            new(cx, it - 2f),
            new(ir - w * 0.18f, it + archRise * 0.35f),
            new(ir, it + archRise),
            new(ir, ib - 6f),
            new(ir - 6f, ib),
            new(il + 6f, ib),
            new(il, ib - 6f),
        };
    }

    private void DrawFullBleedArtwork(Rect2 r, Color clanCol, Font font)
    {
        if (_artTexture != null)
        {
            DrawTextureRect(_artTexture, r, false);
        }
        else
        {
            // 立绘区整体压暗：原版卡面是深色插画，不是亮白底。
            Color bgTop = clanCol.Darkened(0.78f).Lerp(new Color("0B0A1F"), 0.55f);
            Color bgMid = clanCol.Darkened(0.62f).Lerp(new Color("141020"), 0.50f);
            Color bgBot = clanCol.Darkened(0.72f).Lerp(new Color("070A14"), 0.60f);

            DrawRect(r, bgMid);
            DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y * 0.45f), bgTop);
            DrawRect(new Rect2(r.Position.X, r.Position.Y + r.Size.Y * 0.65f, r.Size.X, r.Size.Y * 0.35f),
                     new Color(bgBot.R, bgBot.G, bgBot.B, 0.55f));

            Vector2 mc = new(r.Position.X + r.Size.X * 0.5f, r.Position.Y + r.Size.Y * 0.48f);
            Color ringCol = new(0.92f, 0.96f, 1.0f, 0.13f);
            Color glowCol = clanCol.Lightened(0.45f);
            glowCol.A = 0.08f;

            DrawCircle(mc, r.Size.X * 0.42f, glowCol);
            DrawArc(mc, r.Size.X * 0.43f, 0, Mathf.Tau, 48, ringCol, 1.5f);
            DrawArc(mc, r.Size.X * 0.36f, 0, Mathf.Tau, 48, ringCol, 1.0f);
            DrawArc(mc, r.Size.X * 0.22f, 0, Mathf.Tau, 36, ringCol, 1.0f);

            float rad = r.Size.X * 0.35f;
            for (int i = 0; i < 6; i++)
            {
                float a1 = i * Mathf.Tau / 6f - Mathf.Pi * 0.5f;
                float a2 = ((i + 2) % 6) * Mathf.Tau / 6f - Mathf.Pi * 0.5f;
                Vector2 p1 = mc + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * rad;
                Vector2 p2 = mc + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * rad;
                DrawLine(p1, p2, ringCol, 1.0f);
            }

            string emblem = Card.Kind switch
            {
                CardKind.Follower => "⚔",
                CardKind.Spell    => "✦",
                CardKind.Amulet   => "⛩",
                _                 => "❖",
            };
            Vector2 embSize = font.GetStringSize(emblem, HorizontalAlignment.Left, -1, 44);
            DrawString(font, new Vector2(mc.X - embSize.X * 0.5f, mc.Y + 14f),
                       emblem, HorizontalAlignment.Left, -1, 44, new Color(1f, 1f, 1f, 0.26f));

            if (Card.Traits != null && Card.Traits.Count > 0)
            {
                string trName = Card.Traits[0].Name;
                string badgeText = $"— {SvWbTheme.ClanDisplayZh(Card.Clan.IsValid ? Card.Clan.Name : null)} · {trName} —";
                int fs = 11;
                float tw = font.GetStringSize(badgeText, HorizontalAlignment.Left, -1, fs).X;
                // 放在立绘区偏下（约 82% 高度处）：
                // 贴 r.End.Y 会被下方卡框边缘切掉，而露不全的标签比没有更难看。
                float badgeY = r.Position.Y + r.Size.Y * 0.82f;
                DrawString(font, new Vector2(mc.X - tw * 0.5f, badgeY),
                           badgeText, HorizontalAlignment.Left, -1, fs, new Color(0.92f, 0.94f, 1f, 0.62f));
            }
        }

        DrawRect(r, new Color("8E86A8") { A = 0.35f }, filled: false, width: 1.2f);
    }

    private void DrawTopNameBanner(float frameL, float frameT, float frameW, Font font)
    {
        float bannerL = frameL + 18f;
        float bannerR = frameL + frameW - 6f;
        float bannerT = frameT + 11f;
        float bannerB = bannerT + 26f;
        float bannerMidY = (bannerT + bannerB) * 0.5f;

        Vector2[] plaque =
        {
            new(bannerL, bannerT),
            new(bannerR - 6f, bannerT),
            new(bannerR, bannerMidY),
            new(bannerR - 6f, bannerB),
            new(bannerL, bannerB),
        };

        DrawColoredPolygon(plaque, new Color("4C1D28"));
        Vector2[] topSheen =
        {
            new(bannerL, bannerT),
            new(bannerR - 6f, bannerT),
            new(bannerR - 3f, bannerMidY),
            new(bannerL, bannerMidY),
        };
        DrawColoredPolygon(topSheen, new Color(1f, 0.85f, 0.88f, 0.08f));
        DrawPolyline(ClosePoly(plaque), new Color("D8C8E8"), 1.8f);

        string displayName = !string.IsNullOrWhiteSpace(Card.NameChs) ? Card.NameChs : Card.Name;
        float availW = (bannerR - 10f) - (bannerL + 22f);
        int fontSize = 13;
        while (fontSize > 9 && font.GetStringSize(displayName, HorizontalAlignment.Left, -1, fontSize).X > availW)
            fontSize--;

        Vector2 textSize = font.GetStringSize(displayName, HorizontalAlignment.Left, -1, fontSize);
        float textX = bannerL + 20f + Math.Max(0f, (availW - textSize.X) * 0.5f);
        float textY = bannerMidY + fontSize * 0.36f;

        DrawString(font, new Vector2(textX + 1f, textY + 1f), displayName,
                   HorizontalAlignment.Left, availW, fontSize, new Color(0, 0, 0, 0.85f));
        DrawString(font, new Vector2(textX, textY), displayName,
                   HorizontalAlignment.Left, availW, fontSize, Colors.White);
    }

    private void DrawEmeraldCostOrb(Vector2 center, float radius, int cost, Font font)
    {
        DrawCircle(center + new Vector2(1.5f, 2.5f), radius + 2f, new Color(0, 0, 0, 0.6f));
        DrawCircle(center, radius + 2.5f, new Color("92400E"));
        DrawCircle(center, radius + 1.0f, new Color("FBBF24"));
        DrawArc(center, radius + 1.5f, Mathf.Pi * 1.1f, Mathf.Pi * 1.9f, 24, new Color("FEF08A"), 2.0f);

        DrawCircle(center, radius - 2.0f, new Color("14532D"));
        DrawCircle(center + new Vector2(-1f, -1f), radius - 4.0f, new Color("15803D"));
        DrawCircle(center + new Vector2(-3f, -4f), radius * 0.55f, new Color("22C55E"));
        DrawCircle(center + new Vector2(-6f, -7f), radius * 0.22f, new Color(0.85f, 1.0f, 0.9f, 0.65f));

        string txt = cost.ToString();
        int fs = 22;
        Vector2 sz = font.GetStringSize(txt, HorizontalAlignment.Left, -1, fs);
        Vector2 pos = new(center.X - sz.X * 0.5f, center.Y + fs * 0.36f);
        DrawOutlinedNumber(font, pos, txt, fs, Colors.White, new Color("052E16"));
    }

    private void DrawBlueSwordAttackBadge(Vector2 center, int attack, Font font)
    {
        float cx = center.X;
        float cy = center.Y;
        Vector2[] swordOuter =
        {
            new(cx,        cy - 30f),
            new(cx + 19f,  cy + 3f),
            new(cx + 10f,  cy + 9f),
            new(cx + 15f,  cy + 16f),
            new(cx + 6f,   cy + 24f),
            new(cx - 6f,   cy + 24f),
            new(cx - 15f,  cy + 16f),
            new(cx - 10f,  cy + 9f),
            new(cx - 19f,  cy + 3f),
        };

        Vector2[] swordInner = ScalePolygon(swordOuter, center, 0.84f);
        DrawColoredPolygon(swordOuter, new Color("D97706"));
        DrawColoredPolygon(swordInner, new Color("1D4ED8"));

        Vector2[] leftFacet =
        {
            new(cx,       cy - 25f),
            new(cx,       cy + 20f),
            new(cx - 5f,  cy + 20f),
            new(cx - 12f, cy + 14f),
            new(cx - 8f,  cy + 8f),
            new(cx - 16f, cy + 3f),
        };
        DrawColoredPolygon(leftFacet, new Color("0284C7"));
        DrawPolyline(ClosePoly(swordOuter), new Color("FDE047"), 2.0f);

        string txt = attack.ToString();
        int fs = 22;
        Vector2 sz = font.GetStringSize(txt, HorizontalAlignment.Left, -1, fs);
        Vector2 pos = new(cx - sz.X * 0.5f, cy + 10f);
        DrawOutlinedNumber(font, pos, txt, fs, Colors.White, new Color("0F172A"));
    }

    private void DrawRedShieldLifeBadge(Vector2 center, int life, Font font)
    {
        float cx = center.X;
        float cy = center.Y;
        Vector2[] shieldOuter =
        {
            new(cx,        cy - 24f),
            new(cx + 20f,  cy - 19f),
            new(cx + 19f,  cy + 1f),
            new(cx + 12f,  cy + 14f),
            new(cx,        cy + 24f),
            new(cx - 12f,  cy + 14f),
            new(cx - 19f,  cy + 1f),
            new(cx - 20f,  cy - 19f),
        };

        Vector2[] shieldInner = ScalePolygon(shieldOuter, center, 0.84f);
        DrawColoredPolygon(shieldOuter, new Color("D97706"));
        DrawColoredPolygon(shieldInner, new Color("991B1B"));

        Vector2[] leftFacet =
        {
            new(cx,       cy - 20f),
            new(cx,       cy + 20f),
            new(cx - 10f, cy + 12f),
            new(cx - 16f, cy + 1f),
            new(cx - 16f, cy - 16f),
        };
        DrawColoredPolygon(leftFacet, new Color("DC2626"));
        DrawPolyline(ClosePoly(shieldOuter), new Color("FDE047"), 2.0f);

        string txt = life.ToString();
        int fs = 22;
        Vector2 sz = font.GetStringSize(txt, HorizontalAlignment.Left, -1, fs);
        Vector2 pos = new(cx - sz.X * 0.5f, cy + 8f);
        DrawOutlinedNumber(font, pos, txt, fs, Colors.White, new Color("450A0A"));
    }

    private void DrawBottomOrnamentAndGem(float frameL, float frameB, float frameW, Color clanCol)
    {
        float cx = frameL + frameW * 0.5f;
        float cy = frameB - 7f;

        Vector2[] bar =
        {
            new(cx - 42f, frameB),
            new(cx - 28f, cy - 5f),
            new(cx + 28f, cy - 5f),
            new(cx + 42f, frameB),
        };
        DrawColoredPolygon(bar, new Color("CBD5E1").Lerp(clanCol, 0.25f));
        DrawPolyline(ClosePoly(bar), new Color("F8FAFC"), 1.2f);

        Vector2[] gemSetting =
        {
            new(cx, cy - 9f),
            new(cx + 9f, cy),
            new(cx, cy + 9f),
            new(cx - 9f, cy),
        };
        Vector2[] gemCore =
        {
            new(cx, cy - 6.5f),
            new(cx + 6.5f, cy),
            new(cx, cy + 6.5f),
            new(cx - 6.5f, cy),
        };
        DrawColoredPolygon(gemSetting, new Color("FBBF24"));
        DrawColoredPolygon(gemCore, new Color("E11D48").Lerp(clanCol, 0.35f));
    }

    private void DrawNonFollowerKindBadge(Vector2 center, Font font)
    {
        string label = Card.Kind == CardKind.Spell ? "✦ 法 术 ✦" : "⛩ 护 符 ⛩";
        int fs = 11;
        Vector2 sz = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
        Rect2 pill = new(center.X - sz.X * 0.5f - 10f, center.Y - 10f, sz.X + 20f, 18f);
        DrawRect(pill, new Color(0.08f, 0.10f, 0.18f, 0.88f));
        DrawRect(pill, new Color("FDE68A"), filled: false, width: 1.2f);
        DrawString(font, new Vector2(center.X - sz.X * 0.5f, center.Y + 3f),
                   label, HorizontalAlignment.Left, -1, fs, new Color("FEF08A"));
    }

    private void DrawMechanicBadges(float frameR, float frameT, Font font)
    {
        bool hasMod = Card.DeckModifiers != null && Card.DeckModifiers.Count > 0;
        if (!hasMod && !Card.HasFuse && Card.AccelerateDef == null && Card.CrystallizeDef == null)
            return;

        string tag = hasMod ? "★构筑规则"
                   : Card.HasFuse ? "◆融合"
                   : Card.AccelerateDef != null ? "⚡激奏" : "❖结晶";
        Color tagBg = hasMod ? new Color("7E22CE") : new Color("0369A1");

        int fs = 10;
        Vector2 sz = font.GetStringSize(tag, HorizontalAlignment.Left, -1, fs);
        Rect2 r = new(frameR - sz.X - 18f, frameT + 42f, sz.X + 10f, 16f);
        DrawRect(r, tagBg);
        DrawRect(r, new Color("FDE047"), filled: false, width: 1f);
        DrawString(font, new Vector2(r.Position.X + 5f, r.Position.Y + 12f),
                   tag, HorizontalAlignment.Left, -1, fs, Colors.White);
    }

    private void DrawCopyPipsBar(Rect2 rect, Font font)
    {
        DrawRect(rect, new Color(0.07f, 0.09f, 0.16f, 0.90f));
        DrawRect(rect, new Color("475569"), filled: false, width: 1f);

        int cap = Math.Max(1, _maxCopyLimit);
        float cy = rect.Position.Y + rect.Size.Y * 0.5f;
        float cx = rect.Position.X + rect.Size.X * 0.5f;

        if (cap <= 5)
        {
            float spacing = 14f;
            float startX = cx - (cap - 1) * spacing * 0.5f;
            for (int i = 0; i < cap; i++)
            {
                Vector2 p = new(startX + i * spacing, cy);
                bool filled = i < _inDeckCount;
                DrawCircle(p, 4.5f, filled ? new Color("FBBF24") : new Color("1E293B"));
                DrawArc(p, 4.5f, 0, Mathf.Tau, 16, filled ? new Color("FEF08A") : new Color("64748B"), 1.2f);
            }
        }
        else
        {
            string txt = $"{_inDeckCount} / {cap}";
            Vector2 sz = font.GetStringSize(txt, HorizontalAlignment.Left, -1, 11);
            DrawString(font, new Vector2(cx - sz.X * 0.5f, cy + 4f),
                       txt, HorizontalAlignment.Left, -1, 11,
                       _inDeckCount >= cap ? SvWbTheme.AccentGold : SvWbTheme.AccentCyan);
        }
    }

    private void DrawOutlinedNumber(Font font, Vector2 pos, string text, int fontSize, Color fg, Color outline)
    {
        Vector2[] offsets = { new(-1.5f, 0), new(1.5f, 0), new(0, -1.5f), new(0, 1.5f), new(1.5f, 1.5f) };
        foreach (var off in offsets)
            DrawString(font, pos + off, text, HorizontalAlignment.Left, -1, fontSize, outline);
        DrawString(font, pos, text, HorizontalAlignment.Left, -1, fontSize, fg);
    }

    private static Vector2[] ScalePolygon(Vector2[] poly, Vector2 pivot, float scale)
    {
        var res = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            res[i] = pivot + (poly[i] - pivot) * scale;
        return res;
    }

    private static Vector2[] ClosePoly(Vector2[] poly)
    {
        var res = new Vector2[poly.Length + 1];
        Array.Copy(poly, res, poly.Length);
        res[^1] = poly[0];
        return res;
    }

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
