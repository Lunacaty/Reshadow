using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 目标短语解析器
//
// 英语名词短语的槽位顺序（语言学事实）：
//   [量词] [随机] [其他] [归属] [状态] [兵种/职业] 核心名词 [区域] [后置定语]
//
// 例：all random other allied evolved Ward followers on the field with 3 defense or less
//      量词 随机  其他  归属   状态  兵种   核心      区域        后置定语
//
// 全程只碰 TokenCursor，不碰原文字符串。
// ============================================================================

public sealed class TargetParser
{
    private readonly Lexicon _lex;

    /// <summary>
    /// 代词 it / them 默认指向的槽位。null 表示 $selected。
    /// 编译 "When/Whenever <主语> ... " 触发器时应置为 $trigger，直到本句出现 Select 再切回 $selected。
    /// </summary>
    public string DefaultPronounSlot { get; set; }

    public TargetParser(Lexicon lex) => _lex = lex;

    /// <summary>诊断用：上一次 TryParse 在后置定语循环里消费了多 token。</summary>
    public static int LastPostfixConsumed;

    /// <summary>尝试从 cursor 当前位置解析一个目标。解析不了返回 null（cursor 不动）。</summary>
    public TargetSpec TryParse(TokenCursor cur)
    {
        int start = cur.Position;
        var owner = ZoneScope.Self;
        bool ownerSet = false;
        var zone = ZoneRef.Board;
        var mode = SelectMode.All;
        var wheres = new List<BoolExpr>();
        string slot = null;
        int cardId = 0;
        string cardName = "";
        SortKey sort = SortKey.None;
        bool desc = false;
        bool distinct = false;
        NumExpr count = NumExpr.Const(1);
        bool countSet = false;
        bool matched = false;
        bool hasCardType = false;
        bool includeLeader = false;

        // ---- -1. 特殊整体指代（最高优先级拦截）----

        // "all leaders with the highest/lowest defense"
        if (cur.MatchPhrase("all", "leaders") || cur.MatchPhrase("the", "leaders"))
        {
            cur.MatchPhrase("with", "the");
            while (!cur.IsAtEnd && cur.Current.IsAnyWord("highest", "lowest", "defense", "base", "cost")) cur.Advance();
            return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };
        }

        // "both leaders" / "all leaders"
        if (cur.MatchPhrase("both", "leaders") || cur.MatchPhrase("all", "leaders"))
            return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };

        // "the opposing follower" / "the opposing amulet"
        if (cur.MatchPhrase("the", "opposing", "follower") || cur.MatchPhrase("the", "opposing", "amulet"))
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotOpposing, Mode = SelectMode.All };

        // "the exact copy" / "the exact copies" / "the copy"
        if (cur.MatchPhrase("the", "exact", "copy") || cur.MatchPhrase("the", "exact", "copies")
            || cur.MatchPhrase("the", "copy"))
            return TargetSpec.Created;

        // "yourself"
        if (cur.MatchWord("yourself"))
        {
            // "yourself and your opponent" → 双方主战者
            if (cur.MatchPhrase("and", "your", "opponent"))
                return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };
            return TargetSpec.SelfLeader;
        }

        // "your leader's defense" / "your leader"
        if (cur.MatchPhrase("your", "leader's"))
        {
            cur.TryMatchAnyWord("defense", "life", "attack");
            return TargetSpec.SelfLeader;
        }
        if (cur.MatchPhrase("the", "enemy", "leader's"))
        {
            cur.TryMatchAnyWord("defense", "life", "attack");
            return TargetSpec.OpponentLeader;
        }
        if (cur.MatchPhrase("your", "leader"))
        {
            if (cur.MatchPhrase("and", "your", "opponent") || cur.MatchPhrase("and", "the", "enemy", "leader"))
                return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };
            return TargetSpec.SelfLeader;
        }

        // "your opponent" / "the enemy leader" / "an enemy leader"
        if (cur.MatchPhrase("your", "opponent") || cur.MatchPhrase("the", "enemy", "leader")
            || cur.MatchPhrase("an", "enemy", "leader"))
            return TargetSpec.OpponentLeader;

        // "your hand"
        if (cur.MatchPhrase("your", "hand"))
            return new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.Hand, Mode = SelectMode.All };

        // "all your crests" / "all crests"
        if (cur.MatchPhrase("all", "your", "crests") || cur.MatchPhrase("all", "crests"))
            return new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.CrestArea, Mode = SelectMode.All };

        // "your Crest: <Name>"
        if (cur.MatchPhrase("your", "crest"))
        {
            cur.TryMatch(TokKind.Colon);
            string cn = "";
            if (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period) { cn = cur.Current.Text; cur.Advance(); }
            return new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.CrestArea, CardName = cn, Mode = SelectMode.All };
        }

        // ---- 0. 槽位代词 ----
        if (cur.Current.IsAnyWord("it", "them"))
        {
            cur.Advance();
            // "it and this follower" / "them and the enemy leader"
            if (cur.Current.IsWord("and"))
            {
                int saveAnd = cur.Position;
                cur.Advance();
                if (cur.Current.IsAnyWord("both", "the", "this"))
                {
                    var second = cur.MatchPhrase("both", "leaders")
                        ? new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true }
                        : cur.MatchPhrase("the", "enemy", "leader") || cur.MatchPhrase("an", "enemy", "leader")
                            ? TargetSpec.OpponentLeader
                            : cur.MatchPhrase("this", "follower") || cur.MatchPhrase("this", "card")
                                ? TargetSpec.Self
                                : null;
                    if (second != null)
                        return new TargetSpec
                        {
                            SourceKind = TargetSourceKind.Slot,
                            Slot = TargetSpec.SlotSelected,
                            IncludeLeader = second.IncludeLeader,
                            Where = BoolExpr.True,
                            Mode = SelectMode.All,
                        };
                }
                cur.Seek(saveAnd);
            }
            // 代词 it 的指向取决于当前上下文（R-4 / Stage 4 代词绑定）：
            //   · 前面有 Select <Target>      → $selected
            //   · 前面是 Summon / Add / Draw   → $created
            //   · 处于 When/Whenever <主语> enters/evolves/attacks 触发器且本句未 Select → $trigger
            // 之前死写 $selected，导致 30 张
            // "Whenever X enters the field, give it ..." 在触发时去读空的 $selected。
            return new TargetSpec
            {
                SourceKind = TargetSourceKind.Slot,
                Slot = DefaultPronounSlot ?? TargetSpec.SlotSelected,
                Mode = SelectMode.All,
            };
        }
        if (cur.Current.IsAnyWord("them"))
        {
            cur.Advance();
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelected, Mode = SelectMode.All };
        }
        if (cur.Current.IsAnyWord("its", "their"))
        {
            cur.Advance();
            // its defense / its attack → 指代上一条动作的目标
            cur.TryMatchAnyWord("defense", "attack", "cost", "life");
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelected, Mode = SelectMode.All };
        }
        if (cur.Current.IsAnyWord("itself"))
        {
            cur.Advance();
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelf, Mode = SelectMode.All };
        }

        // "this follower" / "this card"
        if (cur.Current.IsAnyWord("this"))
        {
            cur.Advance();
            cur.TryMatchAnyWord("follower", "card", "amulet");
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelf, Mode = SelectMode.All };
        }

        // ---- 1. 量词 ----
        if (cur.Current.Kind == TokKind.Number)
        {
            int n = (int)cur.Current.Value;
            cur.Advance();
            count = NumExpr.Const(n);
            countSet = true;
            mode = SelectMode.Choose;
            matched = true;
            // "2 leftmost" → 按顺序取前 N 个
            if (cur.Current.IsWord("leftmost"))
            {
                cur.Advance();
                mode = SelectMode.First;
            }
        }
        // "the leftmost"
        else if (cur.Current.IsWord("leftmost"))
        {
            cur.Advance();
            mode = SelectMode.First;
            count = NumExpr.Const(1);
            countSet = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("all"))
        {
            cur.Advance();
            mode = SelectMode.All;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("a", "an"))
        {
            cur.Advance();
            count = NumExpr.Const(1);
            countSet = true;
            mode = SelectMode.Choose;
        }
        else if (cur.Current.IsAnyWord("each"))
        {
            cur.Advance();
            mode = SelectMode.All;
            matched = true;
        }

        // ---- 2. and 是并列边界，在此停止（必须在修饰词循环之前判定）----
        if (cur.Current.IsWord("and"))
        {
            cur.Seek(start);
            return null;
        }

        // ---- 3. 统一前置修饰词循环 ----
        // 英语里这些是同层修饰语，顺序不固定：
        //   all random other allied evolved Ward followers with 3 defense or less
        //   the 2 leftmost / another random / a random unselected enemy / 3-base-cost allied
        // 旧实现把它们散落在循环外，导致一旦顺序与预期不同就永远匹配不到。
        while (true)
        {
            // 冠词
            if (cur.Current.IsAnyWord("the", "a", "an")) { cur.Advance(); continue; }

            // [N] leftmost / leftmost
            if (cur.Current.Kind == TokKind.Number && cur.Peek().IsWord("leftmost"))
            {
                count = NumExpr.Const(cur.Current.Value); countSet = true;
                cur.Advance(); cur.Advance();
                mode = SelectMode.First; matched = true; continue;
            }
            if (cur.Current.IsWord("leftmost"))
            {
                cur.Advance(); mode = SelectMode.First;
                if (!countSet) { count = NumExpr.Const(1); countSet = true; }
                matched = true; continue;
            }

            // random / differently named / exact copy / unselected / another·other
            if (cur.MatchWord("random"))
            {
                mode = SelectMode.Random;
                if (!countSet) { count = NumExpr.Const(1); countSet = true; }
                matched = true; continue;
            }
            if (cur.MatchPhrase("differently", "named")) { distinct = true; matched = true; continue; }
            if (cur.MatchPhrase("exact", "copy") || cur.MatchPhrase("exact", "copies")) { matched = true; continue; }
            if (cur.MatchWord("unselected"))
            {
                wheres.Add(BoolExpr.Not(new IsPreviousExpr { Slot = TargetSpec.SlotSelected }));
                matched = true; continue;
            }
            if (cur.MatchAnyWord("another", "other"))
            {
                wheres.Add(new NotSameEntityExpr { ExcludeSlot = TargetSpec.SlotSelf, Of = TargetSpec.Item });
                matched = true; continue;
            }

            // N-cost / N-base-cost / X-cost（分词器把 X-cost 拆成 X / - / cost）
            if ((cur.Current.Kind == TokKind.Number || cur.Current.IsAnyWord("x", "y", "z"))
                && cur.Peek().Kind == TokKind.Dash)
            {
                NumExpr costExpr = cur.Current.Kind == TokKind.Number
                    ? NumExpr.Const(cur.Current.Value)
                    : NumExpr.Var(_lex.Counter(cur.Current.Text.ToUpperInvariant()));
                cur.Advance(); cur.Advance();
                bool isBase = cur.MatchWord("base");
                cur.TryMatch(TokKind.Dash);
                cur.TryMatchAnyWord("cost", "costs");
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(isBase ? StatKey.BaseCost : StatKey.Cost, TargetSpec.Item),
                    Op = CompareOp.Eq,
                    Right = costExpr,
                });
                matched = true; continue;
            }

            // non-<Trait>（分词器拆成 non / - / Encroacher）
            if (cur.Current.IsWord("non") && cur.Peek().Kind == TokKind.Dash)
            {
                cur.Advance(); cur.Advance();
                var trNon = cur.Current.Text; cur.Advance();
                wheres.Add(BoolExpr.Not(new HasTraitExpr { Trait = _lex.Trait(trNon), Of = TargetSpec.Item }));
                matched = true; continue;
            }

            // 归属
            // "allied and enemy follower" -> Any
            if (cur.Current.IsAnyWord("allied", "allies") && cur.Peek().IsWord("and")
                && cur.Peek(2).IsAnyWord("enemy", "enemies"))
            {
                cur.Advance(); cur.Advance(); cur.Advance();
                owner = ZoneScope.Any; ownerSet = true; matched = true; continue;
            }
            if (cur.Current.IsAnyWord("enemy"))
            {
                cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true; matched = true;
                while (cur.Current.IsAnyWord("a", "an")) cur.Advance();
                continue;
            }
            if (cur.Current.IsAnyWord("allied"))
            {
                cur.Advance();
                owner = ZoneScope.Self; ownerSet = true; matched = true;
                while (cur.Current.IsAnyWord("a", "an")) cur.Advance();
                continue;
            }
            if (cur.Current.IsAnyWord("your") && cur.Peek().IsWord("opponent"))
            {
                cur.Advance(); cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true; matched = true;
                continue;
            }
            // "your" 后面若是 leader/hand/deck 等已知目标，留给专用分支处理
            if (cur.Current.IsAnyWord("your") && !cur.Peek().IsAnyWord("leader", "hand", "deck", "cemetery", "crest"))
            {
                cur.Advance();
                owner = ZoneScope.Self; ownerSet = true; matched = true;
                continue;
            }

            // 状态：super-evolved / evolved / unevolved / damaged
            if (cur.Current.IsAnyWord("super") && cur.Peek().Kind == TokKind.Dash)
            {
                cur.Advance(); cur.Advance(); cur.TryMatchAnyWord("evolved");
                wheres.Add(new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("super-evolved"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("unevolved"))
            {
                cur.Advance();
                wheres.Add(BoolExpr.Not(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Item }));
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("evolved"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            // "damaged" 必须后接名词，否则可能是 "Destroy all damaged enemy followers" 里的修饰
            if (cur.Current.IsAnyWord("damaged"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.Damaged, Of = TargetSpec.Item });
                matched = true; continue;
            }

            // 兵种（可能连续多个）
            if (cur.Current.Kind == TokKind.Trait || _lex.IsTrait(cur.Current.Text))
            {
                var tr = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasTraitExpr { Trait = _lex.Trait(tr), Of = TargetSpec.Item });
                matched = true; continue;
            }

            // 职业
            if (cur.Current.Kind == TokKind.Clan)
            {
                var cn = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasClanExpr { Clan = _lex.Clan(cn), Of = TargetSpec.Item });
                matched = true; continue;
            }

            // "other"（除自身外）
            if (cur.Current.IsAnyWord("another", "other"))
            {
                cur.Advance();
                wheres.Add(new NotSameEntityExpr { ExcludeSlot = TargetSpec.SlotSelf, Of = TargetSpec.Item });
                matched = true; continue;
            }

            break;
        }

        // unselected
        if (cur.MatchWord("unselected"))
        {
            wheres.Add(BoolExpr.Not(new IsPreviousExpr { Slot = TargetSpec.SlotSelected }));
            matched = true;
        }

        // "all N-, N-, and N-cost cards"（费用枚举）
        if (cur.Current.IsAnyWord("all") && cur.Peek().Kind == TokKind.Number)
        {
            int saveE = cur.Position;
            cur.Advance();
            var costs = new List<int>();
            while (cur.Current.Kind == TokKind.Number)
            {
                costs.Add(cur.Current.Value);
                cur.Advance();
                if (cur.Current.Kind == TokKind.Dash) cur.Advance();
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
                wheres.Add(ors.Count == 1 ? ors[0] : BoolExpr.Or(ors.ToArray()));
                matched = true;
            }
            else cur.Seek(saveE);
        }

        // "an allied and enemy follower" -> Any
        if ((cur.Current.IsAnyWord("allied", "allies")) && cur.Peek().IsWord("and")
            && cur.Peek(2).IsAnyWord("enemy", "enemies"))
        {
            cur.Advance(); cur.Advance(); cur.Advance();
            owner = ZoneScope.Any; ownerSet = true; matched = true;
        }

        // ---- 6c. 未知修饰词（未注册的兵种，如 Mysteria / Enhanced）----
        // 它们不是词典里的 Trait/Clan，但语法位置是前置定语，跳过即可
        while (!cur.IsAtEnd && cur.Current.Kind == TokKind.Word
               && !cur.Current.IsAnyWord("follower", "followers", "amulet", "amulets",
                                          "spell", "spells", "card", "cards",
                                          "leader", "leaders", "it", "them", "this",
                                          "enemy", "allied", "your", "the", "a", "an",
                                          "copy", "copies", "and", "or", "with", "on",
                                          "in", "from", "at", "to", "by", "that", "whose",
                                          "duplicate", "duplicates", "ally", "allies",
                                          "enemies", "random", "other", "another",
                                          "differently", "named", "unselected", "leftmost",
                                          "exact", "all"))
        {
            cur.Advance();
        }

        // ---- 7. 卡种 / 卡名 ----
        if (cur.MatchPhrase("ally", "or", "enemy") || cur.MatchPhrase("allies", "or", "enemies"))
        {
            owner = ZoneScope.Any; ownerSet = true; includeLeader = true; matched = true;
        }
        else if (cur.Current.IsAnyWord("duplicate", "duplicates"))
        {
            cur.Advance(); matched = true;
        }
        else
        if (cur.Current.Kind == TokKind.CardName)
        {
            cardId = cur.Current.CardId;
            cardName = cur.Current.Text;
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("follower", "followers"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("amulet", "amulets"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Amulet, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("spell", "spells"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Spell, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("enemies", "enemy") && !cur.Peek().IsAnyWord("follower", "followers"))
        {
            cur.Advance();
            if (!ownerSet) owner = ZoneScope.Opponent;
            includeLeader = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("allies", "ally"))
        {
            cur.Advance();
            if (!ownerSet) owner = ZoneScope.Self;
            includeLeader = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("card", "cards"))
        {
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("leader", "leaders"))
        {
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("copy", "copies"))
        {
            cur.Advance();
            cur.TryMatchAnyWord("of");
            // copies of <卡名>
            if (cur.Current.Kind == TokKind.CardName)
            {
                cardId = cur.Current.CardId;
                cardName = cur.Current.Text;
                cur.Advance();
            }
            else if (cur.Current.IsAnyWord("it", "them"))
            {
                // "all enemy copies of it from the field" -> 指向上一条动作的目标
                cur.Advance();
                cur.TryMatchAnyWord("from", "on");
                cur.TryMatchAnyWord("the");
                cur.TryMatchAnyWord("field");
                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotSelected,
                    Mode = SelectMode.All,
                };
            }
            matched = true;
        }

        // ---- 8. 区域 ----
        if (cur.Current.IsAnyWord("on") && cur.Peek().IsAnyWord("the") && cur.Peek(2).IsAnyWord("field"))
        {
            cur.Advance(); cur.Advance(); cur.Advance();
            zone = ZoneRef.Board;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("in", "from") && cur.Peek().IsAnyWord("your"))
        {
            cur.Advance(); cur.Advance();
            // "in your opponent's hand"
            if (cur.Current.IsAnyWord("opponent's", "opponent") )
            {
                cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true;
            }
            var z = cur.Current.Text.ToLowerInvariant();
            cur.Advance();
            zone = z.StartsWith("hand") ? ZoneRef.Hand
                 : z.StartsWith("deck") ? ZoneRef.Deck
                 : z.StartsWith("cemetery") ? ZoneRef.Cemetery
                 : ZoneRef.Board;
            matched = true;
        }

        // ---- 8b. 更多区域短语 ----
        if (cur.MatchPhrase("from", "your", "deck")) { zone = ZoneRef.Deck; }
        else if (cur.MatchPhrase("from", "your", "hand")) { zone = ZoneRef.Hand; }
        else if (cur.MatchPhrase("from", "your", "cemetery")) { zone = ZoneRef.Cemetery; }
        else if (cur.MatchPhrase("from", "the", "field") || cur.MatchPhrase("on", "the", "field"))
            zone = ZoneRef.Board;
        else if (cur.MatchPhrase("in", "your", "opponent's", "deck")) { zone = ZoneRef.Deck; owner = ZoneScope.Opponent; ownerSet = true; }
        else if (cur.MatchPhrase("in", "your", "opponent's", "hand")) { zone = ZoneRef.Hand; owner = ZoneScope.Opponent; ownerSet = true; }
        else if (cur.MatchPhrase("in", "your", "hand", "and", "deck")) { zone = ZoneRef.Hand; }
        else if (cur.MatchPhrase("in", "your", "deck")) { zone = ZoneRef.Deck; }
        else if (cur.MatchPhrase("in", "your", "hand")) { zone = ZoneRef.Hand; }
        else if (cur.MatchPhrase("in", "your", "cemetery")) { zone = ZoneRef.Cemetery; }
        else if (cur.MatchPhrase("destroyed", "this", "match")) { zone = ZoneRef.Cemetery; }

        // ---- 9. 后置定语 ----
        int postLoopStart = cur.Position;
        while (true)
        {
            // 区域短语可能出现在后置定语之后（"that costs 3 or less from your deck"）
            if (cur.MatchPhrase("from", "your", "deck")) { zone = ZoneRef.Deck; matched = true; continue; }
            if (cur.MatchPhrase("from", "your", "hand")) { zone = ZoneRef.Hand; matched = true; continue; }
            if (cur.MatchPhrase("from", "your", "cemetery")) { zone = ZoneRef.Cemetery; matched = true; continue; }
            if (cur.MatchPhrase("from", "the", "field")) { zone = ZoneRef.Board; matched = true; continue; }
            if (cur.MatchPhrase("in", "your", "hand")) { zone = ZoneRef.Hand; matched = true; continue; }
            if (cur.MatchPhrase("in", "your", "deck")) { zone = ZoneRef.Deck; matched = true; continue; }
            // "whose cost has been changed"
            if (cur.MatchPhrase("whose", "cost", "has", "been", "changed"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.CostModified, Of = TargetSpec.Item });
                matched = true; continue;
            }
            // "drawn"（Summon any Forestcraft followers drawn）
            if (cur.MatchWord("drawn"))
            {
                slot = TargetSpec.SlotCreated; matched = true; continue;
            }
            // "and the enemy leader"
            if (cur.MatchPhrase("and", "the", "enemy", "leader") || cur.MatchPhrase("and", "an", "enemy", "leader"))
            {
                includeLeader = true; matched = true; continue;
            }

            // "and both leaders"
            if (cur.MatchPhrase("and", "both", "leaders") || cur.MatchPhrase("both", "leaders"))
            {
                includeLeader = true; owner = ZoneScope.Any; ownerSet = true;
                matched = true; continue;
            }
            // "with Fuse"（先判断再消耗，避免 MatchWord 抢先吞掉 with）
            if (cur.Current.IsWord("with") && cur.Peek().IsWord("fuse"))
            {
                cur.Advance(); cur.Advance();
                wheres.Add(new IsPreviousExpr { Slot = "fuse_material" });
                matched = true; continue;
            }

            // "or the enemy leader"
            if (cur.MatchPhrase("or", "the", "enemy", "leader") || cur.MatchPhrase("or", "an", "enemy", "leader"))
            {
                includeLeader = true;
                matched = true;
                continue;
            }
            // "other than it" / "other than the attack target"
            if (cur.MatchPhrase("other", "than", "it") || cur.MatchPhrase("other", "than", "the", "attack", "target"))
            {
                wheres.Add(BoolExpr.Not(new IsPreviousExpr { Slot = TargetSpec.SlotTrigger }));
                matched = true;
                continue;
            }
            // "that didn't attack this turn"
            if (cur.MatchPhrase("that", "didn't", "attack", "this", "turn"))
            {
                wheres.Add(BoolExpr.Not(new HasStateExpr { State = UnitState.AttackedThisTurn, Of = TargetSpec.Item }));
                matched = true;
                continue;
            }
            // "with N defense/attack or less/more"
            if (cur.Current.IsWord("with") && cur.Peek().Kind == TokKind.Number
                && cur.Peek(2).IsAnyWord("attack", "defense", "life"))
            {
                cur.Advance();
                int fn = cur.Current.Value; cur.Advance();
                var fstat = cur.Current.Text.ToLowerInvariant().StartsWith("attack") ? StatKey.Attack : StatKey.Life;
                cur.Advance();
                cur.TryMatchAnyWord("or");
                var fop = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(fstat, TargetSpec.Item),
                    Op = fop,
                    Right = NumExpr.Const(fn),
                });
                matched = true; continue;
            }

            // "with On Spellboost"
            if (cur.MatchPhrase("with", "on", "spellboost"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.HasOnSpellboost, Of = TargetSpec.Item });
                matched = true;
                continue;
            }
            // "with Last Words [and a base cost of N or less]"
            if (cur.MatchPhrase("with", "last", "words"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.HasLastWords, Of = TargetSpec.Item });
                if (cur.MatchPhrase("and", "a", "base", "cost", "of") && cur.Current.Kind == TokKind.Number)
                {
                    int bn = cur.Current.Value; cur.Advance();
                    cur.TryMatchAnyWord("or"); cur.TryMatchAnyWord("less");
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = CompareOp.Le,
                        Right = NumExpr.Const(bn),
                    });
                }
                matched = true;
                continue;
            }

            // with the highest/lowest X
            if (cur.Current.IsAnyWord("with") && cur.Peek().IsAnyWord("the")
                && cur.Peek(2).IsAnyWord("highest", "lowest"))
            {
                cur.Advance(); cur.Advance();
                desc = cur.Current.IsAnyWord("highest");
                cur.Advance();
                bool isBaseCost = cur.MatchWord("base");
                var key = cur.Current.Text.ToLowerInvariant();
                cur.Advance();
                sort = key.StartsWith("attack") ? SortKey.Attack
                     : key.StartsWith("cost") ? (isBaseCost ? SortKey.BaseCost : SortKey.Cost)
                     : SortKey.Life;
                matched = true;
                continue;
            }
            // with <Keyword>
            if (cur.Current.IsAnyWord("with") && cur.Peek().Kind == TokKind.Keyword)
            {
                cur.Advance();
                var kw = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasKeywordExpr { Keyword = _lex.Keyword(kw), Of = TargetSpec.Item });
                matched = true;
                continue;
            }
            // "with base costs of N or less"
            if (cur.MatchPhrase("with", "base", "costs", "of"))
            {
                if (cur.Current.Kind == TokKind.Number)
                {
                    int cn2 = cur.Current.Value;
                    cur.Advance();
                    cur.TryMatchAnyWord("or");
                    var cop2 = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                    cur.Advance();
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = cop2,
                        Right = NumExpr.Const(cn2),
                    });
                    matched = true;
                    continue;
                }
            }

            // with a base cost of N or more/less
            if (cur.MatchPhrase("with", "a", "base", "cost", "of"))
            {
                if (cur.Current.Kind == TokKind.Number)
                {
                    int cn = cur.Current.Value;
                    cur.Advance();
                    cur.TryMatchAnyWord("or");
                    var cop = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                    cur.Advance();
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = cop,
                        Right = NumExpr.Const(cn),
                    });
                    matched = true;
                    continue;
                }
            }

            // with N defense/attack or less/more
            if (cur.Current.IsAnyWord("with") && cur.Peek().Kind == TokKind.Number
                && cur.Peek(2).IsAnyWord("attack", "defense", "life"))
            {
                cur.Advance();
                int n = (int)cur.Current.Value;
                cur.Advance();
                var stat = cur.Current.Text.ToLowerInvariant().StartsWith("attack") ? StatKey.Attack : StatKey.Life;
                cur.Advance();
                cur.TryMatchAnyWord("or");
                var op = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(stat, TargetSpec.Item),
                    Op = op,
                    Right = NumExpr.Const(n),
                });
                matched = true;
                continue;
            }
            // that costs N or less
            if (cur.Current.IsAnyWord("that", "costs", "cost") && cur.Peek().IsAnyWord("costs", "cost")
                && cur.Peek(2).Kind == TokKind.Number)
            {
                cur.Advance(); cur.Advance();
                int n = (int)cur.Current.Value;
                cur.Advance();
                cur.TryMatchAnyWord("or");
                var op = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                    Op = op,
                    Right = NumExpr.Const(n),
                });
                matched = true;
                continue;
            }
            break;
        }
        LastPostfixConsumed = cur.Position - postLoopStart;

        if (!matched && !hasCardType && wheres.Count == 0 && cardId == 0)
        {
            cur.Seek(start);
            return null;
        }

        BoolExpr where = wheres.Count switch
        {
            0 => BoolExpr.True,
            1 => wheres[0],
            _ => BoolExpr.And(wheres.ToArray()),
        };

        return new TargetSpec
        {
            // slot 优先："drawn" 之类会写入 $created
            SourceKind = slot != null ? TargetSourceKind.Slot
                       : cardId > 0 ? TargetSourceKind.CardId
                       : TargetSourceKind.Zone,
            Slot = slot ?? "",
            Owner = ownerSet ? owner : ZoneScope.Self,
            Zone = zone,
            CardId = cardId,
            CardName = cardName,
            Where = where,
            Mode = mode,
            Count = count,
            Sort = sort,
            SortDescending = desc,
            DistinctByName = distinct,
            IncludeLeader = includeLeader,
        };
    }
}
