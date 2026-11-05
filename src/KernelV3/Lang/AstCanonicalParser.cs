using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Python 风格游戏术语 AST 逆向解析器（204 号文档第二至四节）
//
// 与 AstCanonicalPrinter 严格配对：打印器打出的每一行，这里都必须能读回来。
// 三大铁律（文档第三节思路 4）：
//   1. 绝不裸调 SymbolId<T>.FromName —— 一律经 GameSchema 分配真实正整数 Id；
//   2. summon "Fairy" 若未写 id=，自动用 Lexicon 反查真实卡号；
//   3. 未注册的新词条自动注册进 Schema。
// ============================================================================

public sealed class AstCanonicalParser
{
    private readonly GameSchema _schema;
    private readonly Lexicon _lex;
    private List<PyToken> _tokens = new();
    private int _pos;

    public List<string> Errors { get; } = new();
    public List<string> Debug { get; } = new();

    public AstCanonicalParser(GameSchema schema, Lexicon lex = null)
    {
        _schema = schema ?? GameSchema.CreateDefault();
        _lex = lex;
    }
    // ---------------------------------------------------------------- 入口

    /// <summary>解析一段能力块。</summary>
    public List<AbilityDef> ParseAbilities(string dslText)
    {
        _tokens = PyAstLexer.Tokenize(dslText);
        _pos = 0;
        Errors.Clear();
        _budget = Math.Max(10000, _tokens.Count * 50);

        var list = new List<AbilityDef>();
        SkipNewlines();
        while (!IsAtEnd && Current.Kind != PyTokKind.Dedent && Current.Kind != PyTokKind.Eof)
        {
            int guard = _pos;
            var ab = ParseAbility();
            if (ab != null) list.Add(ab);
            SkipNewlines();
            if (_pos == guard) { Error("解析无进展，强制跳过"); _pos++; }
        }
        return list;
    }

    /// <summary>解析整张卡。</summary>
    public Ast.CardDef ParseCard(string dslText)
    {
        _tokens = PyAstLexer.Tokenize(dslText);
        _pos = 0;
        Errors.Clear();
        _budget = Math.Max(10000, _tokens.Count * 50);

        SkipNewlines();
        if (!MatchIdent("card")) { Error("期望以 card 开头"); return null; }
        int cardId = Current.Kind == PyTokKind.Number ? TakeNumber() : 0;
        string name = Current.Kind == PyTokKind.String ? TakeString() : "";

        var attrs = new Dictionary<string, string>();
        var kind = CardKind.Follower;
        if (Match(PyTokKind.LParen))
        {
            while (!IsAtEnd && Current.Kind != PyTokKind.RParen)
            {
                if (Current.Kind == PyTokKind.Ident)
                {
                    string key = TakeIdent();
                    string val = "true";
                    if (Match(PyTokKind.Assign))
                    {
                        if (Current.Kind == PyTokKind.String) val = TakeString();
                        else if (Current.Kind == PyTokKind.Number) val = TakeNumber().ToString();
                        else val = TakeIdent();
                    }
                    attrs[key] = val;
                }
                else Advance();
                Match(PyTokKind.Comma);
            }
            Expect(PyTokKind.RParen, "属性表缺少右括号");
        }
        Expect(PyTokKind.Colon, "card 头缺少冒号");

        if (attrs.ContainsKey("Spell")) kind = CardKind.Spell;
        else if (attrs.ContainsKey("Amulet")) kind = CardKind.Amulet;
        else if (attrs.ContainsKey("Crest")) kind = CardKind.Crest;
        else if (attrs.ContainsKey("Faith")) kind = CardKind.Faith;

        int cost = attrs.TryGetValue("cost", out var cs) && int.TryParse(cs, out var cv) ? cv : 0;
        int atk = attrs.TryGetValue("atk", out var av) && int.TryParse(av, out var a2) ? a2 : 0;
        int life = attrs.TryGetValue("life", out var lv) && int.TryParse(lv, out var l2) ? l2 : 0;
        var clan = attrs.TryGetValue("clan", out var cn) && !string.IsNullOrEmpty(cn)
            ? _schema.Clan(cn) : SymbolId<ClanTag>.Invalid;

        var traits = new List<SymbolId<TraitTag>>();
        var keywords = new List<SymbolId<KeywordTag>>();
        var abilities = new List<AbilityDef>();
        Ast.CardDef crestDef = null, faithDef = null, accelDef = null, crystDef = null;
        bool fuseSet = false;
        var deckMods = new List<DeckBuildModifier>();
        TargetSpec fuseFilter = null;

        SkipNewlines();
        if (Current.Kind == PyTokKind.Indent)
        {
            Advance();
            SkipNewlines();
            while (!IsAtEnd && Current.Kind != PyTokKind.Dedent)
            {
                if (Current.Kind == PyTokKind.Ident && Current.Text == "deck_mod")
                {
                    Advance();
                    Expect(PyTokKind.Colon, "deck_mod 后缺少冒号");
                    string kindStr = TakeIdent();
                    int v1 = TakeNumber();
                    int v2 = TakeNumber();
                    string dclan = "";
                    bool stack = false;
                    string grp = "";
                    while (!IsAtEnd && Current.Kind == PyTokKind.Ident)
                    {
                        if (Current.Text == "clan") { Advance(); Match(PyTokKind.Assign); dclan = TakeIdent(); continue; }
                        if (Current.Text == "stack") { Advance(); stack = true; continue; }
                        if (Current.Text == "group") { Advance(); Match(PyTokKind.Assign); grp = TakeIdent(); continue; }
                        break;
                    }
                    if (Enum.TryParse<DeckModKind>(kindStr, true, out var dk))
                    {
                        deckMods.Add(new DeckBuildModifier
                        {
                            Kind = dk, Value = v1, Value2 = v2,
                            TargetClan = dclan, StackPerCopy = stack, ExclusiveGroup = grp,
                        });
                    }
                    SkipNewlines();
                    continue;
                }

                if (Current.Kind == PyTokKind.Ident && Current.Text == "fuse")
                {
                    Advance();
                    Expect(PyTokKind.Colon, "fuse 后缺少冒号");
                    fuseSet = true;
                    // 只有「bare any」才表示「任意材料」；
                    // any:board... 是一个真正的目标过滤，必须交给 ParseTarget。
                    if (Current.Kind == PyTokKind.Ident && Current.Text == "any" && PeekText(1) != ":")
                    {
                        Advance();
                        fuseFilter = null;
                    }
                    else fuseFilter = ParseTarget();
                    SkipNewlines();
                    continue;
                }
                if (Current.Kind == PyTokKind.Ident && (Current.Text == "traits" || Current.Text == "keywords"))
                {
                    string head = TakeIdent();
                    Expect(PyTokKind.Colon, head + " 后缺少冒号");
                    if (Match(PyTokKind.LBracket))
                    {
                        // 兵种/关键词名可能含空格（"Earth Sigil" / "max attacks per turn"），
                        // 必须按逗号分段整体收集，不能一个 token 当一个名字。
                        var cur2 = new List<string>();
                        while (!IsAtEnd && Current.Kind != PyTokKind.RBracket)
                        {
                            if (Current.Kind == PyTokKind.Comma)
                            {
                                if (cur2.Count > 0)
                                {
                                    string nm = string.Join(" ", cur2);
                                    if (head == "traits") traits.Add(_schema.Trait(nm));
                                    else keywords.Add(_schema.Keyword(nm));
                                    cur2.Clear();
                                }
                                Advance();
                                continue;
                            }
                            if (Current.Kind == PyTokKind.Ident || Current.Kind == PyTokKind.Number)
                                cur2.Add(TakeIdent());
                            else Advance();
                        }
                        if (cur2.Count > 0)
                        {
                            string nm = string.Join(" ", cur2);
                            if (head == "traits") traits.Add(_schema.Trait(nm));
                            else keywords.Add(_schema.Keyword(nm));
                        }
                        Expect(PyTokKind.RBracket, head + " 缺少右方括号");
                    }
                    SkipNewlines();
                    continue;
                }
                int guardCard = _pos;
                try
                {
                    var ab = ParseAbility();
                    if (ab != null) abilities.Add(ab);
                }
                catch (SubDefException)
                {
                    ParseSubDef(ref crestDef, ref faithDef, ref accelDef, ref crystDef);
                }
                SkipNewlines();
                if (_pos == guardCard) { Error("卡内能力解析无进展，强制跳过"); _pos++; }
            }
        }

        return new Ast.CardDef
        {
            CardId = cardId,
            Name = name,
            Kind = kind,
            BaseCost = cost,
            BaseAttack = atk,
            BaseLife = life,
            Clan = clan,
            Traits = traits,
            InnateKeywords = keywords,
            Abilities = abilities,
            HasFuse = fuseSet, FuseMaterialFilter = fuseFilter, DeckModifiers = deckMods,
            CrestDef = crestDef, FaithDef = faithDef,
            AccelerateDef = accelDef, CrystallizeDef = crystDef,
            IsToken = attrs.ContainsKey("token"),
        };
    }
    // ---------------------------------------------------------------- 能力


    /// <summary>解析子卡段：crest / faith / accelerate / crystallize。</summary>
    private void ParseSubDef(ref Ast.CardDef crest, ref Ast.CardDef faith,
                             ref Ast.CardDef accel, ref Ast.CardDef cryst)
    {
        string kind = TakeIdent();
        string name = Current.Kind == PyTokKind.String ? TakeString() : "";
        int cost = 0;
        if (Current.Kind == PyTokKind.LParen)
        {
            Advance();
            while (!IsAtEnd && Current.Kind != PyTokKind.RParen)
            {
                if (Current.Kind == PyTokKind.Ident && Current.Text == "cost")
                {
                    Advance(); Match(PyTokKind.Assign);
                    cost = TakeNumber();
                    continue;
                }
                Advance();
                Match(PyTokKind.Comma);
            }
            Expect(PyTokKind.RParen, "子卡头缺少右括号");
        }
        Expect(PyTokKind.Colon, "子卡头缺少冒号");
        SkipNewlines();

        var body = new List<AbilityDef>();
        if (Current.Kind == PyTokKind.Indent)
        {
            Advance();
            SkipNewlines();
            while (!IsAtEnd && Current.Kind != PyTokKind.Dedent)
            {
                int g = _pos;
                if (Current.Kind == PyTokKind.Ident && Current.Text == "pass") { Advance(); SkipNewlines(); continue; }
                try
                {
                    var ab = ParseAbility();
                    if (ab != null) body.Add(ab);
                }
                catch (SubDefException) { /* 嵌套子卡，忽略 */ }
                SkipNewlines();
                if (_pos == g) { Error("子卡段无进展"); _pos++; }
            }
            if (Current.Kind == PyTokKind.Dedent) Advance();
        }

        var def = new Ast.CardDef { Name = name, BaseCost = cost, Abilities = body, Kind = CardKind.Crest };
        switch (kind)
        {
            case "crest": crest = def; break;
            case "faith": faith = def; break;
            case "accelerate": accel = def; break;
            case "crystallize": cryst = def; break;
        }
    }

    private AbilityDef ParseAbility()
    {
        var trig = ParseTriggerHeader(out var costs, out var cond);
        Expect(PyTokKind.Colon, "能力头末尾缺少冒号");
        SkipNewlines();
        var body = ParseIndentedBlock();
        return new AbilityDef { Trigger = trig, Costs = costs, Condition = cond ?? BoolExpr.True, Body = body };
    }

    private TriggerSpec ParseTriggerHeader(out List<CostSpec> costs, out BoolExpr cond)
    {
        costs = new List<CostSpec>();
        cond = null;
        string word = TakeIdent();
        var t = new TriggerSpec();
        bool manual = false;

        switch (word)
        {
            case "fanfare": t = new TriggerSpec { Event = CoreEvent.OnPlay, IsFanfare = true }; break;
            case "on_play": t = new TriggerSpec { Event = CoreEvent.OnPlay }; break;
            case "last_words": case "on_destroy": t = new TriggerSpec { Event = CoreEvent.OnDestroy }; break;
            case "evolve": manual = true; t = new TriggerSpec { Event = CoreEvent.OnEvolve }; break;
            case "on_evolve": t = new TriggerSpec { Event = CoreEvent.OnEvolve }; break;
            case "super_evolve": manual = true; t = new TriggerSpec { Event = CoreEvent.OnSuperEvolve }; break;
            case "on_super_evolve": t = new TriggerSpec { Event = CoreEvent.OnSuperEvolve }; break;
            case "strike": t = new TriggerSpec { Event = CoreEvent.OnAttack }; break;
            case "clash": t = new TriggerSpec { Event = CoreEvent.OnClash }; break;
            case "engage": t = new TriggerSpec { Event = CoreEvent.OnEngage }; break;
            case "aura": t = new TriggerSpec { Event = CoreEvent.PassiveAura }; break;
            case "turn_start": t = new TriggerSpec { Event = CoreEvent.TurnStart }; break;
            case "turn_end": t = new TriggerSpec { Event = CoreEvent.TurnEnd }; break;
            case "on_enter": t = new TriggerSpec { Event = CoreEvent.OnEnterField }; break;
            case "on_leave": t = new TriggerSpec { Event = CoreEvent.OnLeaveField }; break;
            case "on_summon": t = new TriggerSpec { Event = CoreEvent.OnSummon }; break;
            case "on_draw": t = new TriggerSpec { Event = CoreEvent.OnDraw }; break;
            case "on_discard": t = new TriggerSpec { Event = CoreEvent.OnDiscard }; break;
            case "on_damaged": t = new TriggerSpec { Event = CoreEvent.OnDamaged }; break;
            case "on_healed": t = new TriggerSpec { Event = CoreEvent.OnHealed }; break;
            case "on_banish": t = new TriggerSpec { Event = CoreEvent.OnBanish }; break;
            case "on_fuse": t = new TriggerSpec { Event = CoreEvent.OnFuse }; break;
            case "on_select_mode": t = new TriggerSpec { Event = CoreEvent.OnSelectMode }; break;
            case "on_earth_rite": t = new TriggerSpec { Event = CoreEvent.OnEarthRite }; break;
            case "on_deck_out": t = new TriggerSpec { Event = CoreEvent.OnDeckOut }; break;
            case "on_countdown_expire": t = new TriggerSpec { Event = CoreEvent.OnCountdownExpire }; break;
            case "on_counter_change": t = new TriggerSpec { Event = CoreEvent.OnCounterChange }; break;
            // 子卡段头：crest "Name" (...) / faith / accelerate / crystallize。
            // 它们不是能力，遇到时应把游标退回，由 ParseCard 的子卡循环处理。
            case "crest": case "faith": case "accelerate": case "crystallize":
                _pos--;
                throw new SubDefException();
            default:
                if (word.StartsWith("on")) t = new TriggerSpec { Event = EventOf(word.Substring(2)) };
                else { Error("未知的触发头：" + word); t = new TriggerSpec { Event = CoreEvent.OnPlay }; }
                break;
        }

        bool secondarySet = false;
        CoreEvent secondary = CoreEvent.OnPlay;
        bool zoneSet = false; ActiveZoneScope zoneV = ActiveZoneScope.Board;
        bool turnSet = false; TurnOwnerScope turnV = TurnOwnerScope.Any;
        int maxTrig = 0;
        TargetSpec subject = null, secFilter = null;

        while (!IsAtEnd && Current.Kind == PyTokKind.Ident)
        {
            string w = Current.Text;
            // 打印器写的是 or=OnFuse，等号必须消费掉，否则游标停在 '=' 上
            // 直接撞上 Expect(Colon)（206 文档 E5）。
            if (w == "or") { Advance(); Match(PyTokKind.Assign); secondary = EventOf(TakeIdent()); secondarySet = true; continue; }
            if (w == "zone") { Advance(); Match(PyTokKind.Assign); zoneV = ZoneOf(TakeIdent()); zoneSet = true; continue; }
            if (w == "turn") { Advance(); Match(PyTokKind.Assign); turnV = TurnOf(TakeIdent()); turnSet = true; continue; }
            if (w == "max") { Advance(); Match(PyTokKind.Assign); maxTrig = TakeNumber(); continue; }
            if (w == "on") { Advance(); Expect(PyTokKind.LParen, "on 后缺少左括号"); subject = ParseTarget(); Expect(PyTokKind.RParen, "on 缺少右括号"); continue; }
            if (w == "vs") { Advance(); Expect(PyTokKind.LParen, "vs 后缺少左括号"); secFilter = ParseTarget(); Expect(PyTokKind.RParen, "vs 缺少右括号"); continue; }
            break;
        }

        t = new TriggerSpec
        {
            Event = t.Event, IsFanfare = t.IsFanfare, ManualEvolveOnly = manual,
            ActiveZone = zoneSet ? zoneV : ActiveZoneScope.Board,
            DuringTurn = turnSet ? turnV : TurnOwnerScope.Any,
            MaxTriggersPerTurn = maxTrig,
            SubjectFilter = subject, SecondaryFilter = secFilter,
            SecondaryEvent = secondarySet ? secondary : null,
        };

        if (Match(PyTokKind.LBracket))
        {
            while (!IsAtEnd && Current.Kind != PyTokKind.RBracket)
            {
                if (Current.Kind == PyTokKind.Ident)
                {
                    string label = TakeIdent();
                    var amount = (NumExpr)NumExpr.Const(1);
                    if (Match(PyTokKind.LParen)) { amount = ParseNumExpr(); Expect(PyTokKind.RParen, "代价参数缺少右括号"); }
                    costs.Add(BuildCost(label, amount));
                }
                else Advance();
                Match(PyTokKind.Comma);
            }
            Expect(PyTokKind.RBracket, "代价表缺少右方括号");
        }

        if (Current.Kind == PyTokKind.Ident && Current.Text == "if")
        {
            Advance();
            cond = ParseBoolExpr();
        }
        return t;
    }

    private CostSpec BuildCost(string label, NumExpr amount)
    {
        var kind = label switch
        {
            "countdown" => CostKind.Countdown,
            "earth_rite" or "destroy_ally" or "destroyally" => CostKind.DestroyAlly,
            "necromance" or "shadow" => CostKind.Necromance,
            "ep" => CostKind.UsePp,
            "sep" => CostKind.UsePp,
            _ => CostKind.UsePp,
        };
        // 代价标签必须原样保留：打印器写的是 [combo(3)] / [enhance(8)] 这种 label，
        // 丢掉它会让 combo / rally / necromancy 全部退化成 usepp，
        // 而卡面文本一模一样，只有条件求值时才会发现读错了计数器。
        string lbl = label;
        return new CostSpec { Kind = kind, Amount = amount, Label = lbl };
    }
    // ---------------------------------------------------------------- 块

    private List<EffectNode> ParseIndentedBlock()
    {
        var nodes = new List<EffectNode>();
        if (Current.Kind != PyTokKind.Indent)
        {
            SkipNewlines();
            if (!IsAtEnd && Current.Kind != PyTokKind.Dedent && Current.Kind != PyTokKind.Eof)
            {
                var one = ParseStatement();
                if (one != null) nodes.Add(one);
            }
            return nodes;
        }
        Advance();
        SkipNewlines();
        while (!IsAtEnd && Current.Kind != PyTokKind.Dedent)
        {
            int guard = _pos;
            if (Current.Kind == PyTokKind.Ident && Current.Text == "pass") { Advance(); SkipNewlines(); continue; }
            var node = ParseStatement();
            if (node != null) nodes.Add(node);
            SkipNewlines();
            if (_pos == guard) { Error("语句无进展，强制跳过"); _pos++; }
        }
        if (Current.Kind == PyTokKind.Dedent) Advance();
        return nodes;
    }

    private EffectNode ParseStatement()
    {
        BoolExpr preIf = null;
        if (Current.Kind == PyTokKind.At)
        {
            Advance();
            if (MatchIdent("if"))
            {
                preIf = ParseBoolExpr();
                Expect(PyTokKind.Colon, "@if 后缺少冒号");
                SkipNewlines();
                var inner = ParseIndentedBlock();
                if (inner.Count == 1) return WithIf(inner[0], preIf);
                return new ConditionalNode { Condition = preIf, Then = inner };
            }
        }

        string verb = TakeIdent().ToLowerInvariant();
        switch (verb)
        {
            case "destroy": return new MoveZoneNode { Target = ParseTarget(), ToZone = ZoneRef.Cemetery, IsDestroy = true, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "discard": return new MoveZoneNode { Target = ParseTarget(ZoneRef.Hand), ToZone = ZoneRef.Cemetery, IsDestroy = false, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "banish": return new MoveZoneNode { Target = ParseTarget(), ToZone = ZoneRef.Banished, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "bounce": return new MoveZoneNode { Target = ParseTarget(), ToZone = ZoneRef.Hand, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "return_to_deck": return new MoveZoneNode { Target = ParseTarget(), ToZone = ZoneRef.Deck, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "invoke_self": return new MoveZoneNode { Target = TargetSpec.Self, ToZone = ZoneRef.Board, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "draw": return ParseDraw();
            case "tutor": return new MoveZoneNode { Target = ParseTarget(ZoneRef.Deck), ToZone = ZoneRef.Hand, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
            case "move": return ParseRawMove();
            case "reanimate": return ParseReanimate();
            case "gain_crest": return ParseGainCrest();
            case "discover": return ParseDiscover(false);
            case "chaos_discover": return ParseDiscover(true);
            case "pool_spawn": return ParseDiscover(false);
            case "summon": return ParseNamedSpawn(ZoneRef.Board);
            case "add_to_hand": return ParseNamedSpawn(ZoneRef.Hand);
            case "spawn_exact_copy": return ParseCopySpawn(true);
            case "spawn_copy": return ParseCopySpawn(false);
            case "spawn_previous": return ParsePreviousSpawn();
            case "spawn": return ParseRawSpawn();
            case "spellboost": return ParseCounterSugar("spellboost");
            case "advance_countdown": return ParseCountdown(-1);
            case "delay_countdown": return ParseCountdown(1);
            case "counter": return ParseRawCounter();
            case "select": return ParseSelectNode();
            case "deal": return ParseDeal();
            case "restore": return ParseRestore(false);
            case "fully_restore": return ParseRestore(true);
            case "buff": return ParseStats(StatModOp.Add);
            case "set_stats": return ParseStats(StatModOp.Set);
            case "swap_stats": return ParseSwapStats();
            case "grant_keyword": return ParseGrantKeyword();
            case "remove_keyword": return ParseRemoveKeyword();
            case "silence": return new ModifyKeywordNode { Target = ParseTarget(), Op = KeywordOp.SilenceAll };
            case "modify_cost": return ParseModifyCost(CostModOp.Add);
            case "set_cost": return ParseModifyCost(CostModOp.Set);
            case "halve_cost": return ParseModifyCost(CostModOp.Halve);
            case "grant_ability": return ParseGrantAbility();
            case "grant_counter_ability": return ParseGrantCounterAbility();
            case "if": return ParseIfBlock();
            case "repeat": case "for_each": case "repeat_until": return ParseLoop(verb);
            case "modes": return ParseModes();
            case "evolve": return new EvolveNode { Target = ParseTarget(), IsSuperEvolve = false };
            case "super_evolve": return new EvolveNode { Target = ParseTarget(), IsSuperEvolve = true };
            case "transform": return ParseTransform();
            case "invoke_ability": return ParseInvoke();
            case "damage_modifier": return ParseDamageModifier();
            case "reveal": return new RevealCardNode { Target = ParseTarget() };
            case "replace_deck":
            {
                string dn = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
                var rdOwner = ZoneScope.Self;
                if (Current.Kind == PyTokKind.Ident && Current.Text == "owner")
                {
                    Advance(); Match(PyTokKind.Assign);
                    rdOwner = OwnerOf(TakeIdent());
                }
                return new ReplaceDeckNode { DeckName = dn, Owner = rdOwner };
            }
            case "mark_victory_card":
            {
                var mvOwner = ZoneScope.Self;
                if (Current.Kind == PyTokKind.Ident && Current.Text == "owner")
                {
                    Advance(); Match(PyTokKind.Assign);
                    mvOwner = OwnerOf(TakeIdent());
                }
                return new MarkVictoryCardNode { Owner = mvOwner };
            }
            case "macro":
            {
                string mn = TakeIdent();
                var args = new List<NumExpr>();
                if (Match(PyTokKind.LParen))
                {
                    while (!IsAtEnd && Current.Kind != PyTokKind.RParen) { args.Add(ParseNumExpr()); Match(PyTokKind.Comma); }
                    Expect(PyTokKind.RParen, "macro 参数缺少右括号");
                }
                return new MacroNode { Macro = _schema.Macro(mn), Args = args };
            }
            default:
                Error("未知动作：" + verb);
                return null;
        }
    }


    /// <summary>该标识符是否是一个触发头（用于块内遇到子能力时转交 ParseAbility）。</summary>
    private static bool IsTriggerHead(string w) => w is
        "fanfare" or "on_play" or "last_words" or "on_destroy" or "evolve" or "on_evolve"
        or "super_evolve" or "on_super_evolve" or "strike" or "clash" or "engage" or "aura"
        or "turn_start" or "turn_end" or "on_enter" or "on_leave" or "on_summon" or "on_draw"
        or "on_discard" or "on_damaged" or "on_healed" or "on_banish" or "on_fuse"
        or "on_select_mode" or "on_earth_rite" or "on_deck_out" or "on_countdown_expire"
        or "on_counter_change" or "on_spellboost";

    private static EffectNode WithIf(EffectNode n, BoolExpr cond)
    {
        if (n == null) return null;
        if (n is MoveZoneNode x)
            return new MoveZoneNode { Target = x.Target, ToZone = x.ToZone, OutSlot = x.OutSlot, IsDestroy = x.IsDestroy, If = cond };
        if (n is DealDamageNode d)
            return new DealDamageNode { Target = d.Target, Amount = d.Amount, IsSplit = d.IsSplit, IsAbility = d.IsAbility, If = cond };
        return n;
    }
    // ---------------------------------------------------------------- 具体语句

    private MoveZoneNode ParseDraw()
    {
        // draw 的数量可能是表达式（draw ctr:combo）而不只是数字。
        NumExpr count = NumExpr.Const(1);
        if (Current.Kind == PyTokKind.Number || Current.Kind == PyTokKind.Minus
            || (Current.Kind == PyTokKind.Ident && (Current.Text == "ctr" || Current.Text == "count" || Current.Text == "prop")))
            count = ParseNumExpr();
        var owner = ZoneScope.Self;
        if (Current.Kind == PyTokKind.LBracket)
        {
            Advance();
            if (MatchIdent("owner")) { Match(PyTokKind.Assign); owner = OwnerOf(TakeIdent()); }
            Expect(PyTokKind.RBracket, "draw 属性缺少右方括号");
        }
        return new MoveZoneNode
        {
            Target = new TargetSpec { SourceKind = TargetSourceKind.Zone, Owner = owner, Zone = ZoneRef.Deck, Mode = SelectMode.First, Count = count },
            ToZone = ZoneRef.Hand,
            OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private MoveZoneNode ParseRawMove()
    {
        var t = ParseTarget();
        EatIdent("to");
        var zone = ZoneRefOf(TakeIdent());
        bool destroy = false;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "destroy") { Advance(); destroy = true; }
        return new MoveZoneNode { Target = t, ToZone = zone, IsDestroy = destroy, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
    }

    private SpawnCardNode ParseReanimate()
    {
        Expect(PyTokKind.LParen, "reanimate 后缺少左括号");
        var cap = ParseNumExpr();
        Expect(PyTokKind.RParen, "reanimate 参数缺少右括号");
        var node = Diy.ReanimateHelper.Build(cap);
        // 打印器会在 reanimate(n) 之后打 +departed，必须消费掉。
        bool dep = false;
        if (Current.Kind == PyTokKind.Plus && PeekText(1) == "departed") { Advance(); Advance(); dep = true; }
        return new SpawnCardNode
        {
            SourceKind = node.SourceKind, CopyFrom = node.CopyFrom, ToZone = node.ToZone,
            ToOwner = node.ToOwner, Count = node.Count,
            AddDepartedTrait = node.AddDepartedTrait || dep,
            OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private SpawnCardNode ParseGainCrest()
    {
        string name = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
        int id = 0;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "id") { Advance(); Match(PyTokKind.Assign); id = TakeNumber(); }
        // 纹章不补 id：打印器只在 CardId>0 时才打 id=，
        // 这里若自动反查，重新打印会多出一个 id=，往返就对不上。
        var crestOwner = ZoneScope.Self;
        if (Current.Kind == PyTokKind.LBracket)
        {
            Advance();
            if (MatchIdent("owner")) { Match(PyTokKind.Assign); crestOwner = OwnerOf(TakeIdent()); }
            Expect(PyTokKind.RBracket, "gain_crest 属性缺少右方括号");
        }
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CardId, CardId = id, CardName = name, IsCrest = true,
            ToOwner = crestOwner,
            ToZone = ZoneRef.CrestArea, Count = NumExpr.Const(1), OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    /// <summary>解析 grant_ability / grant_counter_ability 的子能力块（含触发头）。</summary>
    private AbilityDef ParseGrantedAbility()
    {
        Expect(PyTokKind.Indent, "grant 块缺少缩进");
        SkipNewlines();
        AbilityDef granted = null;
        if (!MatchIdent("pass")) granted = ParseAbility();
        SkipNewlines();
        if (Current.Kind == PyTokKind.Dedent) Advance();
        return granted ?? new AbilityDef { Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura }, Condition = BoolExpr.True };
    }

    private SpawnCardNode ParseDiscover(bool chaos)
    {
        var count = ParseNumExpr();
        EatIdent("from");
        var from = ParseTarget();
        int offer = 3;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "offer") { Advance(); Match(PyTokKind.Assign); offer = TakeNumber(); }
        var toZone = ZoneRef.Hand;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "to") { Advance(); Match(PyTokKind.Assign); toZone = ZoneRefOf(TakeIdent()); }
        if (from == null) from = TargetSpec.InZone(ZoneRef.CardPool, ZoneScope.Self);
        from = WithZone(from, chaos ? ZoneRef.GlobalPool : ZoneRef.CardPool);
        from = WithMode(from, SelectMode.Choose);
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery, CopyFrom = from, OfferCount = offer,
            Count = count, ToZone = toZone, OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private SpawnCardNode ParseNamedSpawn(ZoneRef toZone)
    {
        var count = ParseNumExpr();
        string name = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
        int id = 0;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "id") { Advance(); Match(PyTokKind.Assign); id = TakeNumber(); }
        if (id <= 0 && _lex != null && _lex.TryGetCardId(name, out int r1)) id = r1;
        var spawnOwner = ZoneScope.Self;
        if (Current.Kind == PyTokKind.LBracket)
        {
            Advance();
            if (MatchIdent("owner")) { Match(PyTokKind.Assign); spawnOwner = OwnerOf(TakeIdent()); }
            Expect(PyTokKind.RBracket, "spawn 属性缺少右方括号");
        }
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CardId, CardId = id, CardName = name, ToOwner = spawnOwner,
            ToZone = toZone, Count = count, OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private SpawnCardNode ParseCopySpawn(bool exact)
    {
        var count = ParseNumExpr();
        EatIdent("of");
        var from = ParseTarget();
        EatIdent("to");
        var toZone = ZoneRefOf(TakeIdent());
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CopyOfTarget, CopyFrom = from, IsExactCopy = exact,
            Count = count, ToZone = toZone, OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private SpawnCardNode ParsePreviousSpawn()
    {
        var count = ParseNumExpr();
        EatIdent("to");
        var toZone = ZoneRefOf(TakeIdent());
        return new SpawnCardNode { SourceKind = CardSourceKind.CopyOfPrevious, Count = count, ToZone = toZone, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
    }

    private SpawnCardNode ParseRawSpawn()
    {
        var count = ParseNumExpr();
        string name = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
        int id = 0;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "id") { Advance(); Match(PyTokKind.Assign); id = TakeNumber(); }
        if (id <= 0 && _lex != null && _lex.TryGetCardId(name, out int r2)) id = r2;
        var toZone = ZoneRef.Board;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "to") { Advance(); toZone = ZoneRefOf(TakeIdent()); }
        return new SpawnCardNode { SourceKind = CardSourceKind.CardId, CardId = id, CardName = name, ToZone = toZone, Count = count, OutSlot = TryOutSlot(TargetSpec.SlotCreated) };
    }
    private ModifyCounterNode ParseCounterSugar(string name)
    {
        // ParseTarget 会贪婪收集到行尾，把 "by 2" 一起吞掉。
        // 先找到 by 的位置，只把之前的部分交给目标解析。
        var target = ParseTarget();
        EatIdent("by");
        // 这里的数值是紧跟在 by 后面的裸数字，不能交给 ParseNumExpr 的宽松收集
        // （它会把行尾其它东西一起吞掉，或者在空游标上返回 1）。
        NumExpr delta = Current.Kind == PyTokKind.Number
            ? NumExpr.Const(TakeNumber())
            : ParseNumExpr();
        return new ModifyCounterNode { Counter = _schema.Counter(name), Op = CounterOp.Add, Delta = delta, Target = target };
    }

    private ModifyCounterNode ParseCountdown(int sign)
    {
        var target = ParseTarget();
        EatIdent("by");
        var amount = ParseNumExpr();
        var delta = sign < 0 ? (NumExpr)NumExpr.Sub(NumExpr.Const(0), amount) : amount;
        return new ModifyCounterNode { Counter = _schema.Counter("countdown"), Op = CounterOp.Add, Delta = delta, Target = target };
    }

    private ModifyCounterNode ParseRawCounter()
    {
        string name = TakeIdent();

        // 实体级形态：counter <name> on <target> [by <n>]
        // 它没有 op 词，on 直接跟在名字后面 —— 必须先于 op 判定检查。
        if (Current.Kind == PyTokKind.Ident && Current.Text == "on")
        {
            Advance();
            var tgt = ParseTarget();
            if (Current.Kind == PyTokKind.Ident && Current.Text == "by") Advance();
            var d = ParseNumExpr();
            return new ModifyCounterNode { Counter = _schema.Counter(name), Op = CounterOp.Add, Delta = d, Target = tgt };
        }

        // 主战者级：Add -> "by" / Set -> "=" / Fill -> "fill"
        string opWord = "by";
        if (Current.Kind == PyTokKind.Assign) opWord = "=";
        else if (Current.Kind == PyTokKind.Ident && (Current.Text == "by" || Current.Text == "fill")) opWord = Current.Text;
        if (opWord == "=" || opWord == "by" || opWord == "fill") Advance();
        var op = opWord == "=" ? CounterOp.Set : (opWord == "fill" ? CounterOp.Fill : CounterOp.Add);

        var delta = ParseNumExpr();
        var owner = ZoneScope.Self;
        if (Current.Kind == PyTokKind.LBracket)
        {
            Advance();
            if (MatchIdent("owner")) { Match(PyTokKind.Assign); owner = OwnerOf(TakeIdent()); }
            Expect(PyTokKind.RBracket, "counter 属性缺少右方括号");
        }
        return new ModifyCounterNode { Counter = _schema.Counter(name), Op = op, Delta = delta, Owner = owner };
    }

    private SelectTargetNode ParseSelectNode()
    {
        var tgt = ParseTarget();
        string slot = TargetSpec.SlotSelected;
        bool append = false;
        while (Current.Kind == PyTokKind.Arrow || (Current.Kind == PyTokKind.Plus && PeekText(1) == "append"))
        {
            if (Current.Kind == PyTokKind.Arrow)
            {
                Advance();
                slot = Current.Kind == PyTokKind.Slot ? TakeSlot() : TakeIdent();
            }
            else { Advance(); Advance(); append = true; }
        }
        return new SelectTargetNode { Target = tgt, OutSlot = slot, AppendToSlot = append };
    }

    private DealDamageNode ParseDeal()
    {
        var amount = ParseNumExpr();
        EatIdent("damage");
        EatIdent("to");
        var tgt = ParseTarget();
        bool split = false, combat = false;
        while (Current.Kind == PyTokKind.Ident)
        {
            if (Current.Text == "split") { Advance(); split = true; continue; }
            if (Current.Text == "combat") { Advance(); combat = true; continue; }
            break;
        }
        return new DealDamageNode { Amount = amount, Target = tgt, IsSplit = split, IsAbility = !combat };
    }

    private RestoreLifeNode ParseRestore(bool full)
    {
        if (full)
        {
            var t0 = ParseTarget();
            return new RestoreLifeNode { Target = t0, FullRestore = true, Amount = NumExpr.Const(0) };
        }
        var amount = ParseNumExpr();
        EatIdent("life");
        EatIdent("to");
        var tgt = ParseTarget();
        return new RestoreLifeNode { Amount = amount, Target = tgt };
    }

    private ModifyStatsNode ParseStats(StatModOp op)
    {
        var tgt = ParseTarget();
        if (op == StatModOp.Set) EatIdent("to");
        var atk = ParseStatPart();
        Match(PyTokKind.Slash);
        var life = ParseStatPart();
        return new ModifyStatsNode { Target = tgt, Op = op, Attack = atk, Life = life, Duration = TryDur() };
    }

    private NumExpr ParseStatPart()
    {
        if (Current.Kind == PyTokKind.Ident && Current.Text == "_") { Advance(); return null; }
        // 攻/防之间用斜杠分隔，必须禁止把斜杠当除法：
        //   +3/+3      两个独立项
        //   (a / 2)/b  括号内的除法仍要能解析
        //
        // "-0" 是个特例：NumExpr.Const 是 int，-0 == 0，
        // 打印器按值判断符号会把它打成 "+0"。
        // 这里显式构造 Arith(0 - 0)，PrintNum 会输出 "-0"，往返才守恒。
        if (Current.Kind == PyTokKind.Minus && PeekText(1) == "0")
        {
            Advance(); Advance();
            return NumExpr.Sub(NumExpr.Const(0), NumExpr.Const(0));
        }
        return ParseNumExpr(true);
    }

    private ModifyStatsNode ParseSwapStats()
    {
        return new ModifyStatsNode { Target = ParseTarget(), Op = StatModOp.Swap };
    }

    private ModifyKeywordNode ParseGrantKeyword()
    {
        string kw = ReadMultiWordUntilBoundary("to");
        NumExpr val = null;
        if (Match(PyTokKind.LParen)) { val = ParseNumExpr(); Expect(PyTokKind.RParen, "关键词参数缺少右括号"); }
        EatIdent("to");
        var tgt = ParseTarget();
        return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Grant, Keyword = _schema.Keyword(kw), Value = val, Duration = TryDur() };
    }

    private ModifyKeywordNode ParseRemoveKeyword()
    {
        // 关键词名可能是多个词：last words / ignores ward / cannot be played
        string kw = ReadMultiWordUntilBoundary("from");
        EatIdent("from");
        var tgt = ParseTarget();
        return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Remove, Keyword = _schema.Keyword(kw) };
    }

    private ModifyCostNode ParseModifyCost(CostModOp op)
    {
        var tgt = ParseTarget();
        NumExpr val = NumExpr.Const(0);
        if (op == CostModOp.Set) { EatIdent("to"); val = ParseNumExpr(); }
        else if (op == CostModOp.Add) { EatIdent("by"); val = ParseNumExpr(); }
        return new ModifyCostNode { Target = tgt, Op = op, Value = val, Duration = TryDur() };
    }
    private GrantAbilityNode ParseGrantAbility()
    {
        EatIdent("to");
        var tgt = ParseTarget();
        var dur = TryDur();
        Expect(PyTokKind.Colon, "grant_ability 后缺少冒号");
        SkipNewlines();
        var granted = ParseGrantedAbility();
        return new GrantAbilityNode { Target = tgt, Granted = granted, Duration = dur };
    }

    private GrantCounterAbilityNode ParseGrantCounterAbility()
    {
        EatIdent("to");
        string counter = TakeIdent();
        var owner = ZoneScope.Self;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "owner") { Advance(); Match(PyTokKind.Assign); owner = OwnerOf(TakeIdent()); }
        Expect(PyTokKind.Colon, "grant_counter_ability 后缺少冒号");
        SkipNewlines();
        var granted = ParseGrantedAbility();
        return new GrantCounterAbilityNode { Counter = _schema.Counter(counter), Owner = owner, Granted = granted };
    }

    private ConditionalNode ParseIfBlock()
    {
        var cond = ParseBoolExpr();
        Expect(PyTokKind.Colon, "if 条件后缺少冒号");
        SkipNewlines();
        var thenBody = ParseIndentedBlock();
        IReadOnlyList<EffectNode> elseBody = Array.Empty<EffectNode>();
        SkipNewlines();
        if (Current.Kind == PyTokKind.Ident && Current.Text == "else")
        {
            Advance();
            Expect(PyTokKind.Colon, "else 后缺少冒号");
            SkipNewlines();
            elseBody = ParseIndentedBlock();
        }
        return new ConditionalNode { Condition = cond, Then = thenBody, Else = elseBody };
    }

    private LoopNode ParseLoop(string verb)
    {
        var kind = verb == "for_each" ? LoopKind.ForEach : (verb == "repeat_until" ? LoopKind.RepeatUntil : LoopKind.Times);
        NumExpr count = null;
        TargetSpec coll = null;
        BoolExpr until = null;
        if (kind == LoopKind.ForEach) { EatIdent("in"); coll = ParseTarget(); }
        else if (kind == LoopKind.RepeatUntil) { until = ParseBoolExpr(); }
        else { count = ParseNumExpr(); EatIdent("times"); }
        int max = 30;
        if (Current.Kind == PyTokKind.LParen)
        {
            Advance();
            if (MatchIdent("max")) { Match(PyTokKind.Assign); max = TakeNumber(); }
            Expect(PyTokKind.RParen, "loop max 缺少右括号");
        }
        Expect(PyTokKind.Colon, "loop 后缺少冒号");
        SkipNewlines();
        var body = ParseIndentedBlock();
        return new LoopNode { Kind = kind, Count = count, Collection = coll, Until = until, Body = body, MaxIterations = max };
    }

    private BranchModeNode ParseModes()
    {
        string kindWord = TakeIdent();
        var kind = kindWord == "Random" ? ModeDispatchKind.Random
                 : kindWord == "Sequence" ? ModeDispatchKind.Sequence
                 : kindWord == "RandomUnused" ? ModeDispatchKind.RandomUnused
                 : kindWord == "SequenceUnused" ? ModeDispatchKind.SequenceUnused
                 : ModeDispatchKind.Choose;
        NumExpr pick = NumExpr.Const(1);
        TargetSpec grantTo = null;
        if (Match(PyTokKind.LParen))
        {
            if (MatchIdent("pick")) { Match(PyTokKind.Assign); pick = ParseNumExpr(); }
            Expect(PyTokKind.RParen, "modes 参数缺少右括号");
        }
        if (Current.Kind == PyTokKind.Ident && Current.Text == "grant_to") { Advance(); Match(PyTokKind.Assign); grantTo = ParseTarget(); }
        Expect(PyTokKind.Colon, "modes 后缺少冒号");
        SkipNewlines();
        var options = new List<IReadOnlyList<EffectNode>>();
        if (Current.Kind == PyTokKind.Indent)
        {
            Advance();
            SkipNewlines();
            while (!IsAtEnd && Current.Kind != PyTokKind.Dedent)
            {
                if (Current.Kind == PyTokKind.Ident && Current.Text == "option")
                {
                    Advance();
                    if (Current.Kind == PyTokKind.Number) Advance();
                    Expect(PyTokKind.Colon, "option 后缺少冒号");
                    SkipNewlines();
                    options.Add(ParseIndentedBlock());
                }
                else SkipNewlines();
            }
            if (Current.Kind == PyTokKind.Dedent) Advance();
        }
        return new BranchModeNode { Kind = kind, PickCount = pick, Options = options, GrantTarget = grantTo };
    }

    private TransformNode ParseTransform()
    {
        var tgt = ParseTarget();
        EatIdent("into");
        if (Current.Kind == PyTokKind.Ident && Current.Text == "copy")
        {
            Advance(); EatIdent("of");
            return new TransformNode { Target = tgt, CopySource = ParseTarget() };
        }
        string name = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
        int id = 0;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "id") { Advance(); Match(PyTokKind.Assign); id = TakeNumber(); }
        if (id <= 0 && _lex != null && _lex.TryGetCardId(name, out int r3)) id = r3;
        return new TransformNode { Target = tgt, IntoCardId = id, IntoCardName = name };
    }

    private InvokeAbilityNode ParseInvoke()
    {
        string ev = TakeIdent();
        EatIdent("on");
        return new InvokeAbilityNode { Target = ParseTarget(), Event = EventOf(ev) };
    }

    private DamageModifierNode ParseDamageModifier()
    {
        string kw = TakeIdent();
        var kind = kw == "TakenCap" ? DamageAuraKind.TakenCap
                 : kw == "DealtDelta" ? DamageAuraKind.DealtDelta
                 : kw == "DealtCap" ? DamageAuraKind.DealtCap
                 : DamageAuraKind.TakenDelta;
        NumExpr amount = NumExpr.Const(0);
        var mask = DamageSourceMask.All;
        bool reduce = true;
        while (Current.Kind == PyTokKind.Ident)
        {
            if (Current.Text == "amount") { Advance(); Match(PyTokKind.Assign); amount = ParseNumExpr(); continue; }
            if (Current.Text == "mask") { Advance(); Match(PyTokKind.Assign); mask = ParseMask(TakeIdent()); continue; }
            if (Current.Text == "reduce") { Advance(); reduce = true; continue; }
            if (Current.Text == "increase") { Advance(); reduce = false; continue; }
            break;
        }
        EatIdent("on");
        return new DamageModifierNode { Kind = kind, Amount = amount, SourceMask = mask, IsReduction = reduce, Target = ParseTarget(), Duration = TryDur() };
    }
    // ---------------------------------------------------------------- 目标 / 数值 / 布尔

    /// <summary>
    /// 语法驱动的目标解析器（206 号文档方案 2）。
    ///
    /// 不再把 token 先收集成数组再猜边界 —— 那种做法要么少吞、
    /// 要么把后面的 [dur=...] / +1/+1 / by / 外层右括号一起吞掉。
    /// 这里严格按 PrintTarget 的输出顺序消费，读完自己的成分就停。
    /// </summary>
    private TargetSpec ParseTarget(ZoneRef? defaultZone = null)
    {
        if (MatchIdent("null")) return null;
        if (IsAtEnd) return defaultZone.HasValue ? TargetSpec.InZone(defaultZone.Value, ZoneScope.Self) : null;

        TargetSpec spec;

        // A. 槽位：$self [$choose1 …]
        if (Current.Kind == PyTokKind.Slot)
        {
            spec = new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TakeSlot(), Mode = SelectMode.All };
            if (MatchIdent("where")) spec = spec.WithWhere(ParseBoolExpr());
            return spec;
        }

        // B. 具名卡：card:<Name>#<Id>@<owner>:<zone>
        if (Current.Kind == PyTokKind.Ident && Current.Text == "card" && PeekText(1) == ":")
        {
            Advance(); Advance();
            var nameParts = new List<string>();
            while (!IsAtEnd && Current.Kind != PyTokKind.Hash && Current.Kind != PyTokKind.At
                   && Current.Kind != PyTokKind.Newline && Current.Kind != PyTokKind.Dedent)
            {
                if (Current.Kind == PyTokKind.Comma)
                {
                    if (nameParts.Count > 0) nameParts[^1] = nameParts[^1] + ",";
                    Advance();
                    continue;
                }
                // 卡名里的 & 是独立 token，要作为名字一部分保留
                if (Current.Kind == PyTokKind.Pipe)
                {
                    nameParts.Add("&");
                    Advance();
                    continue;
                }
                if (Current.Kind == PyTokKind.Ident || Current.Kind == PyTokKind.Number) nameParts.Add(TakeIdent());
                else Advance();
            }
            string cardName = string.Join(" ", nameParts);
            int cardId = 0;
            if (Match(PyTokKind.Hash)) cardId = TakeNumber();
            var owner0 = ZoneScope.Self;
            var zone0 = ZoneRef.Board;
            if (Match(PyTokKind.At))
            {
                owner0 = OwnerOf(TakeIdent());
                Match(PyTokKind.Colon);
                zone0 = ZoneRefOf(TakeIdent());
            }
            spec = new TargetSpec
            {
                SourceKind = TargetSourceKind.CardId, CardId = cardId, CardName = cardName,
                Owner = owner0, Zone = zone0, Mode = SelectMode.All,
            };
        }
        // C. 区域：<owner>:<zone>[<Kind>]
        else if (Current.Kind == PyTokKind.Ident && PeekText(1) == ":")
        {
            var ow = OwnerOf(TakeIdent());
            Advance();
            var zw = ZoneRefOf(TakeIdent());
            spec = new TargetSpec { SourceKind = TargetSourceKind.Zone, Owner = ow, Zone = zw, Mode = SelectMode.All };
            if (Match(PyTokKind.Lt))
            {
                string kStr = TakeIdent().ToLowerInvariant();
                var k = kStr == "spell" ? CardKind.Spell : kStr == "amulet" ? CardKind.Amulet : CardKind.Follower;
                if (Match(PyTokKind.Gt)) { }
                spec = spec.WithWhere(new HasKindExpr { Kind = k, Of = TargetSpec.Item });
            }
        }
        else return defaultZone.HasValue ? TargetSpec.InZone(defaultZone.Value, ZoneScope.Self) : null;

        // D. 修饰词（严格对齐 PrintTarget 的打印顺序）
        while (!IsAtEnd)
        {
            if (MatchIdent("history"))
            {
                Match(PyTokKind.Assign);
                string hs = TakeIdent();
                if (Enum.TryParse<HistoryScope>(hs, true, out var h2)) spec = WithHistory(spec, h2);
                continue;
            }
            if (Current.Kind == PyTokKind.Plus && PeekText(1) == "leader")
            {
                Advance(); Advance();
                spec = WithIncludeLeader(spec, true);
                continue;
            }
            if (MatchIdent("distinct")) { spec = WithDistinct(spec, true); continue; }
            if (MatchIdent("duplicates")) { spec = WithDuplicates(spec, true); continue; }
            if (MatchIdent("sort"))
            {
                Match(PyTokKind.Assign);
                string sk = TakeIdent();
                bool desc = false;
                if (Current.Kind == PyTokKind.Minus)
                {
                    Advance();
                    if (MatchIdent("desc")) desc = true;
                }
                var skv = sk == "Attack" ? SortKey.Attack : sk == "Life" ? SortKey.Life
                        : sk == "Cost" ? SortKey.Cost : sk == "BaseCost" ? SortKey.BaseCost : SortKey.None;
                spec = WithSort(spec, skv, desc);
                continue;
            }
            if (MatchIdent("topN")) { Match(PyTokKind.Assign); spec = WithTopN(spec, TakeNumber()); continue; }
            break;
        }

        // E. 量词模式
        if (!IsAtEnd && Current.Kind == PyTokKind.Ident)
        {
            if (MatchIdent("All"))
            {
                spec = WithMode(spec, SelectMode.All);
            }
            else if (Enum.TryParse<SelectMode>(Current.Text, true, out var mode))
            {
                Advance();
                spec = WithMode(spec, mode);
                if (Match(PyTokKind.LParen))
                {
                    spec = WithCount(spec, ParseNumExpr());
                    Expect(PyTokKind.RParen, "Mode(Count) 缺少右括号");
                }
            }
        }

        // F. where <BoolExpr>
        if (MatchIdent("where")) spec = spec.WithWhere(ParseBoolExpr());

        return spec;
    }

    private static TargetSpec WithHistory(TargetSpec t, HistoryScope h) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = h,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithIncludeLeader(TargetSpec t, bool b) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = b, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithDistinct(TargetSpec t, bool b) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = b, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithDuplicates(TargetSpec t, bool b) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = b,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithTopN(TargetSpec t, int n) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = n,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithSort(TargetSpec t, SortKey k, bool desc) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = k, SortDescending = desc, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithCount(TargetSpec t, NumExpr c) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = c, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithMode(TargetSpec t, SelectMode m) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = m, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithZone(TargetSpec t, ZoneRef z) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = z, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static TargetSpec WithOwner(TargetSpec t, ZoneScope o) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = o, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
        IncludeLeader = t.IncludeLeader, OmittedFields = t.OmittedFields,
    };

    private static Token ToCnToken(PyToken t)
    {
        switch (t.Kind)
        {
            case PyTokKind.Number: return new Token { Kind = TokKind.Number, Text = t.Text, Value = t.IntVal };
            case PyTokKind.Slot: return new Token { Kind = TokKind.Word, Text = t.Text };
            case PyTokKind.String: return new Token { Kind = TokKind.Word, Text = t.Text };
            case PyTokKind.LParen: return new Token { Kind = TokKind.OpenParen, Text = "(" };
            case PyTokKind.RParen: return new Token { Kind = TokKind.CloseParen, Text = ")" };
            case PyTokKind.Comma: return new Token { Kind = TokKind.Comma, Text = "," };
            case PyTokKind.Colon: return new Token { Kind = TokKind.Word, Text = ":" };
            case PyTokKind.Assign: return new Token { Kind = TokKind.Word, Text = "=" };
            case PyTokKind.Gt: return new Token { Kind = TokKind.Word, Text = ">" };
            case PyTokKind.Ge: return new Token { Kind = TokKind.Word, Text = ">=" };
            case PyTokKind.Lt: return new Token { Kind = TokKind.Word, Text = "<" };
            case PyTokKind.Le: return new Token { Kind = TokKind.Word, Text = "<=" };
            case PyTokKind.Minus: return new Token { Kind = TokKind.Dash, Text = "-" };
            case PyTokKind.Slash: return new Token { Kind = TokKind.Word, Text = "/" };
            default: return new Token { Kind = TokKind.Word, Text = t.Text };
        }
    }

    // ============================================================
    // 递归下降数值解析器（206 号文档方案 3）
    //
    // 与 AstEnglishPrinter.PrintNum 严格对齐，绝不借道英文编译器 ——
    // 后者只认自然语言（"if you are in overflow"），
    // 对 count(...) / prop:... 这类 AST 表达式一律失败并静默返回 Const(1)。
    // ============================================================

    private NumExpr ParseNumExpr(bool stopAtSlash = false)
    {
        var left = ParseNumTerm(stopAtSlash);
        while (!IsAtEnd)
        {
            if (Current.Kind != PyTokKind.Plus && Current.Kind != PyTokKind.Minus) break;
            // select 的 +append 后缀不是算术加号
            if (Current.Kind == PyTokKind.Plus && PeekText(1) == "append") break;
            var op = Current.Kind == PyTokKind.Plus ? ArithOp.Add : ArithOp.Sub;
            Advance();
            var right = ParseNumTerm(stopAtSlash);
            left = Arith(left, op, right);
        }
        return left;
    }

    private NumExpr ParseNumTerm(bool stopAtSlash)
    {
        var left = ParseNumFactor(stopAtSlash);
        while (!IsAtEnd)
        {
            if (Current.Kind == PyTokKind.Star)
            { Advance(); left = Arith(left, ArithOp.Mul, ParseNumFactor(stopAtSlash)); continue; }
            if (!stopAtSlash && Current.Kind == PyTokKind.Slash)
            { Advance(); left = Arith(left, ArithOp.Div, ParseNumFactor(stopAtSlash)); continue; }
            if (Current.Kind == PyTokKind.Percent)
            { Advance(); left = Arith(left, ArithOp.Mod, ParseNumFactor(stopAtSlash)); continue; }
            break;
        }
        return left;
    }

    private NumExpr ParseNumFactor(bool stopAtSlash)
    {
        if (Match(PyTokKind.Plus)) return ParseNumFactor(stopAtSlash);
        if (Match(PyTokKind.Minus))
        {
            // 打印器把「0 - X」写成 "-X"，读回来必须还原成同一个结构
            var inner = ParseNumFactor(stopAtSlash);
            if (inner != null && inner.Op == NumOp.Const) return NumExpr.Const(-inner.Value);
            return NumExpr.Sub(NumExpr.Const(0), inner);
        }
        if (Current.Kind == PyTokKind.Number) return NumExpr.Const(TakeNumber());

        if (Match(PyTokKind.LParen))
        {
            if (MatchIdent("if"))
            {
                var cond = ParseBoolExpr();
                EatIdent("then");
                var thenE = ParseNumExpr();
                EatIdent("else");
                var elseE = ParseNumExpr();
                Expect(PyTokKind.RParen, "条件数值缺少右括号");
                return new NumExpr { Op = NumOp.Cond, Cond = cond, Then = thenE, Else = elseE };
            }
            var inner = ParseNumExpr(false);
            Expect(PyTokKind.RParen, "括号数值缺少右括号");
            return inner;
        }

        if (Current.Kind == PyTokKind.Ident) return ParseNumAtom();
        return NumExpr.Const(1);
    }

    private NumExpr ParseNumAtom()
    {
        string head = TakeIdent();

        // ctr:name[@self|@opp|@any]
        if (head == "ctr" && Match(PyTokKind.Colon))
        {
            string cname = TakeIdent();
            TargetSpec of = null;
            if (Match(PyTokKind.At))
            {
                string ow = TakeIdent();
                of = new TargetSpec
                {
                    Owner = ow == "opp" ? ZoneScope.Opponent : ow == "self" ? ZoneScope.Self : ZoneScope.Any,
                    Zone = ZoneRef.Leader,
                };
            }
            return new NumExpr { Op = NumOp.Counter, Counter = _schema.Counter(cname), Of = of };
        }

        // prop:Stat[(Target)]
        if (head == "prop" && Match(PyTokKind.Colon))
        {
            string st = TakeIdent();
            TargetSpec of = null;
            if (Match(PyTokKind.LParen)) { of = ParseTarget(); Expect(PyTokKind.RParen, "prop 缺少右括号"); }
            return new NumExpr { Op = NumOp.Property, Stat = StatOf(st), Of = of };
        }

        // sum:Stat(Target)
        if (head == "sum" && Match(PyTokKind.Colon))
        {
            string st = TakeIdent();
            Expect(PyTokKind.LParen, "sum 缺少左括号");
            var of = ParseTarget();
            Expect(PyTokKind.RParen, "sum 缺少右括号");
            return new NumExpr { Op = NumOp.Sum, Stat = StatOf(st), Of = of };
        }

        // split:name#idx
        if (head == "split" && Match(PyTokKind.Colon))
        {
            string cname = TakeIdent();
            int idx = Match(PyTokKind.Hash) ? TakeNumber() : 0;
            return new NumExpr { Op = NumOp.SplitOf, Counter = _schema.Counter(cname), Value = idx };
        }

        // count(...) / distinct(...) / maxgroup(...)
        if ((head == "count" || head == "distinct" || head == "maxgroup") && Match(PyTokKind.LParen))
        {
            var of = ParseTarget();
            Expect(PyTokKind.RParen, head + " 缺少右括号");
            var op = head == "count" ? NumOp.Count : head == "distinct" ? NumOp.DistinctCount : NumOp.MaxGroupSize;
            return new NumExpr { Op = op, Of = of };
        }

        // rand(min,max)#rollId
        if (head == "rand" && Match(PyTokKind.LParen))
        {
            int min = TakeNumber();
            Match(PyTokKind.Comma);
            var maxE = ParseNumExpr();
            Expect(PyTokKind.RParen, "rand 缺少右括号");
            int rollId = Match(PyTokKind.Hash) ? TakeNumber() : 0;
            return new NumExpr { Op = NumOp.RandomRange, Value = min, Args = new[] { maxE }, RollId = rollId };
        }

        // clamp(a,b,c) / max(...) / min(...)
        if ((head == "clamp" || head == "max" || head == "min") && Match(PyTokKind.LParen))
        {
            var args = new List<NumExpr>();
            while (!IsAtEnd && Current.Kind != PyTokKind.RParen)
            {
                args.Add(ParseNumExpr());
                Match(PyTokKind.Comma);
            }
            Expect(PyTokKind.RParen, head + " 缺少右括号");
            if (head == "clamp") return new NumExpr { Op = NumOp.Clamp, Args = args };
            return new NumExpr { Op = NumOp.Extreme, TakeMax = head == "max", Args = args };
        }

        return NumExpr.Const(1);
    }

    private static NumExpr Arith(NumExpr l, ArithOp op, NumExpr r)
        => new NumExpr { Op = NumOp.Arith, Arith = op, Args = new[] { l, r } };

    private static StatKey StatOf(string s) => s switch
    {
        "Attack" => StatKey.Attack,
        "Life" => StatKey.Life,
        "Cost" => StatKey.Cost,
        "BaseAttack" => StatKey.BaseAttack,
        "BaseLife" => StatKey.BaseLife,
        "BaseCost" => StatKey.BaseCost,
        "MaxLife" => StatKey.MaxLife,
        _ => StatKey.None,
    };

    // ============================================================
    // 递归下降布尔解析器（206 号文档方案 3）
    // 优先级：or < and < not < 原子
    // ============================================================

    private BoolExpr ParseBoolExpr() => ParseBoolOr();

    private BoolExpr ParseBoolOr()
    {
        var items = new List<BoolExpr> { ParseBoolAnd() };
        while (MatchIdent("or")) items.Add(ParseBoolAnd());
        return items.Count == 1 ? items[0] : new OrExpr(items);
    }

    private BoolExpr ParseBoolAnd()
    {
        var items = new List<BoolExpr> { ParseBoolUnary() };
        while (MatchIdent("and")) items.Add(ParseBoolUnary());
        return items.Count == 1 ? items[0] : new AndExpr(items);
    }

    private BoolExpr ParseBoolUnary()
    {
        if (MatchIdent("not")) return new NotExpr(ParseBoolUnary());
        if (Current.Kind == PyTokKind.LParen)
        {
            // 括号可能是布尔分组 (A and B)，也可能是算术左操作数 ((a + b) >= 3)。
            int save = _pos;
            Advance();
            var inner = ParseBoolExpr();
            if (Match(PyTokKind.RParen) && !IsCompareOp(Current.Kind)) return inner;
            _pos = save;
        }
        return ParseBoolAtom();
    }

    private static bool IsCompareOp(PyTokKind k)
        => k == PyTokKind.Assign || k == PyTokKind.Eq || k == PyTokKind.Ne
        || k == PyTokKind.Ge || k == PyTokKind.Le || k == PyTokKind.Gt || k == PyTokKind.Lt;

    private BoolExpr ParseBoolAtom()
    {
        if (MatchIdent("true")) return BoolExpr.True;
        if (MatchIdent("false")) return BoolExpr.False;

        // is selected | is <CardKind>[(Of)] | is <UnitState>[(Of)]
        if (MatchIdent("is"))
        {
            if (MatchIdent("selected")) return new IsPreviousExpr { Slot = TargetSpec.SlotSelected };
            string what = TakeIdent();
            TargetSpec of = TargetSpec.Item;
            if (Match(PyTokKind.LParen)) { of = ParseTarget(); Expect(PyTokKind.RParen, "is(...) 缺少右括号"); }
            if (Enum.TryParse<CardKind>(what, true, out var ck))
                return new HasKindExpr { Kind = ck, Of = of };
            if (Enum.TryParse<UnitState>(what, true, out var us))
                return new HasStateExpr { State = us, Of = of };
            return new HasKindExpr { Kind = CardKind.Follower, Of = of };
        }

        // has trait <Name> | has clan <C|$self> | has <Keyword>
        if (MatchIdent("has"))
        {
            if (MatchIdent("trait"))
                return new HasTraitExpr { Trait = _schema.Trait(ReadMultiWordUntilBoundary()), Of = TargetSpec.Item };
            if (MatchIdent("clan"))
            {
                if (Current.Kind == PyTokKind.Slot && Current.Text == TargetSpec.SlotSelf)
                {
                    Advance();
                    return new HasClanExpr { IsSelfClan = true, Of = TargetSpec.Item };
                }
                var clans = new List<SymbolId<ClanTag>> { _schema.Clan(TakeIdent()) };
                while (Match(PyTokKind.Pipe)) clans.Add(_schema.Clan(TakeIdent()));
                if (clans.Count == 1) return new HasClanExpr { Clan = clans[0], Of = TargetSpec.Item };
                return new HasClanExpr { Clans = clans, Of = TargetSpec.Item };
            }
            return new HasKeywordExpr { Keyword = _schema.Keyword(ReadMultiWordUntilBoundary()), Of = TargetSpec.Item };
        }

        // in pack <id>
        if (MatchIdent("in") && MatchIdent("pack"))
            return new HasPackExpr { PackId = ReadMultiWordUntilBoundary(), Of = TargetSpec.Item };

        // same name as <Target> | named <Name>
        if (MatchIdent("same") && MatchIdent("name") && MatchIdent("as"))
            return new HasCardNameExpr { Of = ParseTarget() };
        if (MatchIdent("named"))
        {
            // 卡名可能带逗号与撇号（Belial, Archangel of Cunning / Dragon's Vale Elder）
            var nameParts2 = new List<string>();
            while (!IsAtEnd)
            {
                if (Current.Kind == PyTokKind.Newline || Current.Kind == PyTokKind.Dedent
                    || Current.Kind == PyTokKind.Eof || Current.Kind == PyTokKind.RParen) break;
                if (Current.Kind == PyTokKind.Comma) { if (nameParts2.Count > 0) nameParts2[^1] += ","; Advance(); continue; }
                if (Current.Kind == PyTokKind.Ident
                    && (Current.Text == "by" || Current.Text == "to" || Current.Text == "from"
                        || Current.Text == "and" || Current.Text == "or")) break;
                if (Current.Kind == PyTokKind.Ident || Current.Kind == PyTokKind.Number) { nameParts2.Add(TakeIdent()); continue; }
                break;
            }
            string cname = string.Join(" ", nameParts2);
            int cid = 0;
            if (_lex != null) _lex.TryGetCardId(cname, out cid);
            return new HasCardNameExpr { CardId = cid, CardName = cname, Of = TargetSpec.Item };
        }

        // other than ($trigger | $opposing | $chooseN | itself)
        if (MatchIdent("other") && MatchIdent("than"))
        {
            if (MatchIdent("itself"))
                return new NotSameEntityExpr { ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf, Of = TargetSpec.Item };
            string slot = Current.Kind == PyTokKind.Slot ? TakeSlot() : TakeIdent();
            return new NotSameEntityExpr { ExcludeSlot = slot, Of = TargetSpec.Item };
        }

        // exists(<Target>)
        if (MatchIdent("exists") && Match(PyTokKind.LParen))
        {
            var tgt = ParseTarget();
            Expect(PyTokKind.RParen, "exists 缺少右括号");
            return new ExistsExpr { Target = tgt, Op = CompareOp.Ge, Count = NumExpr.Const(1) };
        }

        // 牌库规则
        if (MatchIdent("no") && MatchIdent("duplicates") && MatchIdent("in"))
        {
            var ow = ParseOwnerWord(); EatIdent("deck");
            return new DeckRuleExpr { Rule = DeckRule.NoDuplicates, Owner = ow };
        }
        if (MatchIdent("only"))
        {
            string parity = TakeIdent();
            if (Match(PyTokKind.Minus)) EatIdent("cost");
            EatIdent("cards"); EatIdent("in");
            var ow = ParseOwnerWord(); EatIdent("deck");
            return new DeckRuleExpr
            {
                Rule = parity == "even" ? DeckRule.EvenCostOnly : DeckRule.OddCostOnly,
                Owner = ow,
            };
        }

        // 通用数值比较
        var left = ParseNumExpr();
        CompareOp? op = null;
        if (Match(PyTokKind.Assign) || Match(PyTokKind.Eq)) op = CompareOp.Eq;
        else if (Match(PyTokKind.Ne)) op = CompareOp.Ne;
        else if (Match(PyTokKind.Ge)) op = CompareOp.Ge;
        else if (Match(PyTokKind.Le)) op = CompareOp.Le;
        else if (Match(PyTokKind.Gt)) op = CompareOp.Gt;
        else if (Match(PyTokKind.Lt)) op = CompareOp.Lt;
        if (op == null) return BoolExpr.True;
        var right = ParseNumExpr();
        return new CompareExpr { Left = left, Op = op.Value, Right = right };
    }

    /// <summary>读一个可能含空格的词条名，遇到布尔/括号边界即停。</summary>
    private string ReadMultiWordUntilBoundary(params string[] stops)
    {
        var parts = new List<string>();
        while (!IsAtEnd)
        {
            if (Current.Kind == PyTokKind.Newline || Current.Kind == PyTokKind.Dedent
                || Current.Kind == PyTokKind.Eof || Current.Kind == PyTokKind.RParen
                || Current.Kind == PyTokKind.Comma || Current.Kind == PyTokKind.Colon) break;
            if (Current.Kind == PyTokKind.Ident)
            {
                string w = Current.Text;
                if (w == "and" || w == "or") break;
                bool hit = false;
                foreach (var sw in stops) if (w == sw && parts.Count > 0) { hit = true; break; }
                if (hit) break;
                parts.Add(TakeIdent());
                continue;
            }
            if (Current.Kind == PyTokKind.Number) { parts.Add(TakeIdent()); continue; }
            break;
        }
        return string.Join(" ", parts);
    }

    private ZoneScope ParseOwnerWord()
    {
        string w = TakeIdent();
        return w == "enemy" ? ZoneScope.Opponent : w == "either" ? ZoneScope.Any : ZoneScope.Self;
    }

    private DurationKind TryDur()
    {
        if (Current.Kind == PyTokKind.LBracket)
        {
            int save = _pos;
            Advance();
            if (MatchIdent("dur"))
            {
                Match(PyTokKind.Assign);
                string d = TakeIdent();
                Expect(PyTokKind.RBracket, "dur 缺少右方括号");
                return d == "ThisTurn" ? DurationKind.ThisTurn
                     : d == "ThisMatch" ? DurationKind.ThisMatch
                     : d == "UntilEndOfOpponentTurn" ? DurationKind.UntilEndOfOpponentTurn
                     : DurationKind.Permanent;
            }
            _pos = save;
        }
        return DurationKind.Permanent;
    }

    private string TryOutSlot(string fallback)
    {
        if (Current.Kind == PyTokKind.Arrow)
        {
            Advance();
            return Current.Kind == PyTokKind.Slot ? TakeSlot() : TakeIdent();
        }
        return fallback;
    }



    private static ZoneScope OwnerOf(string s)
        => s == "Opponent" || s == "opponent" ? ZoneScope.Opponent
         : s == "Any" || s == "any" ? ZoneScope.Any : ZoneScope.Self;

    private static ActiveZoneScope ZoneOf(string s)
        => s == "Hand" ? ActiveZoneScope.Hand : s == "Deck" ? ActiveZoneScope.Deck
         : s == "ExArea" ? ActiveZoneScope.ExArea : ActiveZoneScope.Board;

    private static TurnOwnerScope TurnOf(string s)
        => s == "Self" ? TurnOwnerScope.Self : s == "Opponent" ? TurnOwnerScope.Opponent : TurnOwnerScope.Any;

    private static ZoneRef ZoneRefOf(string s) => (s ?? "").ToLowerInvariant() switch
    {
        "leader" => ZoneRef.Leader,
        "hand" => ZoneRef.Hand,
        "deck" => ZoneRef.Deck,
        "cemetery" => ZoneRef.Cemetery,
        "banished" => ZoneRef.Banished,
        "exarea" => ZoneRef.ExArea,
        "crestarea" => ZoneRef.CrestArea,
        "cardpool" => ZoneRef.CardPool,
        "globalpool" => ZoneRef.GlobalPool,
        _ => ZoneRef.Board,
    };

    private static ZoneRef ZoneRefOfOld(string s) => s switch
    {
        "Hand" => ZoneRef.Hand,
        "Deck" => ZoneRef.Deck,
        "Cemetery" => ZoneRef.Cemetery,
        "Banished" => ZoneRef.Banished,
        "ExArea" => ZoneRef.ExArea,
        "CrestArea" => ZoneRef.CrestArea,
        "CardPool" => ZoneRef.CardPool,
        "GlobalPool" => ZoneRef.GlobalPool,
        "Leader" => ZoneRef.Leader,
        _ => ZoneRef.Board,
    };

    private static DamageSourceMask ParseMask(string s)
        => s == "Combat" ? DamageSourceMask.Combat : s == "Ability" ? DamageSourceMask.Ability
         : s == "None" ? DamageSourceMask.None : DamageSourceMask.All;

    private static CoreEvent EventOf(string s)
    {
        if (string.IsNullOrEmpty(s)) return CoreEvent.OnPlay;
        // 打印器写的是 on_spellboost / on_select_mode 这种下划线形式，
        // Event 枚举是 OnSpellboost / OnSelectMode。统一去掉下划线再比对，
        // 不能简单 Substring(2) —— 那会得到 "_spellboost" 并解析失败。
        string key = s.StartsWith("On", StringComparison.OrdinalIgnoreCase) && s.Length > 2 ? s.Substring(2) : s;
        key = key.Replace("_", "").ToLowerInvariant();
        return key switch
        {
            "play" or "onplay" or "fanfare" => CoreEvent.OnPlay,
            "destroy" or "ondestroy" or "lastwords" => CoreEvent.OnDestroy,
            "evolve" or "onevolve" => CoreEvent.OnEvolve,
            "superevolve" or "onsuperevolve" => CoreEvent.OnSuperEvolve,
            "attack" or "onattack" or "strike" => CoreEvent.OnAttack,
            "clash" or "onclash" => CoreEvent.OnClash,
            "engage" or "onengage" => CoreEvent.OnEngage,
            "aura" or "passiveaura" => CoreEvent.PassiveAura,
            "turnstart" => CoreEvent.TurnStart,
            "turnend" => CoreEvent.TurnEnd,
            "battlestart" or "onbattlestart" => CoreEvent.BattleStart,
            "enter" or "enterfield" or "onenter" or "onenterfield" => CoreEvent.OnEnterField,
            "leave" or "leavefield" or "onleave" or "onleavefield" => CoreEvent.OnLeaveField,
            "summon" or "onsummon" => CoreEvent.OnSummon,
            "draw" or "ondraw" => CoreEvent.OnDraw,
            "discard" or "ondiscard" => CoreEvent.OnDiscard,
            "returntohand" or "onreturntohand" => CoreEvent.OnReturnToHand,
            "addtohand" or "onaddtohand" => CoreEvent.OnAddToHand,
            "damaged" or "ondamaged" => CoreEvent.OnDamaged,
            "healed" or "onhealed" => CoreEvent.OnHealed,
            "banish" or "onbanish" => CoreEvent.OnBanish,
            "fuse" or "onfuse" => CoreEvent.OnFuse,
            "spellboost" or "onspellboost" => CoreEvent.OnSpellboost,
            "transform" or "ontransform" => CoreEvent.OnTransform,
            "selectmode" or "onselectmode" => CoreEvent.OnSelectMode,
            "earthrite" or "onearthrite" => CoreEvent.OnEarthRite,
            "deckout" or "ondeckout" => CoreEvent.OnDeckOut,
            "countdownexpire" or "oncountdownexpire" => CoreEvent.OnCountdownExpire,
            "counterchange" or "oncounterchange" => CoreEvent.OnCounterChange,
            "statsmodified" or "onstatsmodified" => CoreEvent.OnStatsModified,
            _ => Enum.TryParse<CoreEvent>(s, true, out var ev) ? ev : CoreEvent.OnPlay,
        };
    }

    // ---------------------------------------------------------------- 游标

    /// <summary>解析步数预算：超过 token 数的 50 倍就判定为失控，直接抛错。</summary>
    /// <remarks>
    /// 起因：初版解析器缺少「无进展」保护，某个分支返回 null 却没消费 token 时，
    /// 外层 while 会永远停在同一位置 —— 整机卡死而不是报错。
    /// 预算是最外层兜底：即使某处护栏漏了，也只会失败不会挂死。
    /// </remarks>
    private int _budget;
    private void ConsumeBudget()
    {
        if (_budget-- <= 0) throw new LangException("AST 解析超出步数预算，疑似死循环");
    }

    private bool IsAtEnd => _pos >= _tokens.Count;
    private PyToken Current => _pos < _tokens.Count ? _tokens[_pos] : new PyToken(PyTokKind.Eof, "<eof>", 0, 0);
    private string PeekText(int n) => _pos + n < _tokens.Count ? _tokens[_pos + n].Text : "";

    private void Advance() { ConsumeBudget(); if (_pos < _tokens.Count) _pos++; }
    private bool Match(PyTokKind k) { if (!IsAtEnd && Current.Kind == k) { _pos++; return true; } return false; }
    private bool MatchIdent(string s) { if (!IsAtEnd && Current.Kind == PyTokKind.Ident && Current.Text == s) { _pos++; return true; } return false; }
    private void EatIdent(string s) { MatchIdent(s); }
    private void SkipNewlines() { while (!IsAtEnd && Current.Kind == PyTokKind.Newline) _pos++; }

    private void Expect(PyTokKind k, string msg) { if (!Match(k)) Error(msg); }

    private int TakeNumber() { if (Current.Kind == PyTokKind.Number) { int v = Current.IntVal; Advance(); return v; } return 0; }
    private string TakeString() { if (Current.Kind == PyTokKind.String) { string s = Current.Text; Advance(); return s; } return ""; }
    private string TakeSlot() { if (Current.Kind == PyTokKind.Slot) { string s = Current.Text; Advance(); return s; } return TakeIdent(); }
    private string TakeIdent()
    {
        if (!IsAtEnd && (Current.Kind == PyTokKind.Ident || Current.Kind == PyTokKind.Slot)) { string s = Current.Text; Advance(); return s; }
        if (!IsAtEnd && Current.Kind == PyTokKind.Number) { string s = Current.Text; Advance(); return s; }
        return "";
    }

    private void Error(string msg) { Errors.Add("第 " + Current.Line + " 行：" + msg); }
}

/// <summary>子卡段（crest/faith/accelerate/crystallize）不是能力，遇到时用它退出能力解析。</summary>
public sealed class SubDefException : Exception { }
