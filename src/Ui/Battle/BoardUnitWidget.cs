using System;
using System.Collections.Generic;
using System.Linq;
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


    /// <summary>
    /// 点击回调。返回 true 表示这次点击已被「选择目标（PendingChoice）」消费，
    /// 调用方不得再启动拖拽攻击 —— 否则点选我方随从当效果目标时，
    /// 随从会先被上浮成攻击姿态再落下，看起来像「向下瞬移」。
    /// </summary>
    public Func<RuntimeEntity, bool> OnClicked;

    private bool _suppressDraw;

    /// <summary>
    /// 只隐藏绘制、仍占住容器格子。
    ///
    /// 绝不能用 <c>Visible = false</c>：Godot 的 HBoxContainer 一旦看到子节点不可见，
    /// 会立刻把其余兄弟往中间挤压重排 —— 表现就是「一个随从攻击，其他随从瞬间位移」。
    /// </summary>
    public bool SuppressDraw
    {
        get => _suppressDraw;
        set
        {
            if (_suppressDraw == value) return;
            _suppressDraw = value;
            QueueRedraw();
        }
    }
    public event Action<RuntimeEntity> OnRightClicked;
    public event Action<RuntimeEntity, Vector2> OnDragAttackStarted;
    public event Action<RuntimeEntity, Vector2> OnDragAttackMoved;
    public event Action<RuntimeEntity, Vector2> OnDragAttackReleased;

    private bool _selected;
    private bool _canAttack;

    /// <summary>左键已按下但尚未松开。用于区分「轻点查看」与「拖拽攻击」。</summary>
    private bool _pointerDown;
    /// <summary>按下时的全局坐标，松手时比较位移量。</summary>
    private Vector2 _pressGlobalPos;
    /// <summary>超过这个位移量才算拖拽，否则算轻点。8px 是常见触控阈值。</summary>
    private const float DragThreshold = 8f;
    /// <summary>能否攻击敌方主战者。false 表示只能攻击随从（画橙圈）。</summary>
    private bool _canAttackLeader;

    /// <summary>
    /// 候选态：当前有一处挂起选择正在等玩家点它。
    /// 与 _selected 不同 —— _selected 是「已被选中的那个」，
    /// _isCandidate 是「可以被点的那些」。
    /// </summary>
    private bool _isCandidate;

    private bool _isAttackLegal;
    private bool _isAttackHovered;
    private string _attackDenyReason;

    /// <summary>
    /// 拖拽攻击时的目标态。
    /// 合法目标画绯红呼吸边框，鼠标悬停时叠锁定准星；
    /// 非法敌人压暗，悬停时画红叉并给出原因。
    /// </summary>
    public void SetAttackTargetState(bool isLegal, bool isHovered, string denyReason = null)
    {
        if (_isAttackLegal == isLegal && _isAttackHovered == isHovered
            && _attackDenyReason == denyReason) return;
        _isAttackLegal = isLegal;
        _isAttackHovered = isHovered;
        _attackDenyReason = denyReason;
        QueueRedraw();
    }

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
        // 攻击瞄准态：合法目标亮绯红呼吸框，非法目标压暗并可带原因。
        _dimmed = dimmed;
        QueueRedraw();
    }

    public void Setup(RuntimeEntity e, bool selected, bool canAttack, bool canAttackLeader = true)
    {
        Entity = e ?? throw new ArgumentNullException(nameof(e));
        _selected = selected;
        _canAttack = canAttack;
        _canAttackLeader = canAttackLeader;
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

    public int VisualAttackOffset
    {
        get => _visualAttackOffset;
        set
        {
            if (_visualAttackOffset == value) return;
            _visualAttackOffset = value;
            QueueRedraw();
        }
    }

    private int _visualAttackOffset;

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

        // 底部正中徽章有 2 个以上时要轮播，必须持续推进时间并重绘。
        _badgeCycleTime += (float)delta;
        if (CollectBottomCenterBadges().Count > 1) QueueRedraw();

        if (_triggerFlash <= 0f) return;
        _triggerFlash = Mathf.Max(0f, _triggerFlash - (float)delta * 1.6f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_suppressDraw) return;
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

            // 总位移 = 一次性演出（攻击/受击）+ 布局重排残留。
            // 两个通道分开存，攻击动画不会把布板位移冲掉。
            DrawSetTransform(mo.Offset + mo.LayoutOffset + pivotShift, 0f, new Vector2(sxScale, mo.Scale));

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

        DrawCardFrame(clanCol, def);

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

            int shownAtk = Math.Max(0, Entity.Attack + VisualAttackOffset - evoAtk);
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

        // 攻击瞄准态：合法目标绯红呼吸框；悬停叠锁定准星。
        if (_isAttackLegal)
        {
            float ap = 0.55f + 0.45f * Mathf.Sin(_pulseTime * 5f);
            var crimson = new Color(0.98f, 0.30f, 0.35f, ap);
            DrawRect(new Rect2(-5, -5, W + 10, H + 10), crimson, false, 3f);
            if (_isAttackHovered)
            {
                var ctr = new Vector2(W * 0.5f, H * 0.5f);
                DrawArc(ctr, 26f, 0, Mathf.Tau, 32, crimson, 2.5f);
                DrawArc(ctr, 20f, 0, Mathf.Tau, 32, new Color(1f, 0.85f, 0.35f, ap), 1.5f);
                DrawLine(ctr + new Vector2(-30, 0), ctr + new Vector2(-16, 0), crimson, 2f);
                DrawLine(ctr + new Vector2(16, 0), ctr + new Vector2(30, 0), crimson, 2f);
                DrawLine(ctr + new Vector2(0, -30), ctr + new Vector2(0, -16), crimson, 2f);
                DrawLine(ctr + new Vector2(0, 16), ctr + new Vector2(0, 30), crimson, 2f);
            }
        }
        else if (_isAttackHovered)
        {
            // 非法敌方目标：红叉 + 原因
            var deny = new Color(0.92f, 0.25f, 0.25f, 0.95f);
            DrawLine(new Vector2(-4, -4), new Vector2(W + 4, H + 4), deny, 3.5f);
            DrawLine(new Vector2(W + 4, -4), new Vector2(-4, H + 4), deny, 3.5f);
            if (!string.IsNullOrEmpty(_attackDenyReason))
            {
                var fs = ThemeDB.FallbackFont;
                var sz = fs.GetStringSize(_attackDenyReason, HorizontalAlignment.Left, -1, 11);
                var bp = new Vector2((W - sz.X) * 0.5f - 4f, -22f);
                DrawRect(new Rect2(bp, new Vector2(sz.X + 8f, 18f)), new Color(0.10f, 0.02f, 0.02f, 0.92f));
                DrawString(fs, bp + new Vector2(4f, 13f), _attackDenyReason,
                           HorizontalAlignment.Left, -1, 11, new Color(1f, 0.75f, 0.75f));
            }
        }

        // 压暗态：非候选棋子淡出
        if (_dimmed)
            DrawRect(new Rect2(0, 0, W, H), new Color(0.02f, 0.03f, 0.06f, 0.62f));

        if (_selected) DrawRect(new Rect2(-3, -3, W + 6, H + 6), SvWbTheme.AccentCyan, false, 2.5f);

        // 可攻击光环分两档：
        //   绿圈 = 能攻击随从，也能打主战者
        //   橙圈 = 只能攻击随从（对方有守护挡路、或自己带 cannot_attack_leaders、
        //          或突进/进化当回合）
        // 两者都画绿圈会让玩家误以为可以直接糊脸，点下去才被拒。
        if (_canAttack && !_selected)
        {
            var ring = _canAttackLeader
                ? new Color(0.20f, 0.83f, 0.60f, 0.85f)   // 绿
                : new Color(0.95f, 0.60f, 0.15f, 0.90f);  // 橙
            DrawRect(new Rect2(-2, -2, W + 4, H + 4), ring, false, 2.5f);
        }

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

        // 关键词徽章（毁灭 / 虹吸 / 免进化点 / 多次攻击）已统一移到
        // DrawAbilityBadge 的底部正中轮播里 —— 画在左下角会挡住攻击力数字。

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

    /// <summary>
    /// 随从与护符共用的卡框。
    ///
    /// 四层结构：外描边 → 稀有度主框 → 内描边 → 内底，再加一道顶部内高光。
    /// 早先随从与护符各画一套（护符是米色六边形），且都只有一层纯色，
    /// 在战场底色上几乎看不出来。现在两者共用同一套形状与配色。
    /// </summary>
    private void DrawCardFrame(Color clanCol, CardDef def)
    {
        // 1) 外描边：比主框外扩 2px 的深色边 —— 让框从战场底色里分离出来。
        Vector2[] outer =
        {
            new(1f, 36f),
            new(W * 0.22f - 2f, 6f),
            new(W * 0.5f, 0f),
            new(W * 0.78f + 2f, 6f),
            new(W - 1f, 36f),
            new(W - 1f, H - 4f),
            new(1f, H - 4f),
        };
        DrawColoredPolygon(outer, new Color("0B0F1A"));

        // 2) 主框：稀有度色（与手牌稀有度配色一致）。
        // 桌面上卡很小，只染最外一圈 —— 整块铺满稀有度色会让场面花掉。
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
        DrawColoredPolygon(frame, RarityRim(def));

        // 3) 内描边：比内框外扩 2px 的深色边 —— 让立绘不直接压在主框上。
        Vector2[] bevel =
        {
            new(4f, 38f),
            new(W * 0.24f - 2f, 14f),
            new(W * 0.5f, 8f),
            new(W * 0.76f + 2f, 14f),
            new(W - 4f, 38f),
            new(W - 4f, H - 7f),
            new(4f, H - 7f),
        };
        DrawColoredPolygon(bevel, new Color("0B0F1A"));

        // 4) 内底：深色，托住立绘。
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

        // 5) 顶部内高光：沿内框上沿的一道 1px 浅线，给框一点立体感。
        DrawPolyline(new[] { inner[1], inner[2], inner[3] },
                     new Color(1f, 1f, 1f, 0.30f), 1f);
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

    /// <summary>
    /// 把立绘按棋子外轮廓逐行裁切后画出来。
    ///
    /// 参数 src 是「已按比例裁好的源矩形」，dst 是目标矩形。
    /// 对每一行：算出该行在轮廓内的左右边界，只画这一段。
    /// </summary>
    /// <summary>
    /// 棋子立绘预裁切缓存：把方形立绘按拱形轮廓一次性裁成透明底图。
    ///
    /// 原来每帧跑 62 行扫描线、发 62 次 DrawTextureRectRegion；
    /// 场上 10 个棋子就是 620 次绘制指令，动画期间每帧重跑。
    /// 裁一次缓存起来，之后每帧只需 1 次 DrawTextureRect。
    /// </summary>
    private static readonly Dictionary<(int, bool, bool), Texture2D> _clippedCache = new();

    /// <summary>
    /// 玩家切换了某卡的立绘版本（原画⇄异画）时调用：
    /// 只清该卡的预裁切图，其余卡不动 —— 一次性换整套素材才用 ClearCache。
    /// </summary>
    public static void InvalidateArtCache(int cardId)
    {
        foreach (var key in _clippedCache.Keys.Where(k => k.Item1 == cardId).ToList())
            _clippedCache.Remove(key);
    }

    /// <summary>
    /// 双线性插值采样（187 号文档 专题一 ②）。
    ///
    /// 最近邻 GetPixel 在缩小时只取一个纹素，会把原画的高频细节丢掉，
    /// 表现就是硬边、锯齿与马赛克。四邻域加权平均后边缘才能真正平滑。
    /// </summary>
    private static Color SampleBilinear(Image img, float u, float v)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w <= 0 || h <= 0) return new Color(0, 0, 0, 0);

        float x = Mathf.Clamp(u - 0.5f, 0f, w - 1f);
        float y = Mathf.Clamp(v - 0.5f, 0f, h - 1f);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
        float fx = x - x0, fy = y - y0;

        Color c00 = img.GetPixel(x0, y0), c10 = img.GetPixel(x1, y0);
        Color c01 = img.GetPixel(x0, y1), c11 = img.GetPixel(x1, y1);
        return c00.Lerp(c10, fx).Lerp(c01.Lerp(c11, fx), fy);
    }

    private static Texture2D GetClippedArt(int cardId, bool isAmulet, bool evolved = false)
    {
        var key = (cardId, isAmulet, evolved);
        if (_clippedCache.TryGetValue(key, out var hit)) return hit;

        // 进化后优先用进化立绘；没有 _e 图时回落进化前，缓存键不同互不污染。
        var tex = evolved
            ? ShadowverseFan.Ui.CardArtLoader.LoadEvolved(cardId)
              ?? ShadowverseFan.Ui.CardArtLoader.Load(cardId)
            : ShadowverseFan.Ui.CardArtLoader.Load(cardId);
        if (tex == null) { _clippedCache[key] = null; return null; }

        var img = tex.GetImage();
        if (img == null || img.IsEmpty()) { _clippedCache[key] = null; return null; }
        img = img.Duplicate() as Image;
        if (img == null) { _clippedCache[key] = null; return null; }
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgba8);

        var art = OutlineBounds(InnerOutline(isAmulet));
        var poly = InnerOutline(isAmulet);
        var src = FitSourceRect(tex.GetSize(), art.Size);

        // 3 倍超采样（187 号文档 专题一 ②）：
        // 早先直接以 art 尺寸（约 84×109）为输出，并用 GetPixel 最近邻取样，
        // 等于把几百像素的原画硬塞进小图 —— 棋子立绘看起来像马赛克。
        // 现在按 3 倍生成（252×327）再交给 GPU 缩回逻辑尺寸，配合双线性插值，
        // 边缘与细节都能保住。
        const int SS = 3;
        int logicalW = Mathf.Max(1, Mathf.RoundToInt(art.Size.X));
        int logicalH = Mathf.Max(1, Mathf.RoundToInt(art.Size.Y));
        int dstW = logicalW * SS;
        int dstH = logicalH * SS;
        var outImg = Image.CreateEmpty(dstW, dstH, false, Image.Format.Rgba8);

        for (int y = 0; y < dstH; y++)
        {
            // 逻辑坐标：把超采样行号折回控件坐标，多边形跨度仍按逻辑像素算。
            float logicalY = art.Position.Y + (y + 0.5f) / SS;
            var (left, right) = SpanAt(poly, logicalY);
            if (right - left <= 0.5f) continue;

            // SpanAt 返回的是**控件坐标系**的绝对 X（例如 8..88），
            // 而 x0/x1 是输出图的下标（输出图从 0 开始、宽 dstW）。
            // 不减掉 art.Position.X，整张立绘就会右移 8px：
            // 右边被裁掉一条、左边留一条透明 —— 表现为棋子和卡框之间有黑边。
            float ox = art.Position.X;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((left - ox) * SS), 0, dstW - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((right - ox) * SS), 0, dstW);
            if (x1 <= x0) continue;

            for (int x = x0; x < x1; x++)
            {
                float u = src.Position.X + (x + 0.5f) / dstW * src.Size.X;
                float v = src.Position.Y + (y + 0.5f) / dstH * src.Size.Y;
                // 双线性插值替代最近邻 GetPixel，消除硬边与锯齿。
                outImg.SetPixel(x, y, SampleBilinear(img, u, v));
            }
        }

        // 超采样图同样要带 Mipmaps：绘制时缩回 1/3 尺寸，由硬件平滑降采样。
        try { outImg.GenerateMipmaps(); } catch { }
        var outTex = ImageTexture.CreateFromImage(outImg);
        _clippedCache[key] = outTex;
        return outTex;
    }

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
        // 随从与护符共用同一组内框坐标 —— 用户要求「与随从一致的卡框」。
        // isAmulet 入参保留只为兼容现有调用点，不再区分形状。
        _ = isAmulet;
        return new Vector2[]
        {
            new(6f, 36f),
            new(W * 0.24f, 12f),
            new(W * 0.5f, 6f),
            new(W * 0.76f, 12f),
            new(W - 6f, 36f),
            new(W - 6f, H - 9f),
            new(6f, H - 9f),
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

        // 进化后换进化立绘：Entity.IsEvolved 的用 <id>_e.png，
        // 没有进化立绘的卡回落进化前（LoadEvolved 返回 null）。
        bool wantEvolved = Entity?.IsEvolved == true;
        var tex = wantEvolved
            ? ShadowverseFan.Ui.CardArtLoader.LoadEvolved(def.CardId)
              ?? ShadowverseFan.Ui.CardArtLoader.Load(def.CardId)
            : ShadowverseFan.Ui.CardArtLoader.Load(def.CardId);
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
            // 预裁切缓存：命中时每帧只剩 1 次 DrawTextureRect。
            var clipped = GetClippedArt(def.CardId, isAmulet, wantEvolved);
            if (clipped != null)
            {
                DrawTextureRect(clipped, art, false);
                return;
            }

            // 缓存不可用（图片读不出来）时退回扫描线，至少不会白块。
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

    /// <summary>徽章轮换计时（秒）。</summary>
    private float _badgeCycleTime;

    /// <summary>
    /// 收集要画在**底部正中**的徽章。
    ///
    /// 关键词（毁灭 / 虹吸 / 免进化点 / 多次攻击）过去画在左下角，
    /// 会挡住攻击力数字 —— 全部并到底部正中，与谢幕曲、时机能力一起轮播。
    /// </summary>
    private List<(string Kind, int Param)> CollectBottomCenterBadges()
    {
        var list = new List<(string Kind, int Param)>();
        var e = Entity;
        if (e == null || e.IsSilenced) return list;

        // 谢幕曲被移除（或从未有过）的随从不再轮播红书页。
        if (_badge == UnitAbilityBadge.LastWords && !e.LastWordsRemoved)
            list.Add(("last_words", 0));
        else if (_badge == UnitAbilityBadge.Triggered) list.Add(("triggered", 0));

        var schema = CurrentController?.Schema;
        var st = CurrentController?.State;
        if (schema == null) return list;

        bool Has(string kw)
        {
            var key = schema.Keyword(kw);
            return st != null ? st.HasEffectiveKeyword(e, key) : e.Keywords.Contains(key);
        }

        if (Has("bane")) list.Add(("badge_bane", 0));
        if (Has("drain")) list.Add(("badge_drain", 0));
        if (Has("free_evolve") || Has("free_super_evolve")) list.Add(("badge_free_evolve", 0));

        var mkKey = schema.Keyword("max_attacks_per_turn");
        if (e.KeywordValues.TryGetValue(mkKey, out int maxAtk) && maxAtk > 1)
            list.Add(("badge_multi_attack", maxAtk));

        return list;
    }

    /// <summary>
    /// 底部正中徽章。
    ///
    /// 单个徽章常驻；多个徽章以 2 秒为周期轮换，交界处 0.22 秒淡入淡出。
    /// </summary>
    private void DrawAbilityBadge(Vector2 c, Font font)
    {
        var badges = CollectBottomCenterBadges();
        if (badges.Count == 0) return;

        const float Period = 2.0f;
        int idx = 0;
        float alpha = 1f;

        if (badges.Count > 1)
        {
            float total = _badgeCycleTime;
            idx = Mathf.PosMod(Mathf.FloorToInt(total / Period), badges.Count);
            float phase = total - Mathf.Floor(total / Period) * Period;
            if (phase < 0.22f) alpha = phase / 0.22f;
            else if (phase > Period - 0.22f) alpha = (Period - phase) / 0.22f;
            alpha = Mathf.Clamp(alpha, 0.15f, 1f);
        }

        var (kind, param) = badges[idx];
        if (kind == "last_words") { DrawRedPageIcon(c, alpha); return; }
        if (kind == "triggered") { DrawGreenFlagIcon(c, alpha); return; }

        var tex = KeywordSvgAtlas.Get(kind);
        if (tex == null) return;
        DrawCircle(c, 10.5f, new Color(0.05f, 0.06f, 0.09f, 0.88f * alpha));
        DrawTextureRect(tex, new Rect2(c - new Vector2(10, 10), new Vector2(20, 20)),
                        false, new Color(1, 1, 1, alpha));
        if (kind == "badge_multi_attack" && param > 1)
        {
            DrawString(font, c + new Vector2(8, 5), "x" + param,
                       HorizontalAlignment.Left, -1, 10, new Color(1f, 0.92f, 0.55f, alpha));
        }
    }

    /// <summary>红色书页：一个上窄下宽的书页轮廓 + 两道书页纹。</summary>
    private void DrawRedPageIcon(Vector2 c, float alpha = 1f)
    {
        float w = 13f, h = 15f;
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f * alpha));

        Vector2[] page =
        {
            new(c.X - w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.42f, c.Y + h * 0.5f),
            new(c.X - w * 0.42f, c.Y + h * 0.5f),
        };
        DrawColoredPolygon(page, new Color(0.86f, 0.15f, 0.15f, alpha));
        DrawPolyline(new[] { page[0], page[1], page[2], page[3], page[0] },
                     new Color(0.99f, 0.65f, 0.65f, alpha), 1.2f);

        DrawLine(new Vector2(c.X - w * 0.25f, c.Y - h * 0.22f),
                 new Vector2(c.X + w * 0.25f, c.Y - h * 0.22f),
                 new Color(1f, 0.85f, 0.85f, 0.75f * alpha), 1f);
        DrawLine(new Vector2(c.X - w * 0.22f, c.Y + h * 0.05f),
                 new Vector2(c.X + w * 0.22f, c.Y + h * 0.05f),
                 new Color(1f, 0.85f, 0.85f, 0.6f * alpha), 1f);
    }

    /// <summary>绿色旗帜：旗杆 + 三角旗面。</summary>
    private void DrawGreenFlagIcon(Vector2 c, float alpha = 1f)
    {
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f * alpha));

        // 旗杆
        DrawLine(new Vector2(c.X - 4f, c.Y - 8f), new Vector2(c.X - 4f, c.Y + 8f),
                 new Color(0.89f, 0.91f, 0.94f, alpha), 1.6f);

        Vector2[] flag =
        {
            new(c.X - 4f, c.Y - 8f),
            new(c.X + 7f, c.Y - 4.5f),
            new(c.X - 4f, c.Y - 1f),
        };
        DrawColoredPolygon(flag, new Color(0.13f, 0.77f, 0.37f, alpha));
        DrawPolyline(new[] { flag[0], flag[1], flag[2], flag[0] },
                     new Color(0.73f, 0.97f, 0.82f, alpha), 1.1f);
    }

    private void DrawCountdown(CardDef def, Font font)
    {
        // 191 号文档 问题 2：可堆叠护符（土之印 / 证据）的层数此前完全没画 ——
        // 老逻辑只读 countdown 计数器，而这两种资源的层数存在 earth_sigil / clues 里，
        // 玩家在场上根本看不出攒了几层。
        //
        // 判定走 Schema.StackableAmulets（引擎不写死具体卡号），
        // 并用「琥珀金圆章」与吟唱的冰蓝大数字区分开，一眼能分清是哪种资源。
        try
        {
            var schema = CurrentController?.Schema;
            if (schema != null && Entity?.Counters != null)
            {
                foreach (var counterName in schema.StackableAmulets.Keys)
                {
                    var skey = schema.Counter(counterName);
                    if (!skey.IsValid) continue;
                    if (!Entity.Counters.TryGetValue(skey, out int stackVal) || stackVal <= 0) continue;

                    // 层数圆章：右下角，暖金描边与吟唱的冰蓝数字形成对比。
                    Vector2 badge = new(W - 18f, H - 18f);
                    DrawCircle(badge, 13f, new Color(0.10f, 0.08f, 0.04f, 0.92f));
                    DrawArc(badge, 13f, 0, Mathf.Tau, 24, new Color("F59E0B"), 2.0f);

                    string st = stackVal.ToString();
                    var ssz = font.GetStringSize(st, HorizontalAlignment.Left, -1, 18);
                    var spos = new Vector2(badge.X - ssz.X * 0.5f, badge.Y + 6f);
                    DrawString(font, spos + new Vector2(1f, 1f), st,
                               HorizontalAlignment.Left, -1, 18, Colors.Black);
                    DrawString(font, spos, st,
                               HorizontalAlignment.Left, -1, 18, new Color("FEF08A"));
                    return;   // 可堆叠护符不再画吟唱数字，避免两个角标叠在一起
                }
            }
        }
        catch { }

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
                    // 挂起选目标时当场消费点击 —— 这是唯一需要在按下瞬间响应的情形。
                    if (CurrentController?.IsWaitingForInput == true
                        && OnClicked?.Invoke(Entity) == true)
                    {
                        AcceptEvent();
                        return;
                    }

                    // 其余情况**按下不弹窗**：玩家可能只是按住随从准备拖拽攻击，
                    // 按下瞬间就弹详情窗会挡住整个战场。
                    // 改为只记起点，等松手时按位移量决定是「轻点查看」还是「拖拽攻击」。
                    _pointerDown = true;
                    _dragging = false;
                    _pressGlobalPos = mb.GlobalPosition;
                    AcceptEvent();
                }
                else if (_pointerDown)
                {
                    _pointerDown = false;
                    if (_dragging)
                    {
                        _dragging = false;
                        OnDragAttackReleased?.Invoke(Entity, mb.GlobalPosition);
                    }
                    else
                    {
                        // 位移没超过阈值 = 轻点查看，此时才打开左上角详情弹窗。
                        OnClicked?.Invoke(Entity);
                    }
                    AcceptEvent();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                OnRightClicked?.Invoke(Entity);
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                OnDragAttackMoved?.Invoke(Entity, mm.GlobalPosition);
            }
            else if (_pointerDown && _canAttack
                     && mm.GlobalPosition.DistanceTo(_pressGlobalPos) >= DragThreshold)
            {
                // 位移超过阈值才升级成拖拽攻击 —— 这一步之后松手不会再弹详情窗。
                _dragging = true;
                OnDragAttackStarted?.Invoke(Entity, mm.GlobalPosition);
            }
        }
    }
}