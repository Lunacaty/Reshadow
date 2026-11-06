using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowverseFan.KernelV3.Ast;
using Ast = ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Packs;

public static class Program
{
    static readonly string Root = @"E:/text/reshadow/game/";
    static readonly string Out = Root + ".auditB/";
    static Dictionary<string, JsonElement> cards;
    static GameSchema schema;
    static Lexicon lex;
    static StringBuilder log = new();

    static void W(string s) { Console.WriteLine(s); log.AppendLine(s); }

    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var doc = JsonDocument.Parse(File.ReadAllText(Out + "pack_extract.json"));
        cards = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        schema = GameSchema.CreateDefault();
        lex = new Lexicon(schema);

        // Rebuild the exact lexicon GlobalCatalog.Build() uses from pack data.
        var pack = JsonDocument.Parse(File.ReadAllText(Root + "data/packs/official-sv/pack.json"));
        foreach (var t in pack.RootElement.GetProperty("traits").EnumerateArray()) lex.AddTrait(t.GetProperty("name").GetString());
        foreach (var t in pack.RootElement.GetProperty("clans").EnumerateArray()) lex.AddClan(t.GetProperty("name").GetString());
        foreach (var t in pack.RootElement.GetProperty("keywords").EnumerateArray()) lex.AddKeyword(t.GetProperty("name").GetString());
        if (pack.RootElement.TryGetProperty("counters", out var ctrs))
            foreach (var t in ctrs.EnumerateArray()) lex.Counter(t.GetProperty("name").GetString());
        foreach (var kv in cards)
        {
            string auth = AuthoritativeNames.EnglishOf(int.Parse(kv.Key));
            if (!string.IsNullOrWhiteSpace(auth)) lex.AddCard(auth, int.Parse(kv.Key));
            lex.AddCard(kv.Value.GetProperty("name").GetString(), int.Parse(kv.Key));
        }
        lex.Build();

        string mode = args.Length > 0 ? args[0] : "all";
        if (mode == "all" || mode == "roundtrip") Roundtrip();
        if (mode == "all" || mode == "recompile") Recompile();
        if (mode == "all" || mode == "patterns") Patterns();
        if (mode == "all" || mode == "vars") Vars();
        if (mode == "all" || mode == "keywords") Keywords();
        if (mode == "all" || mode == "micro") Micro();
        if (mode == "all" || mode == "semantic") Semantic();
        if (mode == "all" || mode == "astaudit") AstAudit();
        if (mode == "all" || mode == "evalx") EvalX();
        if (mode == "all" || mode == "fallback") Fallback();
        File.WriteAllText(Out + "harness_" + mode + ".log", log.ToString(), Encoding.UTF8);
    }

    // Compare pack PyAST abilities with a fresh English recompile as AST objects.
    static string AbilitiesOnly(string cardPy)
    {
        var lines = (cardPy ?? "").Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        foreach (var ln in lines)
        {
            if (ln.StartsWith("card ") || ln.StartsWith("    traits:") || ln.StartsWith("    keywords:")
                || ln.StartsWith("    fuse:") || ln.StartsWith("    deck_mod:")
                || ln.StartsWith("    innate_counter:") || ln.StartsWith("    countdown(")) continue;
            if (ln.Trim().Length == 0) continue;
            kept.Add(ln);
        }
        // Sub-definitions (crest/faith) are included in fresh AST separately; drop them here.
        int sub = kept.FindIndex(l => l.StartsWith("    crest ") || l.StartsWith("    faith ") || l.StartsWith("    accelerate ") || l.StartsWith("    crystallize "));
        if (sub >= 0) kept = kept.Take(sub).ToList();
        return string.Join("\n", kept).TrimEnd();
    }


    static void WalkBool(BoolExpr e, Action<BoolExpr> f)
    {
        if (e == null) return;
        f(e);
        if (e is AndExpr a) foreach (var x in a.Items ?? Array.Empty<BoolExpr>()) WalkBool(x, f);
        else if (e is OrExpr o) foreach (var x in o.Items ?? Array.Empty<BoolExpr>()) WalkBool(x, f);
        else if (e is NotExpr n) WalkBool(n.Inner, f);
        else if (e is CompareExpr cp) { WalkNum(cp.Left, null); WalkNum(cp.Right, null); }
        else if (e is ExistsExpr ex && ex.Target != null) WalkBool(ex.Target.Where, f);
    }

    static void WalkNum(NumExpr e, Action<NumExpr> f)
    {
        if (e == null) return;
        if (f != null) f(e);
        foreach (var a in e.Args ?? Array.Empty<NumExpr>()) WalkNum(a, f);
        if (e.Of != null) WalkBool(e.Of.Where, x => { });
    }

    static void WalkNodeFull(EffectNode n, Action<EffectNode> f)
    {
        if (n == null) return;
        f(n);
        if (n is ConditionalNode c) { WalkBool(c.Condition, x => { }); foreach (var x in c.Then ?? Array.Empty<EffectNode>()) WalkNodeFull(x, f); foreach (var x in c.Else ?? Array.Empty<EffectNode>()) WalkNodeFull(x, f); }
        else if (n is LoopNode l) foreach (var x in l.Body ?? Array.Empty<EffectNode>()) WalkNodeFull(x, f);
        else if (n is BranchModeNode b && b.Options != null) foreach (var o in b.Options) foreach (var x in o ?? Array.Empty<EffectNode>()) WalkNodeFull(x, f);
    }

    static void AstAudit()
    {
        W("=== AST FALLBACK / NODE AUDIT ===");
        int n = 0, noneState = 0, unsupportedNode = 0, emptyBody = 0, nullCond = 0;
        var known = new HashSet<string>(new[]{"RevealCardNode","DamageModifierNode","SelectTargetNode","DealDamageNode","RestoreLifeNode","ModifyStatsNode","ModifyCostNode","ModifyKeywordNode","ModifyCounterNode","MoveZoneNode","SpawnCardNode","ReplaceDeckNode","EvolveNode","TransformNode","MarkVictoryCardNode","ConditionalNode","LoopNode","BranchModeNode","GrantAbilityNode","GrantCounterAbilityNode","InvokeAbilityNode","MacroNode"});
        var noneIds = new List<string>(); var unknown = new Dictionary<string, List<string>>(); var emptyIds = new List<string>();
        foreach (var kv in cards.OrderBy(q => int.Parse(q.Key)))
        {
            var pp = new AstCanonicalParser(schema, lex);
            var d = pp.ParseCard(S(kv.Value, "text")); n++;
            foreach (var a in d?.Abilities ?? Array.Empty<AbilityDef>())
            {
                if (a.Body == null || a.Body.Count == 0) { emptyBody++; if (emptyIds.Count < 20) emptyIds.Add(kv.Key + ":" + a.Trigger?.Event); }
                if (a.Condition == null) nullCond++;
                WalkBool(a.Condition, b => { if (b is HasStateExpr hs && hs.State == UnitState.None) { noneState++; if (noneIds.Count < 30) noneIds.Add(kv.Key); } });
                foreach (var nd in a.Body ?? Array.Empty<EffectNode>()) WalkNodeFull(nd, x => {
                    string tn = x.GetType().Name;
                    if (!known.Contains(tn)) { unsupportedNode++; if (!unknown.ContainsKey(tn)) unknown[tn] = new List<string>(); unknown[tn].Add(kv.Key); }
                });
            }
        }
        W("cards=" + n + " empty_ability_bodies=" + emptyBody + " (countdown-only expected) ids=" + string.Join(",", emptyIds.Take(20)));
        W("null_conditions=" + nullCond + " HasStateExpr(None)=" + noneState + " ids=" + string.Join(",", noneIds.Distinct().Take(30)));
        W("vm_unhandled_node_instances=" + unsupportedNode + " types=" + string.Join(",", unknown.Select(x => x.Key + "[" + x.Value.Count + "] e.g. " + string.Join(",", x.Value.Take(3)))));
    }

    static void EvalX()
    {
        W("=== NUMERIC EVAL X ===");
        foreach (var id in new[] { "10111130", "10114130", "10163110", "10131320" })
        {
            var c = cards[id];
            var pp = new AstCanonicalParser(schema, lex);
            var d = pp.ParseCard(S(c, "text"));
            // Build a battle world with N cards in hand for count-based probes.
            var st = new BattleState(schema, seed: 7);
            var probe = new Ast.CardDef { CardId = 777777, Name = "probe", Kind = CardKind.Follower, BaseCost = 1, BaseAttack = 1, BaseLife = 1 };
            st.RegisterCard(probe);
            for (int i = 0; i < 4; i++)
                st.Players[0].Hand.Add(new RuntimeEntity { EntityId = 800000 + i, Def = probe, PlayerId = 0, Zone = ZoneRef.Hand, Cost = 1, Attack = 1, Life = 1 });
            st.WriteCounter(0, schema.Counter("X"), 5);
            var ev = new VmEval(st);
            var scope = new VmScope { Owner = ZoneScope.Self, Controller = 0 };
            W("CARD " + id + " " + S(c, "name") + " handCards=4 playerCounterX=5");
            foreach (var a in d?.Abilities ?? Array.Empty<AbilityDef>())
                foreach (var nd in a.Body ?? Array.Empty<EffectNode>()) WalkNodeFull(nd, x => {
                    if (x is DealDamageNode dd) W("  DealDamage EvalNum=" + ev.EvalNum(dd.Amount, scope) + " expr=" + AstCanonicalPrinter.PrintNumExpr(dd.Amount));
                    else if (x is RestoreLifeNode rl) W("  Restore EvalNum=" + ev.EvalNum(rl.Amount, scope) + " expr=" + AstCanonicalPrinter.PrintNumExpr(rl.Amount));
                    else if (x is ModifyStatsNode ms) W("  Buff EvalNum=" + ev.EvalNum(ms.Attack, scope) + "/" + ev.EvalNum(ms.Life, scope) + " expr=" + AstCanonicalPrinter.PrintNumExpr(ms.Attack) + "/" + AstCanonicalPrinter.PrintNumExpr(ms.Life));
                });
        }
    }

    static void Fallback()
    {
        W("=== FRESH COMPILE FALLBACK PROBE ===");
        var comp = new EnglishCompiler(lex);
        int n = 0, noneCards = 0, noneInstances = 0, unknownOwnerAny = 0;
        var examples = new List<string>();
        foreach (var kv in cards.OrderBy(q => int.Parse(q.Key)))
        {
            var c = kv.Value;
            var alts = new List<(string, int, string)>();
            if (c.TryGetProperty("alt", out var ae)) foreach (var a in ae.EnumerateArray()) alts.Add((S(a, "t"), I(a, "c"), S(a, "x")));
            EnglishCompiler.Result r;
            try { r = comp.Compile(S(c, "eng"), alts, S(c, "name"), int.Parse(kv.Key)); }
            catch { continue; }
            n++;
            int local = 0;
            foreach (var a in r.Abilities)
            {
                WalkBool(a.Condition, b => { if (b is HasStateExpr hs && hs.State == UnitState.None) { noneInstances++; local++; } });
                foreach (var nd in a.Body ?? Array.Empty<EffectNode>()) WalkNodeFull(nd, x => {
                    if (x is ConditionalNode cn) WalkBool(cn.Condition, b => { if (b is HasStateExpr hs && hs.State == UnitState.None) { noneInstances++; local++; } });
                    if (x is DealDamageNode dd && dd.Target != null && dd.Target.Owner == ZoneScope.Any) unknownOwnerAny++;
                });
            }
            if (local > 0)
            {
                noneCards++;
                if (examples.Count < 20) examples.Add(kv.Key + " " + S(c, "name") + " count=" + local + " ENG[" + S(c, "eng").Replace("\n", " / ") + "] AST[" + string.Join(" / ", r.Abilities.Select(DescribeAbility)) + "]");
            }
        }
        W("compiled=" + n + " cards_with_HasStateExpr_None=" + noneCards + " instances=" + noneInstances + " DealDamage_target_Owner_Any=" + unknownOwnerAny);
        foreach (var e in examples) W("NONE " + e);
    }

    static string TrimHeader(string cardPy)
    {
        var lines = (cardPy ?? "").Replace("\r\n", "\n").Split('\n');
        int first = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("    ")) continue;
            string t = lines[i].Trim();
            if (t.StartsWith("traits:") || t.StartsWith("keywords:") || t.StartsWith("fuse:")
                || t.StartsWith("deck_mod:") || t.StartsWith("innate_counter:") || t.StartsWith("countdown(")) continue;
            first = i; break;
        }
        if (first < 0) return "";
        int last = lines.Length - 1;
        for (int i = first; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("crest ") || t.StartsWith("faith ") || t.StartsWith("accelerate ") || t.StartsWith("crystallize ")) { last = i - 1; break; }
        }
        return string.Join("\n", lines.Skip(first).Take(last - first + 1)).TrimEnd();
    }

    static void Semantic()
    {
        W("=== SEMANTIC: pack PyAST vs fresh EnglishCompiler AST ===");
        var comp = new EnglishCompiler(lex);
        int n = 0, identical = 0, diff = 0, err = 0;
        var diffs = new List<object>();
        foreach (var kv in cards.OrderBy(p => int.Parse(p.Key)))
        {
            var c = kv.Value;
            var alts = new List<(string, int, string)>();
            if (c.TryGetProperty("alt", out var ae)) foreach (var a in ae.EnumerateArray()) alts.Add((S(a, "t"), I(a, "c"), S(a, "x")));
            EnglishCompiler.Result r;
            try { r = comp.Compile(S(c, "eng"), alts, S(c, "name"), int.Parse(kv.Key)); }
            catch (Exception ex) { err++; continue; }
            if (!r.Ok) { err++; continue; }
            n++;
            var pdef = new AstCanonicalParser(schema, lex).ParseCard(S(c, "text"));
            string pack = AstCanonicalPrinter.PrintAbilities(pdef.Abilities, 1).TrimEnd();
            string fresh = AstCanonicalPrinter.PrintAbilities(r.Abilities, 1).TrimEnd();
            if (pack == fresh) identical++;
            else
            {
                diff++;
                if (diffs.Count < 1000) diffs.Add(new { id = kv.Key, name = S(c, "name"), eng = S(c, "eng"), pack, fresh });
            }
        }
        W("compared=" + n + " identical_abilities=" + identical + " semantic_drift=" + diff + " skipped_fresh_errors=" + err);
        File.WriteAllText(Out + "semantic_diffs.json", JsonSerializer.Serialize(diffs), Encoding.UTF8);
        foreach (var d in diffs.Take(12))
            W("DRIFT " + d.GetType().GetProperty("id").GetValue(d) + " " + d.GetType().GetProperty("name").GetValue(d) + " PACK[" + d.GetType().GetProperty("pack").GetValue(d) + "] FRESH[" + d.GetType().GetProperty("fresh").GetValue(d) + "]");
    }

    static void VisitNodes(IReadOnlyList<EffectNode> body, Action<EffectNode> f)
    {
        foreach (var n in body ?? Array.Empty<EffectNode>())
        {
            if (n == null) continue;
            f(n);
            if (n is ConditionalNode c) { VisitNodes(c.Then, f); VisitNodes(c.Else, f); }
            else if (n is LoopNode l) VisitNodes(l.Body, f);
            else if (n is BranchModeNode b && b.Options != null) foreach (var o in b.Options) VisitNodes(o, f);
        }
    }

    static string BodyAst(Ast.CardDef d)
    {
        var sb = new StringBuilder();
        foreach (var a in d?.Abilities ?? Array.Empty<AbilityDef>()) sb.Append(DescribeAbility(a)).Append("\n");
        return sb.ToString().TrimEnd();
    }

    // Targeted evidence: three storm-bolt sentences, all X-counter cards, parameter keyword VM reads.
    static void Micro()
    {
        W("=== MICRO: THREE X SENTENCES ===");
        string[] probes = {
            "X starts at 2.\nOn Spellboost: Increase X by 1.\n<hr>Select an enemy follower on the field and deal it X damage.",
            "X starts at 2.",
            "On Spellboost: Increase X by 1.",
            "<hr>Select an enemy follower on the field and deal it X damage.",
            "Fanfare: Restore X defense to your leader. X is the number of cards in your hand.",
            "Fanfare: Deal X damage to all enemies. X is the number of amulets destroyed by this ability.",
        };
        foreach (var p in probes)
        {
            var c = new EnglishCompiler(lex);
            var r = c.Compile(p, null, "probe", 10131320);
            W("PROBE [" + p.Replace("\n", " / ") + "] ok=" + r.Ok + " abilities=" + r.Abilities.Count + " errors=" + string.Join(" ; ", r.Errors));
            foreach (var a in r.Abilities)
            {
                W("  AST " + DescribeAbility(a));
                VisitNodes(a.Body, n => {
                    if (n is ModifyCounterNode mc)
                        W("    ModifyCounter: counter=" + mc.Counter.Name + " op=" + mc.Op + " delta=" + AstCanonicalPrinter.PrintNumExpr(mc.Delta)
                          + " Target=" + (mc.Target == null ? "null(player-level)" : AstCanonicalPrinter.PrintTargetExpr(mc.Target)) + " Owner=" + mc.Owner);
                    if (n is DealDamageNode dd)
                        W("    DealDamage: amount=" + AstCanonicalPrinter.PrintNumExpr(dd.Amount) + " target=" + AstCanonicalPrinter.PrintTargetExpr(dd.Target));
                });
            }
        }

        W("=== MICRO: ALL ctr:X/Y/Z CARDS ===");
        var xIds = cards.Where(kv => System.Text.RegularExpressions.Regex.IsMatch(S(kv.Value, "text"), @"ctr:[XYZ]\b")).Select(kv => kv.Key).OrderBy(x => x).ToList();
        W("pack_ctr_XYZ_cards=" + xIds.Count + " ids=" + string.Join(",", xIds));
        foreach (var id in xIds)
        {
            var pp = new AstCanonicalParser(schema, lex);
            var d = pp.ParseCard(S(cards[id], "text"));
            int cntTargetNull = 0, cntTargetSet = 0;
            foreach (var a in d?.Abilities ?? Array.Empty<AbilityDef>())
                VisitNodes(a.Body, n => { if (n is ModifyCounterNode mc && (mc.Counter.Name == "X" || mc.Counter.Name == "Y" || mc.Counter.Name == "Z")) { if (mc.Target == null) cntTargetNull++; else cntTargetSet++; } });
            W("  " + id + " " + S(cards[id], "name") + " counter_target_null=" + cntTargetNull + " counter_target_set=" + cntTargetSet);
        }

        W("=== MICRO: SENTENCE-FAMILY PACK PRESENCE ===");
        string[] fams = { "once on each of your turns", "Can't be", "Give your opponent Crest", "Whenever", "When", "Do this", "Select a Mode", "Enhance", "Engage", "Fuse", "X is the number of" };
        foreach (var fam in fams)
        {
            var ids = cards.Where(kv => S(kv.Value, "eng").IndexOf(fam, StringComparison.OrdinalIgnoreCase) >= 0).Select(kv => kv.Key).OrderBy(x => x).ToList();
            int withA = 0, withRep = 0, withMode = 0, withCrestOpp = 0;
            foreach (var id in ids)
            {
                var py = S(cards[id], "text");
                if (py.Contains("max=1")) withA++;
                if (py.Contains("repeat ")) withRep++;
                if (py.Contains("modes ")) withMode++;
                if (py.Contains("[owner=Opponent]")) withCrestOpp++;
            }
            W("FAMILY [" + fam + "] cards=" + ids.Count + " max=1:" + withA + " repeat:" + withRep + " modes:" + withMode + " crest_opp:" + withCrestOpp + " e.g. " + string.Join(",", ids.Take(6)));
        }

        W("=== MICRO: 22 NODE TYPES COVERED BY VM SWITCH ===");
        W("switch cases=" + string.Join(",", new[]{"RevealCardNode","DamageModifierNode","SelectTargetNode","DealDamageNode","RestoreLifeNode","ModifyStatsNode","ModifyCostNode","ModifyKeywordNode","ModifyCounterNode","MoveZoneNode","SpawnCardNode","ReplaceDeckNode","EvolveNode","TransformNode","MarkVictoryCardNode","ConditionalNode","LoopNode","BranchModeNode","GrantAbilityNode","GrantCounterAbilityNode","InvokeAbilityNode","MacroNode"}));
    }

    static string S(JsonElement c, string n) => c.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : "";
    static int I(JsonElement c, string n) => c.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;

    static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").TrimEnd();

    static void DiffLines(string a, string b, StringBuilder sb, int cap = 8)
    {
        var x = a.Split('\n'); var y = b.Split('\n'); int n = 0;
        for (int i = 0; i < Math.Min(x.Length, y.Length) && n < cap; i++)
            if (x[i] != y[i]) { sb.Append(" L").Append(i + 1).Append(" 原[").Append(x[i].Trim()).Append("] 回[").Append(y[i].Trim()).Append(']'); n++; }
        if (x.Length != y.Length) sb.Append(" 行数 ").Append(x.Length).Append(" vs ").Append(y.Length);
    }

    // 1. PyAST -> CardDef -> print -> parse -> print, comparing original pack text too.
    static void Roundtrip()
    {
        W("=== ROUNDTRIP ===");
        var parser = new AstCanonicalParser(schema, lex);
        int n = 0, parseEx = 0, parseErr = 0, origMismatch = 0, opMismatch = 0, opParseErr = 0;
        var origBad = new List<string>(); var rtBad = new List<string>(); var errSamples = new List<string>();
        foreach (var kv in cards.OrderBy(p => int.Parse(p.Key)))
        {
            var c = kv.Value; string src = Normalize(S(c, "text")); n++;
            Ast.CardDef def; string first, second;
            try
            {
                parser.Errors.Clear();
                def = parser.ParseCard(src);
                if (def == null) throw new Exception("ParseCard returned null");
                if (parser.Errors.Count > 0) { parseErr++; if (errSamples.Count < 15) errSamples.Add(kv.Key + " | " + string.Join(" ;; ", parser.Errors.Take(3))); }
                def = new Ast.CardDef {
                    CardId = int.Parse(kv.Key), Name = S(c, "name"), Rarity = I(c, "rarity"),
                    BaseCost = def.BaseCost, BaseAttack = def.BaseAttack, BaseLife = def.BaseLife,
                    Kind = def.Kind, Clan = def.Clan, Traits = def.Traits, InnateKeywords = def.InnateKeywords,
                    Abilities = def.Abilities, HasFuse = def.HasFuse, FuseMaterialFilter = def.FuseMaterialFilter,
                    CrestDef = def.CrestDef, FaithDef = def.FaithDef, AccelerateDef = def.AccelerateDef,
                    CrystallizeDef = def.CrystallizeDef, DeckModifiers = def.DeckModifiers,
                    InnateCounters = def.InnateCounters,
                    IsToken = c.TryGetProperty("token", out var tk2) && tk2.ValueKind == JsonValueKind.True,
                };
                first = Normalize(AstCanonicalPrinter.PrintCard(def));
            }
            catch (Exception ex) { parseEx++; if (errSamples.Count < 15) errSamples.Add(kv.Key + " PARSE_EX " + ex.Message); continue; }

            if (first != src)
            {
                origMismatch++;
                if (origBad.Count < 20) { var d = new StringBuilder(); DiffLines(src, first, d); origBad.Add(kv.Key + " " + S(c, "name") + d); }
            }
            Ast.CardDef back;
            try
            {
                back = parser.ParseCard(first);
                if (back == null) throw new Exception("reparse null");
                if (parser.Errors.Count > 0) { opParseErr++; if (rtBad.Count < 20) rtBad.Add(kv.Key + " REPARSE_ERR " + string.Join(" ;; ", parser.Errors.Take(2))); parser.Errors.Clear(); }
            }
            catch (Exception ex) { if (rtBad.Count < 20) rtBad.Add(kv.Key + " REPARSE_EX " + ex.Message); continue; }
            second = Normalize(AstCanonicalPrinter.PrintCard(back));
            if (first != second)
            {
                opMismatch++;
                if (rtBad.Count < 20) { var d = new StringBuilder(); DiffLines(first, second, d); rtBad.Add(kv.Key + " " + S(c, "name") + d); }
            }
        }
        W("cards=" + n + " parse_exception=" + parseEx + " parse_errors=" + parseErr + " reparse_errors=" + opParseErr);
        W("pack_text_vs_PrintCard(PyAST): mismatch=" + origMismatch);
        W("PrintCard->ParseCard->PrintCard: mismatch=" + opMismatch);
        W("parse_error_samples:\n" + string.Join("\n", errSamples));
        W("orig_mismatch_samples:\n" + string.Join("\n", origBad));
        W("roundtrip_mismatch_samples:\n" + string.Join("\n", rtBad));
    }

    static string DescribeAbility(AbilityDef a)
    {
        var sb = new StringBuilder();
        try { sb.Append(AstCanonicalPrinter.PrintAbilityDef(a).Replace("\n", " / ").Trim()); }
        catch (Exception ex) { sb.Append("<print_ex:").Append(ex.Message).Append('>'); }
        return sb.ToString();
    }

    // 2. Recompile every official English card face with the actual compiler, with alt modes.
    static void Recompile()
    {
        W("=== RECOMPILE FROM ENGLISH ===");
        var comp = new EnglishCompiler(lex);
        int n = 0, ok = 0, err = 0, silent = 0, zero = 0;
        var byErr = new Dictionary<string, List<string>>();
        var results = new Dictionary<string, object>();
        foreach (var kv in cards.OrderBy(p => int.Parse(p.Key)))
        {
            var c = kv.Value; string eng = S(c, "eng");
            var alts = new List<(string, int, string)>();
            if (c.TryGetProperty("alt", out var ae))
                foreach (var a in ae.EnumerateArray()) alts.Add((S(a, "t"), I(a, "c"), S(a, "x")));
            EnglishCompiler.Result r;
            try { r = comp.Compile(eng, alts, S(c, "name"), int.Parse(kv.Key)); }
            catch (Exception ex)
            {
                err++;
                results[kv.Key] = new { ok = false, ex = ex.Message, abilities = 0, ast = Array.Empty<string>(), errors = new[] { ex.Message } };
                continue;
            }
            n++;
            bool isOk = r.Ok && r.Abilities.Count > 0;
            if (r.Ok) ok++; else err++;
            if (r.Abilities.Count == 0) zero++;
            bool sl = r.Errors.Any(e => e.Contains("静默丢弃"));
            if (sl) silent++;
            foreach (var e in r.Errors)
            {
                string key = e.Split('\n')[0].Length > 110 ? e.Split('\n')[0].Substring(0, 110) : e.Split('\n')[0];
                if (!byErr.ContainsKey(key)) byErr[key] = new List<string>();
                byErr[key].Add(kv.Key);
            }
            var ast = r.Abilities.Select(DescribeAbility).ToArray();
            results[kv.Key] = new { ok = isOk, abilities = r.Abilities.Count, ast, innate = (r.InnateKeywords ?? new List<SymbolId<KeywordTag>>()).Select(k => k.Name).ToArray(), errors = r.Errors.ToArray(), eng, alt = alts.Select(x => x.Item3).ToArray() };
        }
        W("compiled=" + n + " no_errors=" + ok + " with_errors=" + err + " silent_drop_flags=" + silent + " zero_abilities=" + zero);
        W("error_classes=" + byErr.Count);
        foreach (var g in byErr.OrderByDescending(g => g.Value.Count).Take(30))
            W("  [" + g.Value.Count + "] " + g.Key + "  e.g. " + string.Join(",", g.Value.Take(4)));
        File.WriteAllText(Out + "recompile.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8);
    }

    // 3. Pack PyAST pattern classification (residual markers, empty bodies, parameter keywords).
    static readonly RegexLike[] Markers = { };
    static void Patterns()
    {
        W("=== PACK PYAST PATTERNS ===");
        int abTotal = 0, emptyBody = 0, unknownToken = 0, fallback = 0, errors = 0;
        var heads = new Dictionary<string, int>(); var nodeVerb = new Dictionary<string, int>();
        var emptyList = new List<string>(); var oddList = new List<string>();
        foreach (var kv in cards.OrderBy(p => int.Parse(p.Key)))
        {
            var c = kv.Value; string src = S(c, "text");
            var lines = src.Split('\n');
            bool hasBody = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var m = System.Text.RegularExpressions.Regex.Match(lines[i], "^    ([a-z_][a-z0-9_]*)(.*)$");
                if (!m.Success) continue;
                abTotal++; string h = m.Groups[1].Value;
                heads[h] = heads.TryGetValue(h, out var v) ? v + 1 : 1;
                if (m.Groups[2].Value.Trim() == "" || m.Groups[2].Value.Trim().EndsWith(":"))
                {
                    int body = 0;
                    for (int k = i + 1; k < lines.Length; k++)
                    {
                        if (lines[k].StartsWith("        ") && lines[k].Trim().Length > 0) body++;
                        else if (lines[k].Trim().Length > 0 && !lines[k].StartsWith("        ")) break;
                    }
                    if (body == 0) { emptyBody++; if (emptyList.Count < 20) emptyList.Add(kv.Key + " " + S(c, "name") + " head=" + lines[i].Trim()); }
                }
            }
            foreach (var ln in lines)
            {
                string t = ln.Trim();
                if (t.Length == 0) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, "(?i)(^|\\s)(TODO|FIXME|FALLBACK|PLACEHOLDER|STUB|UNKNOWN|UNSUPPORTED|UNPARSED|COMPILE_ERROR|ERROR\\b)"))
                { errors++; if (oddList.Count < 25) oddList.Add(kv.Key + " " + t); }
                if (t == "pass" || t == "noop" || t == "None" || t == "[]") { fallback++; if (oddList.Count < 25) oddList.Add(kv.Key + " " + t); }
                if (t.StartsWith("#") && !t.StartsWith("#!")) unknownToken++;
            }
        }
        W("cards=" + cards.Count + " ability_heads=" + abTotal + " empty_bodies=" + emptyBody + " error_markers=" + errors + " fallback_tokens=" + fallback + " standalone_comments=" + unknownToken);
        W("empty_body_samples=" + string.Join(" | ", emptyList));
        W("marker_samples=" + string.Join(" | ", oddList));
        W("heads=" + string.Join("; ", heads.OrderByDescending(h => h.Value).Select(h => h.Key + ":" + h.Value)));
    }

    // 4. X/Y/Z dynamic values: where does the compiled value come from?
    static void Vars()
    {
        W("=== XYZ VARIABLES ===");
        var engPat = new System.Text.RegularExpressions.Regex("(?i)(starts at|increase X by|increase Y by|increase Z by|X is the number of|Y is the number of|Z is the number of|X is |Y is |Z is )");
        var selected = cards.Where(kv => engPat.IsMatch(S(kv.Value, "eng"))).OrderBy(kv => kv.Key).ToList();
        W("english_pattern_cards=" + selected.Count);
        var buckets = new Dictionary<string, List<string>> { ["inline_numexpr"] = new(), ["ctr_XYZ"] = new(), ["constant_only"] = new(), ["missing"] = new() };
        var rows = new List<object>();
        foreach (var kv in selected)
        {
            var c = kv.Value; string py = S(c, "text"); string eng = S(c, "eng");
            bool ctr = System.Text.RegularExpressions.Regex.IsMatch(py, @"ctr:[XYZ]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool inline = System.Text.RegularExpressions.Regex.IsMatch(py, @"(?i)(count |num_|prop:|sum|fuse_count|split|randrange|fused|cards_in|hand_count|num of|\+X|\+Y|\+Z|by X|by Y|by Z)");
            string bucket = ctr ? "ctr_XYZ" : inline ? "inline_numexpr" : "constant_only";
            // Determine from the freshly compiled English AST too.
            var comp = new EnglishCompiler(lex);
            var alts = new List<(string, int, string)>();
            if (c.TryGetProperty("alt", out var ae)) foreach (var a in ae.EnumerateArray()) alts.Add((S(a, "t"), I(a, "c"), S(a, "x")));
            EnglishCompiler.Result r;
            string freshAst = "";
            try { r = comp.Compile(eng, alts, S(c, "name"), int.Parse(kv.Key)); freshAst = string.Join("\n", r.Abilities.Select(DescribeAbility)); if (r.Abilities.Count == 0) bucket = "missing"; }
            catch (Exception ex) { freshAst = "EX " + ex.Message; }
            bool freshCtr = freshAst.Contains("ctr:X") || freshAst.Contains("ctr:Y") || freshAst.Contains("ctr:Z");
            bool freshZero = System.Text.RegularExpressions.Regex.IsMatch(freshAst, @"(?i)(const 0|\b0\b)");
            buckets[bucket].Add(kv.Key);
            rows.Add(new { id = kv.Key, name = S(c, "name"), bucket, ctr_in_pack = ctr, fresh_ctr = freshCtr, fresh_zero_token = freshZero, eng = eng.Replace("\n", " / "), py = py.Replace("\n", " / "), fresh_ast = freshAst });
        }
        foreach (var b in buckets) W("bucket " + b.Key + " n=" + b.Value.Count + " ids=" + string.Join(",", b.Value));
        File.WriteAllText(Out + "vars.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8);
        foreach (var id in new[] { "10111130", "10114130", "10163110", "10131320", "10133110", "10374110" })
            if (cards.ContainsKey(id))
            {
                var row = rows.FirstOrDefault(x => x.GetType().GetProperty("id")?.GetValue(x)?.ToString() == id);
                W("CASE " + id + " " + S(cards[id], "name") + "\n  ENG " + S(cards[id], "eng").Replace("\n", " / ") + "\n  PYAST " + S(cards[id], "text").Replace("\n", " / "));
            }
    }

    // 5. Parameter keywords: PassiveAura / InnateKeywords placement, and VM reads.
    static void Keywords()
    {
        W("=== PARAMETER KEYWORDS ===");
        foreach (var kw in new[] { "max_attacks_per_turn", "damage_cap", "damage_taken_bonus" })
        {
            var ids = cards.Where(kv => S(kv.Value, "text").Contains(kw + "(")).Select(kv => kv.Key).OrderBy(x => x).ToList();
            W(kw + " pack_cards=" + ids.Count + " ids=" + string.Join(",", ids));
            int inAura = 0, innate = 0, other = 0, nullTarget = 0;
            foreach (var id in ids)
            {
                var c = cards[id]; string py = S(c, "text");
                var lines = py.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(kw + "("))
                    {
                        string head = "";
                        for (int k = i - 1; k >= 0; k--) if (System.Text.RegularExpressions.Regex.IsMatch(lines[k], "^    [a-z_]")) { head = lines[k].Trim(); break; }
                        if (head.StartsWith("aura")) inAura++; else if (head.StartsWith("keywords")) innate++; else other++;
                        W("  " + id + " " + S(c, "name") + " head=[" + head + "] line=[" + lines[i].Trim() + "]");
                    }
                var parser = new AstCanonicalParser(schema, lex);
                try
                {
                    var d = parser.ParseCard(py);
                    foreach (var a in d?.Abilities ?? new List<AbilityDef>())
                        foreach (var n in a.Body ?? new List<EffectNode>())
                            if (n is ModifyKeywordNode mk && mk.Keyword.Name == kw && mk.Target == null) nullTarget++;
                }
                catch { }
            }
            W(kw + " heads aura=" + inAura + " innate=" + innate + " other=" + other + " null_ModifyKeywordNode_Target=" + nullTarget);
        }
    }
}

public sealed class RegexLike { }
