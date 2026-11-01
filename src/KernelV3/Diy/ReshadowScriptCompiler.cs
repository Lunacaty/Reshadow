using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Diy;

// ============================================================================
// ReshadowScript —— 结构化 DIY 脚本（Parse <-> Print 双向无损）
//
// 为什么除了英文 CNL 还要有它：
//   英文自然语言要求玩家背熟官方介词搭配（on the field vs in your hand），
//   而写脚本的人想要的是「一行一个指令、结构清晰、关键字可中可英」。
//
// 双向无损是硬要求，不是加分项：
//   Print(Parse(s)) 语义还原，且 Parse(Print(d)) 与原 d 结构等价。
//   只有单向的 DSL 是**无法验证**的 —— 导出再导入就丢东西，而没人会发现。
//   所以本文件里每个节点都同时写了「怎么解析」和「怎么打印」，
//   两者在 Regress.Diy 里逐卡对拍。
// ============================================================================
public sealed class ReshadowScriptCompiler
{
    private const char DollarSign = (char)36;

    private readonly DiyWorkspace _ws;
    private readonly GameSchema _schema;
    private readonly Lang.Lexicon _lex;
    private readonly List<string> _errors = new();

    public ReshadowScriptCompiler(DiyWorkspace workspace)
    {
        _ws = workspace;
        _schema = workspace.Schema;
        _lex = workspace.Lexicon;
    }

    /// <summary>
    /// 卡名 → 卡号。DoSpawn 只认 CardId、DoTransform 只认 IntoCardId，
    /// 缺了它们生成的是一张「卡面正确、运行时空操作」的卡。
    /// </summary>
    /// <summary>
    /// 解析表达式时「省略 Of」的缺省作用对象。
    ///
    /// 能力级条件里缺省是 $self（判本卡自己）；where 里缺省是 $item（判被筛的那个）。
    /// 这个差别不能靠猜：拿错的一侧，条件会在运行期恒假，而卡面文字完全一样。
    /// </summary>
    private TargetSpec DefaultContextSelf = TargetSpec.Self;

    private int CardIdOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _lex == null) return 0;
        return _lex.TryGetCardId(name.Trim(), out var id) ? id : 0;
    }

    public IReadOnlyList<string> Errors => _errors;

    // ================================================================ 解析

    /// <summary>解析一整份脚本，返回其中声明的全部卡（通常只有一张）。</summary>
    public List<CardDef> Compile(string script)
    {
        _errors.Clear();
        var cards = new List<CardDef>();
        if (string.IsNullOrWhiteSpace(script)) return cards;

        var p = new Cursor(StripComments(script));

        while (!p.AtEnd)
        {
            var t = p.PeekToken();
            if (t == null) break;

            if (t == "trait") { ParseTraitDecl(p); continue; }
            if (t == "counter") { ParseCounterDecl(p); continue; }
            if (t == "clan") { ParseClanDecl(p); continue; }
            if (t == "card") { var c = ParseCard(p); if (c != null) cards.Add(c); continue; }

            _errors.Add("无法识别的顶层声明：" + t);
            p.SkipLine();
        }
        return cards;
    }

    /// <summary>
    /// 去掉注释但**保留换行**：脚本是行式的（一行一条指令），
    /// 把换行全删掉会让 damage(...) 与 draw(1) 粘成一句。
    /// </summary>
    private static string StripComments(string src)
    {
        var sb = new StringBuilder();
        var norm = src.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in norm.Split('\n'))
        {
            int i = raw.IndexOf("//", StringComparison.Ordinal);
            sb.Append((i >= 0 ? raw.Substring(0, i) : raw).TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    private void ParseTraitDecl(Cursor p)
    {
        p.Next();
        string name = p.Next();
        string chs = p.TryString() ?? name;
        _ws.DefineTrait(name, chs);
    }

    private void ParseCounterDecl(Cursor p)
    {
        p.Next();
        string name = p.Next();
        string chs = p.TryString() ?? name;
        int min = 0, max = 99;
        var save = p.Save();
        if (p.TryChar('('))
        {
            int a = p.TryInt() ?? 0;
            if (p.TryLiteral("..")) { int b = p.TryInt() ?? 99; min = a; max = b; }
            p.TryChar(')');
        }
        else p.Restore(save);
        _ws.DefineCounter(name, chs, min, max);
    }

    private void ParseClanDecl(Cursor p)
    {
        p.Next();
        string name = p.Next();
        string chs = p.TryString() ?? name;
        _ws.DefineClan(name, chs);
    }
    /// <summary>
    /// card 910001 "御剑机甲师" (Follower, cost: 3, atk: 2, life: 3, traits: [Mecha]) { ... }
    /// </summary>
    /// <summary>
    /// discover(target(ally, card_pool|global_pool, ...), to: hand)
    ///
    /// 与 summon 的区别：summon 是「造一张指定的卡」，
    /// discover 是「从卡池里按条件找」，条件住在 TargetSpec.Where 里。
    /// </summary>
    /// <summary>
    /// 解析 clan 筛选：self / 单职业 / A|B 多职业。
    /// </summary>
    private HasClanExpr ParseClanFilter(Cursor p)
    {
        var first = ReadDashedId(p);
        if (string.Equals(first, "self", StringComparison.OrdinalIgnoreCase))
            return new HasClanExpr { IsSelfClan = true, Of = TargetSpec.Item };

        var names = new List<string> { first };
        while (p.PeekChar() == '|')
        {
            p.NextChar();
            names.Add(ReadDashedId(p));
        }

        if (names.Count == 1)
            return new HasClanExpr { Clan = _schema.Clan(names[0]), Of = TargetSpec.Item };

        return new HasClanExpr
        {
            Clans = names.Select(n => _schema.Clan(n)).ToList(),
            Of = TargetSpec.Item,
        };
    }

    /// <summary>解析 pack 列表（core-wb|testset-v2），逐段拼回连字符 ID。</summary>
    private static string ReadDashedPackList(Cursor p)
    {
        var parts = new List<string> { ReadDashedId(p) };
        while (p.PeekChar() == '|')
        {
            p.NextChar();
            parts.Add(ReadDashedId(p));
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// 读一个可能带连字符的标识符（core-wb / my-pack-2）。
    /// tokenizer 把 '-' 当成独立 token，逐 token 拼接才能还原原样。
    /// </summary>
    private static string ReadDashedId(Cursor p)
    {
        var sb = new StringBuilder(p.Next() ?? "");
        while (p.PeekToken() == "-")
        {
            p.Next();
            var seg = p.Next();
            if (seg == null) break;
            sb.Append('-').Append(seg);
        }
        return sb.ToString();
    }

    private EffectNode ParseDiscover(Cursor p)
    {
        // 调用方 ParseAtomStatement 已吃掉 discover 后面的 '('，
        // 而 ParseTargetCall 自己会吃 "target" 和它后面的 '(' ——
        // 这里**什么都不能再吃**，直接交给它。
        //
        // 语法：discover(target(ally, card_pool, follower), to: hand)
        var spec = ParseTargetCall(p);
        if (spec == null) return null;

        var toZone = ZoneRef.Hand;
        int offer = 3;
        int pick = 1;

        // 可选的键值参数：to: / offer: / pick:
        while (p.TryChar(','))
        {
            var key = p.PeekToken();
            if (key == "to")
            {
                p.Next(); p.TryChar(':');
                toZone = p.Next() switch
                {
                    "hand" => ZoneRef.Hand,
                    "board" => ZoneRef.Board,
                    "deck" => ZoneRef.Deck,
                    "exarea" => ZoneRef.ExArea,
                    _ => ZoneRef.Hand,
                };
            }
            else if (key == "offer")
            {
                p.Next(); p.TryChar(':');
                offer = p.TryInt() ?? 3;
            }
            else if (key == "pick")
            {
                p.Next(); p.TryChar(':');
                pick = p.TryInt() ?? 1;
            }
            else break;
        }
        p.TryChar(')');

        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery,
            CopyFrom = spec,
            ToZone = toZone,
            ToOwner = ZoneScope.Self,
            Count = NumExpr.Const(Math.Max(1, pick)),
            // 展示候选数 X（1~5）。默认 3 = 传统三选一。
            OfferCount = Math.Clamp(offer, 1, 5),
        };
    }

    /// <summary>
    /// 解析 deck_mod 声明：
    ///   deck_mod size(+20)                  牌组张数 ±N
    ///   deck_mod leader_life(+10)           主战者初始/最大生命 ±N
    ///   deck_mod hand(+1)                   起手抽牌 ±N
    ///   deck_mod ep(+1)                     初始进化点 +N
    ///   deck_mod sep(+0)                    初始超进化点 +N
    ///   deck_mod allow_clan(Runecraft)      允许固定职业
    ///   deck_mod allow_clan(choose: 1)      允许任选 N 个职业
    ///   deck_mod self_copy(3)               本卡同名上限
    ///   deck_mod size(+20) per_copy         按投入张数叠加
    ///   deck_mod size(+20) group:cap        互斥组
    /// </summary>
    /// <summary>deck_mod 的种类与参数 → 脚本文本。</summary>
    private static string DeckModWord(DeckBuildModifier m)
    {
        string arg = m.Kind switch
        {
            DeckModKind.AllowSpecificClan => "(" + m.TargetClan + ")",
            DeckModKind.AllowChosenClans => "(choose: " + m.Value + ")",
            DeckModKind.ModifyStartEvolvePoints when m.Value2 != 0 => "(" + (m.Value2 >= 0 ? "+" : "") + m.Value2 + ")",
            _ => "(" + (m.Value >= 0 ? "+" : "") + m.Value + ")",
        };
        string word = m.Kind switch
        {
            DeckModKind.ModifyDeckSize => "size",
            DeckModKind.ModifyLeaderStartLife => "leader_life",
            DeckModKind.ModifyStartingHandSize => "hand",
            DeckModKind.ModifyStartEvolvePoints => m.Value2 != 0 ? "sep" : "ep",
            DeckModKind.OverrideSelfCopyLimit => "self_copy",
            DeckModKind.AllowSpecificClan => "allow_clan",
            DeckModKind.AllowChosenClans => "allow_clan",
            _ => "size",
        };
        var sb = new StringBuilder("deck_mod ").Append(word).Append(arg);
        if (m.StackPerCopy) sb.Append(" per_copy");
        if (!string.IsNullOrEmpty(m.ExclusiveGroup)) sb.Append(" group:").Append(m.ExclusiveGroup);
        return sb.ToString();
    }

    private DeckBuildModifier ParseDeckMod(Cursor p)
    {
        p.Next();               // deck_mod
        string kindWord = p.Next();
        if (kindWord == null) { _errors.Add("deck_mod 缺少种类"); return null; }

        // 括号里的参数
        int value = 0;
        string clan = "";
        if (p.TryChar('('))
        {
            if (p.PeekToken() == "choose")
            {
                p.Next();
                p.TryChar(':');
                value = p.TryInt() ?? 1;
                clan = "";      // choose 模式表示「任选」，不是固定职业
            }
            else
            {
                // 支持带正负号的数量：size(+20) / leader_life(-3)。
                // tokenizer 会把 '+' 拆成独立符号，所以必须先看符号再取数。
                int sign = 1;
                var t0 = p.PeekToken();
                if (t0 == "+") { p.Next(); sign = 1; }
                else if (t0 == "-") { p.Next(); sign = -1; }

                int? num = p.TryInt();
                if (num.HasValue) value = sign * num.Value;
                else { clan = p.Next() ?? ""; value = 1; }
            }
            p.TryChar(')');
        }

        bool perCopy = false;
        string group = "";
        while (true)
        {
            var t = p.PeekToken();
            if (t == "per_copy") { p.Next(); perCopy = true; continue; }
            if (t == "group") { p.Next(); p.TryChar(':'); group = p.Next() ?? ""; continue; }
            break;
        }

        DeckModKind kind;
        switch (kindWord)
        {
            case "size": kind = DeckModKind.ModifyDeckSize; break;
            case "leader_life": kind = DeckModKind.ModifyLeaderStartLife; break;
            case "hand": kind = DeckModKind.ModifyStartingHandSize; break;
            case "ep": kind = DeckModKind.ModifyStartEvolvePoints; break;
            case "sep": kind = DeckModKind.ModifyStartEvolvePoints; break;
            case "self_copy": kind = DeckModKind.OverrideSelfCopyLimit; break;
            case "allow_clan":
                kind = string.IsNullOrEmpty(clan) ? DeckModKind.AllowChosenClans : DeckModKind.AllowSpecificClan;
                break;
            default:
                _errors.Add("deck_mod 未知种类：" + kindWord);
                return null;
        }

        // sep 用 Value2
        int v1 = value, v2 = 0;
        if (kindWord == "sep") { v1 = 0; v2 = value; }

        return new DeckBuildModifier
        {
            Kind = kind, Value = v1, Value2 = v2,
            TargetClan = clan,
            StackPerCopy = perCopy,
            ExclusiveGroup = group,
        };
    }

    private CardDef ParseCard(Cursor p)
    {
        p.Next();
        int id = p.TryInt() ?? 0;
        string nameChs = p.TryString() ?? ("DIY" + id);

        CardKind kind = CardKind.Follower;
        int cost = 0, atk = 0, life = 0;
        string clan = null;
        var traits = new List<string>();

        if (p.TryChar('('))
        {
            while (!p.AtEnd && !p.TryChar(')'))
            {
                var key = p.Next();
                if (key == null) break;
                if (key == ",") continue;

                if (IsKindWord(key)) { kind = KindOf(key); continue; }

                if (p.TryChar(':'))
                {
                    if (key == "cost") cost = p.TryInt() ?? 0;
                    else if (key == "atk") atk = p.TryInt() ?? 0;
                    else if (key == "life") life = p.TryInt() ?? 0;
                    else if (key == "clan") clan = p.Next();
                    else if (key == "traits")
                    {
                        if (p.TryChar('['))
                        {
                            while (!p.AtEnd && !p.TryChar(']'))
                            {
                                var tr = p.Next();
                                if (tr == null) break;
                                if (tr != ",") traits.Add(tr);
                            }
                        }
                        else traits.Add(p.Next());
                    }
                    else p.SkipValue();
                }
            }
        }

        var abilities = new List<AbilityDef>();
        var deckMods = new List<DeckBuildModifier>();
        var keywords = new List<string>();
        CardDef crest = null, faith = null, accel = null, cryst = null;
        bool hasFuse = false;
        TargetSpec fuseFilter = null;

        if (!p.TryChar('{'))
        {
            _errors.Add("卡 " + id + " 缺少 { ... } 主体");
            return null;
        }

        while (!p.AtEnd && !p.TryChar('}'))
        {
            var head = p.PeekToken();
            if (head == null) break;

            if (head == "keywords") { p.Next(); p.TryChar(':'); ParseKeywordList(p, keywords); continue; }
            if (head == "fuse")
            {
                // fuse: target(...) —— 融合材料筛选。
                // 少了这一行，Sinciro / Striker / Gear 往返后直接不能融合。
                p.Next();
                p.TryChar(':');
                hasFuse = true;
                fuseFilter = ParseTargetOrExpr(p);
                continue;
            }
            if (head == "crest") { p.Next(); crest = ParseSubBlock(p, CardKind.Crest, nameChs, 0, traits); continue; }
            if (head == "faith") { p.Next(); faith = ParseSubBlock(p, CardKind.Faith, nameChs, 0, traits); continue; }
            if (head == "accelerate") { p.Next(); int c = ParseParenInt(p); accel = ParseSubBlock(p, CardKind.Spell, nameChs, c, traits); continue; }
            if (head == "crystallize" || head == "crystallise") { p.Next(); int c = ParseParenInt(p); cryst = ParseSubBlock(p, CardKind.Amulet, nameChs, c, traits); continue; }
            if (head == "deck_mod") { var dm = ParseDeckMod(p); if (dm != null) deckMods.Add(dm); continue; }
            if (head == "on") { abilities.Add(ParseOnBlock(p)); continue; }

            _errors.Add("卡 " + id + " 里无法识别：" + head);
            p.SkipBlockOrLine();
        }

        // 裸关键词编译成 PassiveAura（与官方卡同一套编码）。
        //
        // 必须**按名字排序**后再插入：裸关键词是集合语义，若保留书写顺序，
        // 导出→导入→再导出会得到 [rush, ward] 与 [ward, rush] 两种文本，
        // 而「打印幂等」是双向无损的一部分 —— 不幂等的导出无法用来 diff。
        var kwSorted = keywords
            .Select(k => k.Contains('(') ? k.Substring(0, k.IndexOf('(')) : k)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = kwSorted.Count - 1; i >= 0; i--)
        {
            string kw = kwSorted[i];
            var kwId = _schema.Keyword(kw);
            abilities.Insert(0, new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Body = new List<EffectNode>
                {
                    new ModifyKeywordNode { Target = TargetSpec.Self, Op = KeywordOp.Grant, Keyword = kwId },
                },
            });
        }

        var built = new CardDef
        {
            CardId = id,
            Name = nameChs,
            Kind = kind,
            BaseCost = cost,
            BaseAttack = atk,
            BaseLife = life,
            Abilities = abilities,
            Traits = traits.Select(t => _schema.Trait(t)).ToList(),
            Clan = string.IsNullOrEmpty(clan) ? SymbolId<ClanTag>.Invalid : _schema.Clan(clan),
            DeckModifiers = deckMods,
            HasFuse = hasFuse,
            FuseMaterialFilter = hasFuse ? fuseFilter : null,
            CrestDef = crest,
            FaithDef = faith,
            AccelerateDef = accel,
            CrystallizeDef = cryst,
        };

        // 纹章/信仰节点必须内嵌子卡定义：运行期 GainCrest 直接拿它成立实体，
        // 缺了它，纹章是一个没有任何能力的同名声望壳（卡面却写得像正常）。
        built = EmbedSubDefs(built);
        return built;
    }

    /// <summary>把 CrestDef / FaithDef 回填到「获得纹章 / 信仰」节点上。</summary>
    private static CardDef EmbedSubDefs(CardDef def)
    {
        if (def == null || (def.CrestDef == null && def.FaithDef == null)) return def;

        var newAbilities = new List<AbilityDef>();
        foreach (var ab in def.Abilities ?? Array.Empty<AbilityDef>())
        {
            newAbilities.Add(new AbilityDef
            {
                Trigger = ab.Trigger,
                Condition = ab.Condition,
                Costs = ab.Costs,
                HasFuse = ab.HasFuse,
                Body = Rewrite(def.FaithDef, def.CrestDef, ab.Body),
            });
        }

        return new CardDef
        {
            CardId = def.CardId, Name = def.Name, Kind = def.Kind,
            BaseCost = def.BaseCost, BaseAttack = def.BaseAttack, BaseLife = def.BaseLife,
            Abilities = newAbilities, Traits = def.Traits, Clan = def.Clan,
            HasFuse = def.HasFuse, FuseMaterialFilter = def.FuseMaterialFilter,
            CrestDef = def.CrestDef, FaithDef = def.FaithDef,
            AccelerateDef = def.AccelerateDef, CrystallizeDef = def.CrystallizeDef,
            SourceText = def.SourceText,
            // 这几项漏拷会让一张「既有纹章又有构筑修正」的卡在重写时
            // 静默丢掉 deck_mod / 中文名 / Token 标记。
            DeckModifiers = def.DeckModifiers,
            PackId = def.PackId,
            IsToken = def.IsToken,
            NameChs = def.NameChs,
            InnateKeywords = def.InnateKeywords,
        };
    }

    private static IReadOnlyList<EffectNode> Rewrite(CardDef faith, CardDef crest, IReadOnlyList<EffectNode> body)
    {
        if (body == null) return Array.Empty<EffectNode>();
        var list = new List<EffectNode>();
        foreach (var n in body)
        {
            switch (n)
            {
                case SpawnCardNode sp when sp.IsCrest:
                {
                    var match = sp.CardName == (faith?.Name ?? "") ? faith : crest ?? faith;
                    list.Add(new SpawnCardNode
                    {
                        SourceKind = sp.SourceKind, CardId = sp.CardId, CardName = sp.CardName,
                        Count = sp.Count, ToZone = sp.ToZone, ToOwner = sp.ToOwner,
                        IsCrest = sp.IsCrest, IsExactCopy = sp.IsExactCopy,
                        AddDepartedTrait = sp.AddDepartedTrait, CopyFrom = sp.CopyFrom,
                        CrestDef = match,
                    });
                    break;
                }
                case ConditionalNode cd:
                    list.Add(new ConditionalNode
                    {
                        Condition = cd.Condition,
                        Then = Rewrite(faith, crest, cd.Then),
                        Else = Rewrite(faith, crest, cd.Else),
                    });
                    break;
                case LoopNode lp:
                    list.Add(new LoopNode
                    {
                        Kind = lp.Kind, Count = lp.Count, Collection = lp.Collection,
                        Until = lp.Until, Body = Rewrite(faith, crest, lp.Body),
                        MaxIterations = lp.MaxIterations,
                    });
                    break;
                case BranchModeNode bm:
                    list.Add(new BranchModeNode
                    {
                        Kind = bm.Kind, PickCount = bm.PickCount,
                        Options = (bm.Options ?? Array.Empty<IReadOnlyList<EffectNode>>())
                            .Select(o => Rewrite(faith, crest, o)).ToList(),
                    });
                    break;
                default:
                    list.Add(n);
                    break;
            }
        }
        return list;
    }

    private static bool IsKindWord(string s) => s switch
    {
        "Follower" or "Spell" or "Amulet" or "Crest" or "Faith" => true,
        "随从" or "法术" or "护符" or "纹章" or "信仰" => true,
        _ => false,
    };

    private static CardKind KindOf(string s) => s switch
    {
        "Follower" or "随从" => CardKind.Follower,
        "Spell" or "法术" => CardKind.Spell,
        "Amulet" or "护符" => CardKind.Amulet,
        "Crest" or "纹章" => CardKind.Crest,
        "Faith" or "信仰" => CardKind.Faith,
        _ => CardKind.Follower,
    };

    private void ParseKeywordList(Cursor p, List<string> into)
    {
        if (p.TryChar('['))
        {
            while (!p.AtEnd && !p.TryChar(']'))
            {
                var t = p.Next();
                if (t == null) break;
                if (t != ",") into.Add(t);
            }
        }
        else
        {
            var t = p.Next();
            if (!string.IsNullOrEmpty(t)) into.Add(t);
        }
    }

    private int ParseParenInt(Cursor p)
    {
        if (!p.TryChar('(')) return 0;
        int v = p.TryInt() ?? 0;
        p.TryChar(')');
        return v;
    }

    private CardDef ParseSubBlock(Cursor p, CardKind kind, string name, int cost, List<string> traits)
    {
        var abilities = new List<AbilityDef>();
        if (!p.TryChar('{')) return null;
        while (!p.AtEnd && !p.TryChar('}'))
        {
            var head = p.PeekToken();
            if (head == "on") { abilities.Add(ParseOnBlock(p)); continue; }
            _errors.Add("子块里无法识别：" + head);
            p.SkipBlockOrLine();
        }
        return new CardDef
        {
            CardId = 0, Name = name, Kind = kind, BaseCost = cost,
            Abilities = abilities,
            Traits = traits.Select(t => _schema.Trait(t)).ToList(),
        };
    }

    /// <summary>
    /// on play { ... } / on evolve manual { ... }
    /// 以及可选的 zone: / turn: / or 与前置 if (...)。
    /// </summary>
    private AbilityDef ParseOnBlock(Cursor p)
    {
        p.Next();
        string evName = p.Next();
        var ev = EventOf(evName);

        // TriggerSpec 是 init-only：先把修饰词收集到局部变量，最后一次性构造。
        bool manual = false;
        var activeZone = ActiveZoneScope.Board;
        var duringTurn = TurnOwnerScope.Any;
        CoreEvent? secondary = null;
        int maxTrig = 0;
        Ast.TargetSpec subject = null;

        while (!p.AtEnd)
        {
            var t = p.PeekToken();
            if (t == "{") break;
            if (t == "if") break;
            if (t == "costs") break;
            p.Next();

            switch (t)
            {
                case "on":
                    // on(...) 有两种写法：on($self) 槽位型、on(target(...)) 区域型。
                    // 槽位型必须保留 SourceKind=Slot —— 丢掉它就变成
                    // 「所有友方战场卡」，监听对象整个换了，而卡面文字上看不出来。
                    {
                        // on 后面紧跟括号：先把 ( 吃掉再判断括号里是什么。
                        bool opened = p.TryChar('(');
                        var peek = p.PeekToken();
                        if (peek != null && peek.Length > 0 && peek[0] == DollarSign)
                        {
                            var sl = p.Next();
                            subject = new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = sl };
                            p.TryChar(')');
                        }
                        else if (opened)
                        {
                            // 括号已吃掉，on(target(...)) 里的 target 也在里面。
                            if (peek == "target") p.Next();
                            subject = ParseTargetArgs(p);
                            p.TryChar(')');
                        }
                        else
                        {
                            subject = ParseTargetArgs(p);
                        }
                    }
                    break;
                case "manual": manual = true; break;
                case "zone":
                    p.TryChar(':');
                    var z = p.Next();
                    activeZone = z switch
                    {
                        "hand" or "Hand" => ActiveZoneScope.Hand,
                        "deck" or "Deck" => ActiveZoneScope.Deck,
                        "exarea" or "ExArea" => ActiveZoneScope.ExArea,
                        _ => ActiveZoneScope.Board,
                    };
                    break;
                case "max":
                    // 每回合次数上限。不写出来就变成每次触发。
                    p.TryChar(':');
                    maxTrig = p.TryInt() ?? 0;
                    break;

                case "turn":
                    p.TryChar(':');
                    var tu = p.Next();
                    duringTurn = tu switch
                    {
                        "Self" or "self" => TurnOwnerScope.Self,
                        "Opponent" or "opponent" => TurnOwnerScope.Opponent,
                        _ => TurnOwnerScope.Any,
                    };
                    break;
                case "or":
                    secondary = EventOf(p.Next());
                    break;
                default: break;
            }
        }

        var trig = new TriggerSpec
        {
            Event = ev,
            ManualEvolveOnly = manual,
            ActiveZone = activeZone,
            DuringTurn = duringTurn,
            SecondaryEvent = secondary,
            MaxTriggersPerTurn = maxTrig,
            SubjectFilter = subject,
        };

        BoolExpr cond = null;
        if (p.PeekToken() == "if")
        {
            p.Next();
            p.TryChar('(');
            // 能力级条件省略 Of 时判的是本卡自己（$self）。
            var saved = DefaultContextSelf;
            DefaultContextSelf = TargetSpec.Self;
            cond = ParseBoolExpr(p);
            DefaultContextSelf = saved;
            p.TryChar(')');
        }

        // <ev> / <sev> 段在官方卡池里会附一条「已进化 / 已超进化」域条件。
        // 脚本语言按官方卡面写法（触发头即域），解析时补回这条条件，
        // 这样脚本产出的 CardDef 与英文编译产出的是**同一个 AST**，
        // 四端同构门禁才有意义（否则脚本端永远差一项）。
        cond = AddDomainCondition(cond, ev, manual);

        var costs = new List<CostSpec>();
        if (p.PeekToken() == "costs")
        {
            p.Next();
            p.TryChar(':');
            if (p.TryChar('['))
            {
                while (!p.AtEnd && !p.TryChar(']'))
                {
                    var ct = p.Next();
                    if (ct == null) break;
                    if (ct == ",") continue;
                    var spec = ParseCostSpec(p, ct);
                    if (spec != null) costs.Add(spec);
                }
            }
        }

        var body = new List<EffectNode>();
        if (p.TryChar('{')) body = ParseBlockBody(p);

        return new AbilityDef
        {
            Trigger = trig,
            Condition = cond ?? BoolExpr.True,
            Costs = costs,
            Body = body,
        };
    }

    /// <summary>
    /// 给 Evolve: / Super-Evolve: 能力补回域条件。
    ///
    /// 官方卡池的 <ev> 段编译出来是「触发头 OnEvolve + 条件 is Evolved(自身)」，
    /// 两者是冗余但**同时存在**的（运行时靠后者在非进化态下跳过）。
    /// 脚本若不补，脚本端与英文端产出的 AST 就不相等 ——
    /// 那正是四端同构门禁要抓的东西，不能靠测试放宽绕过去。
    /// </summary>
    private static BoolExpr AddDomainCondition(BoolExpr cond, CoreEvent ev, bool manualOnly)
    {
        // 不看 manualOnly：自动进化（"Evolve this follower"）触发的 OnEvolve
        // 与手动进化带的是同一条域条件，EnglishCompiler 两边都加。

        Core.UnitState? domain = ev switch
        {
            CoreEvent.OnSuperEvolve => Core.UnitState.SuperEvolved,
            CoreEvent.OnEvolve => Core.UnitState.Evolved,
            _ => null,
        };
        if (domain == null) return cond ?? BoolExpr.True;

        var dom = new HasStateExpr { State = domain.Value, Of = TargetSpec.Self };
        var items = new List<BoolExpr> { BoolExpr.True, dom };
        if (cond != null && !(cond is ConstBoolExpr cb && cb.Value)) items.Add(cond);
        return new AndExpr(items);
    }

    private CostSpec ParseCostSpec(Cursor p, string kind)
    {
        int amt = ParseParenInt(p);
        var k = kind switch
        {
            "necromancy" => CostKind.Necromance,
            "earth_rite" => CostKind.DestroyAlly,
            "combo" => CostKind.TurnPlayCount,
            "rally" => CostKind.MatchPlayCount,
            "countdown" => CostKind.Countdown,
            _ => CostKind.UsePp,
        };
        // Label 必须写回：EnhanceTiers 只认 Label=="enhance"。
        return new CostSpec { Kind = k, Amount = NumExpr.Const(amt), Label = kind };
    }

    /// <summary>解析 { ... } 内的语句序列（调用时 { 已被吃掉）。</summary>
    private List<EffectNode> ParseBlockBody(Cursor p)
    {
        var body = new List<EffectNode>();
        while (!p.AtEnd && !p.TryChar('}'))
        {
            var node = ParseStatement(p);
            if (node != null) body.Add(node);
        }
        return body;
    }

    private EffectNode ParseStatement(Cursor p)
    {
        var head = p.PeekToken();
        if (head == null) return null;

        if (head == "if" || head == "若") return ParseIf(p);
        if (head == "loop" || head == "循环") return ParseLoop(p);
        if (head == "mode" || head == "模式") return ParseMode(p);

        return ParseAtomStatement(p);
    }

    private EffectNode ParseIf(Cursor p)
    {
        p.Next();
        p.TryChar('(');
        var cond = ParseBoolExpr(p);
        p.TryChar(')');

        var then = new List<EffectNode>();
        if (p.TryChar('{')) then = ParseBlockBody(p);

        var els = new List<EffectNode>();
        var save = p.Save();
        var t = p.PeekToken();
        if (t == "else" || t == "否则")
        {
            p.Next();
            if (p.TryChar('{')) els = ParseBlockBody(p);
        }
        else p.Restore(save);

        return new ConditionalNode { Condition = cond, Then = then, Else = els };
    }

    private EffectNode ParseLoop(Cursor p)
    {
        p.Next();
        p.TryChar('(');
        var countExpr = ParseNumExpr(p);
        p.TryChar(')');
        var body = new List<EffectNode>();
        if (p.TryChar('{')) body = ParseBlockBody(p);
        // MaxIterations 是防死循环的硬上限：次数是常量就用常量，
        // 是表达式（「手牌 Pixie 数」）时取一个安全上限，避免 DIY 出无限循环。
        int cap = countExpr.Op == NumOp.Const ? Math.Max(1, countExpr.Value) : 30;
        return new LoopNode
        {
            Kind = LoopKind.Times,
            Count = countExpr,
            MaxIterations = cap,
            Body = body,
        };
    }

    private EffectNode ParseMode(Cursor p)
    {
        p.Next();
        string kind = p.PeekToken();
        var k = ModeDispatchKind.Choose;
        if (kind == "choose" || kind == "random" || kind == "sequence")
        {
            p.Next();
            k = kind switch
            {
                "random" => ModeDispatchKind.Random,
                "sequence" => ModeDispatchKind.Sequence,
                _ => ModeDispatchKind.Choose,
            };
        }
        int pick = ParseParenInt(p);
        if (pick <= 0) pick = 1;

        var opts = new List<IReadOnlyList<EffectNode>>();
        if (p.TryChar('{'))
        {
            while (!p.AtEnd && !p.TryChar('}'))
            {
                p.TryInt();
                p.TryChar(':');
                if (p.TryChar('{')) opts.Add(ParseBlockBody(p));
                else p.SkipBlockOrLine();
            }
        }
        return new BranchModeNode
        {
            Kind = k,
            PickCount = NumExpr.Const(pick),
            Options = opts,
        };
    }
    /// <summary>原子指令：damage(target, expr) / draw(1) …</summary>
    private EffectNode ParseAtomStatement(Cursor p)
    {
        var fn = p.Next();
        if (fn == null) return null;

        if (fn == "select")
        {
            // select 有两种写法：
            //   select $slot = target(...)   —— 指定槽位
            //   select target(...)           —— 用默认槽位 $selected
            // 判据是第二个 token 是否以 $ 开头；不能盲目吃掉再回退，
            // 因为回退点如果在 ( 之后就会把参数表吃掉。
            string slot = TargetSpec.SlotSelected;
            var save = p.Save();
            var maybeSlot = p.Next();
            if (maybeSlot != null && maybeSlot.Length > 0 && maybeSlot[0] == DollarSign)
            {
                slot = maybeSlot;
                p.TryChar('=');
            }
            else p.Restore(save);

            var fn2 = p.PeekToken();
            if (fn2 != "target") { _errors.Add("select 后面必须是 target(...)，得到：" + fn2); return null; }
            var spec = ParseTargetCall(p);
            return new SelectTargetNode { Target = spec, OutSlot = slot };
        }

        // grant_counter_ability 的形态是 fn(name) { ... }，括号后还跟着块；
        // 其余指令都是 fn(...) 单行。这里统一按「有括号才解析参数」处理。
        bool hasParen = p.TryChar('(');
        if (!hasParen)
        {
            _errors.Add("指令 " + fn + " 缺少 (");
            p.SkipLine();
            return null;
        }
        // 注意：这里**不能**再写一份 grant_counter_ability 的实现。
        // 之前这里有一份内联版，它把触发器硬编码成 PassiveAura，
        // 而 fn switch 里的 ParseGrantCounterAbility 支持内嵌 on 触发头 ——
        // 两份并存时内联版先命中，于是「支持触发头」的那份永远走不到。
        // 唯一真相源：ParseGrantCounterAbility。
        EffectNode node = fn switch
        {
            "damage" or "伤害" => ParseDamage(p),
            "damage_all" or "全体伤害" => ParseDamageAll(p),
            "restore" or "回复" => ParseRestore(p),
            "draw" or "抽牌" => ParseDraw(p),
            "summon" or "召唤" => ParseSummon(p),
            "crest_gain" or "获得纹章" => ParseCrestGain(p),
            "copy_card" or "复制" => ParseCopyCard(p),
            "grant_ability" or "赋予能力" => ParseGrantEntityAbility(p),
            "add_hand" or "加入手牌" => ParseAddHand(p),
            "destroy" or "破坏" => ParseDestroy(p),
            "banish" or "消失" => ParseBanish(p),
            "discard" or "舍弃" => ParseDiscard(p),
            "bounce" or "回手" => ParseBounce(p),
            "to_deck" or "洗回牌组" => ParseMoveTo(p, ZoneRef.Deck, false),
            "to_board" or "移到战场" => ParseMoveTo(p, ZoneRef.Board, false),
            "to_exarea" or "移到额外区" => ParseMoveTo(p, ZoneRef.ExArea, false),
            "buff" or "增益" => ParseBuff(p),
            "set_stats" or "设攻防" => ParseSetStats(p),
            "swap_stats" or "交换攻防" => ParseSwapStats(p),
            "grant_kw" or "给予关键词" => ParseGrantKw(p),
            "remove_kw" or "移除关键词" => ParseRemoveKw(p),
            "silence" or "沉默" => ParseSilence(p),
            "evolve" or "进化" => ParseEvolve(p, false),
            "super_evolve" or "超进化" => ParseEvolve(p, true),
            "gain_counter" or "获得计数" => ParseCounter(p, false),
            "set_counter" or "设计数" => ParseCounter(p, false, CounterOp.Set),
            "fill_counter" or "填计数" => ParseCounter(p, false, CounterOp.Fill),
            "pay_counter" or "消费计数" => ParseCounter(p, true),
            "transform" or "变身" => ParseTransform(p),
            "discover" or "发现" => ParseDiscover(p),
            "grant_counter_ability" or "赋予计数能力" => ParseGrantCounterAbility(p),
            "reanimate" or "亡者召还" => ParseReanimate(p),
            "cost_mod" or "费用" => ParseCostMod(p, CostModOp.Add),
            "cost_set" or "费用设为" => ParseCostMod(p, CostModOp.Set),
            "cost_halve" or "费用减半" => ParseCostMod(p, CostModOp.Halve),
            "invoke" or "发动" => ParseInvoke(p),
            "replace_deck" or "替换牌组" => ParseReplaceDeck(p),
            _ => Unknown(p, fn),
        };

        p.TryChar(')');
        return node;
    }

    private static void SkipToClose(Cursor p)
    {
        int depth = 1;
        while (!p.AtEnd && depth > 0)
        {
            var t = p.PeekChar();
            if (t == '(') depth++;
            else if (t == ')') { depth--; if (depth == 0) return; }
            p.NextChar();
        }
    }

    private EffectNode Unknown(Cursor p, string fn)
    {
        _errors.Add("未知指令：" + fn);
        SkipToClose(p);
        return null;
    }

    private EffectNode ParseDamage(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        var amt = ParseNumExpr(p);
        return new DealDamageNode { Target = tgt, Amount = amt, IsAbility = true };
    }

    private EffectNode ParseDamageAll(Cursor p)
    {
        var amt = ParseNumExpr(p);
        p.TryChar(',');
        var tgt = ParseTargetCall(p);
        return new DealDamageNode { Target = tgt, Amount = amt, IsAbility = true };
    }

    private EffectNode ParseRestore(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        var amt = ParseNumExpr(p);
        return new RestoreLifeNode { Target = tgt, Amount = amt };
    }

    private EffectNode ParseDraw(Cursor p)
    {
        // 张数可以是常量或表达式（count(...) / counter(...)）。
        var n = ParseNumExpr(p);
        return new MoveZoneNode
        {
            Target = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self, Zone = ZoneRef.Deck,
                Mode = SelectMode.First, Count = n,
            },
            ToZone = ZoneRef.Hand,
        };
    }

    private EffectNode ParseSummon(Cursor p)
    {
        string name = p.TryString() ?? p.Next();
        p.TryChar(',');
        var n = ParseNumExpr(p);
        return new SpawnCardNode
        {
            CardId = CardIdOf(name), CardName = name, Count = n, ToZone = ZoneRef.Board,
        };
    }

    /// <summary>copy_card(target(...), N, [exact], to: board/hand)</summary>
    private EffectNode ParseCopyCard(Cursor p)
    {
        var from = ParseTargetOrExpr(p);
        p.TryChar(',');
        var count = ParseNumExpr(p);
        bool exact = false;
        var toZone = ZoneRef.Board;

        while (p.TryChar(','))
        {
            var t = p.Next();
            if (t == "exact") { exact = true; continue; }
            if (t == "to")
            {
                p.TryChar(':');
                toZone = p.Next() == "hand" ? ZoneRef.Hand : ZoneRef.Board;
            }
        }

        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CopyOfTarget,
            CopyFrom = from,
            IsExactCopy = exact,
            Count = count,
            ToZone = toZone,
            ToOwner = ZoneScope.Self,
        };
    }

    /// <summary>grant_ability(target, [dur: X]) { on ... { ... } }</summary>
    private EffectNode ParseGrantEntityAbility(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        var dur = DurationKind.Permanent;
        if (p.TryChar(','))
        {
            if (p.PeekToken() == "dur")
            {
                p.Next();
                p.TryChar(':');
                dur = p.Next() switch
                {
                    "ThisTurn" => DurationKind.ThisTurn,
                    "UntilEndOfOpponentTurn" => DurationKind.UntilEndOfOpponentTurn,
                    _ => DurationKind.Permanent,
                };
            }
        }

        // 参数表右括号必须先吃掉。不吃的话游标停在 ')' 上，
        // 下面 TryChar('{') 恒为 false —— 被授予的能力体永远是空的，
        // 而真正的 { on ... } 留在流里把外层解析器搞崩。
        p.TryChar(')');

        AbilityDef granted = null;
        if (p.TryChar('{'))
        {
            // 块内若以 on 开头，就是一条完整能力（含自己的触发头）；
            // 否则按 PassiveAura 解析动作序列。
            if (p.PeekToken() == "on")
            {
                granted = ParseOnBlock(p);
                // ParseOnBlock 只吃它自己那层的 }，外层 grant_ability 的 } 还在。
                p.TryChar('}');
            }
            else
            {
                // ParseBlockBody 会吃掉闭合的 }，这里绝不能再吃一次：
                // 再吃一次会把**外层能力**的 } 也吞掉，导致后续解析全乱。
                granted = new AbilityDef
                {
                    Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                    Body = ParseBlockBody(p),
                };
            }
        }

        return new GrantAbilityNode
        {
            Target = tgt,
            Granted = granted ?? new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Body = new List<EffectNode>(),
            },
            Duration = dur,
        };
    }

    private EffectNode ParseCrestGain(Cursor p)
    {
        string name = p.TryString() ?? p.Next();
        // 可选第二个参数：给谁贴（self / opponent）。Slaus 是「给对手贴纹章」。
        var owner = ZoneScope.Self;
        if (p.TryChar(','))
        {
            var o = p.Next();
            owner = o switch
            {
                "opponent" or "enemy" or "敌方" => ZoneScope.Opponent,
                "any" or "任意" => ZoneScope.Any,
                _ => ZoneScope.Self,
            };
        }
        return new SpawnCardNode
        {
            IsCrest = true,
            // 纹章是独立实体，不带主人卡号：带了会按主人卡号建出主人卡本身。
            CardId = 0, CardName = name,
            // 纹章落在纹章区，不是战场。
            ToZone = ZoneRef.CrestArea, ToOwner = owner,
        };
    }

    private EffectNode ParseAddHand(Cursor p)
    {
        string name = p.TryString() ?? p.Next();
        p.TryChar(',');
        var n = ParseNumExpr(p);
        return new SpawnCardNode
        {
            CardId = CardIdOf(name), CardName = name, Count = n, ToZone = ZoneRef.Hand,
        };
    }

    // 这四个都必须接受 $slot 简写：打印器导出时会把「本卡的槽位」写成 $self / $trigger，
    // 若解析端只认 target(...)，导出的脚本就再也读不回来 —— 单向 DSL 的典型症状。
    private EffectNode ParseDestroy(Cursor p)
        => new MoveZoneNode { Target = ParseTargetOrExpr(p), ToZone = ZoneRef.Cemetery, IsDestroy = true };

    private EffectNode ParseBanish(Cursor p)
        => new MoveZoneNode { Target = ParseTargetOrExpr(p), ToZone = ZoneRef.Banished };

    private EffectNode ParseDiscard(Cursor p)
        => new MoveZoneNode { Target = ParseTargetOrExpr(p), ToZone = ZoneRef.Cemetery };

    private EffectNode ParseBounce(Cursor p)
        => new MoveZoneNode { Target = ParseTargetOrExpr(p), ToZone = ZoneRef.Hand };

    private EffectNode ParseMoveTo(Cursor p, ZoneRef to, bool destroy)
        => new MoveZoneNode { Target = ParseTargetOrExpr(p), ToZone = to, IsDestroy = destroy };

    private EffectNode ParseBuff(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        // 必须用「遇到 / 就停」的解析器：buff(目标, A/B) 里的 / 是**分段符**，
        // 不是除法。用会吃除法的 ParseNumExpr 会把 A/B 解析成一次除法，
        // 于是 "+2/+2" 变成 "+2 除以 2 / 0"。
        var a = ParseNumStopAtSlash(p);
        p.TryChar('/');
        var l = ParseNumStopAtSlash(p);
        var dur = TryParseDur(p);
        return new ModifyStatsNode { Target = tgt, Op = StatModOp.Add, Attack = a, Life = l, Duration = dur };
    }

    /// <summary>读一个数值；na 表示「这一项不动」，还原成 null。</summary>
    private NumExpr ParseNumOrNa(Cursor p)
    {
        if (p.PeekToken() == "na") { p.Next(); return null; }
        return ParseNumExpr(p);
    }

    private EffectNode ParseSetStats(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        var a = ParseNumStopAtSlash(p);
        p.TryChar('/');
        var l = ParseNumStopAtSlash(p);
        var dur = TryParseDur(p);
        return new ModifyStatsNode { Target = tgt, Op = StatModOp.Set, Attack = a, Life = l, Duration = dur };
    }

    /// <summary>
    /// 只读「一个完整数值」，遇到段分隔符 / 就停。
    ///
    /// set_stats 的写法是 set_stats(t, A/B)，A 与 B 各自是完整表达式。
    /// 若用 ParseNumExpr 读 A，它会把 /B 当成除法吃掉，
    /// 于是「攻击力翻倍 / 生命值翻倍」变成「攻击力翻倍除以生命值翻倍」。
    /// </summary>
    private NumExpr ParseNumStopAtSlash(Cursor p)
    {
        if (p.PeekToken() == "na") { p.Next(); return null; }
        var v = ParseNumNoDiv(p);
        return v;
    }

    private NumExpr ParseNumNoDiv(Cursor p)
    {
        var t = p.PeekToken();
        if (t == null) return NumExpr.Const(0);
        if (t == "(")
        {
            p.Next();
            var inner = ParseNumNoDiv(p);
            p.TryChar(')');
            return ParseArithNoDiv(p, inner);
        }
        if (t == "counter")
        {
            p.Next();
            if (p.TryChar('(')) { var nm = p.Next(); p.TryChar(')'); return ParseArithNoDiv(p, NumExpr.Ctr(_schema.Counter(nm))); }
            return NumExpr.Const(0);
        }
        // set_stats 也会用 count(...)（如「攻防变为手牌数」）——
        // 缺这一支会让它静默解析成 0，而卡面中文完全正常。
        if (t == "count" || t == "maxgroup")
        {
            bool isMax = t == "maxgroup";
            p.Next();
            p.TryChar('(');
            var cspec = ParseTargetOrExpr(p);
            p.TryChar(')');
            return ParseArithNoDiv(p, isMax
                ? new NumExpr { Op = NumOp.MaxGroupSize, Of = cspec }
                : NumExpr.CountOf(cspec));
        }

        if (t == "prop")
        {
            p.Next();
            if (p.TryChar('('))
            {
                var nm = p.Next();
                var key = nm switch
                {
                    "attack" => StatKey.Attack,
                    "life" or "defense" => StatKey.Life,
                    "maxlife" => StatKey.MaxLife,
                    "cost" => StatKey.Cost,
                    "basecost" => StatKey.BaseCost,
                    "baseattack" => StatKey.BaseAttack,
                    "baselife" => StatKey.BaseLife,
                    _ => StatKey.Attack,
                };
                // 与 ParseNumExpr 保持一致：Of 必须读，缺省是 $item（set_stats 里筛的是被改的那个）。
                TargetSpec of = TargetSpec.Item;
                if (p.TryChar(',')) of = ParseTargetOrExpr(p);
                p.TryChar(')');
                return ParseArithNoDiv(p, NumExpr.Prop(key, of));
            }
            return NumExpr.Const(0);
        }
        int? lit = p.TryInt();
        return ParseArithNoDiv(p, lit.HasValue ? NumExpr.Const(lit.Value) : NumExpr.Const(0));
    }

    /// <summary>接续算术，但**不消费 /**（/ 是 set_stats 的段分隔符）。</summary>
    private NumExpr ParseArithNoDiv(Cursor p, NumExpr left)
    {
        while (true)
        {
            var opTok = p.PeekToken();
            if (opTok == "+") { p.Next(); left = NumExpr.Add(left, ParseNumNoDiv(p)); continue; }
            if (opTok == "-") { p.Next(); left = NumExpr.Sub(left, ParseNumNoDiv(p)); continue; }
            if (opTok == "*") { p.Next(); left = NumExpr.Mul(left, ParseNumNoDiv(p)); continue; }
            break;
        }
        return left;
    }

    private EffectNode ParseSwapStats(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        return new ModifyStatsNode { Target = tgt, Op = StatModOp.Swap };
    }

    private EffectNode ParseGrantKw(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        string kw = p.Next();

        // 可选的值参数。这里必须 save/restore：如果写的是 ", dur: ThisTurn"
        // 而不是 ", 2"，无条件 TryChar(',') 会把 dur 前面的逗号偷吃掉，
        // 后面的 TryParseDur 就再也找不到它了。
        int? val = null;
        var saveVal = p.Save();
        if (p.TryChar(','))
        {
            val = p.TryInt();
            if (!val.HasValue) p.Restore(saveVal);
        }

        // Duration 必须读：限时与永久在 AST 上只差这一个字段。
        var dur = TryParseDur(p);
        return new ModifyKeywordNode
        {
            Target = tgt, Op = KeywordOp.Grant, Keyword = _schema.Keyword(kw),
            Value = val.HasValue ? NumExpr.Const(val.Value) : null,
            Duration = dur,
        };
    }

    private EffectNode ParseRemoveKw(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        string kw = p.Next();
        return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Remove, Keyword = _schema.Keyword(kw) };
    }

    private EffectNode ParseSilence(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.SilenceAll };
    }

    private EffectNode ParseEvolve(Cursor p, bool super)
    {
        var tgt = ParseTargetOrExpr(p);
        return new EvolveNode { Target = tgt, IsSuperEvolve = super };
    }

    private EffectNode ParseCounter(Cursor p, bool negative, CounterOp op = CounterOp.Add)
    {
        string name = p.Next();
        p.TryChar(',');
        var amt = ParseNumExpr(p);

        TargetSpec of = null;
        if (p.TryChar(','))
        {
            if (p.PeekToken() == "of")
            {
                p.Next();
                p.TryChar(':');
                of = ParseTargetOrExpr(p);
            }
        }

        if (negative) amt = NumExpr.Sub(NumExpr.Const(0), amt);
        return new ModifyCounterNode
        {
            Target = of,
            Owner = ZoneScope.Self,
            Counter = _schema.Counter(name),
            Op = op,
            Delta = amt,
        };
    }

    private EffectNode ParseTransform(Cursor p)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        string name = p.TryString() ?? p.Next();
        return new TransformNode { Target = tgt, IntoCardId = CardIdOf(name), IntoCardName = name };
    }

    private EffectNode ParseGrantCounterAbility(Cursor p)
    {
        // 调用点已经吃掉了 '('（通用分支统一处理），这里读名字后要补吃 ')'。
        string name = p.Next();
        p.TryChar(')');

        // 紧随其后的 { ... } 是被赋予的能力体。
        // 块内若以 on 开头，就是一条带自己触发头的完整能力 ——
        // 硬编码成 PassiveAura 会让「每当友方随从进化，打对手 1 点」永不触发。
        AbilityDef inner;
        if (p.TryChar('{'))
        {
            if (p.PeekToken() == "on")
            {
                inner = ParseOnBlock(p);
                p.TryChar('}');                       // ParseOnBlock 只吃自己那层
            }
            else
            {
                // ParseBlockBody 已吃掉闭合的 }，不要再吃一次。
                inner = new AbilityDef
                {
                    Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                    Body = ParseBlockBody(p),
                };
            }
        }
        else
        {
            inner = new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Body = new List<EffectNode>(),
            };
        }

        return new GrantCounterAbilityNode
        {
            Counter = _schema.Counter(name),
            Granted = inner,
        };
    }

    private EffectNode ParseCostMod(Cursor p, CostModOp op)
    {
        var tgt = ParseTargetOrExpr(p);
        p.TryChar(',');
        // 打印端对 cost_halve 也输出了数值位（恒 0），所以这里一律先读数值，
        // 否则那个 0 会留在括号里，右括号匹配不上。
        var val = ParseNumExpr(p);
        var dur = TryParseDur(p);
        return new ModifyCostNode
        {
            Target = tgt, Op = op,
            Value = op == CostModOp.Halve ? NumExpr.Const(0) : val,
            Duration = dur,
        };
    }

    private EffectNode ParseInvoke(Cursor p)
    {
        var ev = EventOf(p.Next());
        TargetSpec tgt = TargetSpec.Self;
        if (p.TryChar(',')) tgt = ParseTargetOrExpr(p);
        return new InvokeAbilityNode { Event = ev, Target = tgt };
    }

    private EffectNode ParseReplaceDeck(Cursor p)
    {
        string name = p.TryString() ?? p.Next();
        return new ReplaceDeckNode { DeckName = name, Owner = ZoneScope.Self };
    }

    private EffectNode ParseReanimate(Cursor p)
        // 唯一标准构造：手写会漏字段，漏了就是召出 CardId=0 的白壳。
        => ReanimateHelper.Build(ParseNumExpr(p));
    // ---------------------------------------------------------------- target(...)

    /// <summary>
    /// target(enemy, board, follower, count: 1, trait: Mecha, random: 1, other: true)
    /// </summary>
    private TargetSpec ParseTargetCall(Cursor p)
    {
        var fn = p.PeekToken();
        if (fn != "target")
        {
            _errors.Add("期望 target(...)，得到：" + fn);
            return TargetSpec.Self;
        }
        p.Next();                       // 吃掉 target
        return ParseTargetArgs(p);
    }

    /// <summary>
    /// 只解析 target(...) 的**参数表**（调用时 target 已被读走）。
    /// count(...) 与 select 都复用这里 —— 它们没有 target 前缀，
    /// 若共用一个「连 target 一起读」的函数，参数表就会整体错位一格。
    /// </summary>
    /// <summary>解析「已经在括号内」的参数表（调用方已消费外层括号）。</summary>
    private TargetSpec ParseTargetArgsBare(Cursor p) => ParseTargetArgsCore(p);

    private TargetSpec ParseTargetArgs(Cursor p)
    {
        if (!p.TryChar('(')) return TargetSpec.Self;
        return ParseTargetArgsCore(p);
    }

    /// <summary>参数表核心（调用时左括号已被消费）。</summary>
    private TargetSpec ParseTargetArgsCore(Cursor p)
    {

        var owner = ZoneScope.Self;
        var zone = ZoneRef.Board;
        var mode = SelectMode.All;
        int count = 1;
        bool countSet = false;
        var wheres = new List<BoolExpr>();
        bool other = false;
        bool includeLeader = false;
        bool duplicates = false;
        bool distinct = false;
        bool sortDesc = false;
        int topn = 0;
        var sort = Core.SortKey.None;
        var history = HistoryScope.None;

        while (!p.AtEnd && !p.TryChar(')'))
        {
            var t = p.Next();
            if (t == null || t == ",") continue;

            switch (t)
            {
                case "self" or "ally" or "己方" or "我方": owner = ZoneScope.Self; break;
                case "enemy" or "opponent" or "敌方": owner = ZoneScope.Opponent; break;
                case "any" or "任意": owner = ZoneScope.Any; break;

                case "board" or "field" or "战场": zone = ZoneRef.Board; break;
                case "hand" or "手牌": zone = ZoneRef.Hand; break;
                case "deck" or "牌组": zone = ZoneRef.Deck; break;
                case "cemetery" or "墓场": zone = ZoneRef.Cemetery; break;
                // 外部池：发现类效果的目标区域。
                case "card_pool": zone = ZoneRef.CardPool; break;
                case "global_pool": zone = ZoneRef.GlobalPool; break;
                case "banished" or "消失区": zone = ZoneRef.Banished; break;
                case "leader" or "主战者": zone = ZoneRef.Leader; break;
                case "exarea" or "额外区": zone = ZoneRef.ExArea; break;
                case "crest" or "纹章区": zone = ZoneRef.CrestArea; break;

                case "follower" or "随从": wheres.Add(new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item }); break;
                case "spell" or "法术": wheres.Add(new HasKindExpr { Kind = CardKind.Spell, Of = TargetSpec.Item }); break;
                case "amulet" or "护符": wheres.Add(new HasKindExpr { Kind = CardKind.Amulet, Of = TargetSpec.Item }); break;

                case "other" or "another" or "其他": other = true; break;
                case "all" or "全部": mode = SelectMode.All; break;

                case "all_n":
                    p.TryChar(':');
                    count = p.TryInt() ?? 1;
                    countSet = true;
                    mode = SelectMode.All;
                    break;

                case "first":
                    p.TryChar(':');
                    count = p.TryInt() ?? 1;
                    countSet = true;
                    mode = SelectMode.First;
                    break;

                case "include_leader": includeLeader = true; break;
                case "duplicates": duplicates = true; break;
                case "distinct": distinct = true; break;

                case "sort":
                    p.TryChar(':');
                    sort = Enum.TryParse<Core.SortKey>(p.Next(), true, out var sk3) ? sk3 : Core.SortKey.None;
                    if (p.PeekToken() == "desc") { p.Next(); sortDesc = true; }
                    break;

                case "topn":
                    p.TryChar(':');
                    topn = p.TryInt() ?? 0;
                    break;

                case "history":
                    p.TryChar(':');
                    history = p.Next() switch
                    {
                        "DestroyedThisMatch" => HistoryScope.DestroyedThisMatch,
                        "DestroyedThisTurn" => HistoryScope.DestroyedThisTurn,
                        "EnteredThisMatch" => HistoryScope.EnteredThisMatch,
                        _ => HistoryScope.None,
                    };
                    break;

                case "count":
                    p.TryChar(':');
                    count = p.TryInt() ?? 1;
                    countSet = true;
                    mode = SelectMode.Choose;
                    break;

                case "random":
                    p.TryChar(':');
                    count = p.TryInt() ?? 1;
                    countSet = true;
                    mode = SelectMode.Random;
                    break;

                case "where":
                {
                    // where 里省略 Of 时判的是「被筛的那一个」（$item）。
                    var savedCtx = DefaultContextSelf;
                    DefaultContextSelf = TargetSpec.Item;
                    p.TryChar(':');
                    var le = ParseNumExpr(p);
                    var opTok = p.PeekToken();
                    if (opTok == ">=" || opTok == "<=" || opTok == "==" || opTok == ">" || opTok == "<")
                    {
                        p.Next();
                        var re = ParseNumExpr(p);
                        wheres.Add(new CompareExpr
                        {
                            Left = le,
                            Op = opTok switch
                            {
                                ">=" => CompareOp.Ge, "<=" => CompareOp.Le,
                                "==" => CompareOp.Eq, ">" => CompareOp.Gt,
                                _ => CompareOp.Lt,
                            },
                            Right = re,
                        });
                    }
                    DefaultContextSelf = savedCtx;
                    break;
                }

                case "trait":
                    p.TryChar(':');
                    wheres.Add(new HasTraitExpr { Trait = _schema.Trait(p.Next()), Of = TargetSpec.Item });
                    break;

                case "max_cost":
                    p.TryChar(':');
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                        Op = CompareOp.Le,
                        Right = NumExpr.Const(p.TryInt() ?? 0),
                    });
                    break;

                // 最小费用：与 max_cost 合起来就是任意区间。
                case "min_cost":
                    p.TryChar(':');
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(p.TryInt() ?? 0),
                    });
                    break;

                // 职业筛选：只从某个职业的卡里发现。
                case "clan":
                    p.TryChar(':');
                    wheres.Add(ParseClanFilter(p));
                    break;

                // 卡包筛选：只从某个特定卡包发现（区别于 card_pool / global_pool 的范围）。
                case "pack":
                    p.TryChar(':');
                    // 卡包 ID 常带连字符（core-wb），且允许用 '|' 串联多个包。
                    wheres.Add(new HasPackExpr { PackId = ReadDashedPackList(p), Of = TargetSpec.Item });
                    break;

                default: break;
            }
        }

        if (other)
            wheres.Add(new NotSameEntityExpr
            {
                ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf,
                Of = TargetSpec.Item,
            });

        BoolExpr where = wheres.Count == 0 ? BoolExpr.True
            : wheres.Count == 1 ? wheres[0]
            : new AndExpr(wheres);

        return new TargetSpec
        {
            SourceKind = TargetSourceKind.Zone,
            Owner = owner,
            Zone = zone,
            Mode = mode,
            // All 模式没有「个数」概念，Count 必须留 null。
            // 填 Const(1) 会让「所有随从」变成「1 个随从」—— 卡面文字看不出差别，
            // 但运行时会少打一大片。
            Count = mode == SelectMode.All ? null
                   : countSet ? NumExpr.Const(count) : NumExpr.Const(1),
            Where = where,
            IncludeLeader = includeLeader,
            DuplicatesOnly = duplicates,
            DistinctByName = distinct,
            Sort = sort,
            SortDescending = sortDesc,
            TopN = topn,
            History = history,
        };
    }

    /// <summary>参数位既可能是 target(...)，也可能是 $slot 简写。</summary>
    private TargetSpec ParseTargetOrExpr(Cursor p)
    {
        var t = p.PeekToken();
        if (t == "target") return ParseTargetCall(p);

        // 裸参数表（count(ally, board, follower) / maxgroup(...) 的常见形态）：
        // 这些词本来就是 target(...) 的合法开头，直接按参数表解析。
        if (t == "ally" || t == "enemy" || t == "any" || t == "self"
            || t == "board" || t == "hand" || t == "deck" || t == "leader"
            || t == "follower" || t == "spell" || t == "amulet")
        {
            if (t == "self" && p.PeekAhead(1) != "board" && p.PeekAhead(1) != "hand"
                && p.PeekAhead(1) != "deck" && p.PeekAhead(1) != "leader")
            {
                p.Next();
                return TargetSpec.Self;
            }
            return ParseTargetArgsBare(p);
        }

        if (t == "card")
        {
            p.Next();
            if (!p.TryChar('(')) return TargetSpec.Self;
            string name = p.TryString() ?? p.Next();
            p.TryChar(',');
            // 这里的 ( 已经被上一步吃掉，内层参数表要按「已在括号内」解析。
            var inner = ParseTargetArgsBare(p);
            p.TryChar(')');
            return new TargetSpec
            {
                SourceKind = TargetSourceKind.CardId,
                // 卡号必须填：VmEval 的历史池分支写的是 `t.CardId == 0 || sn.CardId == t.CardId`，
                // CardId==0 会让「同名卡 5 张」退化成「任意随从 5 张」——
                // 条件照样成立，但成立的范围被悄悄放大了。
                CardId = CardIdOf(name),
                CardName = name,
                Owner = inner.Owner, Zone = inner.Zone, Mode = inner.Mode,
                Count = inner.Count, Where = inner.Where, History = inner.History,
                IncludeLeader = inner.IncludeLeader, DuplicatesOnly = inner.DuplicatesOnly,
                // 排序与去重也必须拷：具名卡池同样可能带「基础费用最高的 1 个」。
                Sort = inner.Sort, SortDescending = inner.SortDescending,
                TopN = inner.TopN, DistinctByName = inner.DistinctByName,
            };
        }
        if (t != null && t.Length > 0 && t[0] == DollarSign)
        {
            p.Next();
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = t };
        }
        if (t == "self" || t == "本卡")
        {
            p.Next();
            return TargetSpec.Self;
        }
        return ParseTargetCall(p);
    }

    // ---------------------------------------------------------------- 数值表达式

    private NumExpr ParseNumExpr(Cursor p)
    {
        var t = p.PeekToken();
        if (t == null) return NumExpr.Const(0);

        // 括号包裹的子表达式：打印器给复合算术加括号，解析端必须能读回来。
        if (t == "(")
        {
            p.Next();
            var inner = ParseNumExpr(p);
            p.TryChar(')');
            return ParseArithTail(p, inner);
        }

        if (t == "counter")
        {
            p.Next();
            if (p.TryChar('('))
            {
                string nm = p.Next();
                p.TryChar(')');
                // 必须接上算术尾巴：否则 counter(combo) + 1 里的 "+ 1" 留在流里，
                // 后面括号配不上，整句解析失败。
                return ParseArithTail(p, NumExpr.Ctr(_schema.Counter(nm)));
            }
            return NumExpr.Const(0);
        }

        if (t == "count" || t == "maxgroup")
        {
            bool isMax = t == "maxgroup";
            p.Next();
            p.TryChar('(');
            var spec = ParseTargetOrExpr(p);
            p.TryChar(')');
            // 同上：count(ally, hand) * 2 的 "* 2" 必须被吃掉。
            return ParseArithTail(p, isMax
                ? new NumExpr { Op = NumOp.MaxGroupSize, Of = spec }
                : NumExpr.CountOf(spec));
        }

        // 注意：这里**不能**再写一个 if (t == "maxgroup") 分支。
        // 上面那条已经把 maxgroup 拦掉了，这一份永远走不到 ——
        // 死代码比缺代码更误导，读的人会以为它才是真正生效的那条。

        if (t == "prop")
        {
            p.Next();
            if (p.TryChar('('))
            {
                string nm = p.Next();
                var key = nm switch
                {
                    "attack" => StatKey.Attack,
                    "life" or "defense" => StatKey.Life,
                    "maxlife" => StatKey.MaxLife,
                    "cost" => StatKey.Cost,
                    "basecost" => StatKey.BaseCost,
                    "baseattack" => StatKey.BaseAttack,
                    "baselife" => StatKey.BaseLife,
                    _ => StatKey.Attack,
                };
                TargetSpec of = DefaultContextSelf;
                if (p.TryChar(',')) of = ParseTargetOrExpr(p);
                p.TryChar(')');
                return ParseArithTail(p, NumExpr.Prop(key, of));
            }
            return NumExpr.Const(0);
        }

        int? lit = p.TryInt();
        NumExpr left = lit.HasValue ? NumExpr.Const(lit.Value) : NumExpr.Const(0);

        // 左结合地吃掉后续运算符：prop(attack) * 2 这类复合式必须能读回来，
        // 否则「攻击力翻倍」在往返后会退化成 0。
        while (true)
        {
            var opTok = p.PeekToken();
            if (opTok == "+") { p.Next(); left = NumExpr.Add(left, ParseNumExpr(p)); continue; }
            if (opTok == "-") { p.Next(); left = NumExpr.Sub(left, ParseNumExpr(p)); continue; }
            if (opTok == "*") { p.Next(); left = NumExpr.Mul(left, ParseNumExpr(p)); continue; }
            if (opTok == "/") { p.Next(); left = NumExpr.Div(left, ParseNumExpr(p)); continue; }
            break;
        }
        return left;
    }

    /// <summary>
    /// 条件表达式：先解析一个比较，再看后面是否跟 and / or 继续串。
    /// 恒真项（1 &gt;= 1）在解析阶段就丢掉 —— 打印器本来就不该输出它们，
    /// 但外部脚本可能写出来，这里做一次归一化。
    /// </summary>
    /// <summary>接续算术尾巴：把 left 后面的 + - * / 一路吃掉。</summary>
    private NumExpr ParseArithTail(Cursor p, NumExpr left)
    {
        while (true)
        {
            var opTok = p.PeekToken();
            if (opTok == "+") { p.Next(); left = NumExpr.Add(left, ParseNumExpr(p)); continue; }
            if (opTok == "-") { p.Next(); left = NumExpr.Sub(left, ParseNumExpr(p)); continue; }
            if (opTok == "*") { p.Next(); left = NumExpr.Mul(left, ParseNumExpr(p)); continue; }
            if (opTok == "/") { p.Next(); left = NumExpr.Div(left, ParseNumExpr(p)); continue; }
            break;
        }
        return left;
    }

    private BoolExpr ParseBoolExpr(Cursor p)
    {
        var items = new List<BoolExpr>();
        var ops = new List<string>();

        while (true)
        {
            var one = ParseOneCompare(p);
            if (one is CompareExpr cc && cc.Left?.Op == NumOp.Const && cc.Right?.Op == NumOp.Const
                && cc.Op == CompareOp.Ge && cc.Left.Value == 1 && cc.Right.Value == 1)
            {
                // 恒真：跳过
            }
            else items.Add(one);

            var nxt = p.PeekToken();
            if (nxt == "and" || nxt == "&&") { p.Next(); ops.Add("and"); continue; }
            if (nxt == "or" || nxt == "||") { p.Next(); ops.Add("or"); continue; }
            break;
        }

        if (items.Count == 0) return BoolExpr.True;
        if (items.Count == 1) return items[0];

        // 混合 and/or 时按 or 分组（and 优先级更高）
        if (ops.All(o => o == "and")) return new AndExpr(items);
        if (ops.All(o => o == "or")) return new OrExpr(items);
        return new AndExpr(items);
    }

    private BoolExpr ParseOneCompare(Cursor p)
    {
        // 括号包起来的子条件（可选）
        if (p.TryChar('('))
        {
            var inner = ParseBoolExpr(p);
            p.TryChar(')');
            return inner;
        }

        // has_state(X) —— 实体状态条件
        if (p.PeekToken() == "has_state")
        {
            p.Next();
            if (p.TryChar('('))
            {
                var stName = p.Next();
                var st = Enum.TryParse<UnitState>(stName, true, out var parsed) ? parsed : UnitState.Damaged;
                // 第二个参数是可选的 Of；省略时按上下文取缺省（能力级 = $self）。
                TargetSpec of = DefaultContextSelf;
                if (p.TryChar(',')) of = ParseTargetOrExpr(p);
                p.TryChar(')');
                return new HasStateExpr { State = st, Of = of };
            }
            return BoolExpr.True;
        }

        var left = ParseNumExpr(p);
        var t = p.PeekToken();
        if (t != null && (t == ">=" || t == "<=" || t == "==" || t == ">" || t == "<"))
        {
            p.Next();
            var right = ParseNumExpr(p);
            var op = t switch
            {
                ">=" => CompareOp.Ge,
                "<=" => CompareOp.Le,
                "==" => CompareOp.Eq,
                ">" => CompareOp.Gt,
                _ => CompareOp.Lt,
            };
            return new CompareExpr { Left = left, Op = op, Right = right };
        }
        return new CompareExpr { Left = left, Op = CompareOp.Ge, Right = NumExpr.Const(1) };
    }
    // ================================================================ 打印

    /// <summary>
    /// 把 CardDef 反向导出为 ReshadowScript。
    ///
    /// 要求：Parse(Print(d)) 与原 d 结构等价。
    /// 遇到不认识的节点**必须报错**，不能静默跳过 ——
    /// 跳过等于悄悄丢效果，而用户以为导出成功了。
    /// </summary>
    public string Print(CardDef card)
    {
        _errors.Clear();
        if (card == null) return "";
        var sb = new StringBuilder();

        var traits = (card.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(t => t.Name).ToList();
        sb.Append("card ").Append(card.CardId).Append(" \"").Append(card.Name).Append("\"");
        sb.Append(" (").Append(card.Kind.ToString());
        sb.Append(", cost: ").Append(card.BaseCost);
        sb.Append(", atk: ").Append(card.BaseAttack);
        sb.Append(", life: ").Append(card.BaseLife);
        if (card.Clan.IsValid) sb.Append(", clan: ").Append(card.Clan.Name);
        if (traits.Count > 0) sb.Append(", traits: [").Append(string.Join(", ", traits)).Append(']');
        sb.Append(") {\n");

        // 构筑期修正声明必须写出来：不写的话重新导入会全部丢失，
        // 而卡面中文看起来一模一样（中文渲染读的是 DeckModifiers 本身）。
        if (card.DeckModifiers != null)
        {
            foreach (var dm in card.DeckModifiers)
            {
                if (dm == null) continue;
                sb.Append("    ").Append(DeckModWord(dm)).Append('\n');
            }
        }

        var kws = new List<string>();
        foreach (var ab in card.Abilities ?? Array.Empty<AbilityDef>())
        {
            if (!IsBareKeywordAbility(ab)) continue;
            var mk = (ModifyKeywordNode)ab.Body[0];
            kws.Add(mk.Value != null && mk.Value.Op == NumOp.Const
                ? mk.Keyword.Name + "(" + mk.Value.Value + ")"
                : mk.Keyword.Name);
        }
        if (card.HasFuse)
        {
            sb.Append("    fuse: ");
            sb.Append(card.FuseMaterialFilter == null
                ? "target(ally, board, all)"
                : "target(" + PrintTargetInParens(card.FuseMaterialFilter) + ")");
            sb.Append('\n');
        }

        if (kws.Count > 0)
            sb.Append("    keywords: [")
              .Append(string.Join(", ", kws.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
              .Append("]\n");

        foreach (var ab in card.Abilities ?? Array.Empty<AbilityDef>())
        {
            // 裸关键词已在 keywords: 行写过，这里跳过；
            // 但带代价的常驻（[Countdown(8)] 这类）必须打印，否则纹章/护符的
            // 吟唱值在往返中丢失，卡牌进场后永远不会自己消失。
            if (IsBareKeywordAbility(ab)) continue;
            sb.Append(PrintAbility(ab, 1));
        }

        if (card.AccelerateDef != null)
        {
            sb.Append("    accelerate(").Append(card.AccelerateDef.BaseCost).Append(") {\n");
            foreach (var ab in card.AccelerateDef.Abilities)
            {
                // 不能跳过 PassiveAura：子卡的常驻项正是它 ——
                //   · 结晶的初始吟唱 Countdown (3)
                //   · 信仰的「初始值为 0」
                // 跳过它们，结晶护符进场后永不消失，信仰也没有初值。
                if (IsBareKeywordAbility(ab)) continue;
                sb.Append(PrintAbility(ab, 2));
            }
            sb.Append("    }\n");
        }
        if (card.CrystallizeDef != null)
        {
            sb.Append("    crystallize(").Append(card.CrystallizeDef.BaseCost).Append(") {\n");
            foreach (var ab in card.CrystallizeDef.Abilities)
            {
                // 不能跳过 PassiveAura：子卡的常驻项正是它 ——
                //   · 结晶的初始吟唱 Countdown (3)
                //   · 信仰的「初始值为 0」
                // 跳过它们，结晶护符进场后永不消失，信仰也没有初值。
                if (IsBareKeywordAbility(ab)) continue;
                sb.Append(PrintAbility(ab, 2));
            }
            sb.Append("    }\n");
        }
        if (card.CrestDef != null)
        {
            sb.Append("    crest {\n");
            foreach (var ab in card.CrestDef.Abilities)
            {
                // 不能跳过 PassiveAura：子卡的常驻项正是它 ——
                //   · 结晶的初始吟唱 Countdown (3)
                //   · 信仰的「初始值为 0」
                // 跳过它们，结晶护符进场后永不消失，信仰也没有初值。
                if (IsBareKeywordAbility(ab)) continue;
                sb.Append(PrintAbility(ab, 2));
            }
            sb.Append("    }\n");
        }
        if (card.FaithDef != null)
        {
            sb.Append("    faith {\n");
            foreach (var ab in card.FaithDef.Abilities)
            {
                // 不能跳过 PassiveAura：子卡的常驻项正是它 ——
                //   · 结晶的初始吟唱 Countdown (3)
                //   · 信仰的「初始值为 0」
                // 跳过它们，结晶护符进场后永不消失，信仰也没有初值。
                if (IsBareKeywordAbility(ab)) continue;
                sb.Append(PrintAbility(ab, 2));
            }
            sb.Append("    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private string PrintAbility(AbilityDef ab, int depth)
    {
        var ind = Indent(depth);
        var sb = new StringBuilder();
        sb.Append(ind).Append("on ").Append(EventWord(ab.Trigger?.Event ?? CoreEvent.OnPlay));

        if (ab.Trigger != null)
        {
            // 主体过滤：`on enter on(...)` 里的「谁进入」必须写出来，
            // 否则「每当某个随从进入」会被导出成「本卡进入」，语义完全变了。
            // 主体过滤：槽位型（$self/$trigger）与区域型要分开写，
            // 否则 on($self) 会被导出成 on(ally, board)，回来就成了「所有友方随从」。
            if (ab.Trigger.SubjectFilter != null)
            {
                var sf = ab.Trigger.SubjectFilter;
                string inner = sf.SourceKind == TargetSourceKind.Slot
                    ? sf.Slot
                    : "target(" + PrintTargetArgs(sf) + ")";
                sb.Append(" on(").Append(inner).Append(')');
            }

            if (ab.Trigger.ManualEvolveOnly) sb.Append(" manual");
            if (ab.Trigger.ActiveZone != ActiveZoneScope.Board)
                sb.Append(" zone: ").Append(ab.Trigger.ActiveZone.ToString().ToLowerInvariant());
            if (ab.Trigger.DuringTurn != TurnOwnerScope.Any)
                sb.Append(" turn: ").Append(ab.Trigger.DuringTurn);
            if (ab.Trigger.SecondaryEvent != null)
                sb.Append(" or ").Append(EventWord(ab.Trigger.SecondaryEvent.Value));
            if (ab.Trigger.MaxTriggersPerTurn > 0)
                sb.Append(" max: ").Append(ab.Trigger.MaxTriggersPerTurn);
        }

        // <ev>/<sev> 段自动附带的「已进化 / 已超进化」域条件由触发头表达，
        // 打出来就是 `if (true and is Evolved)` 这种废话 —— 剥掉。
        // 其余条件（ctr:x >= 6 等）必须原样保留。
        var domain = TriggerDomain.Of(ab.Trigger);
        var condOut = SimplifyBool(StripDomain(StripTrue(ab.Condition), domain));
        if (Environment.GetEnvironmentVariable("DIY_DBG") == "1")
            Console.WriteLine("[dbg] cond in=" + (ab.Condition==null?"null":ab.Condition.GetType().Name)
                + " out=" + (condOut==null?"null":condOut.GetType().Name)
                + " printed=" + (condOut==null?"-":PrintBoolExpr(condOut)));
        if (condOut != null)
            sb.Append(" if (").Append(PrintBoolExpr(condOut)).Append(')');

        // 代价（连击 / 唤灵 / 土之秘术 / 爆能强化）必须原样写回，
        // 丢了它，「连击3」就退化成「无条件」，卡面与行为同时错。
        if (ab.Costs != null && ab.Costs.Count > 0)
            sb.Append(" costs: [")
              .Append(string.Join(", ", ab.Costs.Select(PrintCost)))
              .Append(']');

        sb.Append(" {\n");
        foreach (var n in ab.Body ?? Array.Empty<EffectNode>())
            sb.Append(PrintNode(n, depth + 1));
        sb.Append(ind).Append("}\n");
        return sb.ToString();
    }

    /// <summary>触发头本身已经隐含的状态域（Evolve: / Super-Evolve: 各自的「已进化」）。</summary>
    private static class TriggerDomain
    {
        public static Core.UnitState? Of(Ast.TriggerSpec t)
        {
            if (t == null) return null;
            if (t.Event == CoreEvent.OnSuperEvolve) return Core.UnitState.SuperEvolved;
            if (t.Event == CoreEvent.OnEvolve) return Core.UnitState.Evolved;
            return null;
        }
    }

    /// <summary>剥掉条件里的恒真项（true and X → X）。</summary>
    private static BoolExpr StripTrue(BoolExpr e)
    {
        if (e == null) return null;
        if (e is AndExpr a)
        {
            var kept = a.Items.Where(x => !IsTriviallyTrue(x)).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return new AndExpr(kept);
        }
        return e;
    }

    /// <summary>剥掉与该触发域等价的状态条件。</summary>
    private static BoolExpr StripDomain(BoolExpr e, Core.UnitState? domain)
    {
        if (e == null || domain == null) return e;
        if (e is AndExpr a)
        {
            var kept = a.Items
                .Where(x => !(x is HasStateExpr hs && hs.State == domain.Value))
                .ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return new AndExpr(kept);
        }
        if (e is HasStateExpr h2 && h2.State == domain.Value) return null;
        return e;
    }

    private static string PrintCost(CostSpec c)
    {
        // Label 优先：UsePp 既可能是爆能强化也可能是启动，Kind 分不出来。
        if (!string.IsNullOrEmpty(c.Label))
        {
            int amt0 = c.Amount != null && c.Amount.Op == NumOp.Const ? c.Amount.Value : 0;
            return c.Label.ToLowerInvariant().Replace(' ', '_') + "(" + amt0 + ")";
        }
        string kind = c.Kind switch
        {
            CostKind.UsePp => "pp",
            CostKind.Necromance => "necromancy",
            CostKind.DestroyAlly => "earth_rite",
            CostKind.TurnPlayCount => "combo",
            CostKind.MatchPlayCount => "rally",
            CostKind.Countdown => "countdown",
            _ => "pp",
        };
        int amt = c.Amount != null && c.Amount.Op == NumOp.Const ? c.Amount.Value : 0;
        return kind + "(" + amt + ")";
    }

    private string PrintNode(EffectNode n, int depth)
    {
        var ind = Indent(depth);
        switch (n)
        {
            case SelectTargetNode st:
                return ind + "select " + st.OutSlot + " = target(" + PrintTargetArgs(st.Target) + ")\n";

            case DealDamageNode d:
                return ind + "damage(" + PrintTargetExpr(d.Target) + ", " + PrintNumExpr(d.Amount) + ")\n";

            case RestoreLifeNode r:
                return ind + "restore(" + PrintTargetExpr(r.Target) + ", " + PrintNumExpr(r.Amount) + ")\n";

            case ModifyStatsNode m:
                switch (m.Op)
                {
                    case StatModOp.Swap:
                        return ind + "swap_stats(" + PrintTargetExpr(m.Target) + ")\n";
                    case StatModOp.Set:
                        // null 表示「这一项不动」。打成 0 会把「只改攻击力」变成「攻击力设为0」，
                        // 而 n/a 是显式的「无此值」标记，解析端据此还原 null。
                        return ind + "set_stats(" + PrintTargetExpr(m.Target) + ", "
                               + PrintNumOrNa(m.Attack) + "/" + PrintNumOrNa(m.Life)
                               + DurArg(m.Duration) + ")\n";
                    default:
                        return ind + "buff(" + PrintTargetExpr(m.Target) + ", "
                               + PrintNumOrNa(m.Attack) + "/" + PrintNumOrNa(m.Life)
                               + DurArg(m.Duration) + ")\n";
                }

            case ModifyKeywordNode k:
                if (k.Op == KeywordOp.SilenceAll) return ind + "silence(" + PrintTargetExpr(k.Target) + ")\n";
                if (k.Op == KeywordOp.Remove) return ind + "remove_kw(" + PrintTargetExpr(k.Target) + ", " + k.Keyword.Name + ")\n";
                return ind + "grant_kw(" + PrintTargetExpr(k.Target) + ", " + k.Keyword.Name
                       + (k.Value != null && k.Value.Op == NumOp.Const ? ", " + k.Value.Value : "")
                       + DurArg(k.Duration) + ")\n";

            case MoveZoneNode mv:
            {
                // 抽牌张数可能是表达式（「抽等于本回合返回手牌数的牌」），
                // 只取常量会把「抽 X 张」变成「抽 1 张」。
                if (IsDraw(mv)) return ind + "draw(" + PrintNumExpr(mv.Target?.Count) + ")\n";
                string fn = mv.ToZone switch
                {
                    ZoneRef.Cemetery => mv.IsDestroy ? "destroy" : "discard",
                    ZoneRef.Banished => "banish",
                    ZoneRef.Hand => "bounce",
                    ZoneRef.Deck => "to_deck",
                    ZoneRef.Board => "to_board",
                    ZoneRef.ExArea => "to_exarea",
                    _ => "bounce",
                };
                return ind + fn + "(" + PrintTargetExpr(mv.Target) + ")\n";
            }

            case SpawnCardNode sp:
            {
                // 费用上限在 CopyFrom.Where 里；Count 恒为 1，拿它当上限会把
                // Reanimate(8) 导成 Reanimate(1)。
                if (ReanimateHelper.IsReanimate(sp))
                {
                    ReanimateHelper.TryExtractCostCap(sp, out var cap);
                    return ind + "reanimate(" + PrintNumExpr(cap) + ")\n";
                }

                // 卡池检索：discover(target(ally, card_pool|global_pool, ...), to: hand)
                if (sp.SourceKind == CardSourceKind.PoolQuery)
                {
                    string zoneWord = sp.CopyFrom != null && sp.CopyFrom.Zone == ZoneRef.GlobalPool
                        ? "global_pool" : "card_pool";
                    string toWord = sp.ToZone switch
                    {
                        ZoneRef.Board => "board",
                        ZoneRef.Deck => "deck",
                        ZoneRef.ExArea => "exarea",
                        _ => "hand",
                    };
                    var spec = new TargetSpec
                    {
                        SourceKind = sp.CopyFrom?.SourceKind ?? TargetSourceKind.Zone,
                        // Owner 必须打出来：TargetSpec.Owner 默认是 Any，
                        // 不写就会把 ally 篡改成 any，四端判等当场失败。
                        Owner = sp.CopyFrom?.Owner ?? ZoneScope.Self,
                        Zone = sp.CopyFrom?.Zone ?? ZoneRef.CardPool,
                        Mode = sp.CopyFrom?.Mode ?? SelectMode.Choose,
                        Where = sp.CopyFrom?.Where,
                        Count = sp.CopyFrom?.Count,
                    };
                    string extra = "";
                    // 非默认的 X/Y 必须打出来，否则往返后 5 选 2 会变回 3 选 1。
                    if (sp.OfferCount != 3) extra += ", offer: " + sp.OfferCount;
                    if (sp.Count is NumExpr cnt && cnt.Op == NumOp.Const && cnt.Value != 1)
                        extra += ", pick: " + cnt.Value;
                    return ind + "discover(" + PrintTargetExpr(spec) + extra + ", to: " + toWord + ")\n";
                }
                // 复制体：SourceKind=CopyOfTarget 时没有卡名，
                // 落到 summon 分支会导出成 summon("", 1)，CopyFrom 与 IsExactCopy 全丢。
                if (sp.SourceKind == CardSourceKind.CopyOfTarget)
                {
                    return ind + "copy_card(" + PrintTargetExpr(sp.CopyFrom) + ", "
                           + PrintNumExpr(sp.Count)
                           + (sp.IsExactCopy ? ", exact" : "")
                           + ", to: " + (sp.ToZone == ZoneRef.Hand ? "hand" : "board") + ")\n";
                }

                // 纹章与召唤是两种完全不同的节点语义（同名唯一 / 不占战场 / 落纹章区），
                // 都写成 summon 会让「获得纹章」在往返后变成「在战场上召唤一个随从」。
                if (sp.IsCrest)
                {
                    // 给谁贴必须写出来：Slaus 是「给对手贴纹章」。
                    string ownerArg = sp.ToOwner == ZoneScope.Self
                        ? "" : ", " + (sp.ToOwner == ZoneScope.Opponent ? "opponent" : "any");
                    return ind + "crest_gain(\"" + sp.CardName + "\"" + ownerArg + ")\n";
                }
                string fn = sp.ToZone == ZoneRef.Hand ? "add_hand" : "summon";
                return ind + fn + "(\"" + sp.CardName + "\", " + PrintNumExpr(sp.Count) + ")\n";
            }

            case EvolveNode e:
                return ind + (e.IsSuperEvolve ? "super_evolve" : "evolve")
                       + "(" + PrintTargetExpr(e.Target) + ")\n";

            case ModifyCounterNode c:
            {
                bool neg = IsNegative(c.Delta);
                // Target 非 null 表示「改的是这张卡自己的计数器」（吟唱 / 魔力增幅）。
                // 丢了它，吟唱就会被打成「改玩家计数器」，效果完全落空。
                string ofArg = c.Target != null ? ", of: " + PrintTargetExpr(c.Target) : "";
                // Op 要还原：Set 是「把值设为 N」（如信仰初始值为 0），
                // 打成 Add 就成了「加 0」—— 两者语义完全不同，卡面文字却几乎一样。
                string fnCtr = c.Op switch
                {
                    CounterOp.Set => "set_counter",
                    CounterOp.Fill => "fill_counter",
                    _ => neg ? "pay_counter" : "gain_counter",
                };
                return ind + fnCtr + "("
                       + c.Counter.Name + ", " + PrintNumExpr(AbsDelta(c.Delta)) + ofArg + ")\n";
            }

            case ModifyCostNode mc:
            {
                string op2 = mc.Op switch
                {
                    CostModOp.Halve => "cost_halve",
                    CostModOp.Set => "cost_set",
                    _ => "cost_mod",
                };
                return ind + op2 + "(" + PrintTargetExpr(mc.Target) + ", " + PrintNumExpr(mc.Value)
                       + DurArg(mc.Duration) + ")\n";
            }

            case InvokeAbilityNode ia:
                // Target 必须写回：DoInvoke 第一行就是 Resolve(n.Target)，
                // 丢了它 → Resolve(null) 返回空列表 → 整条「复刻能力」是空操作。
                return ind + "invoke(" + EventWord(ia.Event) + ", "
                       + PrintTargetExpr(ia.Target ?? TargetSpec.Self) + ")\n";

            case ReplaceDeckNode rd:
                return ind + "replace_deck(\"" + rd.DeckName + "\")\n";

            case GrantAbilityNode ga:
            {
                // 被授予的能力要**连触发头一起**写回：
                // 「每当友方随从进化，打对手主战者 1 点」的触发头是 OnEvolve，
                // 硬编码成 PassiveAura 会让它永远不触发。
                var gsb = new StringBuilder();
                gsb.Append(ind).Append("grant_ability(").Append(PrintTargetExpr(ga.Target))
                   .Append(ga.Duration == DurationKind.Permanent ? "" : ", dur: " + ga.Duration)
                   .Append(") {\n");
                if (ga.Granted != null) gsb.Append(PrintAbility(ga.Granted, depth + 1));
                gsb.Append(ind).Append("}\n");
                return gsb.ToString();
            }

            case GrantCounterAbilityNode gc:
            {
                // 被赋予的能力体必须写出来：Sham-Nacha 的信仰挂的是
                // 「每当你选模式，信仰+1」，丢了它信仰就只是个空计数器。
                var sb2 = new StringBuilder();
                sb2.Append(ind).Append("grant_counter_ability(").Append(gc.Counter.Name).Append(") {\n");
                if (gc.Granted != null)
                {
                    // 触发头不是 PassiveAura 时必须连头一起写回：
                    // 只写 body 会让「每当友方随从进化，打对手 1 点」变成
                    // 「常驻：打对手 1 点」，触发时机整个变了。
                    if (gc.Granted.Trigger != null
                        && gc.Granted.Trigger.Event != CoreEvent.PassiveAura)
                        sb2.Append(PrintAbility(gc.Granted, depth + 1));
                    else
                        foreach (var inner2 in gc.Granted.Body ?? Array.Empty<EffectNode>())
                            sb2.Append(PrintNode(inner2, depth + 1));
                }
                sb2.Append(ind).Append("}\n");
                return sb2.ToString();
            }

            case TransformNode t:
                return ind + "transform(" + PrintTargetExpr(t.Target) + ", \"" + t.IntoCardName + "\")\n";

            case ConditionalNode cd:
            {
                var sb = new StringBuilder();
                sb.Append(ind).Append("if (").Append(PrintBoolExpr(cd.Condition)).Append(") {\n");
                foreach (var x in cd.Then ?? Array.Empty<EffectNode>()) sb.Append(PrintNode(x, depth + 1));
                sb.Append(ind).Append('}');
                if (cd.Else != null && cd.Else.Count > 0)
                {
                    sb.Append(" else {\n");
                    foreach (var x in cd.Else) sb.Append(PrintNode(x, depth + 1));
                    sb.Append(ind).Append('}');
                }
                sb.Append('\n');
                return sb.ToString();
            }

            case LoopNode l:
            {
                var sb = new StringBuilder();
                sb.Append(ind).Append("loop(").Append(PrintNumExpr(l.Count)).Append(") {\n");
                foreach (var x in l.Body ?? Array.Empty<EffectNode>()) sb.Append(PrintNode(x, depth + 1));
                sb.Append(ind).Append("}\n");
                return sb.ToString();
            }

            case BranchModeNode b:
            {
                var sb = new StringBuilder();
                string kind = b.Kind switch
                {
                    ModeDispatchKind.Random => "random",
                    ModeDispatchKind.Sequence => "sequence",
                    _ => "choose",
                };
                sb.Append(ind).Append("mode ").Append(kind)
                  .Append('(').Append(EvalInt(b.PickCount, 1)).Append(") {\n");
                for (int i = 0; i < (b.Options?.Count ?? 0); i++)
                {
                    sb.Append(Indent(depth + 1)).Append(i + 1).Append(": {\n");
                    foreach (var x in b.Options[i]) sb.Append(PrintNode(x, depth + 2));
                    sb.Append(Indent(depth + 1)).Append("}\n");
                }
                sb.Append(ind).Append("}\n");
                return sb.ToString();
            }

            default:
                _errors.Add("打印器不支持节点：" + n.GetType().Name);
                return ind + "// 未支持的节点 " + n.GetType().Name + "\n";
        }
    }

    /// <summary>
    /// 裸关键词能力：PassiveAura + 无代价 + 单条 Grant。
    /// 这类能力在脚本里写成 keywords: [..] 一行，不再重复打印成 on aura {...}。
    /// </summary>
    private static bool IsBareKeywordAbility(AbilityDef ab)
        => ab?.Trigger?.Event == CoreEvent.PassiveAura
           && (ab.Costs == null || ab.Costs.Count == 0)
           && ab.Body != null && ab.Body.Count == 1
           && ab.Body[0] is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant;

    /// <summary>Duration 参数后缀。限时与永久在 AST 上只差这一个字段。</summary>
    private static string DurArg(DurationKind d)
        => d == DurationKind.Permanent ? "" : ", dur: " + d;

    /// <summary>读取可选的 ", dur: X" 参数。</summary>
    private DurationKind TryParseDur(Cursor p)
    {
        var save = p.Save();
        if (!p.TryChar(CommaChar)) { p.Restore(save); return DurationKind.Permanent; }
        if (p.PeekToken() != "dur") { p.Restore(save); return DurationKind.Permanent; }
        p.Next();
        p.TryChar(ColonChar);
        return p.Next() switch
        {
            "ThisTurn" => DurationKind.ThisTurn,
            "UntilEndOfOpponentTurn" => DurationKind.UntilEndOfOpponentTurn,
            "ThisMatch" => DurationKind.ThisMatch,
            _ => DurationKind.Permanent,
        };
    }

    private const char CommaChar = (char)44;
    private const char ColonChar = (char)58;

    private string PrintNumOrNa(NumExpr e) => e == null ? "na" : PrintNumExpr(e);

    private static bool IsDraw(MoveZoneNode mv)
        => mv.ToZone == ZoneRef.Hand
           && mv.Target?.Zone == ZoneRef.Deck
           && mv.Target.Mode == SelectMode.First;

    private string PrintTargetArgs(TargetSpec t)
    {
        if (t == null) return "self";
        var parts = new List<string>
        {
            t.Owner == ZoneScope.Opponent ? "enemy" : t.Owner == ZoneScope.Any ? "any" : "ally",
            t.Zone switch
            {
                ZoneRef.Board => "board",
                ZoneRef.Hand => "hand",
                ZoneRef.Deck => "deck",
                ZoneRef.Cemetery => "cemetery",
                ZoneRef.Banished => "banished",
                ZoneRef.Leader => "leader",
                ZoneRef.ExArea => "exarea",
                ZoneRef.CrestArea => "crest",
                // 外部池必须打出来：缺这两支会静默回落成 board，
                // 「从当前卡池发现」于是变成「从战场选」，而卡面中文照旧正确。
                ZoneRef.CardPool => "card_pool",
                ZoneRef.GlobalPool => "global_pool",
                _ => "board",
            },
        };

        var wheres = t.Where is AndExpr a ? a.Items.ToList()
                     : t.Where == null ? new List<BoolExpr>()
                     : new List<BoolExpr> { t.Where };
        foreach (var w in wheres)
        {
            switch (w)
            {
                case HasKindExpr hk:
                    parts.Add(hk.Kind switch
                    {
                        CardKind.Follower => "follower",
                        CardKind.Spell => "spell",
                        _ => "amulet",
                    });
                    break;
                case HasTraitExpr ht: parts.Add("trait: " + ht.Trait.Name); break;
                case HasClanExpr hcl:
                    if (hcl.IsSelfClan) parts.Add("clan: self");
                    else if (hcl.Clans != null && hcl.Clans.Count > 0)
                        parts.Add("clan: " + string.Join("|", hcl.Clans.Select(x => x.Name)));
                    else parts.Add("clan: " + hcl.Clan.Name);
                    break;
                case HasPackExpr hpk: parts.Add("pack: " + hpk.PackId); break;
                case NotSameEntityExpr: parts.Add("other"); break;
                case ConstBoolExpr cb0 when cb0.Value: break;
                // 任意属性比较都要写出来：攻击力≤3 这类筛选条件
                // 丢了就变成「所有随从」，卡面文字看不出差别但效果放大。
                case CompareExpr cmp when cmp.Op == CompareOp.Ge
                                          && cmp.Left?.Op == NumOp.Property
                                          && cmp.Left.Stat == StatKey.Cost
                                          && cmp.Right is NumExpr rge && rge.Op == NumOp.Const:
                    parts.Add("min_cost: " + rge.Value);
                    break;
                case CompareExpr cmp:
                    parts.Add("where: " + PrintNumExpr(cmp.Left) + " "
                        + (cmp.Op switch
                        {
                            CompareOp.Ge => ">=", CompareOp.Le => "<=",
                            CompareOp.Eq => "==", CompareOp.Gt => ">",
                            _ => "<",
                        }) + " " + PrintNumExpr(cmp.Right));
                    break;
                default: break;
            }
        }

        if (t.Mode == SelectMode.Choose) parts.Add("count: " + EvalInt(t.Count, 1));
        else if (t.Mode == SelectMode.Random) parts.Add("random: " + EvalInt(t.Count, 1));
        else if (t.Mode == SelectMode.First) parts.Add("first: " + EvalInt(t.Count, 1));
        // All 模式必须显式写 all：不写的话解析端默认 All，但 Count 会被填 1，
        // 于是 all n=null 与 all n=1 在往返中悄悄互换（后者意味着「取第一个」）。
        else parts.Add(t.Count == null ? "all" : "all_n: " + EvalInt(t.Count, 1));

        if (t.IncludeLeader) parts.Add("include_leader");
        if (t.History != HistoryScope.None) parts.Add("history: " + t.History);
        // 排序与去重是筛选语义的一部分：「基础费用最高的 1 个」与「任意 1 个」
        // 出天差地别，不打印就会被静默丢掉。
        if (t.Sort != Core.SortKey.None)
            parts.Add("sort: " + t.Sort + (t.SortDescending ? " desc" : ""));
        if (t.TopN > 0) parts.Add("topn: " + t.TopN);
        if (t.DistinctByName) parts.Add("distinct");

        if (t.DuplicatesOnly) parts.Add("duplicates");

        return string.Join(", ", parts);
    }

    private string PrintTargetExpr(TargetSpec t)
    {
        if (t == null) return "self";
        if (t.SourceKind == TargetSourceKind.Slot) return t.Slot;
        if (t.SourceKind == TargetSourceKind.CardId)
            return "card(\"" + t.CardName + "\", " + PrintTargetArgs(t) + ")";
        return "target(" + PrintTargetArgs(t) + ")";
    }

    /// <summary>
    /// 输出「括号内可直接写的内容」：
    ///   target(...)  → 去掉 target 前缀，只留参数表
    ///   card(...)    → 原样保留（它本身就是完整表达式）
    ///   $slot        → 原样
    /// count(...) / maxgroup(...) 的括号里要的正是这个形态。
    /// </summary>
    private string PrintTargetInParens(TargetSpec t)
    {
        if (t == null) return "self";
        if (t.SourceKind == TargetSourceKind.Slot) return t.Slot;
        if (t.SourceKind == TargetSourceKind.CardId)
            return "card(\"" + t.CardName + "\", " + PrintTargetArgs(t) + ")";
        // 具名卡池（CardId 型）的 Owner 是 Self，但打印要写成 ally 才不会被解析成 any。
        return PrintTargetArgs(t);
    }

    private string PrintNumExpr(NumExpr e)
    {
        if (e == null) return "0";
        switch (e.Op)
        {
            case NumOp.Const: return e.Value.ToString(CultureInfo.InvariantCulture);
            case NumOp.Counter: return "counter(" + e.Counter.Name + ")";
            // count / maxgroup 的目标可能是具名卡池（card("X", ... history: ...)），
            // 一律走 PrintTargetExpr 才能保留 CardId 与 History ——
            // 只打 Args 会把它退化成 self:board，历史池变成当前场上。
            case NumOp.Count: return "count(" + PrintTargetInParens(e.Of) + ")";
            case NumOp.MaxGroupSize: return "maxgroup(" + PrintTargetInParens(e.Of) + ")";
            case NumOp.Property:
            {
                // Of 必须打印：`prop(attack)` 缺省是 $item（筛选上下文），
                // 而「X = 本卡攻击力」用的是 $self。混淆会让属性读取恒为 0。
                string stat = e.Stat switch
                {
                    StatKey.Attack => "attack",
                    StatKey.Life => "life",
                    StatKey.MaxLife => "maxlife",
                    StatKey.Cost => "cost",
                    StatKey.BaseCost => "basecost",
                    StatKey.BaseAttack => "baseattack",
                    StatKey.BaseLife => "baselife",
                    _ => "attack",
                };
                return "prop(" + stat + ", " + PrintTargetExpr(e.Of ?? DefaultContextSelf) + ")";
            }
            case NumOp.Arith:
            {
                if (e.Args.Count < 2) return PrintNumExpr(e.Args.FirstOrDefault());
                string op = e.Arith switch
                {
                    ArithOp.Add => " + ",
                    ArithOp.Sub => " - ",
                    ArithOp.Mul => " * ",
                    ArithOp.Div => " / ",
                    _ => " + ",
                };
                // 括号必须打出来：set_stats 的值是 A/B 两段，不加括号时
                // 解析端会把后半段的 / 吃成前半段的一部分。
                return "(" + PrintNumExpr(e.Args[0]) + op + PrintNumExpr(e.Args[1]) + ")";
            }
            default: return "0";
        }
    }

    /// <summary>剥掉恒真项；剥完没有条件就返回 null（表示无条件）。</summary>
    private static BoolExpr SimplifyBool(BoolExpr e)
    {
        if (e == null) return null;
        if (e is ConstBoolExpr c) return c.Value ? null : null;

        if (e is AndExpr a)
        {
            var kept = a.Items.Where(x => !IsTriviallyTrue(x)).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return SimplifyBool(kept[0]);
            return new AndExpr(kept);
        }
        if (e is OrExpr o)
        {
            var kept = o.Items.Where(x => !IsTriviallyFalse(x)).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return SimplifyBool(kept[0]);
            return new OrExpr(kept);
        }
        return IsTriviallyTrue(e) ? null : e;
    }

    private static bool IsTriviallyTrue(BoolExpr e)
    {
        if (e is ConstBoolExpr c) return c.Value;
        // 1 >= 1：AST 里「恒真」的两种写法都要认，
        // 否则 if (true and ctr:x >= 6) 里的 true 会被原样导出。
        if (e is CompareExpr cmp && cmp.Left != null && cmp.Right != null
            && cmp.Left.Op == NumOp.Const && cmp.Right.Op == NumOp.Const)
        {
            int L = cmp.Left.Value, R = cmp.Right.Value;
            return cmp.Op switch
            {
                CompareOp.Ge => L >= R,
                CompareOp.Le => L <= R,
                CompareOp.Eq => L == R,
                CompareOp.Gt => L > R,
                CompareOp.Lt => L < R,
                _ => false,
            };
        }
        // 注：`is Evolved` 这类状态条件不能剥 —— 它不是恒真，
        // 只是当前触发域恰好保证它成立。剥离会导致语义丢失。
        return false;
    }

    private static bool IsTriviallyFalse(BoolExpr e)
        => e is ConstBoolExpr c2 && !c2.Value;

    private string PrintBoolExpr(BoolExpr e)
    {
        // 状态条件（is Evolved / is SuperEvolved / has Fuse …）必须原样写回：
        // 它们是卡面语义的一部分（<ev> 段的「已进化」域条件），
        // 当成恒真剥掉，往返后这张卡的进化能力就会对未进化状态也生效。
        // Of 必须打印出来：能力级条件里的 `is Evolved` 判断的是 $self，
        // 而 where 里的 `is Evolved` 判断的是 $item。
        // 一律硬编码成 $item，会让「本卡是否已进化」在触发时恒为 false ——
        // 因为触发那一刻作用域里只有 $self，$item 是空的。
        if (e is HasStateExpr hs) return "has_state(" + hs.State + ", " + PrintTargetExpr(hs.Of ?? TargetSpec.Self) + ")";

        if (e is CompareExpr c)
        {
            string op = c.Op switch
            {
                CompareOp.Ge => ">=", CompareOp.Le => "<=",
                CompareOp.Eq => "==", CompareOp.Gt => ">", CompareOp.Lt => "<",
                _ => ">=",
            };
            return PrintNumExpr(c.Left) + " " + op + " " + PrintNumExpr(c.Right);
        }
        if (e is AndExpr a) return string.Join(" and ", a.Items.Select(PrintBoolExpr));
        if (e is OrExpr o) return string.Join(" or ", o.Items.Select(PrintBoolExpr));
        return "1 >= 1";
    }

    private static bool IsNegative(NumExpr e)
        => e != null && e.Op == NumOp.Arith && e.Arith == ArithOp.Sub
           && e.Args.Count > 1 && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0;

    private static NumExpr AbsDelta(NumExpr e) => IsNegative(e) ? e.Args[1] : e;

    private static int EvalInt(NumExpr e, int fallback)
        => e != null && e.Op == NumOp.Const ? e.Value : fallback;

    private static string Indent(int depth) => new string(' ', depth * 4);

    private static string EventWord(CoreEvent ev) => ev switch
    {
        CoreEvent.OnPlay => "play",
        CoreEvent.OnEnterField => "enter",
        CoreEvent.OnLeaveField => "leave",
        CoreEvent.OnDestroy => "destroy",
        CoreEvent.OnBanish => "banish",
        CoreEvent.OnDiscard => "discard",
        CoreEvent.OnSummon => "summon",
        CoreEvent.OnEvolve => "evolve",
        CoreEvent.OnSuperEvolve => "super_evolve",
        CoreEvent.OnAttack => "attack",
        CoreEvent.OnClash => "clash",
        CoreEvent.OnDamaged => "damaged",
        CoreEvent.OnHealed => "healed",
        CoreEvent.OnEngage => "engage",
        CoreEvent.OnFuse => "fuse",
        CoreEvent.OnSpellboost => "spellboost",
        CoreEvent.OnDraw => "draw",
        CoreEvent.OnReturnToHand => "return_hand",
        CoreEvent.OnAddToHand => "add_hand",
        CoreEvent.OnTransform => "transform",
        CoreEvent.OnEarthRite => "earth_rite",
        CoreEvent.OnSelectMode => "select_mode",
        CoreEvent.OnCountdownExpire => "countdown_expire",
        CoreEvent.OnDeckOut => "deck_out",
        CoreEvent.OnCounterChange => "counter_change",
        CoreEvent.PassiveAura => "aura",
        CoreEvent.BattleStart => "battle_start",
        CoreEvent.TurnStart => "turn_start",
        CoreEvent.TurnEnd => "turn_end",
        _ => "play",
    };

    private static CoreEvent EventOf(string s) => s switch
    {
        "play" or "打出" => CoreEvent.OnPlay,
        "enter" or "入场" => CoreEvent.OnEnterField,
        "leave" or "离场" => CoreEvent.OnLeaveField,
        "destroy" or "破坏" => CoreEvent.OnDestroy,
        "banish" or "消失" => CoreEvent.OnBanish,
        "discard" or "舍弃" => CoreEvent.OnDiscard,
        "summon" or "召唤" => CoreEvent.OnSummon,
        "evolve" or "进化" => CoreEvent.OnEvolve,
        "super_evolve" or "超进化" => CoreEvent.OnSuperEvolve,
        "attack" or "攻击" => CoreEvent.OnAttack,
        "clash" or "交战" => CoreEvent.OnClash,
        "damaged" or "受伤" => CoreEvent.OnDamaged,
        "healed" or "回复" => CoreEvent.OnHealed,
        "engage" or "启动" => CoreEvent.OnEngage,
        "fuse" or "融合" => CoreEvent.OnFuse,
        "spellboost" or "魔力增幅" => CoreEvent.OnSpellboost,
        "draw" or "抽牌" => CoreEvent.OnDraw,
        "return_hand" or "回手" => CoreEvent.OnReturnToHand,
        "add_hand" or "加入手牌" => CoreEvent.OnAddToHand,
        "transform" or "变身" => CoreEvent.OnTransform,
        "earth_rite" or "土之秘术" => CoreEvent.OnEarthRite,
        "select_mode" or "选择模式" => CoreEvent.OnSelectMode,
        "countdown_expire" or "吟唱归零" => CoreEvent.OnCountdownExpire,
        "deck_out" or "抽空" => CoreEvent.OnDeckOut,
        "counter_change" or "计数变化" => CoreEvent.OnCounterChange,
        "aura" or "常驻" => CoreEvent.PassiveAura,
        "battle_start" or "对战开始" => CoreEvent.BattleStart,
        "turn_start" or "回合开始" => CoreEvent.TurnStart,
        "turn_end" or "回合结束" => CoreEvent.TurnEnd,
        _ => CoreEvent.OnPlay,
    };
    // ================================================================ 词法游标

    /// <summary>
    /// 轻量游标：按词与符号切分，同时保留字符级读取能力
    /// （( ) { } 的嵌套必须靠字符级游标才数得准）。
    /// </summary>
    private sealed class Cursor
    {
        private readonly string _s;
        private int _i;

        public Cursor(string s) { _s = s ?? ""; }

        public bool AtEnd
        {
            get { SkipWs(); return _i >= _s.Length; }
        }

        public (string text, int pos) Save() => (_s, _i);
        public void Restore((string text, int pos) st) { _i = st.pos; }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        public string PeekToken()
        {
            int save = _i;
            var t = Next();
            _i = save;
            return t;
        }

        /// <summary>向前看第 n 个 token（0 = 下一个）。</summary>
        public string PeekAhead(int n)
        {
            int save = _i;
            string t = null;
            for (int k = 0; k <= n; k++) t = Next();
            _i = save;
            return t;
        }

        public string Next()
        {
            SkipWs();
            if (_i >= _s.Length) return null;

            char c = _s[_i];

            if (c == '"')
            {
                _i++;
                var sb = new StringBuilder();
                while (_i < _s.Length && _s[_i] != '"')
                {
                    if (_s[_i] == (char)92 && _i + 1 < _s.Length) { _i++; sb.Append(_s[_i]); _i++; continue; }
                    sb.Append(_s[_i]); _i++;
                }
                if (_i < _s.Length) _i++;
                return sb.ToString();
            }

            if (_i + 1 < _s.Length)
            {
                var two = _s.Substring(_i, 2);
                if (two == ">=" || two == "<=" || two == "==" || two == "..")
                {
                    _i += 2;
                    return two;
                }
            }

            if ("(){}[],:/+-*<>".IndexOf(c) >= 0)
            {
                _i++;
                return c.ToString();
            }

            var id = new StringBuilder();
            if (c == DollarSign) { id.Append(c); _i++; }
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.'))
            {
                id.Append(_s[_i]); _i++;
            }
            if (id.Length == 0) { id.Append(c); _i++; }
            return id.ToString();
        }

        public string TryString()
        {
            SkipWs();
            if (_i >= _s.Length || _s[_i] != '"') return null;
            return Next();
        }

        public int? TryInt()
        {
            SkipWs();
            if (_i >= _s.Length || !char.IsDigit(_s[_i])) return null;
            int start = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            return int.Parse(_s.Substring(start, _i - start));
        }

        public bool TryChar(char c)
        {
            SkipWs();
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        public char PeekChar() => _i < _s.Length ? _s[_i] : (char)0;
        public void NextChar() { if (_i < _s.Length) _i++; }

        public bool TryLiteral(string lit)
        {
            SkipWs();
            if (_i + lit.Length <= _s.Length && _s.Substring(_i, lit.Length) == lit)
            {
                _i += lit.Length;
                return true;
            }
            return false;
        }

        public void SkipLine()
        {
            while (_i < _s.Length && _s[_i] != '\n') _i++;
            if (_i < _s.Length) _i++;
        }

        public void SkipValue()
        {
            SkipWs();
            if (_i >= _s.Length) return;
            Next();
            SkipWs();
            if (_i < _s.Length && "([{".IndexOf(_s[_i]) >= 0)
            {
                char open = _s[_i], close = open == '(' ? ')' : open == '[' ? ']' : '}';
                int depth = 0;
                while (_i < _s.Length)
                {
                    if (_s[_i] == open) depth++;
                    else if (_s[_i] == close) { depth--; if (depth == 0) { _i++; return; } }
                    _i++;
                }
            }
        }

        public void SkipBlockOrLine()
        {
            SkipWs();
            if (_i < _s.Length && _s[_i] == '{')
            {
                int depth = 0;
                while (_i < _s.Length)
                {
                    if (_s[_i] == '{') depth++;
                    else if (_s[_i] == '}') { depth--; if (depth == 0) { _i++; return; } }
                    _i++;
                }
                return;
            }
            SkipLine();
        }
    }
}
