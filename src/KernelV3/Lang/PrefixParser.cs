using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 前缀循环剥离器
//
// 这是对「把 when/whenever 当布尔条件」这个架构错误的修正。
//
// 错误做法（V3 早期）：
//   if (c.Current.IsAnyWord("if","when","whenever","at"))
//       SplitCondition(c)  → 塞进 BoolExpr
// 后果：卡变成「打出时判断条件」，而不是「在场上监听事件」。
//
// 正确做法（Gemini 设计）：
//   句首前缀是一个 Kleene 星号：Prefix* Body
//   每轮剥掉一个前缀，增量填充 TriggerSpec / Condition / Costs，
//   直到遇到真正的祈使句动词。
//
//   影之诗的句首前缀可以任意叠加：
//     [TurnScope] [Frequency] [TurnBoundary] [Header:] [WhenClause] [IfClause] Body
//   例：During your turn, once on each of your turns, whenever X, if Y, do Z.
// ============================================================================

/// <summary>前缀剥离过程中增量累积的能力规格。</summary>
public sealed class AbilityHeaderBuilder
{
    public CoreEvent Event = CoreEvent.OnPlay;
    public TargetSpec SubjectFilter;
    public TargetSpec SecondaryFilter;
    public ActiveZoneScope ActiveZone = ActiveZoneScope.Board;
    public TurnOwnerScope DuringTurn = TurnOwnerScope.Any;
    public int MaxTriggersPerTurn;
    public BoolExpr Condition = BoolExpr.True;
    public readonly List<CostSpec> Costs = new();
    /// <summary>是否已经显式设置了事件（用于 During 的 PassiveAura 兜底不覆盖真实事件）。</summary>
    public bool EventExplicit;

    /// <summary>并列的第二个事件（"play or Fuse"）。null 表示只有 Event 一个。</summary>
    public CoreEvent? SecondaryEvent;

    /// <summary>本段出现过 `Fuse: <材料>` 声明头（材料段本身不产出能力）。</summary>
    public bool HasFuse;

    /// <summary>`Fuse: <材料>` 里那个材料过滤条件（null = 不限）。</summary>
    public TargetSpec FuseMaterialFilter;

    /// <summary>护符的初始吟唱值。属于卡牌属性，不是代价。</summary>
    public int? CountdownValue;

    /// <summary>这是 Evolve: / Super-Evolve: 冒号头，只认玩家手动花费 EP/SEP 的进化。</summary>
    public bool ManualEvolveOnly;

    public TriggerSpec BuildTrigger() => new()
    {
        Event = Event,
        SecondaryEvent = SecondaryEvent,
        SubjectFilter = SubjectFilter,
        SecondaryFilter = SecondaryFilter,
        ActiveZone = ActiveZone,
        DuringTurn = DuringTurn,
        MaxTriggersPerTurn = MaxTriggersPerTurn,
        ManualEvolveOnly = ManualEvolveOnly,
    };
}

public sealed class PrefixParser
{
    private readonly TargetParser _target;
    private readonly Lexicon _lex;

    public PrefixParser(Lexicon lex, TargetParser target)
    {
        _lex = lex;
        _target = target;
    }

    /// <summary>循环剥离所有句首前缀，直到遇到祈使句动词。</summary>
    public void ParseAll(TokenCursor cur, AbilityHeaderBuilder b)
    {
        while (!cur.IsAtEnd)
        {
            int saved = cur.Position;

            if (cur.MatchWord("then")) { ConsumeDelimiter(cur); continue; }
            if (TryConsumeCountdownPrefix(cur, b)) continue;
            if (TryConsumeFusePrefix(cur, b)) continue;
            if (TryConsumeDuringTurn(cur, b)) continue;
            if (TryConsumeFrequency(cur, b)) continue;
            if (TryConsumeTurnBoundary(cur, b)) continue;
            if (TryConsumeKeywordHeader(cur, b)) continue;
            if (cur.Current.IsAnyWord("whenever", "when"))
            {
                // 从句里的**主语**是「谁算数」的判据，不是效果体的视角。
                // 必须打上标记，否则主语里的 another 会被当成效果体的 other
                // （"Whenever another allied follower enters the field, …" 的
                //  another 指本卡以外，不是事件主体以外 —— 后者恒假，能力永不触发）。
                // 用 try/finally 是因为 TryConsumeWhenClause 有多个返回点。
                bool consumed;
                _target.InTriggerSubject = true;
                try { consumed = TryConsumeWhenClause(cur, b); }
                finally { _target.InTriggerSubject = false; }
                if (consumed) continue;
            }
            if (cur.Current.IsWord("if") && TryConsumeIfClause(cur, b)) continue;

            cur.Seek(saved);
            break;
        }
    }

    /// <summary>吃掉前缀末尾的分隔符：逗号 / 冒号 / 连字符。</summary>
    private static bool ConsumeDelimiter(TokenCursor cur)
    {
        if (cur.Current.Kind == TokKind.Comma || cur.Current.Kind == TokKind.Colon
            || cur.Current.Kind == TokKind.Dash)
        {
            cur.Advance();
            return true;
        }
        return false;
    }

    /// <summary>剥离 "Countdown (N)" 前缀（后面直接跟 Whenever / At the end / Last Words）。</summary>
    private bool TryConsumeCountdownPrefix(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("countdown")) return false;
        int param = 0;
        if (cur.Current.Kind == TokKind.OpenParen)
        {
            cur.Advance();
            if (cur.Current.Kind == TokKind.Number) { param = cur.Current.Value; cur.Advance(); }
            cur.TryMatch(TokKind.CloseParen);
        }
        ConsumeDelimiter(cur);

        // Countdown (N) 是护符自身的初始吟唱值，不是代价。
        // 它与紧跟的 Last Words / Whenever 等能力头**完全独立**：
        //   · 吟唱是属性：拍下来时为 N，每回合 -1，归零破坏
        //   · 谢幕曲是触发：被破坏就触发（无论为何被破坏）
        // 旧实现把它当 CostSpec 并转成 "ctr:countdown >= N" 条件，
        // 等于要求「吟唱还没减完才能触发谢幕曲」，语义完全错。
        b.CountdownValue = param;
        return true;
    }

    /// <summary>剥离 "Fuse: <Material> [后续头]"。</summary>
    private bool TryConsumeFusePrefix(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("fuse")) return false;
        if (!cur.Match(TokKind.Colon)) { cur.Seek(save); return false; }

        b.Event = CoreEvent.OnFuse;
        b.EventExplicit = true;
        // 材料声明段不产出能力，所以「这张卡能不能融合」必须单独记一个事实位。
        b.HasFuse = true;

        // 材料描述不是垃圾：`Fuse: Loot cards` / `Fuse: Artifact amulets`
        // 决定了「什么卡才能喂进去」。先把它切出来，再交给目标语法解析，
        // 否则任何一张手牌都能当材料。
        {
            int matStart = cur.Position;
            while (!cur.IsAtEnd &&
                   !cur.Current.IsAnyWord("whenever", "when", "fanfare", "evolve", "super-evolve",
                                          "at", "draw", "deal", "transform", "summon", "give", "destroy"))
            {
                cur.Advance();
            }
            var matToks = new List<Token>();
            for (int k = matStart; k < cur.Position; k++) matToks.Add(cur.All[k]);
            if (matToks.Count > 0)
                b.FuseMaterialFilter = _target.TryParse(new TokenCursor(matToks));
        }

        // "When you've Fused both to this card," —— 融合条件从句。
        // 从句本身要消费掉，但它携带的条件不能一起丢：
        // "both" 的意思是「两个融合材料都到位了」，即 fuse_count >= 2，
        // 少了这一条，一次融合就会提前满足能力条件。
        if (cur.Current.IsAnyWord("when", "whenever"))
        {
            int probe = cur.Position;
            cur.Advance();
            if (cur.Current.IsAnyWord("you've", "you"))
                ConsumeFusedToCardClause(cur, b);
            else cur.Seek(probe);
        }
        return true;
    }

    /// <summary>
    /// 消费 "you've Fused [both] to this card," 的剩余部分，并按是否出现 both
    /// 折出 fuse_count 下限条件。调用前 cur 必须停在 you've / you 之后。
    /// </summary>
    private void ConsumeFusedToCardClause(TokenCursor cur, AbilityHeaderBuilder b)
    {
        bool fusedBoth = false;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Comma)
        {
            if (cur.Current.IsWord("both")) fusedBoth = true;
            cur.Advance();
        }
        if (cur.Current.Kind == TokKind.Comma) cur.Advance();

        if (!fusedBoth) return;
        // "both" 指的是**两种不同材料**都到位（Ominous Artifact α 的
        // Fuse 行限定了只能是 β 和 γ），所以判据是「融合种类数 >= 2」，
        // 不是「融合次数 >= 2」—— 喂两张同名材料次数也是 2，但那不算 both。
        b.Condition = BoolExpr.And(b.Condition, new CompareExpr
        {
            Left = NumExpr.Ctr(_lex.Counter("fuse_kinds")),
            Op = CompareOp.Ge,
            Right = NumExpr.Const(2),
        });
    }

    // ---------- 1. During your turn, ----------

    private bool TryConsumeDuringTurn(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("during")) return false;

        if (cur.MatchPhrase("your", "opponent's", "turn") || cur.MatchPhrase("your", "opponent", "turn"))
            b.DuringTurn = TurnOwnerScope.Opponent;
        else if (cur.MatchPhrase("your", "turn"))
            b.DuringTurn = TurnOwnerScope.Self;
        else { cur.Seek(save); return false; }

        // 若后面还有 when/if，事件会被后续轮次覆盖；此处先给 PassiveAura 兜底
        if (!b.EventExplicit) b.Event = CoreEvent.PassiveAura;
        ConsumeDelimiter(cur);
        return true;
    }

    // ---------- 2. Once on each of your turns, ----------

    private bool TryConsumeFrequency(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("once")) return false;

        if (cur.MatchPhrase("on", "each", "of", "your", "opponent's", "turns"))
            b.DuringTurn = TurnOwnerScope.Opponent;
        else if (cur.MatchPhrase("on", "each", "of", "your", "turns"))
            b.DuringTurn = TurnOwnerScope.Self;
        else if (cur.MatchPhrase("per", "turn"))
        { }
        else { cur.Seek(save); return false; }

        b.MaxTriggersPerTurn = 1;
        ConsumeDelimiter(cur);
        return true;
    }

    // ---------- 3. At the start/end of your turn, ----------

    private bool TryConsumeTurnBoundary(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchPhrase("at", "the"))
        {
            // "At the end of a turn" 变体：直接 at + end
            if (cur.MatchWord("at") && cur.Current.IsAnyWord("end", "start"))
            {
                bool isS = cur.MatchWord("start");
                if (!isS) cur.MatchWord("end");
                cur.TryMatchAnyWord("of");
                cur.TryMatchAnyWord("the", "a", "your", "an");
                cur.TryMatchAnyWord("opponent's", "opponent");
                cur.TryMatchAnyWord("turn", "turns");
                b.Event = isS ? CoreEvent.TurnStart : CoreEvent.TurnEnd;
                b.EventExplicit = true;
                return true;
            }
            return false;
        }

        bool isStart = cur.MatchWord("start");
        bool isEnd = !isStart && cur.MatchWord("end");
        if ((!isStart && !isEnd) || !cur.MatchWord("of")) { cur.Seek(save); return false; }

        if (cur.MatchPhrase("your", "opponent's", "turn")) b.DuringTurn = TurnOwnerScope.Opponent;
        else if (cur.MatchPhrase("each", "turn")) b.DuringTurn = TurnOwnerScope.Any;
        else if (cur.MatchPhrase("your", "turn")) b.DuringTurn = TurnOwnerScope.Self;
        else { cur.Seek(save); return false; }

        b.Event = isStart ? CoreEvent.TurnStart : CoreEvent.TurnEnd;
        b.EventExplicit = true;
        ConsumeDelimiter(cur);
        return true;
    }

    // ---------- 4. 冒号/连字符能力头 ----------

    private static readonly (string head, CoreEvent ev, string condFlag)[] HeaderTable =
    {
        ("fanfare",        CoreEvent.OnPlay,          null),
        ("last words",     CoreEvent.OnDestroy,       null),
        ("evolve",         CoreEvent.OnEvolve,        "manual_evolve"),
        ("super evolve",   CoreEvent.OnSuperEvolve,   "manual_evolve"),
        ("super-evolve",   CoreEvent.OnSuperEvolve,   "manual_evolve"),
        ("strike",         CoreEvent.OnAttack,        null),
        ("follower strike",CoreEvent.OnAttack,        null),
        ("clash",          CoreEvent.OnClash,         null),
        ("on spellboost",  CoreEvent.OnSpellboost,    null),
        ("on evolve",      CoreEvent.OnEvolve,        null),
        ("engage",         CoreEvent.OnEngage,        null),
        ("accel",          CoreEvent.OnPlay,          null),
        ("crystal",        CoreEvent.OnPlay,          null),
        ("enhance",        CoreEvent.OnPlay,          null),
        ("fuse",           CoreEvent.OnFuse,          null),
        ("countdown",      CoreEvent.OnCountdownExpire,null),
        ("activates in hand", CoreEvent.PassiveAura,  "zone_hand"),
        ("activates in deck", CoreEvent.PassiveAura,  "zone_deck"),
        ("activates",      CoreEvent.PassiveAura,     "zone_hand"),
        ("skybound art",   (CoreEvent)(-1),           "skybound_art"),
        ("super skybound art", (CoreEvent)(-1),       "super_skybound_art"),
        ("then",           CoreEvent.OnPlay,          null),
    };

    private bool TryConsumeKeywordHeader(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;

        // 带 (N) 参数的：Necromancy (6) / Combo (3) / Rally (10) / Earth Rite (1)
        if (cur.Current.IsAnyWord("necromancy", "combo", "rally", "enhance", "engage", "countdown")
            || cur.Current.IsAnyWord("earth"))
        {
            var name = cur.Current.Text.ToLowerInvariant();
            cur.Advance();
            if (name == "earth" && cur.MatchWord("rite")) name = "earth rite";
            int param = 0;
            if (cur.Current.Kind == TokKind.OpenParen && cur.Peek().Kind == TokKind.Number)
            {
                cur.Advance();
                param = cur.Current.Value;
                cur.Advance();
                cur.TryMatch(TokKind.CloseParen);
            }
            if (ConsumeDelimiter(cur))
            {
                var kind = name switch
                {
                    "combo" => CostKind.TurnPlayCount,
                    "rally" => CostKind.MatchPlayCount,
                    "necromancy" => CostKind.Necromance,
                    "earth rite" => CostKind.DestroyAlly,
                    "enhance" or "engage" => CostKind.UsePp,
                    "countdown" => CostKind.Countdown,
                    _ => CostKind.None,
                };
                if (kind != CostKind.None)
                    b.Costs.Add(new CostSpec { Kind = kind, Amount = NumExpr.Const(param), Label = name });

                // 带参头同时也是**事件**：Engage (N) 是启动式能力，不是 Fanfare。
                // 之前这里直接 return，而 Event 默认是 OnPlay，
                // 导致所有护符启动能力都被编译成「打出时」。
                // 只有**本身即时机**的头才允许改写事件：
                //   Engage (N)    → 启动式能力
                //   Countdown (N) → 倒计时到期
                //
                // Necromancy / Combo / Rally / Earth Rite / Enhance 是**代价**，
                // 修饰的是「在什么时机下、满足什么代价才发动」，本身不决定时机。
                // 之前把它们一律映射成 OnPlay 并置 EventExplicit，于是
                // "Evolve: Necromancy (4) - ..." 里的 Evolve 被冲掉，
                // 整条能力变成「打出时」触发 —— 91 张卡因此错标时机。
                var evFromHead = name switch
                {
                    "engage" => CoreEvent.OnEngage,
                    "countdown" => CoreEvent.OnCountdownExpire,
                    _ => (CoreEvent)(-1),
                };
                if ((int)evFromHead >= 0 && !b.EventExplicit)
                {
                    b.Event = evFromHead;
                    b.EventExplicit = true;
                }
                return true;
            }
            cur.Seek(save);
            return false;
        }

        // 词表里的头（允许 : 或 - 分隔）
        foreach (var (head, ev, condFlag) in HeaderTable)
        {
            var words = head.Split(' ');
            if (!cur.MatchPhrase(words)) continue;

            // 分隔符：冒号或连字符（或直接跟内容）
            bool hadDelim = ConsumeDelimiter(cur);
            if (!hadDelim && !cur.IsAtEnd && cur.Current.Kind != TokKind.Comma)
            {
                // 「Fanfare:」后可能直接是正文，允许无分隔符但必须有冒号
                cur.Seek(save);
                continue;
            }

            if ((int)ev >= 0) { b.Event = ev; b.EventExplicit = true; }
            if (condFlag == "manual_evolve")
                b.ManualEvolveOnly = true;

            // "Activates in hand / in deck"：能力在哪个区域监听。
            // 之前这两个头都不设 ActiveZone，于是全部默认在 Board，
            // 牌组/手牌里的能力永远不会触发。
            if (condFlag == "zone_hand") b.ActiveZone = ActiveZoneScope.Hand;
            if (condFlag == "zone_deck") b.ActiveZone = ActiveZoneScope.Deck;

            // 【奥义 / 解放奥义】：条件头，不是事件头。
            //   Skybound Art        -> 奥义槽 >= 10
            //   Super Skybound Art  -> 奥义槽 >= 15
            // 奥义槽 = 当前回合数 + 本卡在手时我方随从进化/超进化的次数。
            // 之前这里只是占位 (CoreEvent)(-1)，运行时完全不认，能力永不触发。
            if (condFlag == "skybound_art" || condFlag == "super_skybound_art")
            {
                int need = condFlag == "skybound_art" ? 10 : 15;
                b.Event = CoreEvent.OnPlay;
                b.EventExplicit = true;
                b.Condition = BoolExpr.And(b.Condition, new CompareExpr
                {
                    Left = NumExpr.Ctr(_lex.Counter("skybound_gauge")),
                    Op = Core.CompareOp.Ge,
                    Right = NumExpr.Const(need),
                });
            }
            return true;
        }

        cur.Seek(save);
        return false;
    }

    // ---------- 5. Whenever / When <Subject> <EventVP>, ----------

    private bool TryConsumeWhenClause(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("whenever") && !cur.MatchWord("when")) return false;

        // 形态 A：主语是 you（含缩写 you've）
        bool isYou = cur.MatchWord("you");
        if (!isYou && cur.MatchPhrase("you've")) isYou = true;
        if (!isYou && cur.MatchPhrase("you", "have")) isYou = true;
        if (isYou)
        {
            // "When you gain this crest, ..." —— 触发时机是「获得这个纹章」，
            // 不是「打出这张卡」。落成 OnPlay 会让它在纹章经别的途径获得时永不触发。
            if (cur.MatchPhrase("gain", "this", "crest") || cur.MatchPhrase("gain", "a", "crest"))
            {
                b.Event = CoreEvent.OnEnterField; b.EventExplicit = true;
                b.SubjectFilter = TargetSpec.Self;
                if (cur.Current.Kind == TokKind.Comma) cur.Advance();
            }
            // "Whenever you select Modes, ..." —— 选择 Mode 本身是一个事件。
            else if (cur.MatchPhrase("select", "modes") || cur.MatchPhrase("select", "mode"))
            {
                b.Event = CoreEvent.OnSelectMode; b.EventExplicit = true;
                b.SubjectFilter = null;   // 「你」选择的，不是某张卡的状态
                if (cur.Current.Kind == TokKind.Comma) cur.Advance();
            }
            else if (cur.MatchWord("draw")) { b.Event = CoreEvent.OnDraw; b.EventExplicit = true; b.SubjectFilter = _target.TryParse(cur); }
            else if (cur.MatchPhrase("play", "or", "fuse"))
            {
                // 两个事件并列：打出与融合都要触发。
                b.Event = CoreEvent.OnPlay;
                b.SecondaryEvent = CoreEvent.OnFuse;
                b.EventExplicit = true;
                b.SubjectFilter = _target.TryParse(cur);
            }
            else if (cur.MatchWord("play")) { b.Event = CoreEvent.OnPlay; b.EventExplicit = true; b.SubjectFilter = _target.TryParse(cur); }
            else if (cur.MatchWord("discard")) { b.Event = CoreEvent.OnDiscard; b.EventExplicit = true; b.SubjectFilter = _target.TryParse(cur); }
            else if (cur.MatchPhrase("fuse", "to", "this", "card")) { b.Event = CoreEvent.OnFuse; b.EventExplicit = true; b.SubjectFilter = TargetSpec.Self; }
            // "When you've Fused both to this card, ..."（已融合完成）
            // 这条是卡面实际走到的分支：TryConsumeFusePrefix 只吃 "Fuse:" 冒号头，
            // "When you've Fused ..." 整句落在 when 从句里，条件就在这里丢的。
            else if (cur.MatchWord("fused") || cur.MatchPhrase("fuse", "d"))
            {
                b.Event = CoreEvent.OnFuse; b.EventExplicit = true;
                b.SubjectFilter = TargetSpec.Self;
                ConsumeFusedToCardClause(cur, b);
            }
            else if (cur.MatchWord("activate") || cur.MatchWord("engage")) { b.Event = CoreEvent.OnEngage; b.EventExplicit = true; b.SubjectFilter = _target.TryParse(cur); }
            // 「发动土之秘术」是玩家主动消耗土之印，不是护符吟唱到期。
            // 落成 OnCountdownExpire 会让「Whenever you perform Earth Rite」
            // 只在自己有护符倒计时归零时才触发 —— 与卡面语义毫无关系。
            // 玩家级事件，SubjectFilter 留空（不要求事件主体是某张卡）。
            else if (cur.MatchPhrase("perform", "earth", "rite"))
            { b.Event = CoreEvent.OnEarthRite; b.EventExplicit = true; b.SubjectFilter = null; }
            else if (cur.MatchWord("fuse"))
            {
                b.Event = CoreEvent.OnFuse; b.EventExplicit = true;
                b.SubjectFilter = _target.TryParse(cur) ?? TargetSpec.Self;
            }
            else if (cur.MatchWord("summon")) { b.Event = CoreEvent.OnSummon; b.EventExplicit = true; b.SubjectFilter = _target.TryParse(cur); }
            else if (cur.MatchWord("attack")) { b.Event = CoreEvent.OnAttack; b.EventExplicit = true; b.SubjectFilter = TargetSpec.Self; }
            else if (cur.MatchWord("evolve")) { b.Event = CoreEvent.OnEvolve; b.EventExplicit = true; b.SubjectFilter = TargetSpec.Self; }
            // "play or Fuse a Loot card" —— play 后面可能跟 or <动词>
            else if (needSkipToComma(cur)) { b.Event = CoreEvent.OnPlay; b.EventExplicit = true; }
            else { cur.Seek(save); return false; }

            ConsumeDelimiter(cur);
            return true;
        }

        // 形态 B：your leader's defense is restored
        if (cur.MatchPhrase("your", "leader's", "defense", "is", "restored"))
        {
            b.Event = CoreEvent.OnHealed; b.EventExplicit = true;
            b.SubjectFilter = TargetSpec.SelfLeader;
            ConsumeDelimiter(cur);
            return true;
        }

        // 形态 C：通用主语 + 事件谓语
        var subject = _target.TryParse(cur);
        if (subject == null) { cur.Seek(save); return false; }
        b.SubjectFilter = subject;

        var ev = MatchEventPredicate(cur, b);
        if (ev == null) { cur.Seek(save); return false; }
        b.Event = ev.Value;
        b.EventExplicit = true;

        ConsumeDelimiter(cur);
        return true;
    }

    /// <summary>消费到逗号为止（用于 'play or Fuse a Loot card' 这类复合谓语）。</summary>
    private static bool needSkipToComma(TokenCursor cur)
    {
        int save = cur.Position;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Comma) cur.Advance();
        if (cur.Current.Kind != TokKind.Comma) { cur.Seek(save); return false; }
        return true;
    }

    /// <summary>匹配事件谓语动词短语，返回对应事件。</summary>
    private static CoreEvent? MatchEventPredicate(TokenCursor cur, AbilityHeaderBuilder b)
    {
        if (cur.MatchPhrase("enters", "the", "field")) return CoreEvent.OnEnterField;
        if (cur.MatchPhrase("leaves", "the", "field")) return CoreEvent.OnLeaveField;
        if (cur.MatchPhrase("is", "destroyed")) return CoreEvent.OnDestroy;
        if (cur.MatchPhrase("is", "banished", "from", "the", "field")) return CoreEvent.OnBanish;
        if (cur.MatchPhrase("is", "banished")) return CoreEvent.OnBanish;
        // 受伤事件有两种卡面写法，语义完全等价：
        //   "takes damage"            —— 随从/主战者主语
        //   "is damaged"              —— 被动写法，NaturalPrinter 渲染 OnDamaged 用的就是它
        // 后者此前只在 ConditionCompiler 里有分支（那是条件路径，不是事件头），
        // 事件头这里不认，于是 "Whenever this follower is damaged, ..." 整句
        // 在位置 0 报「无法解析的动作 / 停在 [Whenever]」—— 编译不出能力。
        // 两条必须走同一段逻辑，"but isn't destroyed" 的存活条件对两者同样成立。
        if (cur.MatchPhrase("takes", "damage") || cur.MatchPhrase("is", "damaged"))
        {
            // "takes damage but isn't destroyed" = 受到伤害后仍然存活。
            // 旧实现塞的是 UnitState.None —— VmEval.MatchState 对它恒返回 false，
            // 取反后恒真，等于「任何伤害都触发」，条件形同虚设。
            // 伤害管线是「先扣血、再发 OnDamaged」，所以判定时读 $trigger 的当前生命
            // 就是「这一击之后它还活着」。已离场的实体快照停在 <=0，同样判否。
            if (cur.MatchPhrase("but", "isn't", "destroyed"))
                b.Condition = BoolExpr.And(b.Condition,
                    new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Life, TargetSpec.Trigger),
                        Op = CompareOp.Gt,
                        Right = NumExpr.Const(0),
                    });
            return CoreEvent.OnDamaged;
        }
        if (cur.MatchWord("attacks"))
        {
            // "attacks a follower" / "attacks a leader" —— 攻击目标的副体限定。
            // 这里必须把宾语真正消费掉：以前只认动词就返回 OnAttack，
            // 剩下的 "a follower" 落到动作解析器上，整句直接报「无法推进」。
            int atkSave = cur.Position;
            if (cur.MatchPhrase("a", "follower") || cur.MatchPhrase("an", "enemy", "follower")
                || cur.MatchPhrase("an", "allied", "follower"))
            {
                // 目标是随从 == 目标不是主战者。
                // CombatResolver 在发 OnAttack **之前**写定 AttackedLeaderLastTurn，
                // 触发判定阶段它就是「本次攻击的目标是不是主战者」。
                b.Condition = BoolExpr.And(b.Condition,
                    BoolExpr.Not(new HasStateExpr
                    {
                        State = UnitState.AttackedLeaderLastTurn,
                        Of = TargetSpec.Trigger,
                    }));
            }
            else if (cur.MatchPhrase("a", "leader") || cur.MatchPhrase("the", "enemy", "leader")
                     || cur.MatchPhrase("the", "opposing", "leader"))
            {
                b.Condition = BoolExpr.And(b.Condition,
                    new HasStateExpr
                    {
                        State = UnitState.AttackedLeaderLastTurn,
                        Of = TargetSpec.Trigger,
                    });
            }
            else cur.Seek(atkSave);
            return CoreEvent.OnAttack;
        }
        // super-evolves 可能被词法拆成 super / - / evolves
        if (cur.Current.IsWord("super"))
        {
            int sp = cur.Position;
            cur.Advance();
            if (cur.Current.Kind == TokKind.Dash) cur.Advance();
            if (cur.MatchWord("evolves")) return CoreEvent.OnSuperEvolve;
            cur.Seek(sp);
        }
        if (cur.MatchWord("evolves")) return CoreEvent.OnEvolve;
        if (cur.MatchPhrase("is", "given"))
        {
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Comma) cur.Advance();
            return CoreEvent.PassiveAura;
        }
        if (cur.MatchPhrase("is", "discarded")) return CoreEvent.OnDiscard;
        if (cur.MatchPhrase("is", "invoked"))   return CoreEvent.OnEnterField;
        if (cur.MatchPhrase("is", "summoned")) return CoreEvent.OnSummon;
        if (cur.MatchPhrase("is", "played")) return CoreEvent.OnPlay;
        return null;
    }

    // ---------- 6. If <BoolExpr>, ----------

    private bool TryConsumeIfClause(TokenCursor cur, AbilityHeaderBuilder b)
    {
        int save = cur.Position;
        if (!cur.MatchWord("if")) return false;

        var buf = new List<Token>();
        while (!cur.IsAtEnd)
        {
            if (cur.Current.Kind == TokKind.Comma)
            {
                // 逗号后是数字，或 "and <数字>"，说明是条件内部的枚举逗号，不切分
                if (cur.Peek(1).Kind == TokKind.Number
                    || (cur.Peek(1).IsWord("and") && cur.Peek(2).Kind == TokKind.Number))
                {
                    buf.Add(cur.Current); cur.Advance(); continue;
                }
                break;
            }
            buf.Add(cur.Current);
            cur.Advance();
        }
        if (cur.IsAtEnd) { cur.Seek(save); return false; }
        cur.Advance();   // 逗号

        b.Condition = BoolExpr.And(b.Condition, ConditionCompiler.Build(buf, _lex));
        return true;
    }
}
