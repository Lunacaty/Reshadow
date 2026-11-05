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

        SkipNewlines();
        if (Current.Kind == PyTokKind.Indent)
        {
            Advance();
            SkipNewlines();
            while (!IsAtEnd && Current.Kind != PyTokKind.Dedent)
            {
                if (Current.Kind == PyTokKind.Ident && (Current.Text == "traits" || Current.Text == "keywords"))
                {
                    string head = TakeIdent();
                    Expect(PyTokKind.Colon, head + " 后缺少冒号");
                    if (Match(PyTokKind.LBracket))
                    {
                        while (!IsAtEnd && Current.Kind != PyTokKind.RBracket)
                        {
                            if (Current.Kind == PyTokKind.Ident)
                            {
                                string nm = TakeIdent();
                                if (head == "traits") traits.Add(_schema.Trait(nm));
                                else keywords.Add(_schema.Keyword(nm));
                            }
                            else Advance();
                            Match(PyTokKind.Comma);
                        }
                        Expect(PyTokKind.RBracket, head + " 缺少右方括号");
                    }
                    SkipNewlines();
                    continue;
                }
                int guardCard = _pos;
                var ab = ParseAbility();
                if (ab != null) abilities.Add(ab);
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
            IsToken = attrs.ContainsKey("token"),
        };
    }
    // ---------------------------------------------------------------- 能力

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
            if (w == "or") { Advance(); secondary = EventOf(TakeIdent()); secondarySet = true; continue; }
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
                return new ReplaceDeckNode { DeckName = dn };
            }
            case "mark_victory_card": return new MarkVictoryCardNode();
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
        NumExpr count = NumExpr.Const(1);
        if (Current.Kind == PyTokKind.Number || Current.Kind == PyTokKind.Minus) count = ParseNumExpr();
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
        return new SpawnCardNode
        {
            SourceKind = node.SourceKind, CopyFrom = node.CopyFrom, ToZone = node.ToZone,
            ToOwner = node.ToOwner, Count = node.Count, AddDepartedTrait = node.AddDepartedTrait,
            OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
    }

    private SpawnCardNode ParseGainCrest()
    {
        string name = Current.Kind == PyTokKind.String ? TakeString() : TakeIdent();
        int id = 0;
        if (Current.Kind == PyTokKind.Ident && Current.Text == "id") { Advance(); Match(PyTokKind.Assign); id = TakeNumber(); }
        if (id <= 0 && _lex != null && _lex.TryGetCardId(name, out int r0)) id = r0;
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CardId, CardId = id, CardName = name, IsCrest = true,
            ToZone = ZoneRef.CrestArea, Count = NumExpr.Const(1), OutSlot = TryOutSlot(TargetSpec.SlotCreated),
        };
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
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CardId, CardId = id, CardName = name,
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
        string opWord = Current.Kind == PyTokKind.Ident ? Current.Text : "by";
        if (opWord == "by" || opWord == "=" || opWord == "fill") Advance();
        var op = opWord == "=" ? CounterOp.Set : (opWord == "fill" ? CounterOp.Fill : CounterOp.Add);
        if (Current.Kind == PyTokKind.Ident && Current.Text == "on")
        {
            Advance();
            var tgt = ParseTarget();
            var d = ParseNumExpr();
            return new ModifyCounterNode { Counter = _schema.Counter(name), Op = op, Delta = d, Target = tgt };
        }
        var delta = ParseNumExpr();
        return new ModifyCounterNode { Counter = _schema.Counter(name), Op = op, Delta = delta };
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
        bool neg = false;
        if (Current.Kind == PyTokKind.Plus) Advance();
        else if (Current.Kind == PyTokKind.Minus) { Advance(); neg = true; }
        var e = ParseNumExpr();
        return neg ? (NumExpr)NumExpr.Sub(NumExpr.Const(0), e) : e;
    }

    private ModifyStatsNode ParseSwapStats()
    {
        return new ModifyStatsNode { Target = ParseTarget(), Op = StatModOp.Swap };
    }

    private ModifyKeywordNode ParseGrantKeyword()
    {
        string kw = TakeIdent();
        NumExpr val = null;
        if (Match(PyTokKind.LParen)) { val = ParseNumExpr(); Expect(PyTokKind.RParen, "关键词参数缺少右括号"); }
        EatIdent("to");
        var tgt = ParseTarget();
        return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Grant, Keyword = _schema.Keyword(kw), Value = val, Duration = TryDur() };
    }

    private ModifyKeywordNode ParseRemoveKeyword()
    {
        string kw = TakeIdent();
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
        var inner = ParseIndentedBlock();
        var granted = new AbilityDef { Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura }, Condition = BoolExpr.True, Body = inner };
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
        var inner = ParseIndentedBlock();
        var granted = new AbilityDef { Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura }, Condition = BoolExpr.True, Body = inner };
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

    private TargetSpec ParseTarget(ZoneRef? defaultZone = null)
    {
        if (Current.Kind == PyTokKind.Slot)
        {
            string s = TakeSlot();
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = s };
        }
        var toks = new List<Token>();
        int depth = 0;
        while (!IsAtEnd)
        {
            var t = Current;
            if (t.Kind == PyTokKind.Newline || t.Kind == PyTokKind.Dedent || t.Kind == PyTokKind.Eof) break;
            if (depth == 0)
            {
                if (t.Kind == PyTokKind.Arrow) break;
                if (t.Kind == PyTokKind.Plus && PeekText(1) == "append") break;
                if (t.Kind == PyTokKind.Ident && (t.Text == "to" || t.Text == "destroy" || t.Text == "combat" || t.Text == "split")) break;
            }
            // 与开头配对的右括号属于外层（on(...) / vs(...) 的收尾），不能收进目标。
            if (t.Kind == PyTokKind.RParen && depth == 0) break;
            if (t.Kind == PyTokKind.LParen) depth++;
            if (t.Kind == PyTokKind.RParen) depth = Math.Max(0, depth - 1);
            toks.Add(ToCnToken(t));
            Advance();
        }
        if (toks.Count == 0)
            return defaultZone.HasValue ? TargetSpec.InZone(defaultZone.Value, ZoneScope.Self) : null;

        // 直接构造 TargetSpec，不借道 TargetParser（见 DirectTarget 的说明）。
        var direct = DirectTarget(toks);
        if (direct != null) return direct;

        var lex = _lex ?? new Lexicon(_schema);
        var parser = new TargetParser(lex);
        try
        {
            var spec = parser.TryParse(new TokenCursor(toks));
            if (spec != null) return spec;
        }
        catch (Exception ex) { Debug.Add("ParseTarget 异常: " + ex.Message + " 串=" + string.Join("|", toks.Select(x => x.Text))); }
        Debug.Add("ParseTarget 失败: " + string.Join("|", toks.Select(x => x.Text)));
        return defaultZone.HasValue ? TargetSpec.InZone(defaultZone.Value, ZoneScope.Self) : null;
    }



    /// <summary>
    /// 把 self:board&lt;Follower&gt; Choose(1) where ... 这类 DSL 速记
    /// 展开成 TargetParser 能读的英文短语。
    /// </summary>
    private static List<Token> ExpandZoneShorthand(List<Token> toks)
    {
        if (toks == null || toks.Count == 0) return toks;
        var outp = new List<Token>();
        int i = 0;

        if (toks.Count >= 3 && toks[1].Text == ":")
        {
            string owner = toks[0].Text.ToLowerInvariant();
            string zone = toks[2].Text.ToLowerInvariant();
            string ownerWord = owner == "opponent" || owner == "opp" ? "enemy" : (owner == "any" ? "any" : "allied");

            if (zone == "leader")
            {
                outp.Add(new Token { Kind = TokKind.Word, Text = ownerWord == "enemy" ? "enemy" : "your" });
                outp.Add(new Token { Kind = TokKind.Word, Text = "leader" });
                i = 3;
            }
            else
            {
                if (ownerWord.Length > 0) outp.Add(new Token { Kind = TokKind.Word, Text = ownerWord });
                outp.Add(new Token { Kind = TokKind.Word, Text = "__N__" + zone });
                i = 3;
            }
        }

        if (i < toks.Count && toks[i].Text == "<")
        {
            int close = i + 1;
            while (close < toks.Count && toks[close].Text != ">") close++;
            string kind = i + 1 < toks.Count ? toks[i + 1].Text.ToLowerInvariant() : "";
            string noun = kind == "spell" ? "spells" : kind == "amulet" ? "amulets" : "followers";
            outp.Add(new Token { Kind = TokKind.Word, Text = noun });
            i = close + 1;
        }

        // 选取模式：Choose(n) / All / Random(n) / First(n)
        bool sawChoose = false;
        int chooseN = 1;
        bool sawExplicitMode = false;
        if (i < toks.Count)
        {
            string mode = toks[i].Text;
            if (mode == "Choose" || mode == "Random" || mode == "First")
            {
                i++;
                if (i < toks.Count && toks[i].Text == "(")
                {
                    i++;
                    if (i < toks.Count && toks[i].Kind == TokKind.Number) { int.TryParse(toks[i].Text, out chooseN); i++; }
                    if (i < toks.Count && toks[i].Text == ")") i++;
                }
                sawChoose = true;
            }
            else if (mode == "All") { sawExplicitMode = true; i++; }
        }

        // where <BoolExpr>：转成英文 "with ..." 结构交给条件解析
        List<Token> whereToks = null;
        if (i < toks.Count && toks[i].Text == "where")
        {
            i++;
            whereToks = new List<Token>();
            for (; i < toks.Count; i++) whereToks.Add(toks[i]);
        }

        // 组装：把 __N__ 占位符展开
        var final = new List<Token>();
        foreach (var t in outp)
        {
            if (!t.Text.StartsWith("__N__")) { final.Add(t); continue; }
            string zone = t.Text.Substring(5);
            string phrase = zone == "board" ? "on the field"
                          : zone == "hand" ? "in your hand"
                          : zone == "deck" ? "in your deck"
                          : zone == "cemetery" ? "in your cemetery"
                          : zone == "banished" ? "in your banished"
                          : zone == "crestarea" ? "in your crest area"
                          : "on the field";
            foreach (var w in phrase.Split(' ')) final.Add(new Token { Kind = TokKind.Word, Text = w });
        }

        foreach (var t in WhereToEnglish(whereToks)) final.Add(t);

        // Choose(n) 等价于 "choose n" —— TargetParser 用 "N random/any" 或 "choose" 表达
        // Choose(n) 必须无条件保留：连 n=1 也不能丢，
        // 否则 TargetParser 会默认成 All，弹窗变成「选全部」。
        // Choose(n) 在 TargetParser 里的表达就是「纯数字 + 名词」（不带 random 词）：
        //   2 followers -> mode=Choose, count=2
        //   2 random followers -> mode=Random, count=2
        // 所以这里只插数字，绝不能插 random。
        if (sawChoose)
        {
            // 数量必须插在**归属词之后、名词之前**：
            //   "allied followers on the field" -> "allied 1 followers on the field"
            // 直接 Insert(0) 会变成 "1 allied followers ..."，
            // TargetParser 认「数字紧跟名词」，前面隔了归属词就退化成 All。
            int insertAt = 0;
            for (int k = 0; k < final.Count; k++)
            {
                string tx = final[k].Text.ToLowerInvariant();
                if (tx == "allied" || tx == "enemy" || tx == "any") { insertAt = k + 1; break; }
            }
            final.Insert(insertAt, new Token { Kind = TokKind.Number, Text = chooseN.ToString(), Value = chooseN });
        }
        return final;
    }

    /// <summary>把 where 之后的 DSL 布尔式翻成英文短语（认常见的几种）。</summary>
    private static List<Token> WhereToEnglish(List<Token> whereToks)
    {
        var outp = new List<Token>();
        if (whereToks == null || whereToks.Count == 0) return outp;
        bool needAnd = false;
        for (int i = 0; i < whereToks.Count; i++)
        {
            var t = whereToks[i];
            string w = t.Text.ToLowerInvariant();
            if (w == "and") continue;
            if (w == "is") continue;
            if (w == "(" || w == ")") continue;

            if (w == "has" && i + 1 < whereToks.Count && whereToks[i + 1].Text == "trait")
            {
                if (i + 2 < whereToks.Count)
                {
                    outp.Add(new Token { Kind = TokKind.Word, Text = "with" });
                    outp.Add(new Token { Kind = TokKind.Word, Text = "the" });
                    outp.Add(new Token { Kind = TokKind.Word, Text = "trait" });
                    outp.Add(whereToks[i + 2]);
                    i += 2;
                }
                continue;
            }
            if (w == "other")
            {
                outp.Add(new Token { Kind = TokKind.Word, Text = "other" });
                continue;
            }
            // `is Follower` / `is Spell` / `is Amulet` —— 卡种名词是英文里的普通名词，
            // 直接透传即可，但必须给它补一个名词位置而不是当作独立子句。
            if (w == "follower" || w == "followers" || w == "spell" || w == "spells"
                || w == "amulet" || w == "amulets")
            {
                outp.Add(t);
                continue;
            }
            if (w == "not" && i + 1 < whereToks.Count && whereToks[i + 1].Text == "is")
            {
                outp.Add(new Token { Kind = TokKind.Word, Text = "not" });
                i++;
                continue;
            }
            outp.Add(t);
        }
        return outp;
    }


    /// <summary>
    /// 直接把 DSL 速记解析成 TargetSpec，不再借道 TargetParser。
    ///
    /// 起初想复用 TargetParser（把 self:board 翻成英文短语再喂进去），
    /// 但那条路的语义鸿沟太大：Choose/All/Random 的表达方式、
    /// 归属词与名词的先后、where 子句的语法都不同，转译层越写越像平行实现。
    /// 直接构造 TargetSpec 反而更短、更确定。
    /// </summary>
    private TargetSpec DirectTarget(List<Token> toks)
    {
        if (toks == null || toks.Count == 0) return null;
        var spec = new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.Board, Mode = SelectMode.All };
        int i = 0;

        // owner : zone
        if (toks.Count >= 3 && toks[1].Text == ":")
        {
            string owner = toks[0].Text.ToLowerInvariant();
            string zone = toks[2].Text.ToLowerInvariant();
            spec = WithOwner(spec, owner == "opponent" || owner == "opp" ? ZoneScope.Opponent
                                  : owner == "any" ? ZoneScope.Any : ZoneScope.Self);
            spec = WithZone(spec, zone == "hand" ? ZoneRef.Hand
                                 : zone == "deck" ? ZoneRef.Deck
                                 : zone == "cemetery" ? ZoneRef.Cemetery
                                 : zone == "banished" ? ZoneRef.Banished
                                 : zone == "leader" ? ZoneRef.Leader
                                 : zone == "crestarea" ? ZoneRef.CrestArea : ZoneRef.Board);
            i = 3;
        }
        else if (toks[0].Text.StartsWith("$"))
        {
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = toks[0].Text, Mode = SelectMode.All };
        }

        // <Kind>
        bool sawKind = false;
        if (i < toks.Count && toks[i].Text == "<")
        {
            i++;
            if (i < toks.Count)
            {
                string kind = toks[i].Text.ToLowerInvariant();
                var k = kind == "spell" ? CardKind.Spell : kind == "amulet" ? CardKind.Amulet : CardKind.Follower;
                spec = spec.WithWhere(new HasKindExpr { Kind = k, Of = TargetSpec.Item });
                sawKind = true;
                i++;
            }
            if (i < toks.Count && toks[i].Text == ">") i++;
        }

        // 选取模式
        bool sawExplicitMode = false;
        if (i < toks.Count)
        {
            string mode = toks[i].Text;
            if (mode == "Choose" || mode == "Random" || mode == "First")
            {
                var m = mode == "Random" ? SelectMode.Random : mode == "First" ? SelectMode.First : SelectMode.Choose;
                spec = WithMode(spec, m);
                sawExplicitMode = true;
                i++;
                if (i < toks.Count && toks[i].Text == "(")
                {
                    i++;
                    if (i < toks.Count && toks[i].Kind == TokKind.Number && int.TryParse(toks[i].Text, out int n))
                    {
                        spec = WithCount(spec, NumExpr.Const(n));
                        i++;
                    }
                    if (i < toks.Count && toks[i].Text == ")") i++;
                }
            }
            else if (mode == "All") { sawExplicitMode = true; i++; }
        }

        // where 子句
        if (i < toks.Count && toks[i].Text == "where")
        {
            i++;
            var wh = new List<Token>();
            for (; i < toks.Count; i++) wh.Add(toks[i]);
            var wspec = ParseWhereTokens(wh, spec);
            if (wspec != null) spec = wspec;
        }

        // 只有「战场/手牌 + 卡种」这类集合目标才默认 Choose(1)。
        // 主战者（leader）不该被当成可选目标 —— 它本来就是单数。
        if (spec.Mode == SelectMode.All && spec.Count == null && !sawExplicitMode
            && spec.Zone != ZoneRef.Leader)
            spec = WithMode(spec, SelectMode.Choose);

        return spec;
    }

    /// <summary>解析 where 之后的 DSL 布尔式，附加到 spec 的 Where 上。</summary>
    private TargetSpec ParseWhereTokens(List<Token> wh, TargetSpec spec)
    {
        if (wh == null || wh.Count == 0) return spec;
        // 去掉最外层成对括号（可能多层）
        for (int guard = 0; guard < 8; guard++)
        {
            if (wh.Count < 2 || wh[0].Text != "(" || wh[wh.Count - 1].Text != ")") break;
            int d = 0; bool balanced = true;
            for (int k = 0; k < wh.Count; k++)
            {
                if (wh[k].Text == "(") d++;
                else if (wh[k].Text == ")")
                {
                    d--;
                    if (d == 0 && k < wh.Count - 1) { balanced = false; break; }
                }
            }
            if (!balanced) break;
            wh = wh.Skip(1).Take(wh.Count - 2).ToList();
        }

        // 按顶层 and 切分
        var parts = new List<List<Token>>();
        var curPart = new List<Token>();
        int depth = 0;
        foreach (var t in wh)
        {
            if (t.Text == "(") depth++;
            if (t.Text == ")") depth = Math.Max(0, depth - 1);
            if (depth == 0 && t.Text == "and") { parts.Add(curPart); curPart = new List<Token>(); continue; }
            curPart.Add(t);
        }
        parts.Add(curPart);

        foreach (var part in parts)
        {
            if (part.Count == 0) continue;
            if (part[0].Text == "is" && part.Count >= 2)
            {
                string kind = part[1].Text.ToLowerInvariant();
                var k = kind.StartsWith("spell") ? CardKind.Spell : kind.StartsWith("amulet") ? CardKind.Amulet : CardKind.Follower;
                spec = spec.WithWhere(new HasKindExpr { Kind = k, Of = TargetSpec.Item });
                continue;
            }
            if (part[0].Text == "has" && part.Count >= 3 && part[1].Text == "trait")
            {
                spec = spec.WithWhere(new HasTraitExpr { Trait = _schema.Trait(part[2].Text), Of = TargetSpec.Item });
                continue;
            }
            if (part[0].Text == "has" && part.Count >= 3 && part[1].Text == "clan")
            {
                spec = spec.WithWhere(new HasClanExpr { Clan = _schema.Clan(part[2].Text), Of = TargetSpec.Item });
                continue;
            }
            if (part[0].Text == "other")
            {
                spec = spec.WithWhere(new NotSameEntityExpr { Of = TargetSpec.Item, ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf });
                continue;
            }
            if (part[0].Text == "not" && part.Count >= 2 && part[1].Text == "is" && part.Count >= 3)
            {
                // not is Evolved 之类交给条件解析
                var bt = new List<Token>();
                for (int k = 1; k < part.Count; k++) bt.Add(part[k]);
                try
                {
                    var lex = _lex ?? new Lexicon(_schema);
                    var b = ConditionCompiler.Build(bt, lex);
                    if (b != null) spec = spec.WithWhere(new NotExpr(b));
                }
                catch { }
                continue;
            }
        }
        return spec;
    }

    private static TargetSpec WithOwner(TargetSpec t, ZoneScope o) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = o, Zone = t.Zone, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
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

    private NumExpr ParseNumExpr()
    {
        bool neg = false;
        if (Current.Kind == PyTokKind.Minus) { Advance(); neg = true; }
        if (Current.Kind == PyTokKind.Number)
        {
            int v = TakeNumber();
            return neg ? (NumExpr)NumExpr.Const(-v) : NumExpr.Const(v);
        }
        if (Current.Kind == PyTokKind.Ident && Current.Text.StartsWith("ctr:"))
        {
            string nm = Current.Text.Substring(4);
            Advance();
            return NumExpr.Ctr(_schema.Counter(nm));
        }

        var toks = new List<Token>();
        int depth = 0;
        while (!IsAtEnd)
        {
            var t = Current;
            if (depth == 0 && (t.Kind == PyTokKind.Newline || t.Kind == PyTokKind.Dedent || t.Kind == PyTokKind.Eof)) break;
            if (depth == 0 && t.Kind == PyTokKind.Slash) break;
            if (depth == 0 && t.Kind == PyTokKind.Ident && (t.Text == "damage" || t.Text == "life" || t.Text == "to" || t.Text == "times")) break;
            if (t.Kind == PyTokKind.LParen) depth++;
            if (t.Kind == PyTokKind.RParen) depth = Math.Max(0, depth - 1);
            toks.Add(ToCnToken(t));
            Advance();
        }
        if (toks.Count == 0) return NumExpr.Const(neg ? -1 : 1);

        var lex = _lex ?? new Lexicon(_schema);
        var cur = new TokenCursor(toks);
        try
        {
            var comp = new EnglishCompiler(lex);
            var e = ActionParser.NumExprForDsl(cur, lex);
            if (e != null) return e;
        }
        catch { }
        return NumExpr.Const(1);
    }

    private BoolExpr ParseBoolExpr()
    {
        var toks = new List<Token>();
        int depth = 0;
        while (!IsAtEnd)
        {
            var t = Current;
            // 冒号在 DSL 里有两种角色：
            //   · 块定界符（能力头 / option 行的末尾）—— 遇到就该停；
            //   · 计数器名的一部分（ctr:pp_at_play）—— 必须连着读。
            // 判据：前一 token 是 ctr 时，这个冒号属于名字。
            if (depth == 0 && t.Kind == PyTokKind.Colon)
            {
                bool isCounterName = toks.Count > 0 && toks[^1].Text == "ctr";
                if (!isCounterName) break;
            }
            if (depth == 0 && (t.Kind == PyTokKind.Newline || t.Kind == PyTokKind.Dedent || t.Kind == PyTokKind.Eof)) break;
            if (t.Kind == PyTokKind.LParen) depth++;
            if (t.Kind == PyTokKind.RParen) depth = Math.Max(0, depth - 1);
            toks.Add(ToCnToken(t));
            Advance();
        }
        if (toks.Count == 0) return BoolExpr.True;

        // ctr:<name> <op> <n> 是打印器的专用写法，ConditionCompiler 不认识。
        // 直接构造 CompareExpr，比让条件编译器去猜可靠。
        var cmp = TryParseCounterCompare(toks);
        if (cmp != null) return cmp;
        var direct = TryParseSimpleNot(toks);
        if (direct != null) return direct;

        var lex = _lex ?? new Lexicon(_schema);
        try { return ConditionCompiler.Build(toks, lex) ?? BoolExpr.True; }
        catch { return BoolExpr.True; }
    }


    /// <summary>识别 ctr : name op num 形式的计数器比较。</summary>
    private BoolExpr TryParseCounterCompare(List<Token> toks)
    {
        if (toks.Count < 5) return null;
        if (toks[0].Text != "ctr" || toks[1].Text != ":") return null;
        string name = toks[2].Text;
        CompareOp? op = toks[3].Text switch
        {
            ">=" => CompareOp.Ge,
            "<=" => CompareOp.Le,
            ">" => CompareOp.Gt,
            "<" => CompareOp.Lt,
            "==" => CompareOp.Eq,
            "=" => CompareOp.Eq,
            "!=" => CompareOp.Ne,
            _ => (CompareOp?)null,
        };
        if (op == null) return null;
        if (!int.TryParse(toks[4].Text, out int n)) return null;
        return new CompareExpr { Left = NumExpr.Ctr(_schema.Counter(name)), Op = op.Value, Right = NumExpr.Const(n) };
    }


    /// <summary>识别 not is Xxx($self) 这类打印器专用形态。</summary>
    private BoolExpr TryParseSimpleNot(List<Token> toks)
    {
        if (toks.Count < 3) return null;
        if (toks[0].Text != "not" || toks[1].Text != "is") return null;
        string name = toks[2].Text;
        int paren = name.IndexOf('(');
        if (paren > 0) name = name.Substring(0, paren);
        var key = name.ToLowerInvariant();
        if (key == "superevolved") return new NotExpr(new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Self });
        if (key == "evolved") return new NotExpr(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self });
        return null;
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

    private static TargetSpec WithZone(TargetSpec t, ZoneRef z) => new TargetSpec
    {
        SourceKind = t.SourceKind, Owner = t.Owner, Zone = z, Slot = t.Slot,
        CardId = t.CardId, CardName = t.CardName, Where = t.Where,
        Sort = t.Sort, SortDescending = t.SortDescending, TopN = t.TopN,
        DistinctByName = t.DistinctByName, DuplicatesOnly = t.DuplicatesOnly,
        Mode = t.Mode, Count = t.Count, History = t.History,
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

    private static ZoneScope OwnerOf(string s)
        => s == "Opponent" || s == "opponent" ? ZoneScope.Opponent
         : s == "Any" || s == "any" ? ZoneScope.Any : ZoneScope.Self;

    private static ActiveZoneScope ZoneOf(string s)
        => s == "Hand" ? ActiveZoneScope.Hand : s == "Deck" ? ActiveZoneScope.Deck
         : s == "ExArea" ? ActiveZoneScope.ExArea : ActiveZoneScope.Board;

    private static TurnOwnerScope TurnOf(string s)
        => s == "Self" ? TurnOwnerScope.Self : s == "Opponent" ? TurnOwnerScope.Opponent : TurnOwnerScope.Any;

    private static ZoneRef ZoneRefOf(string s)
        => s == "Hand" ? ZoneRef.Hand : s == "Deck" ? ZoneRef.Deck
         : s == "Cemetery" ? ZoneRef.Cemetery : s == "Banished" ? ZoneRef.Banished
         : s == "ExArea" ? ZoneRef.ExArea : s == "CrestArea" ? ZoneRef.CrestArea
         : s == "CardPool" ? ZoneRef.CardPool : s == "GlobalPool" ? ZoneRef.GlobalPool : ZoneRef.Board;

    private static DamageSourceMask ParseMask(string s)
        => s == "Combat" ? DamageSourceMask.Combat : s == "Ability" ? DamageSourceMask.Ability
         : s == "None" ? DamageSourceMask.None : DamageSourceMask.All;

    private static CoreEvent EventOf(string s)
    {
        string key = s.StartsWith("On") && s.Length > 2 ? s : "On" + s;
        return Enum.TryParse<CoreEvent>(key, true, out var ev) ? ev : CoreEvent.OnPlay;
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
