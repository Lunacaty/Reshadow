using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 场上棋子（随从 / 护符）。
///
/// 随从：顶部圆拱金框立绘窗 + 左下蓝剑 + 右下红盾 + 底中【能力标记】。
/// 护符：切角矩形金框 + 底中【能力标记】+ 右下大号吟唱倒计时。
///
/// 底部标记不是「契」纹章，而是**能力类型的图示**：
///   · 红色书页 = 有谢幕曲（Last Words）
///   · 绿色旗帜 = 有时机能力（在 XX 时），触发时发出一圈光效
///   · 两者都没有 = 留空，不画任何图标
/// 把「无能力」也画成一个徽章是错的 —— 那会让玩家以为每张卡都有东西。
/// </summary>
public partial class BoardUnitWidget : Control
{
    public RuntimeEntity Entity { get; private set; }

    /// <summary>
    /// 本棋子所属战斗界面的动画调度器。
    ///
    /// **不能是 static**：截图模式与正常流程都可能各建一个 BattleScreenRoot，
    /// static 会被后建的那个覆盖，导致棋子查 A 实例的状态、
    /// 而驱动在 B 实例里跑 —— 动画表现为「播得极慢或不动」。
    /// </summary>
    private BoardAnimDirector _director;

    public void BindDirector(BoardAnimDirector d) => _director = d;


    public event Action<RuntimeEntity> OnClicked;
    public event Action<RuntimeEntity> OnRightClicked;
    public event Action<RuntimeEntity, Vector2> OnDragAttackStarted;
    public event Action<RuntimeEntity, Vector2> OnDragAttackMoved;
    public event Action<RuntimeEntity, Vector2> OnDragAttackReleased;

    private bool _selected;
    private bool _canAttack;

    /// <summary>
    /// 候选态：当前有一处挂起选择正在等玩家点它。
    /// 与 _selected 不同 —— _selected 是「已被选中的那个」，
    /// _isCandidate 是「可以被点的那些」。
    /// </summary>
    private bool _isCandidate;

    /// <summary>
    /// 压暗态：有挂起选择时，非候选棋子要淡出，
    /// 让玩家的视线自动落到可选目标上。
    /// </summary>
    private bool _dimmed;
    private UnitAbilityBadge _badge = UnitAbilityBadge.None;
    private float _triggerFlash;
    private float _pulseTime;
    private bool _dragging;

    public const float UnitW = 96f;
    public const float UnitH = 124f;
    private const float W = UnitW;
    private const float H = UnitH;

    /// <summary>设置挂起选择的候选态。</summary>
    public void SetChoiceState(bool isCandidate, bool dimmed)
    {
        _isCandidate = isCandidate;
        _dimmed = dimmed;
        QueueRedraw();
    }

    public void Setup(RuntimeEntity e, bool selected, bool canAttack)
    {
        Entity = e ?? throw new ArgumentNullException(nameof(e));
        _selected = selected;
        _canAttack = canAttack;
        _badge = UnitAbilityBadgeResolver.Resolve(e.Def);

        CustomMinimumSize = new Vector2(W, H);
        Size = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        QueueRedraw();
    }

    /// <summary>
    /// 把动画状态写进控件的**真实** Position / Scale / Modulate。
    ///
    /// 为什么不能用 DrawSetTransform 在 _Draw 里偏移：
    ///   那样控件本身仍占着座位坐标，于是
    ///     · 命中判定（GetGlobalRect）与视觉位置错位 —— 攻击点不中随从；
    ///     · 动画结束后视觉「瞬移」回座位，但玩家以为是别的卡；
    ///     · 与前后端分离冲突：UI 层出现了两个位置真相（绘制位置 / 控件位置）。
    ///   改为直接移动控件：视觉、命中、布局三者永远一致。
    ///
    /// 由 BoardAnimDirector 每帧驱动（见 BattleScreenRoot._Process）。
    /// </summary>
    /// <summary>当前动画变换（只读，供 _Draw 应用）。</summary>
    public UnitMotion Motion => _motion;

    private UnitMotion _motion = UnitMotion.Idle;

    /// <summary>
    /// 视觉血量补偿：后端已扣血、但受击动画还在 _seq 排队未播时，
    /// 加上这笔待播差值，让盾牌显示**受击前**的生命值，
    /// 直到受击动画命中的那一帧才跳变 —— 这就是「动画落后于后端」。
    /// </summary>
    public int VisualLifeOffset
    {
        get => _visualLifeOffset;
        set
        {
            if (_visualLifeOffset == value) return;
            _visualLifeOffset = value;
            // 必须主动重绘：控件只在「有动画在跑」时每帧 QueueRedraw，
            // 静止状态下改了血量不重画，盾牌会一直停在旧数字上。
            QueueRedraw();
        }
    }

    private int _visualLifeOffset;

    /// <summary>
    /// 视觉进化延迟：后端已进化、但进化翻面动画还在队列里没播时，
    /// 暂不显示金框与进化身材加成。
    /// </summary>
    public bool HideEvolveVisual
    {
        get => _hideEvolveVisual;
        set
        {
            if (_hideEvolveVisual == value) return;
            _hideEvolveVisual = value;
            QueueRedraw();
        }
    }

    private bool _hideEvolveVisual;

    /// <summary>
    /// 接收动画状态。
    ///
    /// **不修改 Position / Scale / 不摘出容器** —— 早先试过「动画期间把控件
    /// 从容器摘出来自己管位置」，结果灾难性：ApplyMotionsIn 是遍历容器子节点
    /// 找棋子的，控件一旦摘出就再也遍历不到，收不到「动画结束」的信号，
    /// 于是永久留在场景根上 —— 玩家看到的就是「打出后随从直接消失」。
    ///
    /// 现在只存状态，真正的偏移在 _Draw 里用 DrawSetTransform 应用：
    /// 控件始终待在容器里，布局、命中框、遍历都不受影响。
    /// </summary>
    public void ApplyMotion(UnitMotion m)
    {
        bool changed = !_motion.Equals(m);
        _motion = m;
        if (changed) QueueRedraw();
    }



    /// <summary>触发时机能力时调用：播放一圈光效。</summary>
    public void PlayTriggerFlash()
    {
        if (!UnitAbilityBadgeResolver.FlashesOnTrigger(_badge)) return;
        _triggerFlash = 1f;
    }

    public override void _Process(double delta)
    {
        // 有动画在跑就每帧重绘（变换在 _Draw 里应用）。
        if (Entity != null && _director != null && _director.IsAnimating(Entity.EntityId))
            QueueRedraw();

        // 候选圈需要持续脉动，所以有候选时也必须每帧重绘。
        if (_isCandidate)
        {
            _pulseTime += (float)delta;
            QueueRedraw();
        }

        if (_triggerFlash <= 0f) return;
        _triggerFlash = Mathf.Max(0f, _triggerFlash - (float)delta * 1.6f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Entity?.Def == null) return;

        // 处于 PrepareEnter 隐身预备态（已入场但入场动画还在排队）时
        // 完全不绘制任何内容。只把 Modulate 设成透明不够 ——
        // 自绘内容在 _Draw 里，光靠 Modulate 仍会因为残留帧而闪一下。
        if (_motion.Alpha <= 0.001f)
        {
            Modulate = new Color(1, 1, 1, 0f);
            return;
        }

        var def = Entity.Def;

        // ---- 应用动画变换（只影响绘制，不动控件本体）----
        //
        // 用 DrawSetTransform：控件仍待在容器里，布局、命中框、
        // 父级遍历都不受影响；视觉上棋子会偏移/缩放/半透明。
        // 早先试过改 Position 并摘出容器，结果控件永远收不回来
        // —— 玩家看到「打出后随从直接消失」。
        var mo = _motion;
        bool moved = !mo.IsIdle;

        if (moved)
        {
            float squeeze = Mathf.Abs(Mathf.Cos(Mathf.Pi * mo.FlipX));

            // DrawSetTransform 会改变后续绘制的坐标系**原点**。
            // 为了让下面所有绝对坐标（0..W / 0..H）照旧可用，
            // 变换必须以控件的左上角为原点、把偏移折算进 origin：
            //   origin = 控件原点 + 偏移 + (1-s)*中心  ← 让缩放绕中心发生
            float sxScale = mo.Scale * squeeze;
            var pivotShift = new Vector2(
                W * 0.5f * (1f - sxScale),
                H * 0.5f * (1f - mo.Scale));

            DrawSetTransform(mo.Offset + pivotShift, 0f, new Vector2(sxScale, mo.Scale));

            Modulate = new Color(1, 1, 1, Mathf.Clamp(mo.Alpha, 0f, 1f));
        }
        else
        {
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
            Modulate = Colors.White;
        }
        var font = ThemeDB.FallbackFont;
        Color clanCol = SvWbTheme.ClanColor(def.Clan.IsValid ? def.Clan.Name : null);

        bool isAmulet = def.Kind is CardKind.Amulet or CardKind.Crest;

        if (isAmulet) DrawAmuletFrame(clanCol, def);
        else DrawFollowerArch(clanCol, def);

        DrawArtwork(clanCol, def, isAmulet);

        if (def.Kind == CardKind.Follower)
        {
            // 进化动画还没播时，身材要显示**进化前**的数值。
            // 后端已经结算完 +2/+2（或 +3/+3），但翻面动画还排在队列里，
            // 提前显示会让玩家看到「卡还没翻，身材先涨」。
            int evoAtk = 0, evoLife = 0;
            if (HideEvolveVisual && (Entity.IsEvolved || Entity.IsSuperEvolved))
            {
                var rules = CurrentController?.State?.Rules;
                evoAtk = Entity.IsSuperEvolved
                    ? (rules?.SuperEvolveBonusAttack ?? 3)
                    : (rules?.EvolveBonusAttack ?? 2);
                evoLife = Entity.IsSuperEvolved
                    ? (rules?.SuperEvolveBonusLife ?? 3)
                    : (rules?.EvolveBonusLife ?? 2);
            }

            int shownAtk = Math.Max(0, Entity.Attack - evoAtk);
            // VisualLifeOffset 是**已扣但未播**的伤害，符号为正表示「补回去」。
            int shownLife = Math.Max(0, Entity.Life + VisualLifeOffset - evoLife);

            // 攻防数字按 Buff / 受伤变色：+2/+2 变绿、掉血变红，
            // 否则玩家看不出这个随从被加强过还是被打残了。
            Color atkCol = shownAtk > def.BaseAttack ? new Color("4ADE80")
                         : shownAtk < def.BaseAttack ? new Color("F87171") : Colors.White;
            Color lifeCol = shownLife > def.BaseLife ? new Color("4ADE80")
                          : shownLife < def.BaseLife ? new Color("F87171") : Colors.White;
            DrawSwordBadge(new Vector2(16f, H - 22f), shownAtk, font, atkCol);
            DrawShieldBadge(new Vector2(W - 16f, H - 22f), shownLife, font, lifeCol);
        }

        DrawAbilityBadge(new Vector2(W * 0.5f, H - 12f), font);

        if (isAmulet) DrawCountdown(def, font);

        if (!HideEvolveVisual && (Entity.IsEvolved || Entity.IsSuperEvolved))
        {
            Color glow = Entity.IsSuperEvolved ? SvWbTheme.AccentPurple : SvWbTheme.AccentGold;
            DrawRect(new Rect2(0, 0, W, H), glow, false, 3f);
        }

        // 候选态：金色呼吸瞄准圈。
        // 这是 SelectTarget 挂起时给玩家的唯一视觉线索 ——
        // 没有它，玩家只能靠「点了有反应/没反应」来猜哪些是合法目标。
        if (_isCandidate)
        {
            float pulse = 0.55f + 0.45f * Mathf.Sin(_pulseTime * 4f);
            var gold = new Color(0.98f, 0.85f, 0.28f, pulse);
            DrawRect(new Rect2(-5, -5, W + 10, H + 10), gold, false, 3f);
            DrawRect(new Rect2(-9, -9, W + 18, H + 18),
                     new Color(0.98f, 0.85f, 0.28f, pulse * 0.35f), false, 1.5f);

            // 四角瞄准刻线
            float c = 14f;
            var corners = new[]
            {
                (new Vector2(-5, -5), new Vector2(-5 + c, -5), new Vector2(-5, -5 + c)),
                (new Vector2(W + 5, -5), new Vector2(W + 5 - c, -5), new Vector2(W + 5, -5 + c)),
                (new Vector2(-5, H + 5), new Vector2(-5 + c, H + 5), new Vector2(-5, H + 5 - c)),
                (new Vector2(W + 5, H + 5), new Vector2(W + 5 - c, H + 5), new Vector2(W + 5, H + 5 - c)),
            };
            foreach (var (o, a, b) in corners)
            {
                DrawLine(o, a, gold, 2.5f);
                DrawLine(o, b, gold, 2.5f);
            }
        }

        // 压暗态：非候选棋子淡出
        if (_dimmed)
            DrawRect(new Rect2(0, 0, W, H), new Color(0.02f, 0.03f, 0.06f, 0.62f));

        if (_selected) DrawRect(new Rect2(-3, -3, W + 6, H + 6), SvWbTheme.AccentCyan, false, 2.5f);

        // 可攻击光环：绿圈（还原原版「这个随从能动了」的提示）
        if (_canAttack && !_selected)
            DrawRect(new Rect2(-2, -2, W + 4, H + 4), new Color(0.20f, 0.83f, 0.60f, 0.85f), false, 2.5f);

        // ---- 关键词与光环贴图 ----
        //
        // 全部走 KeywordSvgAtlas 的 SVG 光栅化贴图：外框装甲铺满棋子，
        // 角落徽章贴在攻击力附近。之前这里一条关键词都不画，
        // 守护/屏障/潜行在战场上完全不可见。
        DrawKeywordVisuals();
        // 时机能力触发光效：一圈扩散的光环
        if (_triggerFlash > 0f)
        {
            float a = _triggerFlash;
            float r = (1f - a) * 42f;
            DrawArc(new Vector2(W * 0.5f, H * 0.5f), 40f + r, 0, Mathf.Tau, 36,
                    new Color(0.35f, 1.0f, 0.55f, a * 0.9f), 3f);
        }
    }

    /// <summary>绘制关键词外框与角落徽章。</summary>
    private void DrawKeywordVisuals()
    {
        var e = Entity;
        if (e == null) return;
        var schema = CurrentController?.Schema;
        if (schema == null) return;

        // 必须走 HasEffectiveKeyword：由场上常驻光环赋予的
        // 【守护】【屏障】【潜行】【不受能力影响】也要能画出外框。
        var stKw = CurrentController?.State;
        bool Has(string kw)
            => stKw != null ? stKw.HasEffectiveKeyword(e, schema.Keyword(kw))
                            : e.Keywords.Contains(schema.Keyword(kw));

        // 外框装甲：铺在棋子外围，稍微超出边界制造「装甲包边」的观感
        void Frame(string key)
        {
            var tex = KeywordSvgAtlas.Get(key);
            if (tex != null)
                DrawTextureRect(tex, new Rect2(-4, -6, W + 8, H + 12), false);
        }

        if (Has("ward")) Frame("frame_ward");
        if (Has("barrier")) Frame("frame_barrier");
        if (Has("ambush")) Frame("frame_ambush");
        if (Has("intimidate")) Frame("frame_intimidate");
        if (Has("aura")) Frame("frame_aura");
        if (Has("immune_effects") || Has("immune_effects_except_stats")
            || Has("immune_damage") || Has("immune_ability_damage")
            || Has("immune_combat_damage"))
        {
            Frame("frame_immune");
            var ring = KeywordSvgAtlas.Get("ring_immune");
            if (ring != null)
                DrawTextureRect(ring, new Rect2(W * 0.5f - 66, H * 0.5f - 66, 132, 132), false);
        }

        // 角落徽章：从小到大横排在左下角
        int slot = 0;
        void Badge(string key)
        {
            var tex = KeywordSvgAtlas.Get(key);
            if (tex == null) return;
            var pos = new Vector2(2 + slot * 22, H - 26);
            DrawTextureRect(tex, new Rect2(pos, new Vector2(20, 20)), false);
            slot++;
        }

        if (Has("bane")) Badge("badge_bane");
        if (Has("drain")) Badge("badge_drain");
        if (Has("free_evolve") || Has("free_super_evolve")) Badge("badge_free_evolve");

        // 「1 回合可攻击 X 次」：徽章 + ×X 角标。次数在 KeywordValues 里，
        // 只画徽章不写 X 的话，玩家看不出能打几次。
        var mkKey = schema.Keyword("max_attacks_per_turn");
        if (e.KeywordValues.TryGetValue(mkKey, out int maxAtk) && maxAtk > 1)
        {
            var mt = KeywordSvgAtlas.Get("badge_multi_attack");
            if (mt != null)
            {
                var mp = new Vector2(2 + slot * 22, H - 26);
                DrawTextureRect(mt, new Rect2(mp, new Vector2(20, 20)), false);
                DrawString(ThemeDB.FallbackFont, mp + new Vector2(12, 16),
                           "x" + maxAtk, HorizontalAlignment.Left, -1, 11,
                           new Color(1f, 0.92f, 0.55f));
                slot++;
            }
        }

        // 减伤 / 上限光环：生命值正上方一枚琥珀符文小盾
        var st = CurrentController?.State;
        if (st != null)
        {
            var mods = new System.Collections.Generic.List<ShadowverseFan.KernelV3.Ast.DamageModifierNode>();
            foreach (var pair in st.QueryDamageModifiers(e)) mods.Add(pair.Node);
            if (mods.Count > 0)
            {
                var sh = KeywordSvgAtlas.Get("badge_shield");
                if (sh != null)
                {
                    var sp = new Vector2(W - 26, 2);
                    DrawTextureRect(sh, new Rect2(sp, new Vector2(22, 22)), false);

                    // 必须印出数值：只画盾牌图标，玩家分不清是 -1 还是 -5，
                    // 也分不清是减伤还是伤害上限。
                    var dm0 = mods[0];
                    int amt = 0;
                    try
                    {
                        var ev0 = CurrentController?.State;
                        if (ev0 != null)
                        {
                            var eval0 = new ShadowverseFan.KernelV3.Vm.VmEval(ev0);
                            foreach (var pair in ev0.QueryDamageModifiers(e))
                            {
                                amt = eval0.EvalNum(pair.Node.Amount, pair.Scope);
                                break;
                            }
                        }
                    }
                    catch { amt = 0; }

                    string tip = dm0.Kind == ShadowverseFan.KernelV3.Ast.DamageAuraKind.TakenCap
                        ? "<=" + amt
                        : (dm0.IsReduction ? "-" : "+") + amt;
                    DrawString(ThemeDB.FallbackFont, sp + new Vector2(-12, 30), tip,
                               HorizontalAlignment.Center, 34, 10,
                               new Color(1f, 0.92f, 0.55f));
                }
            }
        }
    }

    private void DrawFollowerArch(Color clanCol, CardDef def)
    {
        // 顶部圆拱：近似「圆拱窗」的上半弧 + 直身
        Vector2[] frame =
        {
            new(3f, 34f),
            new(W * 0.22f, 8f),
            new(W * 0.5f, 2f),
            new(W * 0.78f, 8f),
            new(W - 3f, 34f),
            new(W - 3f, H - 6f),
            new(3f, H - 6f),
        };
        // 外框按稀有度上色。桌面上卡很小，只染最外一圈 ——
        // 整块铺满稀有度色会让场面花掉，影响落子判断。
        DrawColoredPolygon(frame, RarityRim(def));

        Vector2[] inner =
        {
            new(6f, 36f),
            new(W * 0.24f, 12f),
            new(W * 0.5f, 6f),
            new(W * 0.76f, 12f),
            new(W - 6f, 36f),
            new(W - 6f, H - 9f),
            new(6f, H - 9f),
        };
        DrawColoredPolygon(inner, new Color("161228"));
    }

    /// <summary>
    /// 稀有度外框色（与 CardVisualWidget.RarityPalette 的 rim 一致）。
    ///
    /// 桌面卡很小，只染最外一圈；整块铺满稀有度色会让场面花掉。
    /// </summary>
    private static Color RarityRim(CardDef d) => (d?.Rarity ?? 0) switch
    {
        1 => new Color("B07A4A"),
        2 => new Color("C8D0DC"),
        3 => new Color("E6B23C"),
        4 => new Color("C77DFF"),
        _ => new Color("B07A4A"),
    };

    private void DrawAmuletFrame(Color clanCol, CardDef def)
    {
        Vector2[] frame =
        {
            new(8f, 4f), new(W - 8f, 4f), new(W - 3f, 16f),
            new(W - 3f, H - 6f), new(3f, H - 6f), new(3f, 16f),
        };
        DrawColoredPolygon(frame, new Color("D6C7B2"));

        Vector2[] inner =
        {
            new(11f, 7f), new(W - 11f, 7f), new(W - 6f, 18f),
            new(W - 6f, H - 9f), new(6f, H - 9f), new(6f, 18f),
        };
        DrawColoredPolygon(inner, new Color("161228"));
    }

    /// <summary>
    /// 把立绘按棋子外轮廓逐行裁切后画出来。
    ///
    /// 参数 src 是「已按比例裁好的源矩形」，dst 是目标矩形。
    /// 对每一行：算出该行在轮廓内的左右边界，只画这一段。
    /// </summary>
    private void DrawArtClippedToOutline(Texture2D tex, Rect2 dst, Rect2 src, bool isAmulet)
    {
        const int ROW = 2;
        int rows = Mathf.CeilToInt(dst.Size.Y / ROW);
        var poly = InnerOutline(isAmulet);

        for (int i = 0; i < rows; i++)
        {
            float y0 = dst.Position.Y + i * ROW;
            float h = Mathf.Min(ROW, dst.Position.Y + dst.Size.Y - y0);
            if (h <= 0f) break;

            // 取该行中线问轮廓边界，避免边缘抖动
            var (left, right) = SpanAt(poly, y0 + h * 0.5f);
            if (right - left <= 0.5f) continue;

            float x0 = left;
            float full = right - left;

            // 对应的源矩形：按比例映射到 src
            float v0 = (y0 - dst.Position.Y) / dst.Size.Y;
            float vh = h / dst.Size.Y;
            var s = new Rect2(
                src.Position.X + (0f) * src.Size.X,
                src.Position.Y + v0 * src.Size.Y,
                src.Size.X,
                vh * src.Size.Y);

            DrawTextureRectRegion(tex, new Rect2(x0, y0, full, h), s);
        }
    }

    /// <summary>
    /// 立绘可绘区域的**外框内沿**多边形。
    ///
    /// 必须与 DrawFollowerArch / DrawAmuletFrame 里那个 inner 多边形是同一组坐标 ——
    /// 之前另写了一条贝塞尔曲线去近似，形状对不上，
    /// 于是边框和立绘之间露出了一圈底色（看起来就是「立绘比框小一圈」）。
    /// 现在两边共用这一个定义，永远严丝合缝。
    /// </summary>
    private static Vector2[] InnerOutline(bool isAmulet)
    {
        if (isAmulet)
        {
            return new Vector2[]
            {
                new(11f, 7f), new(W - 11f, 7f), new(W - 6f, 18f),
                new(W - 6f, H - 9f), new(6f, H - 9f), new(6f, 18f),
            };
        }

        // 与 DrawFollowerArch 的 inner 完全一致，再整体内缩 1px
        // （描边有宽度，不内缩会让立绘压到描边上）。
        return new Vector2[]
        {
            new(7f, 37f),
            new(W * 0.24f, 13f),
            new(W * 0.5f, 7f),
            new(W * 0.76f, 13f),
            new(W - 7f, 37f),
            new(W - 7f, H - 10f),
            new(7f, H - 10f),
        };
    }

    /// <summary>多边形的外接矩形。</summary>
    private static Rect2 OutlineBounds(Vector2[] poly)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var p in poly)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// 求多边形在水平线 y 处的左右边界（扫描线裁剪的核心）。
    ///
    /// 遍历每条边，凡是跨越 y 的边就求交点，取最小与最大。
    /// 直接用 inner 多边形算，不再另做近似曲线 —— 形状必然一致。
    /// </summary>
    private static (float left, float right) SpanAt(Vector2[] poly, float y)
    {
        float min = float.MaxValue, max = float.MinValue;

        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];

            // 水平边不参与求交
            if (Mathf.IsEqualApprox(a.Y, b.Y)) continue;

            float lo = Mathf.Min(a.Y, b.Y);
            float hi = Mathf.Max(a.Y, b.Y);
            if (y < lo || y > hi) continue;

            float t = (y - a.Y) / (b.Y - a.Y);
            float x = a.X + (b.X - a.X) * t;

            if (x < min) min = x;
            if (x > max) max = x;
        }

        return min > max ? (0f, 0f) : (min, max);
    }

    /// <summary>
    /// 从纹理里取一块与目标区域等宽高比的源矩形（居中裁切）。
    ///
    /// 这样 DrawTextureRectRegion 画出来既不变形，也不会超出目标矩形 ——
    /// 等于用源矩形实现了「按边框裁剪」。
    /// </summary>
    private static Rect2 FitSourceRect(Vector2 texSize, Vector2 boxSize)
    {
        if (texSize.X <= 0 || texSize.Y <= 0 || boxSize.X <= 0 || boxSize.Y <= 0)
            return new Rect2(0, 0, texSize.X, texSize.Y);

        float boxAspect = boxSize.X / boxSize.Y;
        float texAspect = texSize.X / texSize.Y;

        if (texAspect > boxAspect)
        {
            // 纹理更宽：左右各裁掉一部分
            float w = texSize.Y * boxAspect;
            return new Rect2((texSize.X - w) * 0.5f, 0, w, texSize.Y);
        }
        else
        {
            // 纹理更高：上下各裁掉一部分
            float h = texSize.X / boxAspect;
            return new Rect2(0, (texSize.Y - h) * 0.5f, texSize.X, h);
        }
    }

    /// <summary>
    /// 棋子的可绘区域多边形（上拱下方）。
    ///
    /// 立绘必须被这个形状裁掉 —— 之前用的是矩形 Rect2，
    /// 于是拱顶被方形立绘填满，看起来「立绘超出了边框」。
    /// </summary>
    private static Vector2[] ArtClipPolygon(bool isAmulet)
    {
        if (isAmulet)
        {
            return new Vector2[]
            {
                new(11f, 9f), new(W - 11f, 9f),
                new(W - 6f, 20f), new(W - 6f, H - 12f),
                new(6f, H - 12f), new(6f, 20f),
            };
        }

        // 随从：与 DrawFollowerArch 的 inner 多边形一致，再内缩 2px
        return new Vector2[]
        {
            new(9f, 38f),
            new(W * 0.24f, 14f),
            new(W * 0.5f, 8f),
            new(W * 0.76f, 14f),
            new(W - 9f, 38f),
            new(W - 9f, H - 11f),
            new(9f, H - 11f),
        };
    }

    private void DrawArtwork(Color clanCol, CardDef def, bool isAmulet)
    {
        // 立绘区域必须是**内框的外接矩形**，不能手写常量。
        // 之前写死 Rect2(8,14,W-16,H-30)，而内框顶点在 y=7 ——
        // 拱顶那 7px 根本没画立绘，露出底色，看起来就是「上方有黑色缺口」。
        var art = OutlineBounds(InnerOutline(isAmulet));

        var tex = ShadowverseFan.Ui.CardArtLoader.Load(def.CardId);
        if (tex != null)
        {
            // 立绘必须被棋子边框裁掉。
            // DrawTextureRect 是**等比铺满**目标矩形：卡牌立绘的宽高比
            // 与棋子的 96x124 不同，等比缩放后必然有一边溢出，
            // 直接画会盖住刚画好的边框，看起来像贴歪了的贴纸。
            //
            // 用 ClipChildren 行不通（那是 Control 的子节点裁剪，
            // 对 _Draw 里的自绘无效），只能靠 CanvasItem 的
            // draw_set_clip / ClipContents 或手动计算贴合区域。
            // 这里用「按 art 比例裁源矩形」：取纹理中心最大可用块，
            // 做到不变形且绝不越界。
            var src = FitSourceRect(tex.GetSize(), art.Size);

            // DrawTextureRectRegion 的源矩形必须落在纹理内。
            // 浮点宽高比不同会差出一两个像素，越界时 Godot 直接画空白 ——
            // 表现就是「路径对、文件在、却是白块」。这里钳到纹理边界内。
            var ts = tex.GetSize();
            float sw = Mathf.Min(src.Size.X, ts.X);
            float sh = Mathf.Min(src.Size.Y, ts.Y);
            float sx = Mathf.Clamp(src.Position.X, 0f, Mathf.Max(0f, ts.X - sw));
            float sy = Mathf.Clamp(src.Position.Y, 0f, Mathf.Max(0f, ts.Y - sh));

            // 按拱形轮廓逐行裁切。
            //
            // CanvasItem 没有「按多边形裁剪自绘」的 API，ClipContents 只能裁矩形，
            // 所以用扫描线：每一行只画该行在轮廓内的那一段。
            //
            // 行高 2px 足够 —— 棋子只有 96 宽，再细肉眼也看不出阶梯。
            DrawArtClippedToOutline(tex, art, new Rect2(sx, sy, sw, sh), isAmulet);
            return;
        }

        // 没有立绘的卡（DIY 卡、新卡）也要按拱形轮廓画，
        // 不能画成一块方形黑底 —— 那样一眼就能看出「这张卡没做」。
        // 逐行裁切到轮廓内，与有立绘的卡保持同一种外形。
        DrawProceduralArtClipped(clanCol, art, isAmulet);
    }

    /// <summary>
    /// 程序化卡面（无立绘时）。形状与有立绘时一致，按轮廓逐行裁切。
    ///
    /// 内容用职业色做主调：上暗下亮 + 同心圆纹样，
    /// 让「这是什么职业的随从」在桌面上也能一眼分辨。
    /// </summary>
    private void DrawProceduralArtClipped(Color clanCol, Rect2 box, bool isAmulet)
    {
        const int ROW = 2;
        int rows = Mathf.CeilToInt(box.Size.Y / ROW);
        var poly = InnerOutline(isAmulet);

        Color top = clanCol.Darkened(0.45f).Lerp(new Color("141020"), 0.30f);
        Color bot = clanCol.Darkened(0.20f).Lerp(new Color("0B0F1A"), 0.35f);

        for (int i = 0; i < rows; i++)
        {
            float y0 = box.Position.Y + i * ROW;
            float h = Mathf.Min(ROW, box.Position.Y + box.Size.Y - y0);
            if (h <= 0f) break;

            var (left, right) = SpanAt(poly, y0 + h * 0.5f);
            if (right - left <= 0.5f) continue;

            // 上下渐变：越靠下越亮
            float t = (y0 - box.Position.Y) / Mathf.Max(1f, box.Size.Y);
            DrawRect(new Rect2(left, y0, right - left, h), top.Lerp(bot, t));
        }

        // 纹样（同样受轮廓约束：只在轮廓内画）
        Vector2 c = box.Position + box.Size * 0.5f;
        float r1 = box.Size.X * 0.34f;
        float r2 = box.Size.X * 0.20f;

        if (PointInsideOutline(c, poly))
        {
            DrawCircle(c, r1, clanCol.Lightened(0.45f) with { A = 0.38f });
            DrawCircle(c, r2, clanCol.Lightened(0.65f) with { A = 0.30f });
            DrawArc(c, box.Size.X * 0.32f, 0, Mathf.Tau, 30,
                    new Color(0.90f, 0.94f, 1f, 0.16f), 1.2f);
            DrawArc(c, box.Size.X * 0.22f, 0, Mathf.Tau, 24,
                    new Color(0.90f, 0.94f, 1f, 0.12f), 1.0f);
        }
    }

    /// <summary>点是否在轮廓多边形内（射线法）。</summary>
    private static bool PointInsideOutline(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y)
                         / (poly[j].Y - poly[i].Y) + poly[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private void DrawSwordBadge(Vector2 c, int atk, Font font, Color numCol)
    {
        Vector2[] poly =
        {
            new(c.X, c.Y - 15f), new(c.X + 10f, c.Y - 2f), new(c.X + 5f, c.Y + 3f),
            new(c.X + 8f, c.Y + 8f), new(c.X + 3f, c.Y + 13f),
            new(c.X - 3f, c.Y + 13f), new(c.X - 8f, c.Y + 8f),
            new(c.X - 5f, c.Y + 3f), new(c.X - 10f, c.Y - 2f),
        };
        DrawColoredPolygon(poly, new Color("D97706"));
        var inner = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            inner[i] = c + (poly[i] - c) * 0.80f;
        DrawColoredPolygon(inner, new Color("1D4ED8"));

        string t = atk.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 15);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y + 6f), t,
                   HorizontalAlignment.Left, -1, 15, numCol);
    }

    private void DrawShieldBadge(Vector2 c, int life, Font font, Color numCol)
    {
        Vector2[] poly =
        {
            new(c.X, c.Y - 15f), new(c.X + 11f, c.Y - 10f), new(c.X + 10f, c.Y + 2f),
            new(c.X + 6f, c.Y + 10f), new(c.X, c.Y + 15f), new(c.X - 6f, c.Y + 10f),
            new(c.X - 10f, c.Y + 2f), new(c.X - 11f, c.Y - 10f),
        };
        DrawColoredPolygon(poly, new Color("D97706"));
        var inner = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            inner[i] = c + (poly[i] - c) * 0.80f;
        DrawColoredPolygon(inner, new Color("991B1B"));

        string t = life.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 15);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y + 6f), t,
                   HorizontalAlignment.Left, -1, 15, numCol);
    }

    /// <summary>
    /// 底部能力标记 —— 三态：红书页（谢幕曲）/ 绿旗（时机能力）/ 空（都没有）。
    /// 空态**不画任何东西**。
    /// </summary>
    private void DrawAbilityBadge(Vector2 c, Font font)
    {
        switch (_badge)
        {
            case UnitAbilityBadge.LastWords: DrawRedPageIcon(c); break;
            case UnitAbilityBadge.Triggered: DrawGreenFlagIcon(c); break;
            default: break; // None：留空
        }
    }

    /// <summary>红色书页：一个上窄下宽的书页轮廓 + 两道书页纹。</summary>
    private void DrawRedPageIcon(Vector2 c)
    {
        float w = 13f, h = 15f;
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f));

        Vector2[] page =
        {
            new(c.X - w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.42f, c.Y + h * 0.5f),
            new(c.X - w * 0.42f, c.Y + h * 0.5f),
        };
        DrawColoredPolygon(page, new Color("DC2626"));
        DrawPolyline(new[] { page[0], page[1], page[2], page[3], page[0] },
                     new Color("FCA5A5"), 1.2f);

        DrawLine(new Vector2(c.X - w * 0.25f, c.Y - h * 0.22f),
                 new Vector2(c.X + w * 0.25f, c.Y - h * 0.22f),
                 new Color(1f, 0.85f, 0.85f, 0.75f), 1f);
        DrawLine(new Vector2(c.X - w * 0.22f, c.Y + h * 0.05f),
                 new Vector2(c.X + w * 0.22f, c.Y + h * 0.05f),
                 new Color(1f, 0.85f, 0.85f, 0.6f), 1f);
    }

    /// <summary>绿色旗帜：旗杆 + 三角旗面。</summary>
    private void DrawGreenFlagIcon(Vector2 c)
    {
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f));

        // 旗杆
        DrawLine(new Vector2(c.X - 4f, c.Y - 8f), new Vector2(c.X - 4f, c.Y + 8f),
                 new Color("E2E8F0"), 1.6f);

        Vector2[] flag =
        {
            new(c.X - 4f, c.Y - 8f),
            new(c.X + 7f, c.Y - 4.5f),
            new(c.X - 4f, c.Y - 1f),
        };
        DrawColoredPolygon(flag, new Color("22C55E"));
        DrawPolyline(new[] { flag[0], flag[1], flag[2], flag[0] },
                     new Color("BBF7D0"), 1.1f);
    }

    private void DrawCountdown(CardDef def, Font font)
    {
        int cd = 0;
        try
        {
            var key = CurrentController?.Schema.Counter("countdown");
            if (key.HasValue && Entity.Counters != null
                && Entity.Counters.TryGetValue(key.Value, out int v)) cd = v;
        }
        catch { }

        if (cd <= 0) return;

        string t = cd.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 28);
        var pos = new Vector2(W - sz.X - 6f, H - 10f);
        DrawString(font, pos + new Vector2(1.5f, 1.5f), t,
                   HorizontalAlignment.Left, -1, 28, new Color(0, 0, 0, 0.8f));
        DrawString(font, pos, t, HorizontalAlignment.Left, -1, 28, new Color("E0F2FE"));
    }

    public static BattleSessionController CurrentController { get; set; }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    OnClicked?.Invoke(Entity);
                    if (_canAttack)
                    {
                        _dragging = true;
                        OnDragAttackStarted?.Invoke(Entity, mb.GlobalPosition);
                    }
                }
                else if (_dragging)
                {
                    _dragging = false;
                    OnDragAttackReleased?.Invoke(Entity, mb.GlobalPosition);
                }
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                OnRightClicked?.Invoke(Entity);
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            OnDragAttackMoved?.Invoke(Entity, mm.GlobalPosition);
        }
    }
}