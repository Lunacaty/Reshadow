using System;
using System.Linq;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.Ui.Bridge;

public static class Program
{
    static GameSessionStore store;
    static CardPoolRuntime pool;
    static BattleState st;
    static CardVm vm;
    static TurnRunner tr;
    static MatchRunner mr;
    static int nextId = 900000;
    static bool quiet;

    static void Say(string s) { if (!quiet) Console.WriteLine(s); }
    static CardDef Def(int id) => store.Catalog.AllCards.Values.FirstOrDefault(c => c.CardId == id);

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        quiet = args.Contains("--quiet");
        store = new GameSessionStore();
        store.Catalog?.Notices?.Clear();
        if (!quiet) store.Initialize();
        else { var sw = new System.IO.StringWriter(); var old = Console.Out; Console.SetOut(sw); store.Initialize(); Console.SetOut(old); }
        pool = store.GetPoolRuntime("official");
        Console.WriteLine("BOOT cards=" + store.Catalog.AllCards.Count + " pool=" + (pool?.PoolId ?? "null"));
        if (!quiet) DumpStorm();
        if (!quiet) RootChain();
        if (!quiet) NodeCoverage();
        if (!quiet) SampleCards();
        if (!quiet) DeepDive();
        if (!quiet) DirectNodes();
        if (!quiet) FinalProbes();
        if (!quiet) Structural();
        if (!quiet) RealSequence();
    }

    static void Fresh(int seed = 42)
    {
        st = pool.CreateBattleState(seed);
        vm = new CardVm(st);
        tr = new TurnRunner(st, st.Schema);
        mr = new MatchRunner(st, st.Schema);
        var deck = store.SavedDecks.FirstOrDefault(d => d.PoolId == "official");
        mr.StartWithConstructedDecks(pool, 0, deck, deck);
        vm.CurrentTurnOwner = st.ActivePlayer;
    }

    static RuntimeEntity Put(int cardId, int pid, ZoneRef zone, int atk = 0, int life = 0)
    {
        var e = st.CreateEntity(cardId, zone, pid, atk, life);
        vm.DropPendingTriggers(); vm.DropPendingContinuations();
        return e;
    }

    static void PostChoice()
    {
        int guard = 0;
        while (vm.Pending != null && guard++ < 12)
        {
            var q = vm.Pending;
            int n = Math.Max(q.MinSelect, 1);
            var pick = q.Candidates.Take(n).ToArray();
            Console.WriteLine($"    choice kind={q.Kind} cands=[{string.Join(",",q.Candidates)}] pick=[{string.Join(",",pick)}] slot={q.Slot}");
            vm.SubmitChoice(q.ChoiceId, pick);
            Console.WriteLine("    step=" + vm.StepUntilBlocked() + (vm.FaultMessage!=null?" FAULT="+vm.FaultMessage:""));
        }
    }

    static string NodeDesc(EffectNode n)
    {
        string s = n.GetType().Name;
        switch (n)
        {
            case ModifyCounterNode mc:
                s += $"{{Counter={mc.Counter.Name},Op={mc.Op},Delta={Num(mc.Delta)},Target={(mc.Target==null?"null":mc.Target.SourceKind+":"+mc.Target.Slot+":"+mc.Target.Zone)},Owner={mc.Owner}}}";
                break;
            case DealDamageNode dd: s += $"{{Amount={Num(dd.Amount)},Target={dd.Target?.SourceKind}:{dd.Target?.Slot}:{dd.Target?.Zone}}}"; break;
            case DamageModifierNode dm: s += $"{{Kind={dm.Kind},Mask={dm.SourceMask},Dur={dm.Duration},Amt={Num(dm.Amount)}}}"; break;
            case BranchModeNode bm: s += $"{{Kind={bm.Kind},Pick={Num(bm.PickCount)},Opts={bm.Options?.Count}}}"; break;
            case LoopNode lp: s += $"{{Kind={lp.Kind},Count={Num(lp.Count)},Until={(lp.Until==null?"null":"set")}}}"; break;
            case SpawnCardNode sp: s += $"{{Kind={sp.SourceKind},CardId={sp.CardId},Count={Num(sp.Count)},To={sp.ToZone}}}"; break;
            case TransformNode tf: s += $"{{Into={tf.IntoCardId},CopySource={(tf.CopySource==null?"null":"set")}}}"; break;
            case EvolveNode ev: s += $"{{Super={ev.IsSuperEvolve}}}"; break;
            case GrantAbilityNode ga: s += $"{{Granted={ga.Granted?.Trigger?.Event},Dur={ga.Duration}}}"; break;
            case GrantCounterAbilityNode gc: s += $"{{Ctr={gc.Counter.Name},Owner={gc.Owner}}}"; break;
            case InvokeAbilityNode ia: s += $"{{Event={ia.Event}}}"; break;
            case ReplaceDeckNode rd: s += $"{{Deck={rd.DeckName}}}"; break;
            case MacroNode ma: s += $"{{Macro={ma.Macro.Name},Args={ma.Args?.Count}}}"; break;
            case RevealCardNode rv: s += $"{{Target={rv.Target?.SourceKind}}}"; break;
            case MarkVictoryCardNode mv: s += $"{{Owner={mv.Owner}}}"; break;
        }
        return s;
    }
    static string Num(NumExpr e) => e == null ? "null" : e.Op + "(" + e.Value + ")" + (e.Counter != null ? ":" + e.Counter.Name : "") + (e.Of != null ? " Of=" + e.Of.Owner : "");

    static void DumpAbility(CardDef d)
    {
        foreach (var ab in d?.Abilities ?? Array.Empty<AbilityDef>())
        {
            Console.WriteLine($"  AB event={ab?.Trigger?.Event} zone={ab?.Trigger?.ActiveZone} subj={(ab?.Trigger?.SubjectFilter==null?"null":"set")} cond={(ab?.Condition==null?"null":"set")} costs={ab?.Costs?.Count} body={ab?.Body?.Count}");
            foreach (var n in ab?.Body ?? Array.Empty<EffectNode>()) Console.WriteLine("    " + NodeDesc(n));
        }
    }

    static void DumpStorm()
    {
        Console.WriteLine("\n===== A. 暴风破 PyAST 与编译结果 =====");
        foreach (int id in new[]{10131320,10831310})
        {
            var d = Def(id);
            Console.WriteLine($"--- {id} {d?.NameChs} kind={d?.Kind} ---");
            Console.WriteLine(d?.SourceText);
            DumpAbility(d);
        }
    }

    static void RootChain()
    {
        Console.WriteLine("\n===== B. 暴风破根因链实际数值 =====");
        Fresh();
        var d = Def(10131320);
        var xKey = st.Schema.Counter("X");
        var sbKey = st.Schema.Counter("spellboost");
        Console.WriteLine($"B0 初始: playerX={st.ReadCounter(0,xKey)} oppX={st.ReadCounter(1,xKey)}");

        var foe = Put(Def(1)?.CardId ?? 0, 1, ZoneRef.Board); // placeholder
        if (foe == null) foe = Put(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId, 1, ZoneRef.Board, 1, 8);
        var card = Put(10131320, 0, ZoneRef.Hand);
        Console.WriteLine($"B1 Put(card) 后: entityX={st.ReadEntityCounter(card,xKey)} entitySpellboost={st.ReadEntityCounter(card,sbKey)} playerX={st.ReadCounter(0,xKey)}");
        Console.WriteLine($"B1 counters={string.Join(",", card.Counters.Select(k=>k.Key.Name+"="+k.Value))}");

        // 手动执行 aura 的 ModifyCounterNode 看写到了哪里
        var aura = d.Abilities.First(a=>a.Trigger?.Event==CoreEvent.PassiveAura);
        var auraNode = (ModifyCounterNode)aura.Body[0];
        var sc = new VmScope { Owner=ZoneScope.Self, Controller=0, Source=EntityRef.From(card) };
        vm.PushAbility(aura, sc);
        Console.WriteLine("B2 aura 手工 PushAbility Step=" + vm.StepUntilBlocked());
        Console.WriteLine($"B2 后: entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)}");

        // 用真实管线 BumpSpellboost: 打出一张法术，触发 OnSpellboost
        var dummySpell = Put(10133310, 0, ZoneRef.Hand); // 做作业啦，也带 on_spellboost
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        Console.WriteLine($"B3 打出前: card.entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)} card.spellboost={st.ReadEntityCounter(card,sbKey)}");
        bool played = tr.PlayCard(0, dummySpell, vm);
        Console.WriteLine($"B3 PlayCard(做作业啦)={played} status={vm.Status}");
        Console.WriteLine($"B3 后: card.entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)} card.spellboost={st.ReadEntityCounter(card,sbKey)} dummySpell.X={st.ReadEntityCounter(dummySpell,xKey)} dummySpell.sb={st.ReadEntityCounter(dummySpell,sbKey)}");

        // 直接构造 OnSpellboost 事件并观察能力是否入栈执行
        vm.EnqueueEvent(new VmEvent{ Kind=CoreEvent.OnSpellboost, PlayerId=0 });
        Console.WriteLine("B4 显式 OnSpellboost Step=" + vm.StepUntilBlocked());
        Console.WriteLine($"B4 后: card.entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)} card.spellboost={st.ReadEntityCounter(card,sbKey)}");

        // 直接跑 OnPlay body 的 deal 节点，看 ctr:X 求值
        var onPlay = d.Abilities.First(a=>a.Trigger?.Event==CoreEvent.OnPlay);
        var deal = (DealDamageNode)onPlay.Body.Last();
        var sc2 = new VmScope { Owner=ZoneScope.Self, Controller=0, Source=EntityRef.From(card) };
        Console.WriteLine($"B5 Eval ctr:X (Source=card, playerX=0, entityX={st.ReadEntityCounter(card,xKey)}) = {vm.Eval.EvalNum(deal.Amount, sc2)}");
        st.WriteEntityCounter(card, xKey, 2);
        Console.WriteLine($"B5b 写实体X=2 后 Eval ctr:X = {vm.Eval.EvalNum(deal.Amount, sc2)}; playerX={st.ReadCounter(0,xKey)}");

        // 出牌预检
        Fresh();
        var card2 = Put(10131320, 0, ZoneRef.Hand);
        var foe2 = Put(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId, 1, ZoneRef.Board, 1, 8);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.ActivePlayer = 0;
        var mi = typeof(TurnRunner).GetMethod("HasLegalTargetForSpell", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        Console.WriteLine("B6 HasLegalTargetForSpell(有敌方随从)=" + mi.Invoke(tr,new object[]{card2}));
        Console.WriteLine("B6 CanPlay=" + tr.CanPlayCard(0,card2,out string r1) + " reason=" + r1);
        bool ok = tr.PlayCard(0,card2,vm);
        Console.WriteLine($"B6 PlayCard={ok} status={vm.Status} foeLife={foe2.Life} pending={(vm.Pending==null?"null":vm.Pending.Kind.ToString())}");
        if (vm.Pending != null) { vm.SubmitChoice(vm.Pending.ChoiceId, new[]{foe2.EntityId}); Console.WriteLine("B6 after choice Step="+vm.StepUntilBlocked()+ $" foeLife={foe2.Life}"); }
        Console.WriteLine($"B6 最终: playerX={st.ReadCounter(0,xKey)} card.entityX={st.ReadEntityCounter(card2,xKey)} foeLife={foe2.Life} (期望<=6)");

        // 无敌方随从
        Fresh();
        var card3 = Put(10131320, 0, ZoneRef.Hand);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10); st.ActivePlayer = 0;
        Console.WriteLine("B7 HasLegalTargetForSpell(空场)=" + mi.Invoke(tr,new object[]{card3}));
        Console.WriteLine("B7 CanPlay=" + tr.CanPlayCard(0,card3,out string r2) + " reason=" + r2);
        Console.WriteLine("B7 PlayCard=" + tr.PlayCard(0,card3,vm));
    }

    static void NodeCoverage()
    {
        Console.WriteLine("\n===== C. 全卡池 22 种节点出现统计（PyAST 加载后的真实 AST） =====");
        var names = new[]{"SelectTargetNode","DealDamageNode","RestoreLifeNode","ModifyStatsNode","ModifyCostNode","ModifyKeywordNode","GrantAbilityNode","GrantCounterAbilityNode","SpawnCardNode","MoveZoneNode","MarkVictoryCardNode","TransformNode","EvolveNode","ModifyCounterNode","InvokeAbilityNode","ConditionalNode","LoopNode","DamageModifierNode","RevealCardNode","BranchModeNode","ReplaceDeckNode","MacroNode"};
        var dict = names.ToDictionary(x=>x,x=>new List<int>());
        void Walk(EffectNode n, int id) {
            if (n == null) return;
            string k = n.GetType().Name;
            if (dict.ContainsKey(k) && !dict[k].Contains(id)) dict[k].Add(id);
            switch(n) {
                case ConditionalNode c: foreach(var z in c.Then??Array.Empty<EffectNode>())Walk(z,id); foreach(var z in c.Else??Array.Empty<EffectNode>())Walk(z,id); break;
                case LoopNode l: foreach(var z in l.Body??Array.Empty<EffectNode>())Walk(z,id); break;
                case BranchModeNode b: foreach(var o in b.Options??Array.Empty<IReadOnlyList<EffectNode>>())foreach(var z in o??Array.Empty<EffectNode>())Walk(z,id); break;
                case GrantAbilityNode g: foreach(var z in g.Granted?.Body??Array.Empty<EffectNode>())Walk(z,id); break;
                case GrantCounterAbilityNode gc: foreach(var z in gc.Granted?.Body??Array.Empty<EffectNode>())Walk(z,id); break;
            }
        }
        var seenCounters = new Dictionary<string,int>();
        var nestedDefs = new List<(int host,string kind,CardDef def)>();
        foreach (var c in store.Catalog.AllCards.Values)
        {
            if (c.CrestDef!=null) nestedDefs.Add((c.CardId,"crest",c.CrestDef));
            if (c.FaithDef!=null) nestedDefs.Add((c.CardId,"faith",c.FaithDef));
            if (c.AccelerateDef!=null) nestedDefs.Add((c.CardId,"accelerate",c.AccelerateDef));
            if (c.CrystallizeDef!=null) nestedDefs.Add((c.CardId,"crystallize",c.CrystallizeDef));
            foreach(var def in nestedDefs.Where(x=>x.host==c.CardId).Select(x=>x.def))
              foreach(var ab in def.Abilities??Array.Empty<AbilityDef>())
                foreach(var n in ab.Body??Array.Empty<EffectNode>()) Walk(n,c.CardId);
          foreach (var ab in c.Abilities??Array.Empty<AbilityDef>())
            foreach (var n in ab.Body??Array.Empty<EffectNode>()) {
                Walk(n,c.CardId);
                if (n is ModifyCounterNode mc) { string k = mc.Counter.Name+"|"+(mc.Target==null?"player":"entity"); seenCounters[k]=seenCounters.GetValueOrDefault(k)+1; }
            }
        }
        Console.WriteLine("嵌套子定义数=" + nestedDefs.Count + " 明细=" + string.Join(",",nestedDefs.Select(x=>x.host+":"+x.kind).Take(12)));
        foreach (var k in names) Console.WriteLine($"{k,-30} cards={dict[k].Count,-5} first=[{string.Join(",",dict[k].Take(6))}]");
        Console.WriteLine("ModifyCounterNode 计数分布: " + string.Join(" ; ",seenCounters.OrderByDescending(x=>x.Value).Take(20).Select(x=>x.Key+"="+x.Value)));
    }

    static void SampleCards()
    {
        Console.WriteLine("\n===== D. 真实卡抽样复现 =====");
        int[] ids = {10131320,10831310,10132130,10133310,10232310,10833310,10834110,10114120,10304110,10104120,11061120,10123110};
        foreach(var id in ids) SampleOne(id);
    }

    static void SampleOne(int id)
    {
        var d = Def(id);
        Console.WriteLine($"--- D {id} {d?.NameChs} ---");
        if (d == null) { Console.WriteLine("  缺失"); return; }
        Console.WriteLine("  AST=" + string.Join(" | ", (d.Abilities??Array.Empty<AbilityDef>()).Select(a=>a.Trigger?.Event+":"+string.Join("+",(a.Body??Array.Empty<EffectNode>()).Select(n=>n.GetType().Name)))));
        Fresh();
        var xKey = st.Schema.Counter("X"); var sbKey=st.Schema.Counter("spellboost");
        var hand = Put(id, 0, ZoneRef.Hand);
        if (hand == null) { Console.WriteLine("  Put 失败"); return; }
        Console.WriteLine($"  初始 entityX={st.ReadEntityCounter(hand,xKey)} playerX={st.ReadCounter(0,xKey)} entitySB={st.ReadEntityCounter(hand,sbKey)} counters=[{string.Join(",",hand.Counters.Select(k=>k.Key.Name+"="+k.Value))}]");
        // 触发 3 次 spellboost（真实管线：打出任意法术）
        st.WriteCounter(0, st.Schema.Counter("pp"), 10); st.ActivePlayer=0;
        int casts=0;
        for(int i=0;i<3;i++){
            var sp = Def(90000000+i); // 不存在；用真实法术
            var any = store.Catalog.AllCards.Values.FirstOrDefault(c=>c.Kind==CardKind.Spell && c.CardId!=id);
            var s = Put(any.CardId,0,ZoneRef.Hand);
            bool ok = tr.PlayCard(0,s,vm); if(ok)casts++;
            Console.WriteLine($"  施法{i+1} {any.CardId} ok={ok} status={vm.Status} entityX={st.ReadEntityCounter(hand,xKey)} playerX={st.ReadCounter(0,xKey)} entitySB={st.ReadEntityCounter(hand,sbKey)}");
            if (vm.Pending!=null) { vm.SubmitChoice(vm.Pending.ChoiceId,new[]{vm.Pending.Candidates[0]}); vm.StepUntilBlocked(); }
        }
        // 打出被测卡
        var foe = Put(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId,1,ZoneRef.Board,1,12);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10); st.ActivePlayer=0;
        bool played = tr.PlayCard(0,hand,vm);
        Console.WriteLine($"  被测卡出牌={played} status={vm.Status} pending={(vm.Pending==null?"null":vm.Pending.Kind.ToString())}");
        if (vm.Pending!=null && vm.Pending.Kind!=ChoiceKind.DiscoverCard) { var q=vm.Pending; vm.SubmitChoice(q.ChoiceId,q.Candidates.Take(q.MinSelect).ToArray()); vm.StepUntilBlocked(); }
        Console.WriteLine($"  结果 foeLife={foe.Life} (初始12) entityX={st.ReadEntityCounter(hand,xKey)} playerX={st.ReadCounter(0,xKey)} handZone={hand.Zone}");
    }

    static void DeepDive()
    {
        Console.WriteLine("\n===== E. 7 张 ctr:X 卡逐卡求值（真实 PyAST + 真实管线） =====");
        int[] ids = {10131320,10831310,10132130,10133310,10232310,10833310,10834110};
        foreach (int id in ids) DeepOne(id);
        Console.WriteLine("\n===== F. OnSpellboost 触发闸门定位 =====");
        Fresh();
        var d=Def(10131320);
        var card=Put(10131320,0,ZoneRef.Hand);
        var xKey=st.Schema.Counter("X"); var sbKey=st.Schema.Counter("spellboost");
        var onSb=d.Abilities.First(a=>a.Trigger.Event==CoreEvent.OnSpellboost);
        Console.WriteLine($"F0 onSpellboost Trigger: Event={onSb.Trigger.Event} ActiveZone={onSb.Trigger.ActiveZone} SubjectFilter={(onSb.Trigger.SubjectFilter==null?"null":"set")} Condition={onSb.Condition?.GetType().Name}");
        Console.WriteLine($"F0 card.Zone={card.Zone} entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)}");
        // A: 原样发事件
        vm.EnqueueEvent(new VmEvent{Kind=CoreEvent.OnSpellboost, PlayerId=0});
        Console.WriteLine($"F1 手牌原样 Step={vm.StepUntilBlocked()} entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)}");
        // B: 把卡放到 Board，再发同样事件
        st.MoveEntity(card,ZoneRef.Board,0); vm.DropPendingTriggers();
        vm.EnqueueEvent(new VmEvent{Kind=CoreEvent.OnSpellboost, PlayerId=0});
        Console.WriteLine($"F2 卡移入Board后 Step={vm.StepUntilBlocked()} entityX={st.ReadEntityCounter(card,xKey)} playerX={st.ReadCounter(0,xKey)}");
        // C: 把卡放回去，手动模拟触发条件（把 ActiveZone 无法改，只能 PushAbility 直跑）
        st.MoveEntity(card,ZoneRef.Hand,0); vm.DropPendingTriggers();
        int px0=st.ReadCounter(0,xKey);
        vm.PushAbility(onSb, new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(card)});
        Console.WriteLine($"F3 直接PushAbility Step={vm.StepUntilBlocked()} playerX {px0}->{st.ReadCounter(0,xKey)} entityX={st.ReadEntityCounter(card,xKey)}");
        // D: 独享的玩家级 X 与实体级 X 的读写方向
        Console.WriteLine($"F4 现 playerX={st.ReadCounter(0,xKey)} entityX={st.ReadEntityCounter(card,xKey)}; VmEval ctr:X={vm.Eval.EvalNum(NumExpr.Ctr(xKey), new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(card)})}");
        Console.WriteLine($"F5 事件 PlayerId=-1（BumpSpellboost 实际写法）时: ");
        Fresh(); var c2=Put(10131320,0,ZoneRef.Hand); var v2=new CardVm(st);
        v2.CurrentTurnOwner=0;
        v2.EnqueueEvent(new VmEvent{Kind=CoreEvent.OnSpellboost});
        Console.WriteLine($"    Step={v2.StepUntilBlocked()} playerX={st.ReadCounter(0,st.Schema.Counter("X"))} entityX={st.ReadEntityCounter(c2,st.Schema.Counter("X"))}");
    }

    static void DeepOne(int id)
    {
        var d=Def(id);
        Console.WriteLine($"--- E {id} {d?.NameChs} ---");
        Console.WriteLine("  PyAST: " + (d.SourceText??"").Replace("\n"," / ").Trim());
        Fresh();
        var xKey=st.Schema.Counter("X"); var sbKey=st.Schema.Counter("spellboost");
        var hand=Put(id,0,ZoneRef.Hand);
        Console.WriteLine($"  初始 entityX={st.ReadEntityCounter(hand,xKey)} playerX={st.ReadCounter(0,xKey)} entitySB={st.ReadEntityCounter(hand,sbKey)}");
        var onPlay=(d.Abilities??Array.Empty<AbilityDef>()).FirstOrDefault(a=>a.Trigger.Event==CoreEvent.OnPlay);
        foreach(var n in onPlay?.Body??Array.Empty<EffectNode>()) {
            if(n is SelectTargetNode stn) Console.WriteLine($"  SelectTarget: Mode={stn.Target.Mode} Zone={stn.Target.Zone} Owner={stn.Target.Owner} Where={stn.Target.Where?.GetType().Name} OutSlot={stn.OutSlot} Count={(stn.Target.Count==null?"null":stn.Target.Count.Op.ToString())}");
            if(n is DealDamageNode ddn) Console.WriteLine($"  DealDamage: Target.SourceKind={ddn.Target.SourceKind} Slot={ddn.Target.Slot} Zone={ddn.Target.Zone} Mode={ddn.Target.Mode}");
        }
        // 找一张无 Choose 目标的低费真实法术用来触发 BumpSpellboost
        int[] casts = {10133310,10833310};
        foreach(int cid in casts) {
            var spell=Put(cid,0,ZoneRef.Hand);
            st.WriteCounter(0,st.Schema.Counter("pp"),10); st.ActivePlayer=0;
            bool ok=tr.PlayCard(0,spell,vm);
            PostChoice();
            Console.WriteLine($"  施放 {cid} ok={ok} => playerX={st.ReadCounter(0,xKey)} target.entityX={st.ReadEntityCounter(hand,xKey)} target.entitySB={st.ReadEntityCounter(hand,sbKey)}");
        }
        // 显式 ctr:X 求值
        var sc=new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(hand)};
        Console.WriteLine($"  VmEval ctr:X (Source=本卡) = {vm.Eval.EvalNum(NumExpr.Ctr(xKey),sc)}");
        Console.WriteLine($"  期望：初始2/0 + 2次增幅 => 暴风破 4，其余 2");
        // 打出被测卡，看伤害 / 抽牌 / 条件分支
        var foe=Put(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId,1,ZoneRef.Board,0,12);
        foe.Life=12; foe.MaxLife=12;
        st.WriteCounter(0,st.Schema.Counter("pp"),10); st.ActivePlayer=0;
        bool played=tr.PlayCard(0,hand,vm);
        PostChoice();
        Console.WriteLine($"  出牌={played} status={vm.Status} foeLife={foe.Life}/12 手牌={st.Players[0].Hand.Count} playerX={st.ReadCounter(0,xKey)} entityX={st.ReadEntityCounter(hand,xKey)}");
        var dealNode = (d.Abilities??Array.Empty<AbilityDef>()).SelectMany(a=>a.Body??Array.Empty<EffectNode>()).OfType<DealDamageNode>().FirstOrDefault();
        if (dealNode != null) {
            var dsc=new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(hand)};
            dsc.Bind(TargetSpec.SlotSelected,new[]{foe});
            int amt=vm.Eval.EvalNum(dealNode.Amount,dsc);
            Console.WriteLine($"  deal ctr:X 求值={amt} 期望(2次增幅后)={(id==10131320||id==10831310?4:2)}");
        }
        var mi = typeof(TurnRunner).GetMethod("HasLegalTargetForSpell", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        var freshCard = Put(id,0,ZoneRef.Hand);
        Console.WriteLine($"  HasLegalTargetForSpell(有敌方随从)={mi.Invoke(tr,new object[]{freshCard})} CanPlay={tr.CanPlayCard(0,freshCard,out string why)} reason={why}");
    }

    static void DirectNodes()
    {
        Console.WriteLine("\n===== G. 高危节点直接执行探测（自建 AST，仅测 VM handler 语义） =====");
        Fresh();
        var xKey=st.Schema.Counter("X");
        var src=Put(10131320,0,ZoneRef.Hand);
        var foe=Put(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId,1,ZoneRef.Board,0,9);
        foe.Life=9; foe.MaxLife=9;
        var sc0=new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(src)};
        var exec=typeof(CardVm).GetMethod("ExecNode",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        var mkFrame=typeof(CardVm).GetMethod("PushBody",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance);
        // 用 PushBody + StepUntilBlocked 执行自建节点，避免反射私有 VmFrame
        void Run(string tag, params EffectNode[] nodes) {
            vm.PushBody(nodes,sc0); var stt=vm.StepUntilBlocked();
            Console.WriteLine($"  {tag}: step={stt} fault={vm.FaultMessage??"-"}");
            PostChoice();
        }
        // 1. ModifyCounterNode 玩家级 vs 实体级
        Run("G1 counter X = 5 (Target=null)", new ModifyCounterNode{Counter=xKey,Op=CounterOp.Set,Delta=NumExpr.Const(5)});
        Console.WriteLine($"     playerX={st.ReadCounter(0,xKey)} src.entityX={st.ReadEntityCounter(src,xKey)}");
        Run("G2 counter X on $self = 7", new ModifyCounterNode{Counter=xKey,Op=CounterOp.Set,Delta=NumExpr.Const(7),Target=TargetSpec.Self});
        Console.WriteLine($"     playerX={st.ReadCounter(0,xKey)} src.entityX={st.ReadEntityCounter(src,xKey)}");
        // 2. DamageModifierNode SourceMask/Duration 是否生效
        var dm=new DamageModifierNode{Target=TargetSpec.Self,Kind=DamageAuraKind.TakenCap,SourceMask=DamageSourceMask.Combat,Amount=NumExpr.Const(1),Duration=DurationKind.ThisTurn};
        Run("G3 DamageModifier TakenCap(1) Combat only",dm);
        int takeAbility=st.ApplyTakenModifiers(src,5,DamageSourceMask.Ability);
        int takeCombat=st.ApplyTakenModifiers(src,5,DamageSourceMask.Combat);
        Console.WriteLine($"     ApplyTaken(Ability,5)={takeAbility} ApplyTaken(Combat,5)={takeCombat} (期望5/1); granted={st.GrantedAbilities.Count()}");
        // 3. RevealCardNode
        var rev=new RevealCardNode{Target=TargetSpec.Self};
        Run("G4 Reveal",rev);
        Console.WriteLine($"     src.IsRevealed={src.IsRevealed}");
        // 4. BranchMode Sequence / Loop Times / Conditional / InvokeAbility / Macro / ReplaceDeck / MarkVictory / Transform
        Run("G5 BranchMode Sequence pick=1",new BranchModeNode{Kind=ModeDispatchKind.Sequence,PickCount=NumExpr.Const(1),Options=new[]{new EffectNode[]{new ModifyCounterNode{Counter=xKey,Op=CounterOp.Add,Delta=NumExpr.Const(1)}}}});
        Console.WriteLine($"     playerX={st.ReadCounter(0,xKey)} (Sequence 语义应执行全部选项，无论 PickCount)");
        Run("G6 Loop Times 3",new LoopNode{Kind=LoopKind.Times,Count=NumExpr.Const(3),Body=new EffectNode[]{new ModifyCounterNode{Counter=xKey,Op=CounterOp.Add,Delta=NumExpr.Const(1)}}});
        Console.WriteLine($"     playerX={st.ReadCounter(0,xKey)}");
        Run("G7 Conditional true",new ConditionalNode{Condition=BoolExpr.True,Then=new EffectNode[]{new ModifyCounterNode{Counter=xKey,Op=CounterOp.Add,Delta=NumExpr.Const(1)}}});
        Console.WriteLine($"     playerX={st.ReadCounter(0,xKey)}");
        Run("G8 MarkVictory",new MarkVictoryCardNode{Owner=ZoneScope.Self});
        Console.WriteLine($"     P0.HasVictoryCard={st.Players[0].HasVictoryCard} WonByVictoryCard={st.Players[0].WonByVictoryCard}");
        Run("G9 Macro(undefined_macro)",new MacroNode{Macro=st.Schema.Macro("definitely_missing_macro")});
        Console.WriteLine($"     fault={vm.FaultMessage??"-"} corrupted={vm.StateCorrupted}");
        vm = new CardVm(st); vm.CurrentTurnOwner = 0;
        // 10. Transform 无卡号无 CopySource
        var tr0=new TransformNode{Target=TargetSpec.Self,IntoCardId=0,IntoCardName="NonExisting"};
        Run("G10 Transform by name",tr0);
        Console.WriteLine($"     src.CardId={src.Def?.CardId} src.Zone={src.Zone} (handler 直接 return false)");
        // 11. SpawnCard CopyOfPrevious 无前序
        var sp=new SpawnCardNode{SourceKind=CardSourceKind.CopyOfPrevious,Count=NumExpr.Const(1),ToZone=ZoneRef.Board};
        Run("G11 Spawn CopyOfPrevious",sp);
        Console.WriteLine($"     board0={st.Players[0].Board.Count} created={sc0.Read(TargetSpec.SlotCreated).Count}");
        // 12. GrantCounterAbility + mode_count
        var gca=new GrantCounterAbilityNode{Counter=st.Schema.Counter("faith"),Owner=ZoneScope.Self,Granted=new AbilityDef{Trigger=new TriggerSpec{Event=CoreEvent.PassiveAura},Body=new EffectNode[]{new ModifyCounterNode{Counter=st.Schema.Counter("mode_count"),Op=CounterOp.Add,Delta=NumExpr.Const(1)}}}};
        Run("G12 GrantCounterAbility PassiveAura mode_count+1",gca);
        Console.WriteLine($"     mode_count={st.ReadCounter(0,st.Schema.Counter("mode_count"))} grantedCounterAbilities={st.GrantedCounterAbilities.Count}");
    }

    static void FinalProbes()
    {
        Console.WriteLine("\n===== H. 终局复核 =====");
        // H1: 暴风破真实出牌，敌方随从用 CreateEntity 正常注册
        Fresh();
        var card=Put(10131320,0,ZoneRef.Hand);
        var foeDef=store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower);
        var foe=st.CreateEntity(foeDef.CardId,ZoneRef.Board,1);
        foe.Life=12; foe.MaxLife=12;
        vm.DropPendingTriggers();
        st.WriteCounter(0,st.Schema.Counter("pp"),10); st.ActivePlayer=0;
        bool ok=tr.PlayCard(0,card,vm);
        Console.WriteLine($"H1 出牌={ok} vmStatus={vm.Status} pending={(vm.Pending==null?"null":vm.Pending.Kind+":"+string.Join(",",vm.Pending.Candidates))}");
        PostChoice();
        Console.WriteLine($"H1 结算后 foe.Life={foe.Life}/12 (期望10, 因X=2) vmStatus={vm.Status} fault={vm.FaultMessage??"-"}");
        Console.WriteLine($"H1 X: player={st.ReadCounter(0,st.Schema.Counter("X"))} entity={st.ReadEntityCounter(card,st.Schema.Counter("X"))}");
        // H1b: 直接 PushBody(on_play) 观察 SelectTarget/Deal 的槽位
        Fresh();
        var cb=Put(10131320,0,ZoneRef.Hand);
        var fb=st.CreateEntity(foeDef.CardId,ZoneRef.Board,1); fb.Life=12; fb.MaxLife=12;
        vm.DropPendingTriggers();
        var op=Def(10131320).Abilities.First(a=>a.Trigger.Event==CoreEvent.OnPlay);
        var bsc=new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(cb)};
        vm.PushBody(op.Body,bsc);
        Console.WriteLine($"H1b push on_play step={vm.StepUntilBlocked()} pending={(vm.Pending==null?"null":vm.Pending.Kind.ToString())}");
        if(vm.Pending!=null){
            var q=vm.Pending;
            Console.WriteLine($"H1b cands=[{string.Join(",",q.Candidates)}] foeId={fb.EntityId}");
            vm.SubmitChoice(q.ChoiceId,new[]{fb.EntityId});
            Console.WriteLine($"H1b after choice step={vm.StepUntilBlocked()} $selected={bsc.Read(TargetSpec.SlotSelected).Count} foe.Life={fb.Life}/12");
        }
        Console.WriteLine($"H1b VmEval ctr:X={vm.Eval.EvalNum(((DealDamageNode)op.Body.Last()).Amount,bsc)}");
        // H2: 玩家级 X 与实体级 X 撕裂 -> 手工写 playerX=7 后伤害读谁
        Fresh();
        var c2=Put(10131320,0,ZoneRef.Hand);
        var f2=st.CreateEntity(foeDef.CardId,ZoneRef.Board,1); f2.Life=12; f2.MaxLife=12;
        vm.DropPendingTriggers();
        st.WriteCounter(0,st.Schema.Counter("X"),7);  // 模拟"X 被正确累加到玩家级"
        st.WriteCounter(0,st.Schema.Counter("pp"),10); st.ActivePlayer=0;
        var d=Def(10131320);
        var deal=(DealDamageNode)d.Abilities.First(a=>a.Trigger.Event==CoreEvent.OnPlay).Body.Last();
        var sc=new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(c2)};
        Console.WriteLine($"H2 playerX=7 entityX={st.ReadEntityCounter(c2,st.Schema.Counter("X"))} => VmEval ctr:X={vm.Eval.EvalNum(deal.Amount,sc)} (实体优先，玩家级7被吞)");
        bool ok2=tr.PlayCard(0,c2,vm); PostChoice();
        Console.WriteLine($"H2 出牌={ok2} foe.Life={f2.Life}/12 (若读玩家级应为5, 若读实体级应为10)");
        // H2b: OnPlay 的 zone=Board Gate（法术结算时卡在 Hand）
        Fresh();
        var oc=Put(10131320,0,ZoneRef.Hand);
        var of=st.CreateEntity(foeDef.CardId,ZoneRef.Board,1); of.Life=12; of.MaxLife=12;
        vm.DropPendingTriggers();
        var opAb=Def(10131320).Abilities.First(a=>a.Trigger.Event==CoreEvent.OnPlay);
        Console.WriteLine($"H2b OnPlay Trigger.ActiveZone={opAb.Trigger.ActiveZone} card.Zone={oc.Zone} SubjectFilter={(opAb.Trigger.SubjectFilter==null?"null":"set")}");
        vm.EnqueueEvent(new VmEvent{Kind=CoreEvent.OnPlay,Subject=EntityRef.From(oc),PlayerId=0});
        Console.WriteLine($"H2b Step(OnPlay, 主体=本卡, 卡在Hand)={vm.StepUntilBlocked()} pending={(vm.Pending==null?"null":vm.Pending.Kind+":"+string.Join(",",vm.Pending.Candidates))}");
        if(vm.Pending!=null){var q=vm.Pending;vm.SubmitChoice(q.ChoiceId,new[]{of.EntityId});Console.WriteLine($"H2b 选择后 Step={vm.StepUntilBlocked()} foe.Life={of.Life}/12 (期望10)");}
        // 对照组：事件主体不是本卡
        Fresh();
        var oc2=Put(10131320,0,ZoneRef.Hand);
        var other=Put(10133310,0,ZoneRef.Hand);
        vm.DropPendingTriggers();
        vm.EnqueueEvent(new VmEvent{Kind=CoreEvent.OnPlay,Subject=EntityRef.From(other),PlayerId=0});
        Console.WriteLine($"H2c 主体=别的卡 Step={vm.StepUntilBlocked()} pending={(vm.Pending==null?"null":vm.Pending.Kind.ToString())} (期望null)");
        // H2d: 引擎正规入口 MatchRunner.PlayCard（使用其私有 _vm，事件槽归属正确）
        st = pool.CreateBattleState(77);
        mr = new MatchRunner(st, st.Schema);
        var deck2 = store.SavedDecks.FirstOrDefault(d=>d.PoolId=="official");
        mr.StartWithConstructedDecks(pool,0,deck2,deck2);
        mr.ConfirmMulligan();
        st.WriteCounter(0,st.Schema.Counter("pp"),10); st.ActivePlayer=0;
        var pc=st.CreateEntity(10131320,ZoneRef.Hand,0);
        var pf=st.CreateEntity(foeDef.CardId,ZoneRef.Board,1); pf.Life=12; pf.MaxLife=12;
        var okMR=mr.PlayCard(pc);
        Console.WriteLine($"H2d MatchRunner.PlayCard={okMR} pending={(mr.PendingChoice==null?"null":mr.PendingChoice.Kind+":"+string.Join(",",mr.PendingChoice.Candidates))}");
        if(mr.PendingChoice!=null){
            mr.SubmitChoice(mr.PendingChoice.ChoiceId,new[]{pf.EntityId});
            Console.WriteLine($"H2d 选完 foe.Life={pf.Life}/12 (期望10) entityX={st.ReadEntityCounter(pc,st.Schema.Counter("X"))} playerX={st.ReadCounter(0,st.Schema.Counter("X"))}");
        }
        // H2e: DamageModifier Duration 单独隔离（不受前面 VM 状态污染）
        st = pool.CreateBattleState(91);
        var vmX=new CardVm(st); var trX=new TurnRunner(st,st.Schema); var mrX=new MatchRunner(st,st.Schema);
        var dk=store.SavedDecks.FirstOrDefault(d=>d.PoolId=="official");
        mrX.StartWithConstructedDecks(pool,0,dk,dk); mrX.ConfirmMulligan();
        var ownerE=st.CreateEntity(10131320,ZoneRef.Board,0);
        vmX.DropPendingTriggers();
        var dmod=new DamageModifierNode{Target=TargetSpec.Self,Kind=DamageAuraKind.TakenCap,SourceMask=DamageSourceMask.All,Amount=NumExpr.Const(2),Duration=DurationKind.ThisTurn};
        vmX.PushBody(new EffectNode[]{dmod},new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(ownerE)});
        Console.WriteLine($"H2e init step={vmX.StepUntilBlocked()} granted={st.GrantedAbilities.Count()} ApplyTaken(9)={st.ApplyTakenModifiers(ownerE,9,DamageSourceMask.Ability)}");
        trX.EndTurn(0,vmX); vmX.StepUntilBlocked();
        Console.WriteLine($"H2e after EndTurn granted={st.GrantedAbilities.Count()} ApplyTaken(9)={st.ApplyTakenModifiers(ownerE,9,DamageSourceMask.Ability)}");
        // H3: Transform 按名字查 AST/卡表
        Fresh();
        var tcard=Put(10131320,0,ZoneRef.Hand);
        var trNames=new[]{new TransformNode{Target=TargetSpec.Self,IntoCardId=0,IntoCardName="不存在的卡"}};
        vm.PushBody(trNames,new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(tcard)});
        Console.WriteLine($"H3 Transform by CardName step={vm.StepUntilBlocked()} cardId={tcard.Def.CardId} (handler 无 name 分支)");
        // H4: BranchMode Sequence 的 PickCount 是否生效
        Fresh();
        var key=st.Schema.Counter("X");
        var bm=new BranchModeNode{Kind=ModeDispatchKind.Sequence,PickCount=NumExpr.Const(1),Options=new[]{
            new EffectNode[]{new ModifyCounterNode{Counter=key,Op=CounterOp.Add,Delta=NumExpr.Const(1)}},
            new EffectNode[]{new ModifyCounterNode{Counter=key,Op=CounterOp.Add,Delta=NumExpr.Const(1)}},
            new EffectNode[]{new ModifyCounterNode{Counter=key,Op=CounterOp.Add,Delta=NumExpr.Const(1)}}}};
        vm.PushBody(new EffectNode[]{bm},new VmScope{Owner=ZoneScope.Self,Controller=0});
        Console.WriteLine($"H4 Sequence pick=1 3选项 step={vm.StepUntilBlocked()} X={st.ReadCounter(0,key)} (卡面语义应=1, 实现执行全部=3)");
        // H5: DamageModifier Duration 到期
        Fresh();
        var owner=Put(10131320,0,ZoneRef.Board);
        var dm=new DamageModifierNode{Target=TargetSpec.Self,Kind=DamageAuraKind.TakenCap,SourceMask=DamageSourceMask.All,Amount=NumExpr.Const(2),Duration=DurationKind.ThisTurn};
        vm.PushBody(new EffectNode[]{dm},new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(owner)});
        vm.StepUntilBlocked();
        Console.WriteLine($"H5 授予后 granted={st.GrantedAbilities.Count()} ApplyTaken(Ability,9)={st.ApplyTakenModifiers(owner,9,DamageSourceMask.Ability)}");
        tr.StartTurn(1,vm); // 结束我方回合
        tr.StartTurn(0,vm);
        Console.WriteLine($"H5 跨回合后 granted={st.GrantedAbilities.Count()} ApplyTaken(Ability,9)={st.ApplyTakenModifiers(owner,9,DamageSourceMask.Ability)}");
    }

    static void Structural()
    {
        Console.WriteLine("\n===== I. 节点字段语义缺口的结构性证据（全卡池真实 AST） =====");
        var all = store.Catalog.AllCards.Values.ToList();
        var bodies = new List<(int id, EffectNode n)>();
        void Collect(int id, EffectNode n) {
            if (n==null) return; bodies.Add((id,n));
            switch(n){
              case ConditionalNode c: foreach(var z in c.Then??Array.Empty<EffectNode>())Collect(id,z); foreach(var z in c.Else??Array.Empty<EffectNode>())Collect(id,z); break;
              case LoopNode l: foreach(var z in l.Body??Array.Empty<EffectNode>())Collect(id,z); break;
              case BranchModeNode b: foreach(var o in b.Options??Array.Empty<IReadOnlyList<EffectNode>>())foreach(var z in o??Array.Empty<EffectNode>())Collect(id,z); break;
              case GrantAbilityNode g: foreach(var z in g.Granted?.Body??Array.Empty<EffectNode>())Collect(id,z); break;
              case GrantCounterAbilityNode gc: foreach(var z in gc.Granted?.Body??Array.Empty<EffectNode>())Collect(id,z); break;
            }
        }
        foreach(var c in all) {
          foreach(var ab in c.Abilities??Array.Empty<AbilityDef>()) foreach(var n in ab.Body??Array.Empty<EffectNode>()) Collect(c.CardId,n);
          foreach(var d in new[]{c.CrestDef,c.FaithDef,c.AccelerateDef,c.CrystallizeDef})
            if(d!=null) foreach(var ab in d.Abilities??Array.Empty<AbilityDef>()) foreach(var n in ab.Body??Array.Empty<EffectNode>()) Collect(c.CardId,n);
        }
        Console.WriteLine("总节点实例="+bodies.Count);
        Console.WriteLine("DealDamageNode IsAbility=false(应走战斗伤害) 卡数="+bodies.Where(x=>x.n is DealDamageNode dd && !dd.IsAbility).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is DealDamageNode dd && !dd.IsAbility).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("ModifyStatsNode Op=Set 且 Duration!=Permanent 卡数="+bodies.Where(x=>x.n is ModifyStatsNode ms && ms.Op==StatModOp.Set && ms.Duration!=DurationKind.Permanent).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is ModifyStatsNode ms && ms.Op==StatModOp.Set && ms.Duration!=DurationKind.Permanent).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("TransformNode IntoCardId=0 且 CopySource=null 卡数="+bodies.Where(x=>x.n is TransformNode t && t.IntoCardId==0 && t.CopySource==null).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is TransformNode t && t.IntoCardId==0 && t.CopySource==null).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("SpawnCardNode CardId=0 且 CardName非空 且非Crest/Copy 卡数="+bodies.Where(x=>x.n is SpawnCardNode sp && sp.CardId==0 && !string.IsNullOrEmpty(sp.CardName) && !sp.IsCrest && sp.SourceKind==CardSourceKind.CardId).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is SpawnCardNode sp && sp.CardId==0 && !string.IsNullOrEmpty(sp.CardName) && !sp.IsCrest && sp.SourceKind==CardSourceKind.CardId).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("SpawnCardNode ToOwner=Any 且非Crest 卡数="+bodies.Where(x=>x.n is SpawnCardNode sp && sp.ToOwner==ZoneScope.Any && !sp.IsCrest).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is SpawnCardNode sp && sp.ToOwner==ZoneScope.Any && !sp.IsCrest).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("GrantCounterAbilityNode Counter!=faith 卡数="+bodies.Where(x=>x.n is GrantCounterAbilityNode gc && !string.Equals(gc.Counter.Name,"faith",StringComparison.OrdinalIgnoreCase)).Select(x=>x.id).Distinct().Count()+" 例="+string.Join(",",bodies.Where(x=>x.n is GrantCounterAbilityNode gc && !string.Equals(gc.Counter.Name,"faith",StringComparison.OrdinalIgnoreCase)).Select(x=>x.id).Distinct().Take(8)));
        Console.WriteLine("ModifyCounterNode Op=Fill 卡数="+bodies.Where(x=>x.n is ModifyCounterNode mc && mc.Op==CounterOp.Fill).Select(x=>x.id).Distinct().Count());
        var loops=bodies.Where(x=>x.n is LoopNode).Select(x=>(x.id,(LoopNode)x.n)).ToList();
        Console.WriteLine("LoopNode kinds="+string.Join(";",loops.GroupBy(x=>x.Item2.Kind).Select(g=>g.Key+"="+g.Count())));
        var branches=bodies.Where(x=>x.n is BranchModeNode).Select(x=>(x.id,(BranchModeNode)x.n)).ToList();
        Console.WriteLine("BranchMode kinds="+string.Join(";",branches.GroupBy(x=>x.Item2.Kind).Select(g=>g.Key+"="+g.Count())));
        Console.WriteLine("BranchMode Sequence with PickCount<Options.Count 卡数="+branches.Count(x=>x.Item2.Kind==ModeDispatchKind.Sequence && x.Item2.Options.Count>(x.Item2.PickCount?.Value??1))+" (AST 注释规定 Sequence 全部执行)");
        // ReplaceDeck 实际复现
        Fresh();
        var d101=Def(10104120);
        var rd=(ReplaceDeckNode)d101.Abilities.First(a=>a.Trigger.Event==CoreEvent.OnPlay).Body[0];
        Console.WriteLine($"I ReplaceDeck DeckName=[{rd.DeckName}] DeckDefs.Has={CardVm.DeckDefs.Has(rd.DeckName)} Expand={CardVm.DeckDefs.Expand(rd.DeckName)?.Count??-1}");
        int before=st.Players[0].Deck.Count;
        vm.PushBody(new EffectNode[]{rd},new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(Put(10104120,0,ZoneRef.Hand))});
        Console.WriteLine($"I 执行 step={vm.StepUntilBlocked()} fault={vm.FaultMessage??"-"} 牌库 {before}->{st.Players[0].Deck.Count}");
        // IsAbility=false 的伤害来源
        Fresh();
        var victim=st.CreateEntity(10131320,ZoneRef.Leader,0); victim.Life=10;victim.MaxLife=10;
        var atkSrc=Put(10131320,0,ZoneRef.Board);
        int combat=st.ApplyTakenModifiers(victim,3,DamageSourceMask.Combat);
        Console.WriteLine($"I DealDamageNode.IsAbility=false 被 DoDamage 传给 ApplyDamage 后完全未使用(CardVm.Nodes.cs:406-424)；CombatResolver 默认 source=Ability。ApplyTaken(Combat,3)={combat}");
    }

    static void RealSequence()
    {
        Console.WriteLine("\n===== J. 真实卡 Sequence(pick=1) 复核 =====");
        // 10703210 巴别隆城：turn_end / modes Sequence(pick=1) 三选一全跑
        Fresh();
        var d=Def(10703210);
        Console.WriteLine("  PyAST: "+(d.SourceText??"").Replace("\n"," / "));
        var te=d.Abilities.FirstOrDefault(a=>a.Trigger.Event==CoreEvent.TurnEnd);
        var bm=(BranchModeNode)te.Body.FirstOrDefault(n=>n is BranchModeNode);
        Console.WriteLine($"  BranchMode Kind={bm.Kind} PickCount={bm.PickCount?.Op}({bm.PickCount?.Value}) Options={bm.Options.Count}");
        var amulet=Put(10703210,0,ZoneRef.Board);
        var enemy=st.CreateEntity(store.Catalog.AllCards.Values.First(c=>c.Kind==CardKind.Follower).CardId,ZoneRef.Board,1);
        enemy.Life=12;enemy.MaxLife=12;
        vm.DropPendingTriggers();
        int leader0=st.Players[0].Leader?.Life??-1, leader1=st.Players[1].Leader?.Life??-1;
        vm.PushBody(te.Body,new VmScope{Owner=ZoneScope.Self,Controller=0,Source=EntityRef.From(amulet)});
        Console.WriteLine($"  执行 step={vm.StepUntilBlocked()} fault={vm.FaultMessage??"-"}");
        PostChoice();
        Console.WriteLine($"  结果 敌方随从Life={enemy.Life}/12 己方主战者Life={st.Players[0].Leader?.Life}(前{leader0}) 敌方主战者Life={st.Players[1].Leader?.Life}(前{leader1}) 护符Zone={amulet.Zone}");
        Console.WriteLine("  卡面 pick=1 应只执行 1 项；实测若三项效果全部落地(随从-2、己方+2、敌方-2、护符被破坏)则确认实现偏差");
    }
}
