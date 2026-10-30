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

    public ActionParser(Lexicon lex, Func<IReadOnlyList<Token>, AbilityDef> compileQuotedAbility)
    {
        _lex = lex;
        _target = new TargetParser(lex);
        _compileQuotedAbility = compileQuotedAbility;
    }

    /// <summary>解析一串动作节点，直到 cursor 耗尽。</summary>
    public List<EffectNode> ParseSequence(TokenCursor cur)
    {
        var result = new List<EffectNode>();
        while (!cur.IsAtEnd)
        {
            // 跳过句点、分号、选项标记，以及动作之间的连接词 (",", "and", "then")
            if (cur.Current.Kind == TokKind.OptionMark ||
                cur.Current.Kind == TokKind.Period ||
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
                ConsumeDuration(cur);
                if (cur.Current.Kind == TokKind.Comma) cur.Advance();
                continue;
            }

            // 句中条件："draw a card and, if X, restore 1 defense"
            if (cur.Current.IsWord("if"))
            {
                var midIf = ParseMidSentenceIf(cur);
                if (midIf != null) { result.Add(midIf); continue; }
            }

            int before = cur.Position;
            var node = ParseOne(cur);
            if (node != null) result.AddRange(node);

            if (cur.Position == before)
                throw new LangException("无法解析的动作：" + cur.Current + "（位置 " + cur.Current.Pos + "）");
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
        return new ConditionalNode { Condition = cond, Then = thenBody };
    }

    /// <summary>解析一个动作。返回 null 表示当前位置不是动作起点。</summary>
    public List<EffectNode> ParseOne(TokenCursor cur)
    {
        var t = cur.Current;
        if (t.Kind != TokKind.Word && t.Kind != TokKind.Keyword) return null;

        var w = t.Text.ToLowerInvariant();
        switch (w)
        {
            case "deal": return ParseDeal(cur);
            case "draw": return ParseDraw(cur);
            case "restore": return ParseRestore(cur);
            case "recover": return ParseRecover(cur);
            case "give": return ParseGive(cur);
            case "summon": return ParseSummon(cur);
            case "add": return ParseAdd(cur);
            case "destroy": return ParseMove(cur, ZoneRef.Cemetery);
            case "banish": return ParseMove(cur, ZoneRef.Banished);
            case "discard": return ParseMove(cur, ZoneRef.Cemetery);
            case "return": return ParseMove(cur, ZoneRef.Hand);
            case "evolve": return ParseEvolve(cur, false);
            case "super-evolve": return ParseEvolve(cur, true);
            case "select": return ParseSelect(cur);
            case "transform": return ParseTransform(cur);
            case "gain": return ParseGain(cur);
            case "increase": return ParseCostChange(cur, 1);
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
                if (cur.MatchWord("to")) nodes.AddRange(ParseSequence(cur));
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
                    if (cur.MatchWord("to")) nodes.AddRange(ParseSequence(cur));
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
            // instead 是修饰词，语义由 InsteadWeaver 在装配层处理；这里消费掉
            case "instead": { cur.Advance(); return new List<EffectNode>(); }
            // Whenever / When / At the ... —— 事件触发句不归 ActionParser 管。
            // 直接返回 null，让 CompileSegment 的 SplitCondition 处理；
            // 绝不能在这里把剩余词吞掉（那是 V2 的兜底病）。
            case "whenever": case "when": case "at":
                return null;
            case "your":
            {
                // "Your opponent can't select anything other than this card"
                if (cur.MatchPhrase("your", "opponent"))
                {
                    while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
                    return new List<EffectNode>
                    {
                        new ModifyKeywordNode { Target = TargetSpec.OpponentLeader, Op = KeywordOp.Grant, Keyword = _lex.Keyword("targeting_restriction") },
                    };
                }
                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) cur.Advance();
                return new List<EffectNode>();
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
                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
                { words.Add(cur.Current.Text.ToLowerInvariant()); cur.Advance(); }
                var j = string.Join(" ", words);
                string kw =
                    j.Contains("attack followers") || j.Contains("attack leaders") ? "cannot_attack"
                  : j.Contains("be destroyed") ? "cannot_destroy"
                  : j.Contains("attack") && j.Contains("time") ? "attack_limit"
                  : j.Contains("take more than") ? "damage_cap"
                  : "cannot_attack";
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
            target = TargetSpec.Previous;
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
            target = _target.TryParse(cur) ?? TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent);
        }
        // "... and 2 damage to your leader" —— 第二段伤害
        while (cur.Current.IsWord("and") && cur.Peek(1).Kind == TokKind.Number && cur.Peek(2).IsWord("damage"))
        {
            cur.Advance();
            int amt2 = cur.Current.Value; cur.Advance(); cur.Advance();
            cur.TryMatchAnyWord("to");
            var t2 = _target.TryParse(cur) ?? TargetSpec.OpponentLeader;
            extraDmg.Add(new DealDamageNode { Target = t2, Amount = NumExpr.Const(amt2), IsAbility = true });
        }

        var dmgNodes = new List<EffectNode>
        {
            new DealDamageNode { Target = target, Amount = amount, IsAbility = true, IsSplit = splitFlag },
        };
        // "deal 6 damage to them and the enemy leader" —— TargetParser 已把 and the enemy leader 折进 IncludeLeader
        if (target.IncludeLeader)
            dmgNodes.Add(new DealDamageNode
            {
                Target = TargetSpec.OpponentLeader,
                Amount = amount,
                IsAbility = true,
            });
        dmgNodes.AddRange(extraDmg);
        return dmgNodes;
    }

    // ---------- 抽牌 ----------

    private List<EffectNode> ParseDraw(TokenCursor cur)
    {
        cur.ExpectWord("draw");
        var n = ParseNumExpr(cur);
        cur.TryMatchAnyWord("card", "cards");
        var filter = _target.TryParse(cur);
        var from = filter != null
            ? new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Zone = ZoneRef.Deck,
                Owner = ZoneScope.Self,
                Where = filter.Where,
                Mode = SelectMode.First,
                Count = n,
            }
            : new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Zone = ZoneRef.Deck,
                Owner = ZoneScope.Self,
                Mode = SelectMode.First,
                Count = n,
            };
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
        var target = _target.TryParse(cur) ?? TargetSpec.Self;
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

                // 消费授予项之间的 , / and
                int sepSave = cur.Position;
                bool hadComma = cur.Current.Kind == TokKind.Comma;
                bool hadAnd = cur.Current.IsWord("and");
                if (hadComma) cur.Advance();
                if (cur.Current.IsWord("and")) { cur.Advance(); hadAnd = true; }

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
            ConsumeDuration(cur);

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
            int atk = 0, life = 0;
            var slash = txt.IndexOf('/');
            if (slash > 0)
            {
                atk = ParseStatValue(txt.Substring(0, slash));
                life = ParseStatValue(txt.Substring(slash + 1));
            }
            else atk = ParseStatValue(txt);
            foreach (var t in targets)
                outNodes.Add(new ModifyStatsNode
                {
                    Target = t, Op = StatModOp.Add,
                    Attack = NumExpr.Const(atk), Life = NumExpr.Const(life),
                });
            return outNodes;
        }

        // B. 关键词
        if (cur.MatchPhrase("last", "words"))
            return new List<EffectNode>
            {
                new ModifyKeywordNode { Target = TargetSpec.Previous, Op = KeywordOp.Remove, Keyword = _lex.Keyword("last words") },
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
            var sb = new System.Text.StringBuilder();
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period
                   && cur.Current.Kind != TokKind.Comma && !cur.Current.IsWord("and"))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(cur.Current.Text);
                cur.Advance();
            }
            var crestName = sb.ToString();
            foreach (var t in targets)
                outNodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardName = crestName,
                    ToOwner = t.Owner,
                    ToZone = ZoneRef.CrestArea,
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

    private static int ParseStatValue(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var sign = s[0] == '-' ? -1 : 1;
        var body = (s[0] == '+' || s[0] == '-') ? s.Substring(1) : s;
        return int.TryParse(body, out var n) ? sign * n : 0;
    }


    // ---------- 召唤 / 加手牌 ----------

    private List<EffectNode> ParseSummon(TokenCursor cur)
    {
        cur.ExpectWord("summon");
        return ParseSpawnCore(cur, ZoneRef.Board);
    }

    private List<EffectNode> ParseAdd(TokenCursor cur)
    {
        cur.ExpectWord("add");
        return ParseSpawnCore(cur, ZoneRef.Hand);
    }

    private List<EffectNode> ParseSpawnCore(TokenCursor cur, ZoneRef toZone)
    {
        var nodes = new List<EffectNode>();

        // ---------- 分支 1：复制体 "an exact copy of <X>" / "N copies of <X>" ----------
        int save0 = cur.Position;
        NumExpr copyCount = NumExpr.Const(1);
        bool isCopy = false;

        if (cur.MatchPhrase("an", "exact", "copy") || cur.MatchPhrase("an", "exact", "copies"))
            isCopy = true;
        else if (cur.Current.Kind == TokKind.Number && cur.Peek().IsAnyWord("exact", "copy", "copies"))
        {
            copyCount = NumExpr.Const(cur.Current.Value);
            cur.Advance();
            cur.TryMatchAnyWord("exact");
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
                var ct = cur.Current;
                cur.Advance();
                ConsumeDestination(cur);
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct.CardId,
                    CardName = ct.Text,
                    ToZone = toZone,
                    ToOwner = ZoneScope.Self,
                    Count = copyCount,
                });
                return nodes;
            }
            var copySrc = _target.TryParse(cur) ?? TargetSpec.Self;
            ConsumeDestination(cur);
            nodes.Add(new SpawnCardNode
            {
                SourceKind = CardSourceKind.CopyOfTarget,
                CopyFrom = copySrc,
                ToZone = toZone,
                ToOwner = ZoneScope.Self,
                Count = copyCount,
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
            while (!cur.IsAtEnd)
            {
                if (cur.Current.Kind != TokKind.CardName) break;
                cardToks.Add(cur.Current);
                cur.Advance();

                int sepPos = cur.Position;
                bool comma = cur.TryMatch(TokKind.Comma);
                bool and = cur.TryMatchWord("and");
                if (!comma && !and) break;

                // 预判后面是否还是卡名
                int la = cur.Position;
                cur.TryMatchAnyWord("a", "an");
                bool nextIsCard = cur.Current.Kind == TokKind.CardName;
                cur.Seek(la);
                if (!nextIsCard) { cur.Seek(sepPos); break; }
                cur.TryMatchAnyWord("a", "an");
            }

            ConsumeDestination(cur);
            foreach (var ct in cardToks)
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct.CardId,
                    CardName = ct.Text,
                    ToZone = toZone,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(listCount),
                });

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
                var ct2 = cur.Current;
                cur.Advance();
                ConsumeDestination(cur);
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct2.CardId,
                    CardName = ct2.Text,
                    ToZone = toZone,
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
                ConsumeDestination(cur);
                nodes.Add(new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardId = ct3.CardId,
                    CardName = ct3.Text,
                    ToZone = toZone,
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

    private List<EffectNode> ParseMove(TokenCursor cur, ZoneRef to)
    {
        cur.Advance();   // 动词

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
                return new List<EffectNode> { new MoveZoneNode { Target = spec, ToZone = to } };
            }
            cur.Seek(se);
        }

        // "Destroy X random enemy followers"：X 数量 + random 修饰
        int forcedCount = 0;
        bool forcedRandom = false;
        if (cur.Current.IsAnyWord("x", "y", "z")) { cur.Advance(); forcedCount = 1; }
        else if (cur.Current.Kind == TokKind.Number) { forcedCount = (int)cur.Current.Value; cur.Advance(); }
        while (cur.Current.IsAnyWord("random")) { cur.Advance(); forcedRandom = true; }

        var extraDmg = new List<EffectNode>();
        var target = _target.TryParse(cur) ?? TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent);
        if (forcedRandom || forcedCount > 0)
        {
            target = new TargetSpec
            {
                SourceKind = target.SourceKind,
                Owner = target.Owner,
                Zone = target.Zone,
                Where = target.Where,
                Mode = forcedRandom ? SelectMode.Random : SelectMode.Choose,
                Count = NumExpr.Const(forcedCount > 0 ? forcedCount : 1),
            };
        }
        ConsumeDestination(cur);
        var moveNodes = new List<EffectNode> { new MoveZoneNode { Target = target, ToZone = to } };
        moveNodes.AddRange(ParseAndAllTargets(cur, to));
        return moveNodes;
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
        var target = _target.TryParse(cur) ?? TargetSpec.Self;
        cur.TryMatchAnyWord("into");
        cur.TryMatchAnyWord("an", "a");
        cur.TryMatchAnyWord("exact", "copies", "copy");
        cur.TryMatchAnyWord("of");
        int id = 0;
        string nm = "";
        if (cur.Current.Kind == TokKind.CardName) { id = cur.Current.CardId; nm = cur.Current.Text; cur.Advance(); }
        else
        {
            var sb2 = new System.Text.StringBuilder();
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period && cur.Current.Kind != TokKind.Comma)
            {
                if (sb2.Length > 0) sb2.Append(' ');
                sb2.Append(cur.Current.Text);
                cur.Advance();
            }
            nm = sb2.ToString();
            if (string.IsNullOrWhiteSpace(nm))
                throw new LangException("transform into 后面没有目标");
        }
        return new List<EffectNode> { new TransformNode { Target = target, IntoCardId = id, IntoCardName = nm } };
    }

    // ---------- 计数器 ----------

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
                // 此时按剩余 token 拼成残名，交给运行时按前缀匹配。
                var sbCrest = new System.Text.StringBuilder();
                while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
                {
                    if (sbCrest.Length > 0) sbCrest.Append(' ');
                    sbCrest.Append(cur.Current.Text);
                    cur.Advance();
                }
                crestName = sbCrest.ToString();
            }
            return new List<EffectNode>
            {
                new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CardId,
                    CardName = crestName,
                    ToZone = ZoneRef.ExArea,
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(1),
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
        cur.Advance();
        // "reduce the cost of this card by 1"
        while (!cur.IsAtEnd && !cur.Current.IsAnyWord("by")) cur.Advance();
        var amt = NumExpr.Const(0);
        if (cur.TryMatchAnyWord("by")) amt = ParseNumExpr(cur);
        return new List<EffectNode>
        {
            new ModifyCostNode
            {
                Target = TargetSpec.Self,
                Op = CostModOp.Add,
                Value = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), amt) : amt,
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
                ConsumeDuration(cur);
                return new List<EffectNode>
                {
                    new ModifyStatsNode
                    {
                        Target = leadOwner == ZoneScope.Self ? TargetSpec.SelfLeader : TargetSpec.OpponentLeader,
                        Op = StatModOp.Set,
                        Life = maxDefV,
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
                new ModifyCostNode { Target = TargetSpec.Previous, Op = CostModOp.Set, Value = cv },
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
            var tgt = _target.TryParse(cur) ?? TargetSpec.Previous;
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
                new ModifyStatsNode { Target = TargetSpec.Previous, Op = StatModOp.Set, Attack = av, Life = lv },
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
            ConsumeDuration(cur);
            return new List<EffectNode>
            {
                new ModifyCostNode { Target = tgt, Op = CostModOp.Set, Value = v0 },
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
            target = TargetSpec.Previous;
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
            times = NumExpr.Var(cur.Current.Text.ToUpperInvariant());
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
            result.Add(new MacroNode
            {
                Macro = _lex.Macro("reanimate"),
                Args = new[] { NumExpr.Const(n) },
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
            var kept = new List<Token>();
            var sentBuf = new List<Token>();
            foreach (var tk2 in q.Inner)
            {
                if (tk2.Kind == TokKind.Period)
                {
                    if (sentBuf.Count > 0 && !IsVariableDecl(sentBuf)) kept.AddRange(sentBuf);
                    if (sentBuf.Count > 0) kept.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = tk2.Pos });
                    sentBuf = new List<Token>();
                    continue;
                }
                sentBuf.Add(tk2);
            }
            if (sentBuf.Count > 0 && !IsVariableDecl(sentBuf)) kept.AddRange(sentBuf);

            var inner = new ActionParser(_lex, _compileQuotedAbility);
            body = inner.ParseSequence(new TokenCursor(kept));
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
    private List<EffectNode> ParseAndAllTargets(TokenCursor cur, ZoneRef to)
    {
        var nodes = new List<EffectNode>();
        while (cur.Current.IsWord("and"))
        {
            cur.Advance();
            var t = _target.TryParse(cur);
            if (t == null) { cur.TryMatchAnyWord("all"); t = _target.TryParse(cur); }
            if (t == null) break;
            nodes.Add(new MoveZoneNode { Target = t, ToZone = to });
        }
        return nodes;
    }

    private void ConsumeDestination(TokenCursor cur)
    {
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
        }
        // "to your hand without revealing it"
        if (cur.MatchPhrase("without", "revealing")) { cur.TryMatchAnyWord("it", "them"); }
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
        if (cur.Current.IsAnyWord("all"))
        {
            cur.Advance();
            if (cur.Current.IsAnyWord("abilities", "ability"))
            {
                cur.Advance();
                while (!cur.IsAtEnd && !cur.Current.IsAnyWord("from")) cur.Advance();
                cur.TryMatchAnyWord("from");
                var tgt = _target.TryParse(cur) ?? TargetSpec.Previous;
                return new List<EffectNode> { new ModifyKeywordNode { Target = tgt, Op = KeywordOp.SilenceAll } };
            }
            return new List<EffectNode>();
        }
        if (cur.Current.IsWord("last") && cur.Peek().IsWord("words"))
        {
            cur.Advance(); cur.Advance();
            while (!cur.IsAtEnd && !cur.Current.IsAnyWord("from")) cur.Advance();
            cur.TryMatchAnyWord("from");
            var lt = _target.TryParse(cur) ?? TargetSpec.Previous;
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
            var tgt = _target.TryParse(cur) ?? TargetSpec.Previous;
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
        var deckName = new System.Text.StringBuilder();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period)
        {
            if (deckName.Length > 0) deckName.Append(' ');
            deckName.Append(cur.Current.Text);
            cur.Advance();
        }
        return new List<EffectNode>
        {
            new MacroNode
            {
                Macro = _lex.Macro("replace_deck"),
            },
        };
    }


    /// <summary>Halve the cost of ...</summary>
    private List<EffectNode> ParseHalve(TokenCursor cur)
    {
        cur.ExpectWord("halve");
        while (!cur.IsAtEnd && !cur.Current.IsAnyWord("of")) cur.Advance();
        cur.TryMatchAnyWord("of");
        var target = _target.TryParse(cur) ?? TargetSpec.Self;
        return new List<EffectNode>
        {
            new ModifyCostNode { Target = target, Op = CostModOp.Halve, Value = NumExpr.Const(0) },
        };
    }

    private List<EffectNode> ParseAdvanceOrDelay(TokenCursor cur, int sign)
    {
        cur.Advance();   // advance / delay
        var target = TargetSpec.Self;
        if (cur.MatchPhrase("this", "amulet's", "count") || cur.MatchPhrase("the", "count", "of", "this", "amulet"))
            target = TargetSpec.Self;
        else if (cur.MatchPhrase("the", "count", "of") || cur.MatchPhrase("the", "counts", "of"))
            target = _target.TryParse(cur) ?? TargetSpec.Self;
        else if (cur.MatchPhrase("this", "amulet's") || cur.MatchWord("count"))
        { }

        cur.TryMatchAnyWord("by");
        var val = ParseNumExpr(cur);
        return new List<EffectNode>
        {
            new ModifyCounterNode
            {
                Owner = target?.Owner ?? ZoneScope.Self,
                Counter = _lex.Counter("countdown"),
                Op = CounterOp.Add,
                Delta = sign < 0 ? NumExpr.Sub(NumExpr.Const(0), val) : val,
            },
        };
    }

    private List<EffectNode> ParseActivate(TokenCursor cur)
    {
        cur.ExpectWord("activate");
        if (cur.MatchPhrase("all", "of", "them", "instead"))
            return new List<EffectNode> { new MacroNode { Macro = _lex.Macro("activate_all") } };
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
        return new List<EffectNode> { new MacroNode { Macro = _lex.Macro("activate") } };
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
            nodes.AddRange(ParseSequence(cur));
        }
        return nodes;
    }

    private List<EffectNode> ParseSwap(TokenCursor cur)
    {
        cur.ExpectWord("swap");
        cur.TryMatchAnyWord("this");
        cur.TryMatchAnyWord("follower's", "follower", "card's", "card");
        cur.TryMatchAnyWord("attack");
        cur.TryMatchAnyWord("and");
        cur.TryMatchAnyWord("defense");
        return new List<EffectNode>
        {
            new ModifyStatsNode { Target = TargetSpec.Self, Op = StatModOp.Swap },
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
            return new List<EffectNode>
            {
                new ModifyCounterNode
                {
                    Counter = _lex.Counter("pp"),
                    Op = CounterOp.Fill,
                    Delta = NumExpr.Const(0),
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
    private void ConsumeDuration(TokenCursor cur)
    {
        if (!cur.MatchWord("until")) return;
        cur.TryMatchAnyWord("the", "your", "their");
        cur.TryMatchAnyWord("end");
        cur.TryMatchAnyWord("of");
        cur.TryMatchAnyWord("the", "your", "their");
        cur.TryMatchAnyWord("opponent's", "opponent");
        cur.TryMatchAnyWord("turn", "turns");
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
