using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 动作解析器 —— 一个句子 → 若干 EffectNode
//
// 铁律一：全程只碰 TokenCursor。
// 解析完一句后由调用方 AssertEnd（这里返回已消费位置）。
//
// 铁律二：遇到 Quoted token，把 Inner 交给递归编译器生成 AbilityDef，
// 塞进 GrantAbilityNode。
// ============================================================================

public sealed class ActionParser
{
    private readonly Lexicon _lex;
    private readonly TargetParser _target;
    private readonly Func<IReadOnlyList<Token>, AbilityDef> _compileQuotedAbility;

    /// <summary>变量解析委托（由 EnglishCompiler 注入，处理 X/Y/Z 绑定）。</summary>
    public Func<string, NumExpr> VariableResolver { get; set; }

    /// <summary>调试开关：打印 ParseGive 的每一步。</summary>

    /// <summary>
    /// 刚刚解析过「transform it based on the total cost of the cards fused」。
    /// 编译器读完一段后取走这个标志，用来决定随后那张分档表按 fuse_cost 还是
    /// fuse_count 分档、以及每档动作是 Transform 还是 Spawn。
    /// </summary>
    public bool FuseTierByCost { get; set; }

    /// <summary>
    /// 取「本卡自己那个纹章/信仰的定义」。由 EnglishCompiler 注入。
    /// 纹章的效果定义只存在于主人卡的 alt_modes，动作解析器看不到它，
    /// 只能通过这个回调在生成 SpawnCardNode 时把定义一起带上。
    /// </summary>
    public Func<Ast.CardDef> CrestDefResolver { get; set; }

    /// <summary>
    /// 变量声明登记器。引号内出现的 "X is the number of ..." 必须真正登记到
    /// 编译器的变量表里，否则 Draw X 会读一个从未被赋值的槽。
    /// 由 EnglishCompiler 注入（动作解析器看不到变量表）。
    /// </summary>
    public Action<IReadOnlyList<Token>> VariableBinder { get; set; }


    public ActionParser(Lexicon lex, Func<IReadOnlyList<Token>, AbilityDef> compileQuotedAbility,
                        TargetParser target = null)
    {
        _lex = lex;
        // 必须与编译器共用同一个 TargetParser：
        // 代词作用域（DefaultPronounSlot）是写在实例上的状态，
        // 各自新建一个实例的话，编译器设置的 $trigger 永远传不到动作解析。
        _target = target ?? new TargetParser(lex);
        _compileQuotedAbility = compileQuotedAbility;
    }

    /// <summary>解析一串动作节点，直到 cursor 耗尽。</summary>
    public List<EffectNode> ParseSequence(TokenCursor cur)
    {
        var result = new List<EffectNode>();

        // 前置时限："..., until the end of your opponent's turn, give it 'Can' t attack.'"
        // 之前 ConsumeDuration 的返回值被直接丢掉，整条时限蒸发，
        // 于是「本回合内 / 下个对手回合结束前」的限制全都变成了永久。
        // 这里把时限暂存起来，落到**紧随其后**的那组节点上。
        var pendingDuration = DurationKind.Permanent;
        while (!cur.IsAtEnd)
        {
            // OptionMark 是**新的选项**起点，必须终止当前动作序列，
            // 交给 EnglishCompiler 的选项聚合逻辑处理；不能在这里跳过。
            if (cur.Current.Kind == TokKind.OptionMark) break;

            // 跳过句点、分号，以及动作之间的连接词 (",", "and", "then")
            if (cur.Current.Kind == TokKind.Period ||
                cur.Current.Kind == TokKind.Semicolon ||
                cur.Current.Kind == TokKind.Comma ||
                cur.Current.IsAnyWord("and", "then"))
            {
                cur.Advance();
                continue;
            }

            // "and, until the end of your opponent's turn, give it ..."
            if (cur.Current.IsWord("until"))
            {
                pendingDuration = ConsumeDuration(cur);
                if (cur.Current.Kind == TokKind.Comma) cur.Advance();
                continue;
            }

            // 句中条件："draw a card and, if X, restore 1 defense"
            if (cur.Current.IsWord("if"))
            {
                var midIf = ParseMidSentenceIf(cur);
                if (midIf != null) { result.Add(midIf); continue; }
            }

            // "..., Otherwise, <else>" 的 else 分支已由 ParseMidSentenceIf 收进
            // 上一个条件节点。走到这里说明它前面没有可挂的条件（畸形句），
            // 直接跳过，别让整个能力因为一个连接词报废。
            if (cur.Current.IsWord("otherwise"))
            {
                cur.Advance();
                continue;
            }

            int before = cur.Position;
            var node = ParseOne(cur);
            if (node != null)
            {
                // 前置时限落到本组节点上，然后立刻复位 ——
                // 时限只约束紧随其后的那一组动作，不影响后面的句子。
                if (pendingDuration != DurationKind.Permanent)
                {
                    for (int k = 0; k < node.Count; k++) node[k] = WithDuration(node[k], pendingDuration);
                    pendingDuration = DurationKind.Permanent;
                }
                result.AddRange(node);
                // 登记本句已产生绑定的槽位（设计3 5.3 ScopeRegister）。
                // 后续句子的 it / them 靠这张登记表判断是否可绑，
                // 槽位为空时必须报错而不是静默绑到 $selected。
                // 只有「创建型」动作才登记 $created；Select 由 ParseSelect 自己登记。
                // 「Discard / Destroy 送墓」也是 MoveZoneNode，但那是移出而非创建，
                // 不能登记成 $created。
                foreach (var nd in node)
                {
                    switch (nd)
                    {
                        case SelectTargetNode: _target.MarkBound(TargetSpec.SlotSelected); break;
                        case SpawnCardNode: _target.MarkBound(TargetSpec.SlotCreated); break;
                        // "Evolve this follower" / "Remove Ward from this follower"
                        // 「本卡」也是可绑定来源，后续的 it 指向它。
                        // 进化目标可能是本卡，也可能是前句选中的群体：
                        //   "Evolve this follower and give it +1/+0"        -> $self
                        //   "Super-evolve them instead"                     -> $selected
                        // 写死 $self 会让后者的 it 指回本卡。
                        case EvolveNode evo when IsSelfTarget(evo.Target):
                            _target.MarkBound(TargetSpec.SlotSelf); break;
                        case EvolveNode:
                            _target.MarkBound(TargetSpec.SlotSelected); break;
                        // 不能写 `ms.Target == TargetSpec.Self`：TargetSpec 是 class、
                        // 未重载 ==，而 TargetSpec.Self 是 `=> new()` 属性 ——
                        // 每次访问都是新实例，引用比较恒为 false，等于这段判断从未生效。
                        // 后果：「swap this follower's ... and give it +1/+0」的 it
                        // 找不到 $self，回退成 $trigger，加成全打到触发器身上。
                        case ModifyStatsNode ms when IsSelfTarget(ms.Target): _target.MarkBound(TargetSpec.SlotSelf); break;
                        case ModifyKeywordNode mk when IsSelfTarget(mk.Target): _target.MarkBound(TargetSpec.SlotSelf); break;
                        // "Reduce your faith's value by 10 to give it ..."
                        // 这里的 it 指被操作的资源计数器（信仰 / 纹章等）。
                        case ModifyCounterNode mc: _target.MarkBound(TargetSpec.SlotCounter); break;
                    }
                }
            }

            if (cur.Position == before)
                throw LangException.At("无法解析的动作", "解析器在此处无法推进", cur, cur.All);
        }
        return result;
    }

    /// <summary>句中条件从句：if &lt;cond&gt;, &lt;body&gt; → ConditionalNode。</summary>
    private EffectNode ParseMidSentenceIf(TokenCursor cur)
    {
        int save = cur.Position;
        cur.ExpectWord("if");

        var condTokens = new List<Token>();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Comma)
        {
            condTokens.Add(cur.Current);
            cur.Advance();
        }
        if (cur.IsAtEnd || condTokens.Count == 0) { cur.Seek(save); return null; }
        cur.Advance();   // 逗号

        BoolExpr cond;
        try { cond = ConditionCompiler.Build(condTokens, _lex); }
        catch (LangException) { cur.Seek(save); return null; }

        var thenBody = ParseSequence(cur);

        // "..., Otherwise, <else>" —— 「Otherwise」是官方的 else 引导词。
        // 句子切分器会把以 Otherwise 开头的下一句并回本句（用逗号接），
        // ParseSequence 到这里正好停在它前面。
        IReadOnlyList<EffectNode> elseBody = null;
        if (cur.Current.IsWord("otherwise"))
        {
            cur.Advance();
            if (cur.Current.Kind == TokKind.Comma || cur.Current.Kind == TokKind.Colon) cur.Advance();
            elseBody = ParseSequence(cur);
        }

        return new ConditionalNode { Condition = cond, Then = thenBody, Else = elseBody };
    }


    /// <summary>
    /// 解析「&lt;Allied|Enemy&gt; followers' Fanfare [and Enhance] abilities don't activate」。
    /// 不是这种句式时返回 null 并让游标复位，交给后续分支。
    /// </summary>
    private List<EffectNode> TryParseSuppressAura(TokenCursor cur)
    {
        int save = cur.Position;
        bool isAlly = cur.Current.IsWord("allied");
        cur.Advance();

        if (!cur.TryMatchAnyWord("followers'", "follower's", "followers", "follower"))
        { cur.Seek(save); return null; }

        bool noFanfare = false, noEnhance = false, sawAbility = false;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            string wd = cur.Current.Text.ToLowerInvariant();
            if (wd == "fanfare") { noFanfare = true; sawAbility = true; }
            else if (wd == "enhance") { noEnhance = true; sawAbility = true; }
            else if (wd is "abilities" or "ability") sawAbility = true;
            else if (wd is "activate" or "activates" or "don't" or "doesn't" or "do") break;
            cur.Advance();
        }

        bool confirmed = sawAbility && (noFanfare || noEnhance);
        if (!confirmed) { cur.Seek(save); return null; }

        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        if (cur.Current.Kind == TokKind.Period) cur.Advance();

        var tgt = new TargetSpec
        {
            SourceKind = TargetSourceKind.Zone,
            Owner = isAlly ? ZoneScope.Self : ZoneScope.Opponent,
            Zone = ZoneRef.Board,
            Mode = SelectMode.All,
            Where = new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item },
        };

        var list = new List<EffectNode>();
        if (noFanfare)
            list.Add(new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Grant, Keyword = _lex.Keyword("suppress_fanfare") });
        if (noEnhance)
            list.Add(new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Grant, Keyword = _lex.Keyword("suppress_enhance") });
        return list;
    }
    /// <summary>解析一个动作。返回 null 表示当前位置不是动作起点。</summary>
    public List<EffectNode> ParseOne(TokenCursor cur)
    {
        var t = cur.Current;
        if (t.Kind != TokKind.Word && t.Kind != TokKind.Keyword) return null;

        var w = t.Text.ToLowerInvariant();

        // 「Allied followers' Fanfare and Enhance abilities don't activate」
        // 这是光环句，不是普通动作：授予场上随从 suppress_fanfare / suppress_enhance。
        if (w is "allied" or "enemy")
        {
            var suppress = TryParseSuppressAura(cur);
            if (suppress != null) return suppress;
        }

        switch (w)
        {
            case "deal": return ParseDeal(cur);
            case "draw": return ParseDraw(cur);
            case "restore": return ParseRestore(cur);
            case "recover": return ParseRecover(cur);
            case "give": return ParseGive(cur);
            case "summon": return ParseSummon(cur);
            case "add": return ParseAdd(cur);
            case "destroy": return ParseMove(cur, ZoneRef.Cemetery, isDestroy: true);
            case "banish": return ParseMove(cur, ZoneRef.Banished);
            case "discard": return ParseMove(cur, ZoneRef.Cemetery, isDestroy: false, fromZone: ZoneRef.Hand);
            case "return": return ParseMove(cur, ZoneRef.Hand);
            // "move this card to the board" / "move it to your hand"
            // Invoke 卡的主体效果就是 move，之前完全没有这个动词，
            // 导致 Activates in deck 的卡在语义层就断了。
            case "move": return ParseMoveVerb(cur);
            case "evolve": return ParseEvolve(cur, false);
            case "super-evolve": return ParseEvolve(cur, true);
            case "select": return ParseSelect(cur);
            case "transform": return ParseTransform(cur);
            case "reveal": return ParseRevealKeyword(cur);
            case "gain": return ParseGain(cur);
            case "double": return ParseDouble(cur);
            case "increase":
            {
                int svNum = cur.Position;
                cur.Advance();

                // "Increase this faith's value by 1" / "increase your faith's value by N"
                // 这是信仰增长的标准写法，此前完全没有分支，整句停在 [this]。
                if (cur.MatchPhrase("this", "faith's", "value")
                    || cur.MatchPhrase("your", "faith's", "value")
                    || cur.MatchPhrase("this", "crest's", "count")
                    || cur.MatchPhrase("your", "crest's", "count"))
                {
                    var ctr = "faith";
                    cur.TryMatchAnyWord("by");
                    var inc = ParseNumExpr(cur);
                    return new List<EffectNode>
                    {
                        new ModifyCounterNode
                        {
                            // Target 必须为 null：信仰是**资源计数器**，住在玩家身上
                            // （BattleState.ReadCounter 的 faith 分支会转去信仰实体）。
                            // 写 TargetSpec.Self 会让「随从卡上的这句」去改随从自己的
                            // 同名计数器 —— 随从没有 faith，值永远加不上。
                            Target = null,
                            Owner = ZoneScope.Self,
                            Counter = _lex.Counter(ctr),
                            Op = CounterOp.Add,
                            Delta = inc,
                        },
                    };
                }
                cur.Seek(svNum);
                cur.Advance();

                // "Increase the number of Modes you can select by 1"
                if (cur.MatchPhrase("the", "number", "of", "modes"))
                {
                    while (!cur.IsAtEnd && !cur.Current.IsWord("by")) cur.Advance();
                    cur.TryMatchAnyWord("by");
                    var inc = ParseNumExpr(cur);
                    return new List<EffectNode>
                    {
                        new ModifyCounterNode
                        {
                            Counter = _lex.Counter("mode_count"),
                            Op = CounterOp.Add,
                            Delta = inc,
                        },
                    };
                }
                cur.Seek(svNum);
                // "Increase the Skybound Art gauges of ... by 1"
                int sv2 = cur.Position;
                cur.Advance();
                if (cur.MatchPhrase("the", "skybound", "art", "gauges"))
                {
                    cur.TryMatchAnyWord("of");
                    var tgt = _target.TryParse(cur);
                    cur.TryMatchAnyWord("by");
                    var amt = ParseNumExpr(cur);
                    return new List<EffectNode>
                    {
                        new ModifyCounterNode
                        {
                            Counter = _lex.Counter("skybound_gauge"),
                            Op = CounterOp.Add,
                            Delta = amt,
                        },
                    };
                }
                cur.Seek(sv2);
                return ParseCostChange(cur, 1);
            }
            case "set": return ParseSet(cur);
            case "spellboost": return ParseSpellboost(cur);
            case "reanimate": return ParseReanimate(cur);
            case "do": return ParseDo(cur);
            case "remove": return ParseRemove(cur);
            case "replace": return ParseReplace(cur);
            case "halve": return ParseHalve(cur);
            case "spend":
            {
                cur.Advance();
                var amt = ParseNumExpr(cur);
                cur.TryMatchAnyWord("play");
                cur.TryMatchAnyWord("point", "points");
                var nodes = new List<EffectNode>
                {
                    new ModifyCounterNode
                    {
                        Counter = _lex.Counter("pp"),
                        Op = CounterOp.Add,
                        Delta = NumExpr.Sub(NumExpr.Const(0), amt),
                    },
                };
                if (cur.MatchWord("to"))
                    return PayThenDo(_lex.Counter("pp"), amt, nodes, ParseSequence(cur));
                return nodes;
            }
            case "invoke":
            {
                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
                return new List<EffectNode>
                {
                    new MoveZoneNode { Target = TargetSpec.Self, ToZone = ZoneRef.Board },
                };
            }
            case "reduce":
            {
                // "Reduce your faith's value by N to <动作>"
                int sv = cur.Position;
                cur.Advance();
                if (cur.MatchPhrase("your", "faith's", "value"))
                {
                    cur.TryMatchAnyWord("by");
                    var amt = ParseNumExpr(cur);
                    var nodes = new List<EffectNode>
                    {
                        new ModifyCounterNode
                        {
                            Counter = _lex.Counter("faith"),
                            Op = CounterOp.Add,
                            Delta = NumExpr.Sub(NumExpr.Const(0), amt),
                        },
                    };
                    // "Reduce your faith's value by 10 to give it ..."
                    // 这里的 it 指被削减的那个资源计数器，先登记再解析后续动作。
                    _target.MarkBound(TargetSpec.SlotCounter);
                    if (cur.MatchWord("to"))
                        // 信仰不足 10 时不能「扣成 0 然后白嫖」。
                        return PayThenDo(_lex.Counter("faith"), amt, nodes, ParseSequence(cur));
                    return nodes;
                }
                cur.Seek(sv);
                return ParseCostChange(cur, -1);
            }
            case "play": return ParsePlayVerb(cur);
            case "advance": return ParseAdvanceOrDelay(cur, -1);
            case "delay": return ParseAdvanceOrDelay(cur, +1);
            case "activate": return ParseActivate(cur);
            case "consume": return ParseConsume(cur);
            case "swap": return ParseSwap(cur);
            case "fully": return ParseFully(cur);
            case "it": return ParseItCosts(cur);
            case "replicate": return ParseReplicate(cur);
            // instead 是「替代」语义：留一个占位标记，由 InsteadWeaver 在装配层合成条件分支。
            // 直接返回空列表会让前后两句都执行（Deal 2 + Deal 4 = 合打 6 点）。
            case "instead":
            {
                cur.Advance();
                return new List<EffectNode> { new MacroNode { Macro = _lex.Macro("instead_marker") } };
            }
            // Whenever / When / At the ... —— 事件触发句不归 ActionParser 管。
            // 直接返回 null，让 CompileSegment 的 SplitCondition 处理；
            // 绝不能在这里把剩余词吞掉（那是 V2 的兜底病）。
            case "whenever": case "when": case "at":
                return null;
            case "your":
            {
                // "Your opponent draws a card." —— 必须优先于下面的限制句判定
                if (cur.Current.IsWord("your") && cur.Peek().IsWord("opponent")
                    && cur.Peek(2).IsWord("draws"))
                    return ParseOpponentDraws(cur);

                // "Your opponent can't select anything other than this card"
                if (cur.MatchPhrase("your", "opponent"))
                {
                    while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
                    return new List<EffectNode>
                    {
                        new ModifyKeywordNode { Target = TargetSpec.OpponentLeader, Op = KeywordOp.Grant, Keyword = _lex.Keyword("targeting_restriction") },
                    };
                }
                return ParseOpponentDraws(cur);
            }
            case "takes":
            {
                // "Takes 1 more damage." —— 受伤增幅，用带参关键词承载。
                //
                // 写入端与读取端必须是**同一个名字**：CombatResolver 读的是
                // damage_taken_bonus，这里却写成 takes_more_damage，
                // 于是授了「多受 1 点」完全没效果。数值也必须带上，
                // 否则「多受 N 点」里的 N 会被吞掉。
                int bonusVal = 1;
                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
                {
                    if (cur.Current.Kind == TokKind.Number) bonusVal = cur.Current.Value;
                    cur.Advance();
                }
                return new List<EffectNode>
                {
                    new ModifyKeywordNode
                    {
                        Target = TargetSpec.Self,
                        Op = KeywordOp.Grant,
                        Keyword = _lex.Keyword("damage_taken_bonus"),
                        Value = NumExpr.Const(bonusVal),
                    },
                };
            }
            case "ignores":
            {
                var kw2 = cur.Current.Text;
                cur.Advance();
                cur.TryMatchAnyWord("ward");
                return new List<EffectNode>
                {
                    new ModifyKeywordNode { Target = TargetSpec.Self, Op = KeywordOp.Grant, Keyword = _lex.Keyword("ignores ward") },
                };
            }
            // Can't / Can 开头的限制句 → 关键词限制
            case "can't": case "cant": case "can": case "cannot":
            {
                var words = new List<string>();
                int? numInClause = null;
                // 动态数值："Can attack X times per turn" 里的 X
                // 必须是 NumExpr，不能只认常数。
                Ast.NumExpr numExpr = null;

                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
                {
                    if (cur.Current.Kind == TokKind.Number && numInClause == null)
                        numInClause = cur.Current.Value;

                    // 常量之外还要认变量：X / Y / Z 以及 count(...) 这类表达式。
                    if (numExpr == null
                        && (cur.Current.Kind == TokKind.Number
                            || cur.Current.IsAnyWord("x", "y", "z")))
                    {
                        int back = cur.Position;
                        var saved = numInClause;
                        try
                        {
                            var e2 = ParseNumExpr(cur);
                            if (e2 != null && e2.Op != Ast.NumOp.Const)
                            {
                                numExpr = e2;
                                words.Add("#expr");
                                continue;
                            }
                        }
                        catch { }
                        cur.Seek(back);
                        numInClause = saved;
                    }

                    words.Add(cur.Current.Text.ToLowerInvariant());
                    cur.Advance();
                }
                var j = string.Join(" ", words);

                // 带参关键词必须把数值带上，否则运行时无法区分「最多受 0 点」和「最多受 3 点」
                string kw;
                NumExpr kwVal = null;
                // 「Can't attack leaders」只禁主战者，随从照打；
                // 「Can't attack followers or leaders」或裸「Can't attack」才是全面禁止。
                // 之前一律并成 cannot_attack，带前者字样的随从连随从都打不了。
                if (j.Contains("attack leaders") && !j.Contains("attack followers"))
                    kw = "cannot_attack_leaders";
                else if (j.Contains("attack followers") && !j.Contains("attack leaders"))
                    kw = "cannot_attack_followers";
                else if (j.Contains("attack followers") || j.Contains("attack leaders"))
                    kw = "cannot_attack";
                else if (j.Contains("be destroyed"))
                    kw = "cannot_destroy";
                // "Can't be played"是「这张卡不能从手牌打出」，与「不能攻击」完全不同。
                // 旧实现落进兜底分支变成 cannot_attack，会让这张卡仍可以打出。
                else if (j.Contains("be played"))
                    kw = "cannot_be_played";
                else if (j.Contains("attack") && j.Contains("time"))
                {
                    kw = "max_attacks_per_turn";
                    // 有表达式就用表达式（X / Y / count(...)），否则退到常数。
                    kwVal = numExpr ?? NumExpr.Const(numInClause ?? 1);
                }
                else if (j.Contains("take more than"))
                {
                    kw = "damage_cap";
                    kwVal = numExpr ?? NumExpr.Const(numInClause ?? 0);
                }
                else kw = "cannot_attack";

                return new List<EffectNode>
                {
                    new ModifyKeywordNode
                    {
                        Target = TargetSpec.Self,
                        Op = KeywordOp.Grant,
                        Keyword = _lex.Keyword(kw),
                        Value = kwVal,
                    },
                };
            }
            // During your turn, <事件句> → 剥掉前缀，交给外层重新解析
            case "during":
            {
                cur.Advance();
                cur.TryMatchAnyWord("your", "each", "the");
                cur.TryMatchAnyWord("turn");
                cur.TryMatch(TokKind.Comma);
                return ParseSequence(cur);
            }
            // Countdown (4) 独立成句 → 给自身挂倒计时
            case "countdown":
            {
                cur.Advance();
                int n = ConsumeParenParam(cur);
                return new List<EffectNode>
                {
                    new ModifyCounterNode
                    {
                        Counter = _lex.Counter("countdown"),
                        Op = CounterOp.Set,
                        Delta = NumExpr.Const(n),
                        // 倒计时是**这个护符自己**的计数器
                        Target = TargetSpec.Self,
                    },
                };
            }
            // Combo (3) - / Rally (10) - 这类修饰头：消费后继续解析剩余动作
            case "combo": case "rally": case "necromancy": case "earth":
            {
                var mod = ConsumeModifierHeader(cur);
                if (mod == null) return null;
                return ParseSequence(cur);
            }
            default:
                // 裸关键词（Ward / Rush / Storm …）
                if (t.Kind == TokKind.Keyword)
                    return ParseBareKeyword(cur);
                return null;
        }
    }

    // ---------- 伤害 ----------

    /// <summary>
    /// 给「原文根本没写目标、由解析器兜底」的 TargetSpec 打标记。
    ///
    /// InsteadWeaver 的透镜要在「替代句自己写了目标」时保持原样，
    /// 在「没写目标」时才继承前一句 —— 这两种情况的 TargetSpec 字段值可能完全一样
    /// （"deal 4 damage" 兜底出来的 All/Board/Opponent 与 "to all enemy followers"
    /// 只差 Where），所以判据只能是这个显式标记，不能是值判等。
    /// </summary>
    private static TargetSpec OmittedTargetSpec(TargetSpec t) => new()
    {
        SourceKind = t.SourceKind,
        Owner = t.Owner,
        Zone = t.Zone,
        Slot = t.Slot,
        CardId = t.CardId,
        CardName = t.CardName,
        Where = t.Where,
        Sort = t.Sort,
        SortDescending = t.SortDescending,
        TopN = t.TopN,
        DistinctByName = t.DistinctByName,
        DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode,
        Count = t.Count,
        History = t.History,
        IncludeLeader = t.IncludeLeader,
        OmittedFields = TargetField.All,
    };

    private List<EffectNode> ParseDeal(TokenCursor cur)
    {
        cur.ExpectWord("deal");

        // "Deal N damage split between all enemies"
        var splitFlag = false;
        var extraDmg = new List<EffectNode>();

        // 形态 A: deal N damage to <target>
        // 形态 B: deal it N damage          （it 指代上一条动作的目标）
        TargetSpec target;
        NumExpr amount;
        if (cur.Current.IsAnyWord("it", "them"))
        {
            cur.Advance();
            amount = ParseNumExpr(cur);
            cur.TryMatchAnyWord("damage", "damage,");
            target = _target.ResolvePronounTarget();
        }
        else
        {
            amount = ParseNumExpr(cur);
            cur.TryMatchAnyWord("damage", "damage,");
            if (cur.Current.IsAnyWord("split"))
            {
                splitFlag = true;
                cur.Advance();
                cur.TryMatchAnyWord("between");
            }
            cur.TryMatchAnyWord("to");
            // 读不出目标才兜底成「敌方场上随从」，并整体置上 OmittedFields：
            // 这个标记是替代句透镜唯一可靠的依据 —— 兜底值与显式写出的
            // "to all enemy followers" 在 Owner/Zone/Mode/Count 上完全一样。
            target = NormalizeAllCount(_target.TryParse(cur)
                ?? OmittedTargetSpec(TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent)));
        }
        // "... and 2 damage to your leader" —— 第二段伤害
        while (cur.Current.IsWord("and") && cur.Peek(1).Kind == TokKind.Number && cur.Peek(2).IsWord("damage"))
        {
            cur.Advance();
            int amt2 = cur.Current.Value; cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("to");
            var t2 = NormalizeAllCount(_target.TryParse(cur) ?? OmittedTargetSpec(TargetSpec.OpponentLeader));
            extraDmg.Add(new DealDamageNode { Target = t2, Amount = NumExpr.Const(amt2), IsAbility = true });
        }

        var dmgNodes = new List<EffectNode>
        {
            new DealDamageNode { Target = target, Amount = amount, IsAbility = true, IsSplit = splitFlag },
        };
        // IncludeLeader 一律由 VmEval.ResolveRef 统一展开成「场上随从 + 主战者」。
        //
        // 这里**不能再手工补一条打主战者的节点**：
        //   · 「Deal X damage to all enemies」会变成 随从 X + 主战者 X（各一条），
        //     而第一条的 Target 本身已带 IncludeLeader，主战者被打了 **两次**；
        //   · 「Deal 7 damage split between all enemies」会先分 7 点给随从，
        //     再满额打主战者 7 点，共 14 点；
        //   · 同理要打在 ParseRestore / 其它按 Target 结算的动作上时，
        //     这条手工补丁根本覆盖不到，主战者永远回不上血。
        // 展开交给求值层，AST 上就只有一个干净的节点。
        dmgNodes.AddRange(extraDmg);
        return dmgNodes;
    }

    /// <summary>
    /// Mode=All 时 Count 不参与选取（VmEval 的 All 分支直接返回整个池）。
    /// 但 TargetParser 总会留下默认的 Count=Const(1)，打印器如实渲染成
    /// "All n=1"，看起来就像「全体只打 1 个」。这里把 All 下的数量统一置空，
    /// 让 "to all enemy followers" 与 "to an enemy follower" 在 AST 上可区分。
    /// 只动 Mode 为 All 的形态，First / Random / Choose 的数量原样保留 ——
    /// 它们真的需要这个数量。
    /// </summary>
    private static TargetSpec NormalizeAllCount(TargetSpec t)
    {
        if (t == null || t.Mode != Core.SelectMode.All || t.Count == null) return t;
        return new TargetSpec
        {
            SourceKind = t.SourceKind,
            Owner = t.Owner,
            Zone = t.Zone,
            Slot = t.Slot,
            CardId = t.CardId,
            CardName = t.CardName,
            Where = t.Where,
            Sort = t.Sort,
            SortDescending = t.SortDescending,
            TopN = t.TopN,
            DistinctByName = t.DistinctByName,
            DuplicatesOnly = t.DuplicatesOnly,
            Mode = t.Mode,
            Count = null,
            History = t.History,
            IncludeLeader = t.IncludeLeader,
            OmittedFields = t.OmittedFields,
        };
    }

    // ---------- 抽牌 ----------

    private List<EffectNode> ParseDraw(TokenCursor cur)
    {
        cur.ExpectWord("draw");

        // "Draw a card" / "Draw an amulet" 里的冠词不是目标。
        // 之前直接 ParseNumExpr → TryMatchAnyWord("card") 失败，
        // 于是 "a card" 被 TargetParser 当成目标，
        // 整条抽牌变成了「把牌库里一张随机牌移到手牌」。
        cur.TryMatchAnyWord("a", "an", "the");

        var n = ParseNumExpr(cur);
        cur.TryMatchAnyWord("a", "an", "the");

        // "Draw an X-cost follower" / "Draw a 3-cost spell"：
        // 这里的 X / 3 是「费用过滤」，不是抽牌张数。
        // ParseNumExpr 已经把它当成数量吃掉了，剩下 "-cost follower" 无人识别。
        NumExpr costFilter = null;
        if (cur.Current.Kind == TokKind.Dash)
        {
            cur.Advance();                       // -
            cur.TryMatchAnyWord("cost");
            costFilter = n;
            n = NumExpr.Const(1);                // 数量恢复为 1
            cur.TryMatchAnyWord("a", "an", "the");
        }

        cur.TryMatchAnyWord("card", "cards");
        var filter = _target.TryParse(cur);

        // 费用过滤归到 Where
        if (costFilter != null)
        {
            var cf = costFilter;
            if (filter == null)
                filter = new TargetSpec { SourceKind = TargetSourceKind.Zone, Zone = ZoneRef.Deck, Owner = ZoneScope.Self };
            filter = filter.WithWhere(new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                Op = CompareOp.Eq,
                Right = cf,
            });
        }

        // 定向检索必须继承 filter 的 CardId / CardName / Mode：
        //   "Draw all copies of Rusty"  -> Mode=All + CardId
        //   "Draw a Swordcraft follower" -> Mode=Random + Trait 过滤
        // 之前把这些全丢了、Mode 硬写成 First，
        // 结果变成了「无条件抽牌库顶第 1 张」。
        TargetSpec from;
        if (filter != null)
        {
            from = new TargetSpec
            {
                SourceKind = filter.SourceKind,
                Zone = ZoneRef.Deck,
                Owner = ZoneScope.Self,
                CardId = filter.CardId,
                CardName = filter.CardName,
                Where = filter.Where,
                // "all copies" 保留 All；其余无明确模式时按随机检索。
                // Count 是不可空 NumExpr：写 null 会让抽牌数量整条丢失，
                // 应始终传入 n，由 Mode 决定它的语义（All 时 n 被忽略）。
                Mode = filter.Mode == SelectMode.All ? SelectMode.All : SelectMode.Random,
                Count = n,
                DistinctByName = filter.DistinctByName,
                Sort = filter.Sort,
                SortDescending = filter.SortDescending,
                Slot = filter.Slot,
            };
        }
        else
        {
            from = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Zone = ZoneRef.Deck,
                Owner = ZoneScope.Self,
                Mode = SelectMode.First,   // 普通抽牌：牌库顶
                Count = n,
            };
        }
        // 抽到的牌进入手牌 = 创建了一个新实体，
        // 后续 "give it ..." 应指向它（设计3 5.3：Add/Draw -> $created）。
        _target.MarkBound(TargetSpec.SlotCreated);

        return new List<EffectNode>
        {
            new MoveZoneNode { Target = from, ToZone = ZoneRef.Hand, OutSlot = TargetSpec.SlotCreated },
        };
    }

    // ---------- 恢复 ----------

    private List<EffectNode> ParseRestore(TokenCursor cur)
    {
        cur.ExpectWord("restore");
        bool full = false;
        NumExpr amount;
        if (cur.Current.IsAnyWord("all"))
        {
            cur.Advance();
            cur.TryMatchAnyWord("of");
            full = true;
            amount = NumExpr.Const(0);
        }
        else if (cur.MatchPhrase("the", "same", "amount"))
            amount = NumExpr.Prop(StatKey.Life, TargetSpec.Self);
        else amount = ParseNumExpr(cur);
        cur.TryMatchAnyWord("defense", "defense,", "health");
        cur.TryMatchAnyWord("to");
        var target = NormalizeAllCount(_target.TryParse(cur) ?? OmittedTargetSpec(TargetSpec.Self));
        return new List<EffectNode>
        {
            new RestoreLifeNode { Target = target, Amount = amount, FullRestore = full },
        };
    }

    private List<EffectNode> ParseRecover(TokenCursor cur)
    {
        cur.ExpectWord("recover");
        var nodes = new List<EffectNode>();

        while (!cur.IsAtEnd)
        {
            if (cur.Current.IsAnyWord("all", "your", "fully"))
            {
                cur.Advance();
                cur.TryMatchAnyWord("of");
                continue;
            }

            var amount = ParseNumExpr(cur);

            string counter;
            if (cur.MatchPhrase("super-evolution", "point") || cur.MatchPhrase("super-evolution", "points"))
                counter = "sep";
            else if (cur.Current.IsAnyWord("super-evolution"))
            {
                cur.Advance();
                cur.TryMatchAnyWord("point", "points");
                counter = "sep";
            }
            else if (cur.Current.IsAnyWord("super"))
            {
                cur.Advance();
                if (cur.Current.Kind == TokKind.Dash) cur.Advance();
                cur.TryMatchAnyWord("evolution", "evolutions");
                cur.TryMatchAnyWord("point", "points");
                counter = "sep";
            }
            else if (cur.MatchPhrase("evolution", "point") || cur.MatchPhrase("evolution", "points")
                  || cur.MatchPhrase("evolve", "point") || cur.MatchPhrase("evolve", "points"))
                counter = "ep";
            else if (cur.MatchPhrase("play", "point") || cur.MatchPhrase("play", "points")
                  || cur.MatchWord("pp"))
            {
                cur.TryMatchAnyWord("point", "points");
                counter = "pp";
            }
            else
            {
                var w = cur.Current.Text.ToLowerInvariant();
                cur.Advance();
                counter = w;
            }

            nodes.Add(new ModifyCounterNode
            {
                Counter = _lex.Counter(counter),
                Op = CounterOp.Add,
                Delta = amount,
            });

            // "and 1 evolution point"
            if (cur.Current.IsWord("and") && cur.Peek().Kind == TokKind.Number) { cur.Advance(); continue; }
            break;
        }
        return nodes;
    }

    // ---------- 授予 ----------

    private List<EffectNode> ParseGive(TokenCursor cur)
    {
        cur.ExpectWord("give");
        var result = new List<EffectNode>();

        // 外层循环：支持 "Give <T1> <G1> and <T2> <G2>"（第二组省略 give）
        while (!cur.IsAtEnd)
        {
            // 0. "give your faith "..."" —— 给**资源计数器**挂能力，必须在目标解析之前判断
            if (cur.Current.IsWord("your") && cur.Peek().IsAnyWord("faith", "crest"))
            {
                int saveF = cur.Position;
                cur.Advance();
                var ctrName0 = cur.Current.Text.ToLowerInvariant();
                cur.Advance();
                if (cur.Current.Kind == TokKind.Quoted)
                {
                    var q0 = cur.Current;
                    cur.Advance();
                    result.Add(new GrantCounterAbilityNode
                    {
                        Counter = _lex.Counter(ctrName0),
                        Owner = ZoneScope.Self,
                        Granted = _compileQuotedAbility(q0.Inner),
                    });
                    ConsumeDuration(cur);
                    continue;
                }
                cur.Seek(saveF);
            }

            // 0b. "... to give it \"Increase the number of Modes you can select by 1.\""
            // 前半句（"Reduce your faith's value by 10"）已经把 $counter 登记为本句主体，
            // 这里的 it 指的就是那个资源计数器。
            // 不识别的话它会被当成实体目标，编译成 GrantAbilityNode(slot:$counter)，
            // 而 $counter 槽位里没有实体，运行期完全落空。
            if (cur.Current.IsWord("it")
                && _target.BoundSlots.Contains(TargetSpec.SlotCounter)
                && cur.Peek().Kind == TokKind.Quoted)
            {
                cur.Advance();                       // it
                var qIt = cur.Current;
                cur.Advance();                       // 引号块
                result.Add(new GrantCounterAbilityNode
                {
                    Counter = _lex.Counter("faith"),
                    Owner = ZoneScope.Self,
                    Granted = _compileQuotedAbility(qIt.Inner),
                });
                ConsumeDuration(cur);
                continue;
            }

            // 1. 解析 1 个或多个并列目标
            var targets = new List<TargetSpec>();
            var first = _target.TryParse(cur);
            if (first == null && IsGrantStart(cur))
            {
                // "give +3/+3 instead" —— 省略目标时默认本卡
                first = TargetSpec.Self;
            }
            if (first == null && cur.Current.IsAnyWord("the", "an") && cur.Peek().IsAnyWord("enemy"))
            {
                cur.Advance();
                cur.TryMatchAnyWord("enemy");
                cur.TryMatchAnyWord("leader");
                first = TargetSpec.OpponentLeader;
            }
            if (first == null)
                throw new LangException("Give 缺少目标，停在 " + cur.Current);
            targets.Add(first);

            while (cur.Current.IsWord("and"))
            {
                int saveAfterAnd = cur.Position;
                cur.Advance();
                if (IsGrantStart(cur)) { cur.Seek(saveAfterAnd); break; }
                var nt = _target.TryParse(cur);
                if (nt == null) { cur.Seek(saveAfterAnd); break; }
                targets.Add(nt);
            }

            // 2. "N random abilities from the following"
            if (cur.Current.Kind == TokKind.Number && cur.Peek(1).IsWord("random")
                && cur.Peek(2).IsAnyWord("abilities", "ability"))
            {
                int pickN = cur.Current.Value;
                cur.Advance(); cur.Advance(); cur.Advance();
                while (!cur.IsAtEnd && !cur.Current.IsWord("following")) cur.Advance();
                cur.TryMatchWord("following");
                result.Add(new BranchModeNode
                {
                    Kind = ModeDispatchKind.Random,
                    PickCount = NumExpr.Const(pickN),
                    Options = Array.Empty<IReadOnlyList<EffectNode>>(),
                });
                return result;
            }

            // 3. 解析 1 个或多个并列授予项
            int granted = 0;
            while (!cur.IsAtEnd)
            {
                var built = TryParseSingleGrant(cur, targets);
                if (built == null) break;
                result.AddRange(built);
                granted++;

                // 授予项后可能紧跟 "at random"，先消费掉
                if (cur.Current.IsWord("at") && cur.Peek().IsWord("random"))
                {
                    cur.Advance(); cur.Advance();
                    break;
                }

                // 消费授予项之间的 , / and。
                // 注意「and ,」的写法（`give it "A" and, until the end of the turn, "B"`）：
                // 以前先判逗号再判 and，and 之后的逗号没被吃掉，
                // IsGrantStart 看到逗号返回 false，直接把后一项留给外层撞墙。
                int sepSave = cur.Position;
                bool hadComma = false, hadAnd = false;
                while (cur.Current.Kind == TokKind.Comma || cur.Current.IsWord("and"))
                {
                    if (cur.Current.Kind == TokKind.Comma) hadComma = true;
                    if (cur.Current.IsWord("and")) hadAnd = true;
                    cur.Advance();
                }

                // 两项授予之间可能插一句时限："and, until the end of the turn, "B""
                // 它修饰的是**下一项**，这一项本身仍按原持续时间处理。
                if (hadAnd && cur.Current.IsWord("until"))
                {
                    ConsumeDuration(cur);
                    cur.TryMatch(TokKind.Comma);
                }

                // "Bane or Ward at random" —— or 后面的关键词作为第二个授予项
                if (cur.Current.IsWord("or"))
                {
                    cur.Advance();
                    var moreOr = TryParseSingleGrant(cur, targets);
                    if (moreOr != null) { result.AddRange(moreOr); granted++; }
                    if (cur.Current.IsWord("at") && cur.Peek().IsWord("random"))
                    {
                        cur.Advance(); cur.Advance();
                    }
                    break;
                }

                // 分隔符之后若是 "at random"，消费并结束
                if (cur.Current.IsWord("at") && cur.Peek().IsWord("random"))
                {
                    cur.Advance(); cur.Advance();
                    break;
                }
                if (!hadComma && !hadAnd) break;
                if (!IsGrantStart(cur)) { cur.Seek(sepSave); break; }
            }

            if (granted == 0)
                throw new LangException("Give 在目标之后未找到可授予的内容，停在 " + cur.Current);

            // 4. 可选持续时间：until the end of ... turn
            // 授予节点已经生成了，只能回填 —— 用 ChangeDuration 重建带持续时间的副本。
            var giveDur = ConsumeDuration(cur);
            if (giveDur != DurationKind.Permanent)
            {
                for (int gi = 0; gi < result.Count; gi++)
                    result[gi] = WithDuration(result[gi], giveDur);
            }

            // 5. 是否还有第二组 "<T2> <G2>"
            int checkNext = cur.Position;
            if (cur.Current.IsWord("and"))
            {
                cur.Advance();
                if (IsGrantStart(cur) || cur.IsAtEnd) { cur.Seek(checkNext); break; }
                var probe = _target.TryParse(cur);
                if (probe == null) { cur.Seek(checkNext); break; }
                cur.Seek(checkNext + 1);   // 跳过 and，进入下一轮
                continue;
            }
            break;
        }

        return result;
    }

    /// <summary>当前位置是否是授予项的起点。</summary>
    private static readonly string[] KnownKeywords =
    {
        "ward", "rush", "storm", "bane", "drain", "ambush", "aura",
        "intimidate", "barrier", "ignores ward",
    };

    private static bool IsGrantStart(TokenCursor cur)
    {
        var t = cur.Current;
        if (t.Kind == TokKind.StatMod) return true;
        if (t.Kind == TokKind.Keyword) return true;
        if (t.Kind == TokKind.Quoted) return true;
        if (t.IsWord("crest")) return true;
        // 词典未注册的关键词（Bane / Ward 等）也算授予项起点
        foreach (var kw in KnownKeywords) if (t.IsWord(kw)) return true;
        return false;
    }

    /// <summary>解析一个授予项，展开到所有目标。</summary>
    private List<EffectNode> TryParseSingleGrant(TokenCursor cur, List<TargetSpec> targets)
    {
        var outNodes = new List<EffectNode>();

        // A. +X/+Y 攻防
        if (cur.Current.Kind == TokKind.StatMod)
        {
            var txt = cur.Current.Text;
            cur.Advance();
            NumExpr atkE = NumExpr.Const(0), lifeE = NumExpr.Const(0);
            var slash = txt.IndexOf('/');
            if (slash > 0)
            {
                atkE = ParseStatValue(txt.Substring(0, slash));
                lifeE = ParseStatValue(txt.Substring(slash + 1));
            }
            else atkE = ParseStatValue(txt);
            foreach (var t in targets)
                outNodes.Add(new ModifyStatsNode
                {
                    Target = t, Op = StatModOp.Add,
                    Attack = atkE, Life = lifeE,
                });
            return outNodes;
        }

        // B. 关键词
        if (cur.MatchPhrase("last", "words"))
            return new List<EffectNode>
            {
                new ModifyKeywordNode { Target = _target.ResolvePronounTarget(), Op = KeywordOp.Remove, Keyword = _lex.Keyword("last words") },
            };
        bool isKwWord2 = false;
        foreach (var kk in KnownKeywords) if (cur.Current.IsWord(kk)) { isKwWord2 = true; break; }
        if (cur.Current.Kind == TokKind.Keyword || isKwWord2)
        {
            var kw = cur.Current.Text;
            cur.Advance();
            foreach (var t in targets)
                outNodes.Add(new ModifyKeywordNode
                {
                    Target = t, Op = KeywordOp.Grant,
                    Keyword = _lex.Keyword(kw),
                });
            return outNodes;
        }

        // C. Crest: <Name>
        if (cur.Current.IsWord("crest"))
        {
            cur.Advance();
            cur.TryMatch(TokKind.Colon);
            // 卡名本身可能带逗号（「Aria, Lady of the Woods」），
            // 所以不能遇到 Comma 就停 —— 那会把名字截成「Aria,」。
            // 先收集到句号，再用词法层的最长卡名匹配还原：
            // Lexicon 认得完整名字时直接取它，认不出才退回整段文本。
            var raw = new System.Text.StringBuilder();
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period
                   && !cur.Current.IsWord("and"))
            {
                if (raw.Length > 0) raw.Append(' ');
                raw.Append(cur.Current.Text);
                cur.Advance();
            }
            var rawName = raw.ToString().Trim();
            // 去掉尾随的逗号/句点残留（「…, 」这类）。
            rawName = rawName.TrimEnd(',', '.', ' ');
            var crestName = rawName;
            foreach (var t in targets)
                outNodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardName = crestName,
                    ToOwner = t.Owner,
                    ToZone = ZoneRef.CrestArea,
                    IsCrest = true,
                    // 主人卡自己在编译期注入的那个纹章定义（含 Abilities）。
                    // 为空时运行时只能按名字造空壳。
                    CrestDef = CrestDefResolver?.Invoke(),
                    Count = NumExpr.Const(1),
                });
            return outNodes;
        }

        // D. 双引号授予能力
        if (cur.Current.Kind == TokKind.Quoted)
        {
            var quoted = cur.Current;
            cur.Advance();
            var granted = _compileQuotedAbility(quoted.Inner);

            // 引号内只是单纯授予关键词 / 属性限制（如 "Can't attack followers or leaders"）时，
            // 直接展开成对目标 t 的 ModifyKeywordNode。
            // 否则包成 GrantAbilityNode 后，外层的
            // "until the end of your opponent's turn" 无法挂到关键词上，时限直接丢失。
            if (granted.Trigger != null
                && granted.Trigger.Event == Core.CoreEvent.PassiveAura
                && (granted.Condition == null || granted.Condition is Ast.ConstBoolExpr)
                && granted.Body != null
                && granted.Body.Count > 0
                && granted.Body.All(n => n is ModifyKeywordNode))
            {
                foreach (var t in targets)
                    foreach (var kn in granted.Body)
                    {
                        var mk = (ModifyKeywordNode)kn;
                        outNodes.Add(new ModifyKeywordNode
                        {
                            Target = t,
                            Op = mk.Op,
                            Keyword = mk.Keyword,
                            Value = mk.Value,
                            Duration = mk.Duration,
                        });
                    }
                return outNodes;
            }

            foreach (var t in targets)
                outNodes.Add(new GrantAbilityNode { Target = t, Granted = granted });
            return outNodes;
        }

        return null;
    }

    /// <summary>"X is the number of ..." / "X starts at 0" —— 变量声明句，不产生动作。</summary>
    private static bool IsVariableDecl(IReadOnlyList<Token> sent)
    {
        if (sent.Count < 3) return false;
        if (!sent[0].IsAnyWord("x", "y", "z", "x/y")) return false;
        for (int i = 1; i < Math.Min(sent.Count, 8); i++)
            if (sent[i].IsAnyWord("is", "are", "starts")) return true;
        return false;
    }

    /// <summary>
    /// 解析 +N / -N / +X / -X 形式的数值。
    /// 之前返回 int，X/Y/Z 因为 TryParse 失败被静默变成 0，
    /// 导致「+X/+0」被编译成「+0/+0」。
    /// </summary>
    private NumExpr ParseStatValue(string s)
    {
        if (string.IsNullOrEmpty(s)) return NumExpr.Const(0);

        bool neg = s[0] == '-';
        var body = (s[0] == '+' || s[0] == '-') ? s.Substring(1) : s;
        body = body.Trim();

        NumExpr mag;
        if (int.TryParse(body, out var num))
            mag = NumExpr.Const(num);
        else if (body.Length > 0 && body.ToUpperInvariant() is "X" or "Y" or "Z")
            mag = VariableResolver != null
                ? VariableResolver(body.ToUpperInvariant())
                : NumExpr.Ctr(_lex.Counter(body.ToUpperInvariant()));
        else
            throw new LangException("无法解析攻防数值：" + s);

        return neg ? NumExpr.Sub(NumExpr.Const(0), mag) : mag;
    }


    // ---------- 召唤 / 加手牌 ----------

    private List<EffectNode> ParseSummon(TokenCursor cur)
    {
        _target.MarkBound(TargetSpec.SlotCreated);
        cur.ExpectWord("summon");
        return ParseSpawnCore(cur, ZoneRef.Board);
    }

    private List<EffectNode> ParseAdd(TokenCursor cur)
    {
        _target.MarkBound(TargetSpec.SlotCreated);
        cur.ExpectWord("add");
        return ParseSpawnCore(cur, ZoneRef.Hand);
    }

    private List<EffectNode> ParseSpawnCore(TokenCursor cur, ZoneRef toZone)
    {
        var nodes = new List<EffectNode>();

        // ---------- 分支 1：复制体 "an exact copy of <X>" / "N copies of <X>" ----------
        //
        // 「exact」是语义分水岭，不是一个可以省略的修饰词：
        //   an exact copy of X —— 复制体继承源目标**当前**的攻/血/上限/费用/关键词/进化；
        //   a copy of X        —— 只是把那张卡的**印刷白板**再拿出来一份。
        // 旧实现两个分支都只置 isCopy，「exact」被吞掉、两种句子编出同一棵 AST，
        // 于是运行期无从区分（SpawnCardNode.IsExactCopy 恒 false）。
        int save0 = cur.Position;
        NumExpr copyCount = NumExpr.Const(1);
        bool isCopy = false;
        bool isExact = false;

        if (cur.MatchPhrase("an", "exact", "copy") || cur.MatchPhrase("an", "exact", "copies"))
        {
            isCopy = true;
            isExact = true;
        }
        else if (cur.Current.Kind == TokKind.Number && cur.Peek().IsAnyWord("exact", "copy", "copies"))
        {
            copyCount = NumExpr.Const(cur.Current.Value);
            cur.Advance();
            isExact = cur.TryMatchAnyWord("exact");
            cur.TryMatchAnyWord("copy", "copies");
            isCopy = true;
        }
        else if (cur.MatchPhrase("a", "copy") || cur.MatchPhrase("a", "copies"))
            isCopy = true;
        else if (cur.Current.Kind == TokKind.Number)
        {
            int probe = cur.Position;
            copyCount = NumExpr.Const(cur.Current.Value);
            cur.Advance();
            cur.TryMatchAnyWord("enemy");
            if (cur.MatchWord("copy") || cur.MatchWord("copies"))
                isCopy = true;
            else { cur.Seek(probe); }
        }

        if (isCopy)
        {
            cur.TryMatchAnyWord("each");
            cur.TryMatchAnyWord("of");
            if (cur.Current.Kind == TokKind.CardName)
            {
                // 「summon an enemy A or B at random」也走这条路径：
                // 先把卡名列表收集全，再决定是逐个召唤还是随机二选一。
                var nameToks = new List<Token> { cur.Current };
                cur.Advance();
                bool sawOr = false;
                while (cur.Current.IsAnyWord("or", "and") || cur.Current.Kind == TokKind.Comma)
                {
                    if (cur.Current.IsWord("or")) sawOr = true;
                    cur.Advance();
                    if (cur.Current.Kind != TokKind.CardName) break;
                    nameToks.Add(cur.Current);
                    cur.Advance();
                }

                var ct = nameToks[0];
                // 用 ConsumeDestination 返回的真实区域覆盖默认值：
                // 「Add 10 copies of X to your deck」必须落到牌组，
                // 原来这里只吞词不记录，永远落到默认的手牌。
                bool atRnd = cur.MatchPhrase("at", "random");
                var dest1 = ConsumeDestination(cur);

                if ((sawOr || atRnd) && nameToks.Count > 1)
                {
                    var opts2 = nameToks.Select(nt => (IReadOnlyList<EffectNode>)new List<EffectNode>
                    {
                        new SpawnCardNode
                        {
                            SourceKind = CardSourceKind.CardId,
                            CardId = nt.CardId,
                            CardName = nt.Text,
                            ToZone = dest1 ?? toZone,
                            ToOwner = ZoneScope.Self,
                            Count = NumExpr.Const(1),
                        },
                    }).ToList();
                    nodes.Add(new BranchModeNode
                    {
                        Kind = ModeDispatchKind.Random,
                        PickCount = NumExpr.Const(1),
                        Options = opts2,
                    });
                    return nodes;
                }

                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct.CardId,
                    CardName = ct.Text,
                    ToZone = dest1 ?? toZone,
                    ToOwner = ZoneScope.Self,
                    Count = copyCount,
                    // 卡名来源没有「当前状态」可继承（那张卡不一定在场上），
                    // 但标志仍要如实落下：让 "exact" 在 AST 里继续可见，
                    // 而不是又一次被悄悄吞掉。
                    IsExactCopy = isExact,
                });
                return nodes;
            }
            var copySrc = _target.TryParse(cur) ?? TargetSpec.Self;
            var dest2 = ConsumeDestination(cur);
            nodes.Add(new SpawnCardNode
            {
                SourceKind = CardSourceKind.CopyOfTarget,
                CopyFrom = copySrc,
                ToZone = dest2 ?? toZone,
                ToOwner = ZoneScope.Self,
                Count = copyCount,
                // 只有「an exact copy」继承源目标的当前状态；
                // 少了这个词就是「把那张卡再印一份」，取白板。
                IsExactCopy = isExact,
            });
            return nodes;
        }
        cur.Seek(save0);

        // ---------- 分支 2：卡名枚举（含牛津逗号 "A, B, and C"） ----------
        int save1 = cur.Position;
        int listCount = 1;
        if (cur.Current.Kind == TokKind.Number)
        {
            listCount = cur.Current.Value;
            cur.Advance();
        }
        cur.TryMatchAnyWord("a", "an");
        cur.TryMatchAnyWord("copies", "copy");
        cur.TryMatchAnyWord("of");

        if (cur.Current.Kind == TokKind.CardName)
        {
            var cardToks = new List<Token>();
            bool sawOr = false;
            while (!cur.IsAtEnd)
            {
                if (cur.Current.Kind != TokKind.CardName) break;
                cardToks.Add(cur.Current);
                cur.Advance();

                int sepPos = cur.Position;
                bool comma = cur.TryMatch(TokKind.Comma);
                bool and = cur.TryMatchWord("and");
                bool or = !and && cur.TryMatchWord("or");
                if (or) sawOr = true;
                if (!comma && !and && !or) break;

                // 预判后面是否还是卡名
                int la = cur.Position;
                cur.TryMatchAnyWord("a", "an", "enemy");
                bool nextIsCard = cur.Current.Kind == TokKind.CardName;
                cur.Seek(la);
                if (!nextIsCard) { cur.Seek(sepPos); break; }
                cur.TryMatchAnyWord("a", "an", "enemy");
            }

            // 不带 copies of 的 "Add a X to your deck" 也走这里，
            // 必须接住 ConsumeDestination 返回的真实区域。
            bool atRandom = cur.MatchPhrase("at", "random");
            var dest2a = ConsumeDestination(cur) ?? toZone;

            // 「summon A or B at random」：随机召唤其中之一，
            // 不是「A 和 B 都召唤」。
            if ((sawOr || atRandom) && cardToks.Count > 1)
            {
                var opts = cardToks.Select(ct => (IReadOnlyList<EffectNode>)new List<EffectNode>
                {
                    new SpawnCardNode
                    {
                        SourceKind = CardSourceKind.CardId,
                        CardId = ct.CardId,
                        CardName = ct.Text,
                        ToZone = dest2a,
                        ToOwner = ZoneScope.Self,
                        Count = NumExpr.Const(listCount),
                    },
                }).ToList();

                nodes.Add(new BranchModeNode
                {
                    Kind = ModeDispatchKind.Random,
                    PickCount = NumExpr.Const(1),
                    Options = opts,
                });
            }
            else
            {
                foreach (var ct in cardToks)
                    nodes.Add(new SpawnCardNode
                    {
                        SourceKind = CardSourceKind.CardId,
                        CardId = ct.CardId,
                        CardName = ct.Text,
                        ToZone = dest2a,
                        ToOwner = ZoneScope.Self,
                        Count = NumExpr.Const(listCount),
                    });
            }

            // 接续 "and give it ..." / "and evolve it ..."
            while (cur.Current.IsWord("and"))
            {
                int sv = cur.Position;
                cur.Advance();
                var more = ParseOne(cur);
                if (more == null) { cur.Seek(sv); break; }
                nodes.AddRange(more);
            }
            return nodes;
        }
        cur.Seek(save1);

        // ---------- 分支 2b："an <修饰词> <卡名>"（如 an Enhanced Puppet）----------
        {
            int s2b = cur.Position;
            cur.TryMatchAnyWord("an", "a");
            // 跳过 1-2 个修饰词，看是否能落到卡名
            int skipped = 0;
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.CardName && skipped < 3
                   && cur.Current.Kind == TokKind.Word)
            {
                // 遇到动作动词就停
                if (cur.Current.IsAnyWord("to", "and", "with", "on", "from", "at", "or")) break;
                cur.Advance(); skipped++;
            }
            if (cur.Current.Kind == TokKind.CardName && skipped <= 2)
            {
                // 「summon an enemy A or B at random」：先收全卡名列表。
                var names2b = new List<Token> { cur.Current };
                cur.Advance();
                bool sawOr2b = false;
                while (cur.Current.IsAnyWord("or", "and") || cur.Current.Kind == TokKind.Comma)
                {
                    if (cur.Current.IsWord("or")) sawOr2b = true;
                    cur.Advance();
                    if (cur.Current.Kind != TokKind.CardName) break;
                    names2b.Add(cur.Current);
                    cur.Advance();
                }

                var ct2 = names2b[0];
                bool atRnd2b = cur.MatchPhrase("at", "random");
                var dest2b = ConsumeDestination(cur);

                if ((sawOr2b || atRnd2b) && names2b.Count > 1)
                {
                    var opts2b = names2b.Select(nt => (IReadOnlyList<EffectNode>)new List<EffectNode>
                    {
                        new SpawnCardNode
                        {
                            SourceKind = CardSourceKind.CardId,
                            CardId = nt.CardId,
                            CardName = nt.Text,
                            ToZone = dest2b ?? toZone,
                            ToOwner = ZoneScope.Self,
                            Count = NumExpr.Const(1),
                        },
                    }).ToList();
                    nodes.Add(new BranchModeNode
                    {
                        Kind = ModeDispatchKind.Random,
                        PickCount = NumExpr.Const(1),
                        Options = opts2b,
                    });
                    return nodes;
                }

                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct2.CardId,
                    CardName = ct2.Text,
                    ToZone = dest2b ?? toZone,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(1),
                });
                return nodes;
            }
            cur.Seek(s2b);
        }

        // ---------- 分支 2c："a Puppet" 这种最简形式（分支2 已消费冠词但未命中）----------
        {
            int s2c = cur.Position;
            cur.TryMatchAnyWord("a", "an");
            if (cur.Current.Kind == TokKind.CardName)
            {
                var ct3 = cur.Current;
                cur.Advance();
                var dest2c = ConsumeDestination(cur);
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct3.CardId,
                    CardName = ct3.Text,
                    ToZone = dest2c ?? toZone,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(1),
                });
                while (cur.Current.IsWord("and"))
                {
                    int sv2 = cur.Position;
                    cur.Advance();
                    var more2 = ParseOne(cur);
                    if (more2 == null) { cur.Seek(sv2); break; }
                    nodes.AddRange(more2);
                }
                return nodes;
            }
            cur.Seek(s2c);
        }

        // ---------- 分支 2d："N instead" —— 重复上一个召唤，只改次数 ----------
        // 卡面："Evolve: Summon a Mecha Cavalier. Super-Evolve: Summon 2 instead."
        // 这里的 2 是**次数**，宾语是前一句的卡，不是目标数量。
        // 之前落到分支 3，被当成「从场上选 2 个」，于是整句变成 MoveZone。
        {
            int s2d = cur.Position;
            int n = 0;
            bool sawNum = false;
            if (cur.Current.Kind == TokKind.Number) { n = cur.Current.Value; cur.Advance(); sawNum = true; }
            if (sawNum && cur.Current.IsWord("instead"))
            {
                cur.Advance();
                // 必须带 instead 占位标记，否则 InsteadWeaver 认不出这是替代句，
                // 会把它当成独立能力，于是基础句与替代句都执行。
                nodes.Add(new MacroNode { Macro = _lex.Macro("instead_marker") });
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CopyOfPrevious,
                    ToZone = toZone,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(n),
                });
                return nodes;
            }
            cur.Seek(s2d);
        }

        // ---------- 分支 3：跨区拉人 TargetSpec（"3 random ... from your deck"） ----------
        cur.TryMatchAnyWord("any", "all");
        var moveTarget = _target.TryParse(cur);
        if (moveTarget != null)
        {
            ConsumeDestination(cur);
            nodes.Add(new MoveZoneNode
            {
                Target = moveTarget,
                ToZone = toZone,
            });
            return nodes;
        }

        throw new LangException("Summon/Add 无法解析宾语，停在 " + cur.Current);
    }

    // ---------- 移区 ----------

    /// <summary>
    /// 移动动作。fromZone 非 null 时，若 TargetParser 没给出明确区域，
    /// 则用它作为来源区域。
    /// 例："Discard a card" 的弃牌来自手牌，不是战场。
    /// </summary>
    /// <summary>
    /// "move &lt;target&gt; to &lt;zone&gt;"。目标在谓语之前，区域在 to 之后。
    /// </summary>
    private List<EffectNode> ParseMoveVerb(TokenCursor cur)
    {
        cur.ExpectWord("move");
        var target = _target.TryParse(cur);
        if (target == null) throw new LangException("move 后面没有目标");

        ZoneRef to = ZoneRef.Board;
        if (cur.TryMatchAnyWord("to", "into"))
        {
            cur.TryMatchAnyWord("the", "your");
            if (cur.Current.IsAnyWord("board", "field")) to = ZoneRef.Board;
            else if (cur.Current.IsAnyWord("hand")) to = ZoneRef.Hand;
            else if (cur.Current.IsAnyWord("deck")) to = ZoneRef.Deck;
            else if (cur.Current.IsAnyWord("cemetery", "graveyard")) to = ZoneRef.Cemetery;
            else if (cur.Current.IsAnyWord("banished", "banish")) to = ZoneRef.Banished;
            else if (cur.Current.IsAnyWord("ex", "exarea")) to = ZoneRef.ExArea;
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        }

        return new List<EffectNode>
        {
            new MoveZoneNode { Target = target, ToZone = to },
        };
    }

    private List<EffectNode> ParseMove(TokenCursor cur, ZoneRef to, bool isDestroy = false,
                                      ZoneRef? fromZone = null)
    {
        var verb = cur.Current.Text.ToLowerInvariant();
        cur.Advance();   // 动词

        // "return it to deck" / "return it to hand" —— 目的地由短语决定。
        // 只按动词取默认目的地（return -> Hand）会把「回牌库」静默变成「回手牌」。
        if (verb == "return")
        {
            int saveVerb = cur.Position;
            var explicitTo = PeekDestinationZone(cur);
            if (explicitTo.HasValue) to = explicitTo.Value;
            else cur.Seek(saveVerb);
        }

        // "Banish all 1-, 3-, 5-, 7-, and 9-cost cards ..." —— 费用枚举
        if (cur.Current.IsWord("all") && cur.Peek().Kind == TokKind.Number)
        {
            int se = cur.Position;
            cur.Advance();
            var costs = new List<int>();
            while (cur.Current.Kind == TokKind.Number)
            {
                costs.Add(cur.Current.Value);
                cur.Advance();
                if (cur.Current.Kind == TokKind.Dash) cur.Advance();
                // 分隔符可能是 "," 或 "and"，也可能两者都有
                bool sawSep = false;
                if (cur.Current.Kind == TokKind.Comma) { cur.Advance(); sawSep = true; }
                if (cur.Current.IsWord("and")) { cur.Advance(); sawSep = true; }
                if (!sawSep) break;
                if (cur.Current.Kind != TokKind.Number) break;
            }
            if (costs.Count > 0 && cur.MatchWord("cost"))
            {
                var ors = new List<BoolExpr>();
                foreach (var cc in costs)
                    ors.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                        Op = CompareOp.Eq,
                        Right = NumExpr.Const(cc),
                    });
                var t = _target.TryParse(cur);
                var spec = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = t?.Owner ?? ZoneScope.Self,
                    Zone = t?.Zone ?? ZoneRef.Deck,
                    Mode = SelectMode.All,
                    Where = ors.Count == 1 ? ors[0] : BoolExpr.Or(ors.ToArray()),
                };
                return new List<EffectNode> { new MoveZoneNode { Target = spec, ToZone = to, IsDestroy = isDestroy } };
            }
            cur.Seek(se);
        }

        // 数量与 random 由 TargetParser 读；它不认识句首的变量 X，
        // 所以这里只把 X 当数量占位消耗掉，**不覆盖 Mode**。
        // 之前用 Mode = forcedRandom ? Random : Choose 强行改写，把
        // "6 other random followers" 的 Random 压成 Choose，随机破坏全变成手选。
        var leadCount = default(NumExpr);
        if (cur.Current.IsAnyWord("x", "y", "z"))
        {
            var vn = cur.Current.Text.ToUpperInvariant();
            cur.Advance();
            leadCount = NumExpr.Ctr(_lex.Counter(vn));
        }
        else if (cur.Current.Kind == TokKind.Number)
        {
            leadCount = NumExpr.Const((int)cur.Current.Value);
            cur.Advance();
        }

        var target = _target.TryParse(cur) ?? TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent);

        // 弃牌 / 回洗这类动作的来源区域是固定的，不能默认成战场。
        if (fromZone != null && target != null
            && target.SourceKind == TargetSourceKind.Zone
            && target.Zone == ZoneRef.Board)
        {
            target = new TargetSpec
            {
                SourceKind = target.SourceKind,
                Owner = ZoneScope.Self,
                Zone = fromZone.Value,
                Mode = target.Mode,
                Count = target.Count,
                Where = target.Where,
            };
        }

        if (leadCount != null)
        {
            target = new TargetSpec
            {
                SourceKind = target.SourceKind,
                Owner = target.Owner,
                Zone = target.Zone,
                Where = target.Where,
                Mode = target.Mode,          // 保留 TargetParser 算出的 Random / Choose
                Count = leadCount,
            };
        }
        ConsumeDestination(cur);
        var moveNodes = new List<EffectNode> { new MoveZoneNode { Target = target, ToZone = to, IsDestroy = isDestroy } };
        moveNodes.AddRange(ParseAndAllTargets(cur, to, isDestroy));
        return moveNodes;
    }

    /// <summary>
    /// 向前窥视「to &lt;zone&gt;」短语并返回目标区域，不改动游标。
    /// 只认明确写出的区域名；没有就返回 null，调用方保留默认值。
    /// </summary>
    private static ZoneRef? PeekDestinationZone(TokenCursor cur)
    {
        int save = cur.Position;
        try
        {
            // 跳到本句内的 "to"
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
            {
                if (cur.Current.IsWord("to")) break;
                cur.Advance();
            }
            if (cur.IsAtEnd || !cur.Current.IsWord("to")) return null;
            cur.Advance();

            // "to your deck" / "to the top of your deck" / "to your hand"
            if (cur.Current.IsAnyWord("your", "the", "a", "an")) cur.Advance();
            while (cur.Current.IsAnyWord("top", "bottom", "of", "your", "their")) cur.Advance();

            if (cur.Current.IsAnyWord("deck")) return ZoneRef.Deck;
            if (cur.Current.IsAnyWord("hand")) return ZoneRef.Hand;
            if (cur.Current.IsAnyWord("cemetery")) return ZoneRef.Cemetery;
            if (cur.Current.IsAnyWord("banished")) return ZoneRef.Banished;
            if (cur.Current.IsAnyWord("field", "board")) return ZoneRef.Board;
            return null;
        }
        finally
        {
            cur.Seek(save);
        }
    }

    // ---------- 进化 ----------

    private List<EffectNode> ParseEvolve(TokenCursor cur, bool super)
    {
        cur.Advance();
        var target = _target.TryParse(cur) ?? TargetSpec.Self;
        return new List<EffectNode> { new EvolveNode { Target = target, IsSuperEvolve = super } };
    }

    // ---------- 选择 ----------

    private List<EffectNode> ParseSelect(TokenCursor cur)
    {
        cur.ExpectWord("select");
        // Select a Mode / Select 2 Modes → 交给模式块处理器，此处消费掉整句
        if (cur.Peek().IsAnyWord("mode", "modes")
            || (cur.Current.Kind == TokKind.Number && cur.Peek().IsAnyWord("mode", "modes")))
        {
            // 只消费 "select a mode to activate" 这串固定短语；
            // 后面若还有内容，让无进展守卫报错，而不是吞掉。
            while (!cur.IsAtEnd && !cur.Current.IsAnyWord("activate", "activated")) cur.Advance();
            if (cur.Current.IsAnyWord("activate", "activated")) cur.Advance();
            return new List<EffectNode>();
        }

        var target = _target.TryParse(cur);
        if (target == null)
            throw new LangException("select 后面没有可识别的目标");

        // 立刻登记 $selected：本方法内部会继续解析 ", and <动词>" 链，
        // 那些后续动作的 it / them 必须能看见这次 Select。
        _target.MarkBound(TargetSpec.SlotSelected);

        var result = new List<EffectNode>
        {
            new SelectTargetNode { Target = target, OutSlot = TargetSpec.SlotSelected },
        };

        // 后续 , / and <动词> 继续解析
        while (true)
        {
            if (cur.Current.Kind == TokKind.Comma || cur.Current.IsAnyWord("and")) cur.Advance();
            else break;
            if (cur.IsAtEnd || cur.Current.Kind == TokKind.Period) break;
            var save = cur.Position;
            var more = ParseOne(cur);
            if (more == null) { cur.Seek(save); break; }
            result.AddRange(more);
        }
        return result;
    }

    // ---------- 变形 ----------

    private List<EffectNode> ParseTransform(TokenCursor cur)
    {
        cur.ExpectWord("transform");

        // 先把整句剩余 token 收集起来，用 into / based 做分界，避免目标解析吃掉结果部分
        var rest = new List<Token>();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            rest.Add(cur.Current);
            cur.Advance();
        }

        // 找到分界位置
        int intoIdx = -1, basedIdx = -1;
        for (int k = 0; k < rest.Count; k++)
        {
            if (intoIdx < 0 && rest[k].IsWord("into")) intoIdx = k;
            if (basedIdx < 0 && rest[k].IsWord("based")) basedIdx = k;
        }

        // "transform it based on the total cost of the cards fused" —— 融合分派。
        // 具体变形成什么取决于融合费用，由 Battle 层的融合逻辑在结算时决定；
        // 这里用一个标记性的条件节点表达「按融合费用选择」，不再走宏。
        if (basedIdx >= 0 && (intoIdx < 0 || basedIdx < intoIdx))
        {
            // 通知编译器：紧随其后的分档表按**材料总费用**分档，动作是 Transform。
            FuseTierByCost = true;
            // 这句本身**不产出动作**：变形成哪一张由紧随其后的分档表决定，
            // 分档表由 EnglishCompiler.FlushFuseTable 按 fuse_cost 合成。
            //
            // 早期这里会生成「transform into copy of 牌库随机一张」——
            // 既丢掉了分档表，又让这张卡随机变成牌库里的任意一张，
            // 与卡面「based on the total cost of the cards fused」完全无关。
            return new List<EffectNode>();
        }

        // 分界之前是目标
        int splitAt = intoIdx >= 0 ? intoIdx : basedIdx;
        var targetToks = splitAt > 0 ? rest.Take(splitAt).ToList() : new List<Token>();
        var target = targetToks.Count > 0
            ? (_target.TryParse(new TokenCursor(targetToks)) ?? TargetSpec.Self)
            : TargetSpec.Self;

        if (intoIdx < 0)
            throw LangException.At("transform", "既没有 into 也没有 based on", cur, rest);

        // into 之后是结果
        var rhs = rest.Skip(intoIdx + 1).ToList();

        // 「transform the Reaper Card at the bottom of your deck into a Victory Card」
        // 是 Mjerrabaine 纹章的胜负改判机制，不是把卡改成另一张真实卡：
        // 胜利卡没有卡号也没有卡面，走普通 transform 必然解析失败。
        if (rhs.Any(t => t.IsWord("victory")))
        {
            return new List<EffectNode>
            {
                new MarkVictoryCardNode { Owner = Core.ZoneScope.Self },
            };
        }
        var rc = new TokenCursor(rhs);
        rc.TryMatchAnyWord("an", "a");
        rc.TryMatchAnyWord("exact");
        rc.TryMatchAnyWord("copies", "copy");
        rc.TryMatchAnyWord("of");

        // 具体卡名
        if (rc.Current.Kind == TokKind.CardName)
        {
            int id = rc.Current.CardId;
            string nm = rc.Current.Text;
            return new List<EffectNode> { new TransformNode { Target = target, IntoCardId = id, IntoCardName = nm } };
        }

        // 否则是「复制某集合中的卡」
        var copySrc = _target.TryParse(rc);
        if (copySrc == null)
            throw LangException.At("transform into", "既不是卡名也不是可解析的目标集合", rc, rhs);
        return new List<EffectNode> { new TransformNode { Target = target, CopySource = copySrc } };
    }

    /// <summary>
    /// 该目标是否是「本卡自身」。
    ///
    /// 必须按字段判断：TargetSpec 是 class 且未重载 ==，而 TargetSpec.Self 是
    /// `=> new()` 属性，每次访问都是新实例 —— 用 == 比较恒为 false。
    /// </summary>
    private static bool IsSelfTarget(TargetSpec t)
        => t != null && t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelf;

    /// <summary>
    /// 「消耗 N 点资源 **才能** 做某事」：把「扣费 + 效果」包进同一条件分支。
    ///
    /// 不能直接串成「先扣再执行」：WriteCounter 会把负数钳到 0，
    /// 资源不足时扣费静默变成 0，而后续效果照常全额结算 —— 0 信仰白嫖、
    /// 0 线索白嫖炸场。扣费与效果必须同生共死。
    /// </summary>
    private List<EffectNode> PayThenDo(Core.SymbolId<Core.CounterTag> counter, NumExpr amount,
                                       List<EffectNode> payNodes, List<EffectNode> effectNodes)
    {
        var body = new List<EffectNode>(payNodes);
        body.AddRange(effectNodes);
        return new List<EffectNode>
        {
            new ConditionalNode
            {
                Condition = new CompareExpr
                {
                    Left = NumExpr.Ctr(counter),
                    Op = CompareOp.Ge,
                    Right = amount,
                },
                Then = body,
            },
        };
    }

    // ---------- 攻防倍化 ----------

    /// <summary>
    /// "Double the attack and defense of an allied follower."
    ///
    /// 用 Set + prop*2 表达，而不是 Add：「翻倍」是基于当前值重算，
    /// Add 会把已加过的加成再算一遍，同一张卡连吃两次翻倍就成了加四次。
    /// </summary>
    private List<EffectNode> ParseDouble(TokenCursor cur)
    {
        cur.Advance();   // double
        cur.TryMatchAnyWord("the");

        bool both = false;
        bool atk = false, def = false;
        int save = cur.Position;
        while (!cur.IsAtEnd && !cur.Current.IsWord("of") && cur.Current.Kind != TokKind.Period)
        {
            if (cur.Current.IsAnyWord("attack", "attacks")) atk = true;
            if (cur.Current.IsAnyWord("defense", "defence", "life", "health")) def = true;
            if (cur.Current.IsWord("and") || cur.Current.Kind == TokKind.Comma) { }
            cur.Advance();
        }
        if (atk && def) both = true;
        cur.Seek(save);

        // 跳过 "attack and defense" 之类的属性名，定位目标
        while (!cur.IsAtEnd && !cur.Current.IsWord("of") && cur.Current.Kind != TokKind.Period)
            cur.Advance();
        cur.TryMatchAnyWord("of");

        var target = _target.TryParse(cur) ?? TargetSpec.Self;

        return new List<EffectNode>
        {
            new ModifyStatsNode
            {
                Target = target,
                Op = StatModOp.Set,
                // 属性读取的锚点必须是**当前遍历项** $item，不是 target ——
                // target 在 Choose 模式下是整个候选池，求值时永远取第一个，
                // 于是「翻倍 A」实际读的是 A 的身材却赋给玩家选中的 B。
                // 未修改的那一项给 null（不是 Const(0)），否则会被清零。
                Attack = (atk || both)
                    ? NumExpr.Mul(NumExpr.Prop(StatKey.Attack, TargetSpec.Item), NumExpr.Const(2))
                    : null,
                Life = (def || both)
                    ? NumExpr.Mul(NumExpr.Prop(StatKey.Life, TargetSpec.Item), NumExpr.Const(2))
                    : null,
            },
        };
    }

    // ---------- 计数器 ----------

    /// <summary>Reveal &lt;target&gt;：公开一张牌。</summary>
    private List<EffectNode> ParseRevealKeyword(TokenCursor cur)
    {
        cur.ExpectWord("reveal");
        var t = _target.TryParse(cur) ?? TargetSpec.Self;
        return new List<EffectNode> { new RevealCardNode { Target = t } };
    }

    private List<EffectNode> ParseGain(TokenCursor cur)
    {
        cur.ExpectWord("gain");

        // Gain Crest: <卡名>
        if (cur.Current.IsAnyWord("crest"))
        {
            cur.Advance();
            cur.TryMatch(TokKind.Colon);
            string crestName = "";
            if (cur.Current.Kind == TokKind.CardName)
            {
                crestName = cur.Current.Text;
                cur.Advance();
            }
            else
            {
                // 语料可能把带 "vs." 的卡名截断（"Istyndet vs."），
                // 此时按 token 拼成残名，交给运行时按前缀匹配。
                // 但必须在 and / 逗号 / 下一个动词处停下 —— 否则会把
                // "... gain Crest: Sandalphon and return this card to hand."
                // 里的 "and return this card to hand" 当成纹章名字吞掉。
                var sbCrest = new System.Text.StringBuilder();
                while (!cur.IsAtEnd
                       && cur.Current.Kind != TokKind.Period
                       && cur.Current.Kind != TokKind.Comma
                       && !cur.Current.IsAnyWord("and", "then", "return", "draw", "deal",
                                                 "destroy", "give", "summon", "banish", "restore"))
                {
                    if (sbCrest.Length > 0) sbCrest.Append(' ');
                    sbCrest.Append(cur.Current.Text);
                    cur.Advance();
                }
                crestName = sbCrest.ToString();
                // 丢掉紧跟着的分隔符，让后续动作能接上
                if (cur.Current.Kind == TokKind.Comma) cur.Advance();
            }
            // 纹章必须进 CrestArea。
            // 这里曾写成 ExArea（与 TryParseSingleGrant 里的 Crest: 分支不一致），
            // 结果是纹章被塞进额外区，PlayerState.CrestArea 永远为空，
            // 任何"纹章在场"类判定与 MaxCrest 上限都不会生效。
            return new List<EffectNode>
            {
                new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardName = crestName,
                    ToZone = ZoneRef.CrestArea,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(1),
                    IsCrest = true,
                    // 主人卡编译期注入的纹章定义（含 Abilities）。
                    // 为空时运行时只能按名字造空壳 —— 纹章拿得到但没有能力。
                    CrestDef = CrestDefResolver?.Invoke(),
                },
            };
        }

        // Gain [an|N] <resource>
        int amount = 1;
        if (cur.Current.Kind == TokKind.Number) { amount = (int)cur.Current.Value; cur.Advance(); }
        while (cur.Current.IsAnyWord("a", "an")) cur.Advance();

        // 收集剩余词判断资源类型
        var words = new List<string>();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) { words.Add(cur.Current.Text.ToLowerInvariant()); cur.Advance(); }
        var joined = string.Join(" ", words);

        string key =
            joined.Contains("max play point") ? "max_pp"
          : joined.Contains("play point") ? "pp"
          : joined.Contains("earth sigil") ? "earth_sigil"
          : joined.Contains("shadow") ? "shadow"
          : joined.Contains("crest") ? "crest"
          : joined.Contains("faith") ? "faith"
          : joined.Replace(" ", "_");

        if (string.IsNullOrWhiteSpace(key))
            throw new LangException("gain 后面没有可识别的资源");

        return new List<EffectNode>
        {
            new ModifyCounterNode
            {
                Counter = _lex.Counter(key),
                Op = CounterOp.Add,
                Delta = NumExpr.Const(amount),
            },
        };
    }

    private List<EffectNode> ParseCostChange(TokenCursor cur, int sign)
    {
        cur.Advance();   // reduce / increase

        // 「减少主战者生命上限」：Reduce the enemy leader's max defense by 2。
        // 这不是费用变更，以前落进下面的费用分支后整句解析失败。
        {
            int svLead = cur.Position;
            ZoneScope leadOwner = ZoneScope.Self;
            bool hitLeaderMax = false;
            if (cur.MatchPhrase("your", "leader's", "max", "defense"))
            { leadOwner = ZoneScope.Self; hitLeaderMax = true; }
            else if (cur.MatchPhrase("the", "enemy", "leader's", "max", "defense"))
            { leadOwner = ZoneScope.Opponent; hitLeaderMax = true; }

            if (hitLeaderMax)
            {
                cur.TryMatchAnyWord("by");
                var deltaL = ParseNumExpr(cur);
                // 用 ModifyStatsNode 表达：主战者身上 Life 即「生命上限」，
                // 与上面「set max defense to N」走同一条路径。
                return new List<EffectNode>
                {
                    new ModifyStatsNode
                    {
                        Target = leadOwner == ZoneScope.Self ? TargetSpec.SelfLeader : TargetSpec.OpponentLeader,
                        Op = StatModOp.Add,
                        Life = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), deltaL) : deltaL,
                    },
                };
            }
            cur.Seek(svLead);
        }

        // "Reduce your faith's value by N to <动作>"
        if (cur.Current.IsWord("your") && cur.Peek().IsAnyWord("faith's", "crest's"))
        {
            cur.Advance();
            var resName = cur.Current.Text.ToLowerInvariant().Replace("'s", "");
            cur.Advance();
            cur.TryMatchAnyWord("value");
            cur.TryMatchAnyWord("by");
            var d = ParseNumExpr(cur);
            var nodesF = new List<EffectNode>
            {
                new ModifyCounterNode
                {
                    Counter = _lex.Counter(resName),
                    Op = CounterOp.Add,
                    Delta = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), d) : d,
                },
            };
            if (cur.MatchWord("to")) nodesF.AddRange(ParseSequence(cur));
            return nodesF;
        }

        // "Increase X by 1" —— X/Y/Z 变量
        if (cur.Current.IsAnyWord("x", "y", "z"))
        {
            var vn = cur.Current.Text.ToUpperInvariant();
            cur.Advance();
            cur.TryMatchAnyWord("by");
            var dv = ParseNumExpr(cur);
            return new List<EffectNode>
            {
                new ModifyCounterNode
                {
                    Counter = _lex.Counter(vn),
                    Op = CounterOp.Add,
                    Delta = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), dv) : dv,
                },
            };
        }

        // "Increase your Combo by 1" —— 这是计数器，不是费用
        if (cur.Current.IsWord("your") && cur.Peek().IsAnyWord("combo", "faith"))
        {
            cur.Advance();
            var res = cur.Current.Text.ToLowerInvariant();
            cur.Advance();
            cur.TryMatchAnyWord("by");
            var delta = ParseNumExpr(cur);
            ConsumeDuration(cur);
            return new List<EffectNode>
            {
                new ModifyCounterNode
                {
                    Counter = _lex.Counter(res),
                    Op = CounterOp.Add,
                    Delta = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), delta) : delta,
                },
            };
        }

        // 目标："the cost of <TargetNP>" / "its cost" / "their costs"
        TargetSpec target = TargetSpec.Self;
        if (cur.MatchPhrase("the", "cost", "of") || cur.MatchPhrase("the", "costs", "of"))
        {
            target = _target.TryParse(cur) ?? TargetSpec.Self;
            // "Mysteria spells in your hand" 这类未注册兵种：跳到区域短语
            if (target.Zone == ZoneRef.Board && !cur.IsAtEnd && cur.Current.IsWord("spells"))
            {
                cur.Advance();
                target = _target.TryParse(cur) ?? target;
            }
        }
        else if (cur.MatchPhrase("its", "cost") || cur.MatchPhrase("their", "costs")
                 || cur.MatchPhrase("their", "cost"))
            target = _target.ResolvePronounTarget();

        cur.TryMatchAnyWord("by");
        var amt = ParseNumExpr(cur);
        var durCost = ConsumeDuration(cur);

        return new List<EffectNode>
        {
            new ModifyCostNode
            {
                Target = target,
                Op = CostModOp.Add,
                Value = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), amt) : amt,
                Duration = durCost,
            },
        };
    }

    private List<EffectNode> ParseSet(TokenCursor cur)
    {
        cur.Advance();

        // "set your/the enemy leader's max defense to N"
        {
            int sp = cur.Position;
            var leadOwner = ZoneScope.Self;
            bool ok = false;
            if (cur.MatchPhrase("your", "leader's")) { leadOwner = ZoneScope.Self; ok = true; }
            else if (cur.MatchPhrase("the", "enemy", "leader's")) { leadOwner = ZoneScope.Opponent; ok = true; }
            if (ok && cur.MatchPhrase("max", "defense"))
            {
                cur.TryMatchAnyWord("to");
                var maxDefV = ParseNumExpr(cur);
                var durDef = ConsumeDuration(cur);
                return new List<EffectNode>
                {
                    new ModifyStatsNode
                    {
                        Target = leadOwner == ZoneScope.Self ? TargetSpec.SelfLeader : TargetSpec.OpponentLeader,
                        Op = StatModOp.Set,
                        Life = maxDefV,
                        Duration = durDef,
                    },
                };
            }
            cur.Seek(sp);
        }

        // "set their costs to 0"
        if (cur.Current.IsAnyWord("their", "its") && cur.Peek().IsAnyWord("costs", "cost"))
        {
            cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("to");
            var cv = ParseNumExpr(cur);
            return new List<EffectNode>
            {
                new ModifyCostNode { Target = _target.ResolvePronounTarget(), Op = CostModOp.Set, Value = cv },
            };
        }

        // "set their attack and defense to X/Y"
        if (cur.Current.IsAnyWord("its", "their")
            && cur.Peek().IsWord("attack") && cur.Peek(2).IsWord("and"))
        {
            cur.Advance(); cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("defense", "life");
            cur.TryMatchAnyWord("to");
            var atkV = ParseNumExpr(cur);
            // 分词层没有独立的斜杠 token，它是 Word "/"
            if (cur.Current.IsWord("/")) cur.Advance();
            var lifeV = ParseNumExpr(cur);
            return new List<EffectNode>
            {
                new ModifyStatsNode
                {
                    Target = _target.ResolvePronounTarget(),
                    Op = StatModOp.Set,
                    Attack = atkV,
                    Life = lifeV,
                },
            };
        }

        // "set its defense to 0" / "set their attack to 1"
        // 代词 + 属性名必须在这里识别。
        // 之前 TargetParser 会把 it/its 后面的属性名偷吃掉，
        // 现在不偷吃了，就必须在这里自己读。
        if (cur.Current.IsAnyWord("its", "their") && cur.Peek().IsAnyWord("defense", "attack", "life"))
        {
            cur.Advance();
            var sn = cur.Current.Text.ToLowerInvariant();
            cur.Advance();
            cur.TryMatchAnyWord("to");
            var attrV = ParseNumExpr(cur);
            return new List<EffectNode>
            {
                new ModifyStatsNode
                {
                    Target = _target.ResolvePronounTarget(),
                    Op = StatModOp.Set,
                    Attack = sn.StartsWith("attack") ? attrV : null,
                    Life = (sn.StartsWith("defense") || sn.StartsWith("life")) ? attrV : null,
                },
            };
        }

        // "set the attack of all enemy followers on the field to 4"
        // 注意：必须用 Current 判断，否则 MatchWord("the") 会先把 the 吞掉
        if (cur.Current.IsWord("the") && cur.Peek().IsAnyWord("attack", "defense"))
        {
            cur.Advance();
            var statName = cur.Current.Text;
            cur.Advance();
            cur.TryMatchAnyWord("of");
            var tgt = _target.TryParse(cur) ?? _target.ResolvePronounTarget();
            cur.TryMatchAnyWord("to");
            var v2 = ParseNumExpr(cur);
            return new List<EffectNode>
            {
                new ModifyStatsNode
                {
                    Target = tgt, Op = StatModOp.Set,
                    Attack = statName.StartsWith("attack") ? v2 : null,
                    Life = statName.StartsWith("defense") ? v2 : null,
                },
            };
        }

        // "set their/its attack and defense to X"
        if (cur.Current.IsAnyWord("their", "its") && cur.Peek().IsAnyWord("attack"))
        {
            cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("and");
            cur.TryMatchAnyWord("defense");
            cur.TryMatchAnyWord("to");
            var av = ParseNumExpr(cur);
            NumExpr lv = av;
            // "to X/Y" —— 斜杠后是第二个值
            if (cur.Current.Text == "/" || cur.Current.Kind == TokKind.Word && cur.Current.Text == "/")
            {
                cur.Advance();
                lv = ParseNumExpr(cur);
            }
            return new List<EffectNode>
            {
                new ModifyStatsNode { Target = _target.ResolvePronounTarget(), Op = StatModOp.Set, Attack = av, Life = lv },
            };
        }

        // "set the cost of this card to 1"
        if (cur.Current.IsWord("the") && (cur.Peek().IsWord("cost") || cur.Peek().IsWord("costs")))
        {
            cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("of");
            var tgt = _target.TryParse(cur) ?? TargetSpec.Self;
            cur.TryMatchAnyWord("to");
            var v0 = ParseNumExpr(cur);
            var durSet = ConsumeDuration(cur);
            return new List<EffectNode>
            {
                new ModifyCostNode { Target = tgt, Op = CostModOp.Set, Value = v0, Duration = durSet },
            };
        }

        var target = _target.TryParse(cur) ?? TargetSpec.Self;
        cur.TryMatchAnyWord("to");
        var v = ParseNumExpr(cur);
        return new List<EffectNode>
        {
            new ModifyStatsNode
            {
                Target = target,
                Op = StatModOp.Set,
                Attack = v,
                Life = v,
            },
        };
    }

    // ---------- 增幅 / 复活 ----------

    private List<EffectNode> ParseSpellboost(TokenCursor cur)
    {
        cur.ExpectWord("spellboost");
        var target = TargetSpec.Self;

        if (cur.MatchPhrase("your", "hand"))
            target = new TargetSpec { SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self, Zone = ZoneRef.Hand, Mode = SelectMode.All };
        else if (cur.Current.IsAnyWord("it", "them"))
        {
            cur.Advance();
            target = _target.ResolvePronounTarget();
        }
        else
        {
            var t = _target.TryParse(cur);
            if (t != null) target = t;
        }

        NumExpr times = NumExpr.Const(1);
        if (cur.Current.Kind == TokKind.Number)
        {
            times = NumExpr.Const(cur.Current.Value);
            cur.Advance();
        }
        else if (cur.Current.IsAnyWord("x", "y", "z"))
        {
            times = VariableResolver != null
                ? VariableResolver(cur.Current.Text.ToUpperInvariant())
                : NumExpr.Ctr(_lex.Counter(cur.Current.Text.ToUpperInvariant()));
            cur.Advance();
        }
        cur.TryMatchAnyWord("time", "times");

        return new List<EffectNode>
        {
            new ModifyCounterNode
            {
                Counter = _lex.Counter("spellboost"),
                Op = CounterOp.Add,
                Delta = times,
                // 魔力增幅是**手牌/实体自己**的计数器，不是主战者的
                Target = target,
            },
        };
    }

    private List<EffectNode> ParseReanimate(TokenCursor cur)
    {
        var result = new List<EffectNode>();
        while (cur.Current.IsAnyWord("reanimate"))
        {
            cur.Advance();
            int n = 0;
            if (cur.Current.Kind == TokKind.OpenParen && cur.Peek().Kind == TokKind.Number)
            {
                cur.Advance();
                n = (int)cur.Current.Value;
                cur.Advance();
                cur.TryMatch(TokKind.CloseParen);
            }
            // Reanimate (N)：从本局被破坏的随从里，**自动**取一个
            // 基础费用 ≤ N 且基础费用最高的随从出场；同费用按数量加权随机。
            // 绝对不能弹框让玩家手选（之前写成 Choose，运行时会挂起等玩家）。
            //
            // 口径必须是 BaseCost 而不是 Cost（76-c 第 6.2-3 条）：
            // 被减费 / 加费过的随从，当前费用与基础费用不再相同。
            // 实测：本体 4 费被减到 9 费的随从破坏后，按当前费用筛会被 4 挡在门外，
            // Reanimate (4) 召不回来。排序同理 —— 筛选与排序必须是同一把尺子，
            // 否则第 3 步「找 BaseCost 最大的 M」会按当前费用挑出被减费的那张。
            result.Add(new SpawnCardNode
            {
                SourceKind = CardSourceKind.CopyOfTarget,
                CopyFrom = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Zone = ZoneRef.Cemetery,
                    Owner = ZoneScope.Self,
                    Mode = SelectMode.Random,
                    Count = NumExpr.Const(1),
                    History = HistoryScope.DestroyedThisMatch,
                    Sort = SortKey.BaseCost,
                    SortDescending = true,
                    Where = BoolExpr.And(
                        new CompareExpr
                        {
                            Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                            Op = CompareOp.Le,
                            Right = NumExpr.Const(n),
                        },
                        new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item }),
                },
                ToZone = ZoneRef.Board,
                ToOwner = ZoneScope.Self,
                Count = NumExpr.Const(1),
                AddDepartedTrait = true,
            });
            if (!cur.TryMatchAnyWord("and")) break;
        }
        return result;
    }

    // ---------- Do this N times ----------

    private List<EffectNode> ParseDo(TokenCursor cur)
    {
        cur.ExpectWord("do");
        cur.TryMatchAnyWord("this", "it");
        var n = ParseNumExpr(cur);
        cur.TryMatchAnyWord("time", "times");
        cur.TryMatch(TokKind.Colon);

        IReadOnlyList<EffectNode> body = Array.Empty<EffectNode>();
        if (cur.Current.Kind == TokKind.Quoted)
        {
            var q = cur.Current;
            cur.Advance();

            // 引号内按句号切分，剔除变量声明句（"X is ..."）后再逐句解析
            // OptionMark 是「新选项起点」，必须原样保留下来，
            // 否则下面的编号块收集器看不到它，整段引号内容会被当成普通句子。
            var kept = new List<Token>();
            var sentBuf = new List<Token>();
            void Flush()
            {
                if (sentBuf.Count > 0)
                {
                    // 引号内的变量声明必须**登记**，而不只是剔除：
                    //   "Return your hand to deck. Draw X cards.
                    //    X is the number of cards you returned. ..."
                    // 旧实现只把它从 kept 里滤掉，X 从未绑定，
                    // Draw X 于是编译成读空槽 ctr:X，一张都抽不回。
                    if (IsVariableDecl(sentBuf)) VariableBinder?.Invoke(sentBuf);
                    else kept.AddRange(sentBuf);
                }
                sentBuf.Clear();
            }
            foreach (var tk2 in q.Inner)
            {
                if (tk2.Kind == TokKind.OptionMark)
                {
                    Flush();
                    kept.Add(tk2);
                    continue;
                }
                if (tk2.Kind == TokKind.Period)
                {
                    if (sentBuf.Count > 0)
                    {
                        Flush();
                        kept.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = tk2.Pos });
                    }
                    continue;
                }
                sentBuf.Add(tk2);
            }
            Flush();

            // 引号内可能自带编号选项块：
            //   Do this 2 times: "Select a Mode to activate. 1. Draw a card. 2. Deal 2 damage."
            // 设计3 5.1.2「情况 1 引号内嵌式」要求这里递归构造内层 SelectModesNode。
            // 之前直接丢给 ParseSequence 逐句解析，选项头无人识别，
            // 整段引号内容被吃掉，循环体变成空的。
            var inner = new ActionParser(_lex, _compileQuotedAbility, _target);
            // 内层解析器必须继承变量登记器与时针，否则引号内的
            // "X is ..." 仍然只是被剔除，不会真正登记。
            inner.VariableBinder = VariableBinder;
            inner.VariableResolver = VariableResolver;
            var innerCur = new TokenCursor(kept);

            // 引号内通常先有一句模式引导语（"Select a Mode to activate." / 
            // "Activate 2 random abilities from the following."），
            // 真正的编号选项从它之后才开始。所以先把引导句整句跳过。
            int optStart = -1;
            for (int i = 0; i < kept.Count; i++)
            {
                if (kept[i].Kind == TokKind.OptionMark) { optStart = i; break; }
            }
            if (optStart > 0)
            {
                // 回退到引导句起点（上一个 Period 之后）
                int lead = optStart;
                while (lead > 0 && kept[lead - 1].Kind != TokKind.Period) lead--;
                innerCur.Seek(optStart);
            }

            var opts = new List<IReadOnlyList<EffectNode>>();
            while (!innerCur.IsAtEnd && innerCur.Current.Kind == TokKind.OptionMark)
            {
                innerCur.Advance();                       // 消费 "1."
                var branch = new List<EffectNode>();
                while (!innerCur.IsAtEnd && innerCur.Current.Kind != TokKind.OptionMark)
                {
                    branch.AddRange(inner.ParseSequence(innerCur));
                    if (innerCur.IsAtEnd) break;
                    // ParseSequence 遇 OptionMark 会自然停止，这里防止空转
                    if (innerCur.Current.Kind == TokKind.OptionMark) break;
                    innerCur.Advance();
                }
                opts.Add(branch);
            }

            if (opts.Count >= 2)
            {
                body = new List<EffectNode>
                {
                    new BranchModeNode
                    {
                        Kind = ModeDispatchKind.Choose,
                        PickCount = NumExpr.Const(1),
                        Options = opts,
                    },
                };
            }
            else
            {
                body = innerCur.Position > 0 && opts.Count == 0
                    ? inner.ParseSequence(new TokenCursor(kept))
                    : inner.ParseSequence(new TokenCursor(kept));
            }
        }
        return new List<EffectNode> { new LoopNode { Kind = LoopKind.Times, Count = n, Body = body } };
    }

    // ---------- 裸关键词 ----------

    private List<EffectNode> ParseBareKeyword(TokenCursor cur)
    {
        var kw = cur.Current.Text;
        cur.Advance();
        return new List<EffectNode>
        {
            new ModifyKeywordNode
            {
                Target = TargetSpec.Self,
                Op = KeywordOp.Grant,
                Keyword = _lex.Keyword(kw),
            },
        };
    }


    // ---------- 补充：修饰头 / 参数 / 收尾 ----------

    /// <summary>消费 "to your hand" / "to the field" 这类收尾短语。</summary>
    /// <summary>"and all followers" / "and all enemy copies of it" 这类并列全体目标。</summary>
    /// <summary>
    /// "destroy it and a random enemy follower" 里的第二个及后续目标。
    ///
    /// isDestroy 必须透传：否则第二个目标构造出的 MoveZoneNode.IsDestroy 恒为 false，
    /// 运行时走 MoveEntity 而不是 DestroyEntity ——
    /// 被炸的随从不触发谢幕曲、不加墓场、不进破坏池，
    /// 且无视「无法被能力破坏」与超进化免死。
    /// 中文打印还会渲染成「破坏该目标。舍弃敌方战场的随机1个随从。」
    /// </summary>
    private List<EffectNode> ParseAndAllTargets(TokenCursor cur, ZoneRef to, bool isDestroy = false)
    {
        var nodes = new List<EffectNode>();
        while (cur.Current.IsWord("and"))
        {
            int save = cur.Position;
            cur.Advance();
            var t = _target.TryParse(cur);
            if (t == null) { cur.TryMatchAnyWord("all"); t = _target.TryParse(cur); }

            // 解析不出目标就把游标退回去：
            // 否则 "and evolve this follower" 这类并列动作里的 and 被吃掉，
            // 后面的动词跟着丢，整句只剩前半段。
            if (t == null) { cur.Seek(save); break; }
            nodes.Add(new MoveZoneNode { Target = t, ToZone = to, IsDestroy = isDestroy });
        }
        return nodes;
    }

    private ZoneRef? ConsumeDestination(TokenCursor cur)
    {
        // 必须把真实区域**返回**出去：原来这里只吞词、不记录，
        // 「Add 10 copies of Chronos to your deck」于是永远落到默认的
        // ZoneRef.Hand —— 牌组里一张没加，手牌爆掉。
        var explicitZone = PeekDestinationZone(cur);

        while (cur.Current.IsAnyWord("to"))
        {
            var nx = cur.Peek();
            if (nx.IsAnyWord("your", "the"))
            {
                cur.Advance(); cur.Advance();
                if (!cur.IsAtEnd) cur.Advance();
                continue;
            }
            if (nx.IsAnyWord("hand", "deck", "field", "cemetery", "banished", "ex area"))
            {
                cur.Advance(); cur.Advance();
                continue;
            }
            // "to its hand" / "to their hand" / "to its owner's hand"
            if (nx.IsAnyWord("its", "their"))
            {
                cur.Advance(); cur.Advance();
                while (cur.Current.IsAnyWord("owner's", "owner", "own")) cur.Advance();
                if (cur.Current.IsAnyWord("hand", "deck", "field", "cemetery", "banished")) cur.Advance();
                continue;
            }
            // 兜底：吃掉 "to" 保证循环一定推进。
            // 否则 "to <未识别词>" 会让 while 恒真而两个分支都不匹配，造成死循环。
            cur.Advance();
        }
        // "to your hand without revealing it"
        if (cur.MatchPhrase("without", "revealing")) { cur.TryMatchAnyWord("it", "them"); }
        return explicitZone;
    }

    /// <summary>消费括号里的参数 Enhance (4) / Combo (3)。返回参数值，无参数返回 0。</summary>
    private int ConsumeParenParam(TokenCursor cur)
    {
        if (cur.Current.Kind != TokKind.OpenParen) return 0;
        if (cur.Peek().Kind != TokKind.Number) return 0;
        cur.Advance();
        int v = (int)cur.Current.Value;
        cur.Advance();
        cur.TryMatch(TokKind.CloseParen);
        return v;
    }

    /// <summary>解析 "remove <X> from <Y>"。</summary>
    private List<EffectNode> ParseRemove(TokenCursor cur)
    {
        cur.ExpectWord("remove");

        // "Remove Ward from this follower. Give it Intimidate."
        // 第二个句子的 it 指向被移除能力的那个目标。
        // 本卡是最常见情形，先登记 $self；若目标是别的实体，
        // 由后面的 TargetSpec 解析另行覆盖。
        _target.MarkBound(TargetSpec.SlotSelf);
        if (cur.Current.IsAnyWord("all"))
        {
            cur.Advance();
            if (cur.Current.IsAnyWord("abilities", "ability"))
            {
                cur.Advance();
                while (!cur.IsAtEnd && !cur.Current.IsAnyWord("from")) cur.Advance();
                cur.TryMatchAnyWord("from");
                var tgt = _target.TryParse(cur) ?? _target.ResolvePronounTarget();
                return new List<EffectNode> { new ModifyKeywordNode { Target = tgt, Op = KeywordOp.SilenceAll } };
            }
            return new List<EffectNode>();
        }
        if (cur.Current.IsWord("last") && cur.Peek().IsWord("words"))
        {
            cur.Advance(); cur.Advance();
            while (!cur.IsAtEnd && !cur.Current.IsAnyWord("from")) cur.Advance();
            cur.TryMatchAnyWord("from");
            var lt = _target.TryParse(cur) ?? _target.ResolvePronounTarget();
            return new List<EffectNode>
            {
                new ModifyKeywordNode { Target = lt, Op = KeywordOp.Remove, Keyword = _lex.Keyword("last words") },
            };
        }
        if (cur.Current.Kind == TokKind.Keyword)
        {
            var kw = cur.Current.Text;
            cur.Advance();
            while (!cur.IsAtEnd && !cur.Current.IsAnyWord("from")) cur.Advance();
            cur.TryMatchAnyWord("from");
            var tgt = _target.TryParse(cur) ?? _target.ResolvePronounTarget();
            return new List<EffectNode>
            {
                new ModifyKeywordNode
                {
                    Target = tgt,
                    Op = KeywordOp.Remove,
                    Keyword = _lex.Keyword(kw),
                },
            };
        }
        return new List<EffectNode>();
    }

    /// <summary>解析 "replicate the effects of this card Fanfare"。</summary>
    private List<EffectNode> ParseReplicate(TokenCursor cur)
    {
        cur.ExpectWord("replicate");
        var ev = CoreEvent.OnPlay;
        while (!cur.IsAtEnd)
        {
            if (cur.Current.IsAnyWord("fanfare")) { ev = CoreEvent.OnPlay; break; }
            if (cur.Current.IsAnyWord("last")) { ev = CoreEvent.OnDestroy; break; }
            if (cur.Current.IsAnyWord("evolve")) { ev = CoreEvent.OnEvolve; break; }
            if (cur.Current.IsAnyWord("strike")) { ev = CoreEvent.OnAttack; break; }
            cur.Advance();
        }
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        return new List<EffectNode> { new InvokeAbilityNode { Target = TargetSpec.Self, Event = ev } };
    }

    /// <summary>消费 Combo (3) - / Rally (10) - / Necromancy (4) - 这类修饰头。</summary>
    private (string name, int param)? ConsumeModifierHeader(TokenCursor cur)
    {
        var t = cur.Current;
        if (t.Kind != TokKind.Word && t.Kind != TokKind.Keyword) return null;
        var w = t.Text.ToLowerInvariant();
        if (w != "combo" && w != "rally" && w != "necromancy" && w != "earth"
            && w != "enhance" && w != "countdown" && w != "reanimate") return null;

        string name = w;
        cur.Advance();
        if (w == "earth" && cur.Current.IsAnyWord("rite")) { cur.Advance(); name = "earth rite"; }
        int param = ConsumeParenParam(cur);
        if (cur.Current.Kind == TokKind.Dash || cur.Current.Kind == TokKind.Colon) cur.Advance();
        return (name, param);
    }

    // ---------- 数值表达式 ----------


    /// <summary>Replace your deck with the Apocalypse Deck。</summary>
    private List<EffectNode> ParseReplace(TokenCursor cur)
    {
        cur.ExpectWord("replace");
        while (!cur.IsAtEnd && !cur.Current.IsAnyWord("with")) cur.Advance();
        cur.TryMatchAnyWord("with");
        // 牌组名只到「下一个动作」为止，不能一路吞到句号。
        //
        //   "replace your deck with the Apocalypse Deck."
        //   "replace your deck with the Mjerrabaine Deck and transform
        //    the Reaper Card at the bottom of your deck into a Victory Card."
        //
        // 早先无限吞到 Period，于是第二句的整段
        // "the Mjerrabaine Deck and transform ... into a Victory Card"
        // 都被当成牌组名，查表必然失败。
        var deckName = new System.Text.StringBuilder();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            // 遇到并列/后续动作就停：牌组名不会含这些动词。
            if (deckName.Length > 0 && cur.Current.IsAnyWord(
                    "and", "then", "transform", "give", "deal", "draw",
                    "summon", "destroy", "banish", "return", "restore",
                    "discard", "add", "gain"))
                break;

            if (deckName.Length > 0) deckName.Append(' ');
            deckName.Append(cur.Current.Text);
            cur.Advance();
        }
        // 牌库替换是一个原子动作，不是一个"清空 + 生成单卡"的组合：
        //   · 旧牌库直接丢弃，**不进 Banished**（否则 BanishedCount 被永久污染）
        //   · 新牌库是具名牌组，由 DeckDefTable 解析成整套卡表
        // 具名解析失败由 Battle 层报错，绝不静默装进空牌库或伪造单卡。
        return new List<EffectNode>
        {
            new ReplaceDeckNode
            {
                DeckName = deckName.ToString().Trim(),
                Owner = ZoneScope.Self,
            },
        };
    }


    /// <summary>Halve the cost of ...</summary>
    /// <summary>
    /// "Halve the cost of X" 与 "Halve the enemy leader's max defense" 是两回事：
    /// 前者改费用，后者改生命上限。
    /// 之前一律产出 ModifyCostNode，于是「将对方主战者生命上限减半」
    /// 被编成了「修改自身费用」。
    /// </summary>
    private List<EffectNode> ParseHalve(TokenCursor cur)
    {
        cur.ExpectWord("halve");

        // 先看本句剩余词串里有没有 defense / life
        int probe = cur.Position;
        bool isLife = false;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            if (cur.Current.IsAnyWord("defense", "life")) { isLife = true; break; }
            if (cur.Current.IsAnyWord("cost")) break;
            cur.Advance();
        }
        cur.Seek(probe);

        // 目标必须从 "halve" 之后就地读。
        // 旧实现一路 Seek 到 "of" 再解析 —— 而 "Halve the enemy leader's defense"
        // 里根本没有 "of"，游标被推到句尾，TryParse 拿到空流，回退成 TargetSpec.Self。
        // 结果整句变成「把自己的生命减半」，敌方主战者一动不动。
        TargetSpec target;
        if (isLife)
        {
            // "Halve the enemy leader's max defense" —— "max" 落在属主之后，
            // TargetParser 在 "leader's" 处只吃 defense/life/attack，
            // 因此剩余的 max/defense 必须在这里补消费，否则句尾残留撞 AssertEnd。
            cur.TryMatchAnyWord("max");
            target = _target.TryParse(cur) ?? TargetSpec.Self;
            while (cur.Current.IsAnyWord("max", "defense", "life", "attack")) cur.Advance();
        }
        else
        {
            while (!cur.IsAtEnd && !cur.Current.IsAnyWord("of")) cur.Advance();
            cur.TryMatchAnyWord("of");
            cur.TryMatchAnyWord("max");
            target = _target.TryParse(cur) ?? TargetSpec.Self;
        }

        if (isLife)
        {
            // 基准取 MaxLife 而不是当前 Life：卡面写的是 max defense 的上限值，
            // 按当前生命算会随伤害累积而漂移。
            // (MaxLife + 1) / 2 —— 奇数向上取整（同 CostModOp.Halve 的规则）。
            //
            // Attack = null 表示「不动攻击力」。之前这里传的是「读攻击原值」，
            // 因为求值锚点用了整个 target（Choose 池），实际读到的是池里第一个的身材。
            // 现在 DoModifyStats 会按 null 跳过该项，并绑定 $item 逐目标求值。
            return new List<EffectNode>
            {
                new ModifyStatsNode
                {
                    Target = target,
                    Op = StatModOp.Set,
                    Attack = null,
                    Life = NumExpr.Div(
                        NumExpr.Add(NumExpr.Prop(StatKey.MaxLife, TargetSpec.Item), NumExpr.Const(1)),
                        NumExpr.Const(2)),
                },
            };
        }

        return new List<EffectNode>
        {
            new ModifyCostNode { Target = target, Op = CostModOp.Halve, Value = NumExpr.Const(0) },
        };
    }

    private List<EffectNode> ParseAdvanceOrDelay(TokenCursor cur, int sign)
    {
        cur.Advance();   // advance / delay

        // 先看本句剩余词串里提到 crest 还是 count
        int probeCrest = cur.Position;
        bool sawCrest = false;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            if (cur.Current.IsAnyWord("crest", "crests")) { sawCrest = true; break; }
            cur.Advance();
        }
        cur.Seek(probeCrest);

        var target = TargetSpec.Self;
        // "advance this crest's count by 1" —— 纹章用的是 crest's 而不是 amulet's。
        // 少了这一条，Octrice 这类卡的整条能力都会停在 [this] 报无法解析。
        if (cur.MatchPhrase("this", "amulet's", "count")
            || cur.MatchPhrase("this", "crest's", "count")
            || cur.MatchPhrase("this", "crest's")
            || cur.MatchPhrase("the", "count", "of", "this", "amulet")
            || cur.MatchPhrase("the", "count", "of", "this", "crest"))
            target = TargetSpec.Self;
        else if (cur.MatchPhrase("the", "count", "of") || cur.MatchPhrase("the", "counts", "of"))
            target = _target.TryParse(cur) ?? TargetSpec.Self;
        else if (cur.MatchPhrase("this", "amulet's") || cur.MatchWord("count"))
        { }

        // 计数器永远是 countdown —— 护符与**纹章**都用它存「剩余吟唱」。
        // 玩家级的 crest 计数器是「你有几个纹章」，与推进完全无关；
        // 之前按名词分流到 "crest"，于是 Octrice 推的是纹章个数，
        // 它自己的 Countdown (8) 纹丝不动。
        var counterName = "countdown";

        cur.TryMatchAnyWord("by");
        var val = ParseNumExpr(cur);
        return new List<EffectNode>
        {
            new ModifyCounterNode
            {
                // Target 必须指向**实体**：置 null 会改成玩家级计数器，
                // 护符 / 纹章自己的 countdown 永远不会变。
                Target = target ?? TargetSpec.Self,
                Owner = target?.Owner ?? ZoneScope.Self,
                Counter = _lex.Counter(counterName),
                Op = CounterOp.Add,
                Delta = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), val) : val,
            },
        };
    }

    private List<EffectNode> ParseActivate(TokenCursor cur)
    {
        cur.ExpectWord("activate");

        // "activate all of them instead" —— 两处语义要分开：
        //
        //   ① 模式抉择卡（"Select a Mode to activate. ... Activate all of them instead."）
        //      them 指「上面列出的所有选项」，是元声明，不是一个实体集合，
        //      由 MetaModeWeaver 消费。这里给一个全体槽位占位即可。
        //   ② 护符启动（"activate all of them"）才对已选中的护符逐个 InvokeAbility。
        //
        // 早先两处都调 ResolvePronounTarget()，但 MatchPhrase 已经把 them
        // 消费掉了，此刻没有任何绑定可读 —— StrictPronounCheck 当场抛错，
        // 三张 Mode 卡（10771310 / 10912310 / 10962310）全部编译失败。
        if (cur.MatchPhrase("all", "of", "them", "instead"))
            return new List<EffectNode>
            {
                new InvokeAbilityNode
                {
                    Target = TargetSpec.Previous,
                    Event = CoreEvent.OnEngage,
                },
            };
        if (cur.MatchPhrase("all", "of", "them"))
            return new List<EffectNode>
            {
                new InvokeAbilityNode
                {
                    Target = TargetSpec.Previous,
                    Event = CoreEvent.OnEngage,
                },
            };
        if (cur.MatchPhrase("its", "fanfare", "ability"))
            return new List<EffectNode> { new InvokeAbilityNode { Target = TargetSpec.Self, Event = CoreEvent.OnPlay } };
        if (cur.MatchPhrase("an", "ability", "in", "sequence", "from", "the", "following"))
            return new List<EffectNode> { new BranchModeNode { Kind = ModeDispatchKind.Sequence, PickCount = NumExpr.Const(1), Options = Array.Empty<IReadOnlyList<EffectNode>>() } };
        if (cur.Current.Kind == TokKind.Number && cur.Peek(1).IsWord("random"))
        {
            int n = cur.Current.Value;
            cur.Advance(); cur.Advance();
            while (!cur.IsAtEnd && !cur.Current.IsWord("following")) cur.Advance();
            cur.TryMatchWord("following");
            return new List<EffectNode> { new BranchModeNode { Kind = ModeDispatchKind.Random, PickCount = NumExpr.Const(n), Options = Array.Empty<IReadOnlyList<EffectNode>>() } };
        }
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        // "activate a random ability that hasn't been activated" ——
        // 启动一个此前未启动过的能力
        return new List<EffectNode>
        {
            new InvokeAbilityNode
            {
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Zone = ZoneRef.Board,
                    Owner = ZoneScope.Self,
                    Mode = SelectMode.Random,
                    Count = NumExpr.Const(1),
                },
                Event = CoreEvent.OnEngage,
            },
        };
    }

    private List<EffectNode> ParseConsume(TokenCursor cur)
    {
        cur.ExpectWord("consume");
        NumExpr count = cur.MatchWord("a") ? NumExpr.Const(1) : ParseNumExpr(cur);
        cur.TryMatchAnyWord("clue", "clues");
        var nodes = new List<EffectNode>
        {
            new ModifyCounterNode
            {
                Counter = _lex.Counter("clues"),
                Op = CounterOp.Add,
                Delta = NumExpr.Sub(NumExpr.Const(0), count),
            },
        };
        if (cur.Current.IsWord("to") || cur.Current.Kind == TokKind.Comma)
        {
            if (cur.Current.IsWord("to")) cur.Advance();
            // 线索不足时不能「扣成 0 然后白嫖炸场」。
            return PayThenDo(_lex.Counter("clues"), count, nodes, ParseSequence(cur));
        }
        return nodes;
    }

    private List<EffectNode> ParseSwap(TokenCursor cur)
    {
        cur.ExpectWord("swap");

        // 两种写法都要支持：
        //   "swap this follower's attack and defense"                → 本卡
        //   "Swap the attack and defense of an enemy follower."       → 任意目标
        // 旧实现只认第一种，第二种把整句丢给动作解析器后直接报「无法推进」。
        TargetSpec target = TargetSpec.Self;
        if (cur.MatchPhrase("the", "attack", "and", "defense", "of"))
        {
            target = _target.TryParse(cur) ?? TargetSpec.Self;
        }
        else if (cur.MatchPhrase("the", "attack", "and", "defense", "of", "this", "card"))
        {
            target = TargetSpec.Self;
        }
        else
        {
            // "this follower's ..." / "this card's ..."
            cur.TryMatchAnyWord("this");
            cur.TryMatchAnyWord("follower's", "follower", "card's", "card");
            cur.TryMatchAnyWord("attack");
            cur.TryMatchAnyWord("and");
            cur.TryMatchAnyWord("defense");
            // 既不是 "the ... of X" 也不是 "this ..." 时，仍尝试解析一个目标
            if (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
                target = _target.TryParse(cur) ?? TargetSpec.Self;
        }

        return new List<EffectNode>
        {
            new ModifyStatsNode { Target = target, Op = StatModOp.Swap },
        };
    }

    private List<EffectNode> ParseInvoke(TokenCursor cur)
    {
        cur.MatchPhrase("invoke", "this", "card") ;
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        return new List<EffectNode>
        {
            new MoveZoneNode { Target = TargetSpec.Self, ToZone = ZoneRef.Board },
        };
    }

    private List<EffectNode> ParseFully(TokenCursor cur)
    {
        cur.ExpectWord("fully");
        if (cur.Current.IsWord("recover"))
        {
            cur.Advance();
            cur.TryMatchAnyWord("your", "the");
            cur.TryMatchAnyWord("play", "evolution");
            cur.TryMatchAnyWord("point", "points");
            // "Fully recover your play points" = 把 pp 直接设为 max_pp。
            // 之前写成 Fill 0，Fill 的语义是「填到传入值」，
            // 传 0 等于把能量点清零 —— 与「回满」完全相反。
            return new List<EffectNode>
            {
                new ModifyCounterNode
                {
                    Counter = _lex.Counter("pp"),
                    Op = CounterOp.Set,
                    Delta = NumExpr.Ctr(_lex.Counter("max_pp")),
                },
            };
        }
        if (cur.MatchPhrase("restore", "the", "defense", "of"))
        {
            var target = _target.TryParse(cur) ?? TargetSpec.Self;
            return new List<EffectNode> { new RestoreLifeNode { Target = target, FullRestore = true, Amount = NumExpr.Const(0) } };
        }
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
        return new List<EffectNode>();
    }

    private List<EffectNode> ParseOpponentDraws(TokenCursor cur)
    {
        cur.MatchPhrase("your", "opponent", "draws", "a", "card");
        return new List<EffectNode>
        {
            new MoveZoneNode
            {
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = ZoneScope.Opponent,
                    Zone = ZoneRef.Deck,
                    Mode = SelectMode.First,
                    Count = NumExpr.Const(1),
                },
                ToZone = ZoneRef.Hand,
            },
        };
    }

    private List<EffectNode> ParseItCosts(TokenCursor cur)
    {
        cur.MatchPhrase("it", "costs");
        var val = ParseNumExpr(cur);
        return new List<EffectNode>
        {
            new ModifyCostNode { Target = TargetSpec.Created, Op = CostModOp.Set, Value = val },
        };
    }

    /// <summary>消费 "until the end of the/your/your opponent's turn" 持续时间短语。</summary>
    /// <summary>
    /// 解析持续时间短语。
    /// 之前返回 void，把 "until the end of the turn" 吃进垃圾桶，
    /// 导致所有限时效果都被编译成 Permanent（本回合 +1/+0 变成永久 +1/+0）。
    /// </summary>
    /// <summary>
    /// 给已经生成的节点补上持续时间。
    /// 持续时间短语出现在授予项之后（"give it +1/+0 until the end of the turn"），
    /// 解析到它时节点已经构造完毕，只能重建一份带 Duration 的副本。
    /// </summary>
    private static EffectNode WithDuration(EffectNode node, DurationKind dur) => node switch
    {
        ModifyStatsNode n => new ModifyStatsNode
        {
            Target = n.Target, Op = n.Op, Attack = n.Attack, Life = n.Life,
            Duration = dur, If = n.If,
        },
        ModifyKeywordNode n => new ModifyKeywordNode
        {
            Target = n.Target, Op = n.Op, Keyword = n.Keyword, Value = n.Value,
            Duration = dur, If = n.If,
        },
        ModifyCostNode n => new ModifyCostNode
        {
            Target = n.Target, Op = n.Op, Value = n.Value,
            Duration = dur, If = n.If,
        },
        GrantAbilityNode n => new GrantAbilityNode
        {
            Target = n.Target, Granted = n.Granted,
            Duration = dur, If = n.If,
        },
        _ => node,
    };

    private DurationKind ConsumeDuration(TokenCursor cur)
    {
        int save = cur.Position;

        // "this turn" 形式
        if (cur.MatchPhrase("this", "turn")) return DurationKind.ThisTurn;
        if (cur.MatchPhrase("this", "match")) return DurationKind.ThisMatch;

        if (!cur.MatchWord("until")) return DurationKind.Permanent;

        cur.TryMatchAnyWord("the", "your", "their");
        cur.TryMatchAnyWord("end");
        cur.TryMatchAnyWord("of");
        cur.TryMatchAnyWord("the", "your", "their");

        bool opponent = cur.TryMatchAnyWord("opponent's", "opponent");
        cur.TryMatchAnyWord("turn", "turns");

        return opponent ? DurationKind.UntilEndOfOpponentTurn : DurationKind.ThisTurn;
    }

    /// <summary>"Play a card" / "play it" / "Recover 1 play point"（play 作名词已被 Recover 吃掉）。</summary>
    private List<EffectNode> ParsePlayVerb(TokenCursor cur)
    {
        cur.ExpectWord("play");
        // "play point(s)" 是名词残留，不应到这里；若到了直接消费
        if (cur.Current.IsAnyWord("point", "points"))
        {
            cur.Advance();
            return new List<EffectNode>();
        }
        var target = _target.TryParse(cur);
        if (target == null)
            throw new LangException("play 后面无法解析目标，停在 " + cur.Current);
        return new List<EffectNode>
        {
            new MoveZoneNode { Target = target, ToZone = ZoneRef.Board },
        };
    }

    private NumExpr ParseNumExpr(TokenCursor cur)
    {
        if (cur.Current.Kind == TokKind.Number)
        {
            int v = (int)cur.Current.Value;
            cur.Advance();
            return NumExpr.Const(v);
        }
        // X / Y / Z 变量 → 优先用 EnglishCompiler 登记的绑定（含 SplitOf），否则退回计数器
        if (cur.Current.IsAnyWord("x", "y", "z"))
        {
            var name = cur.Current.Text.ToUpperInvariant();
            cur.Advance();
            return VariableResolver != null
                ? VariableResolver(name)
                : NumExpr.Ctr(_lex.Counter(name));
        }
        // X-cost → 按费用 X 筛选，数量取 1
        if (cur.Current.IsAnyWord("x-cost"))
        {
            cur.Advance();
            return NumExpr.Const(1);
        }
        return NumExpr.Const(1);
    }
}
