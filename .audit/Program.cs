using System;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.Ui.Bridge;

public class Program { public static void Main(){
  Console.OutputEncoding = System.Text.Encoding.UTF8;
  var store = new GameSessionStore(); store.Initialize();
  var def = store.Catalog.AllCards.Values.First(c => c.CardId == 10131320);
  var pool = store.GetPoolRuntime("official");
  var st = pool.CreateBattleState(42);
  var deck = store.SavedDecks.First(d => d.PoolId == "official");
  var runner = new MatchRunner(st, st.Schema);
  runner.StartWithConstructedDecks(pool, 0, deck, deck);

  // 用引擎正规路径造实体（会进 _all，GetLive 能找到）
  var e = st.CreateEntity(10131320, ZoneRef.Hand, 0);
  var xKey = st.Schema.Counter("X");
  Console.WriteLine("实体已入_all=" + (st.GetLive(e.EntityId, e.EntrySerial) != null));
  Console.WriteLine("CreateEntity后 实体X=" + (e.Counters.TryGetValue(xKey, out var xv) ? xv : -1)
      + " 玩家X=" + st.ReadCounter(0, xKey));

  var ev = new VmEval(st);
  var scope = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(e) };
  var num = NumExpr.Ctr(xKey);
  Console.WriteLine("VmEval ctr:X (Source=卡) = " + ev.EvalNum(num, scope));
  Console.WriteLine("VmEval ctr:X (无Source) = " + ev.EvalNum(num, new VmScope { Owner = ZoneScope.Self, Controller = 0 }));

  st.EmitEvent(new VmEvent { Kind = CoreEvent.OnSpellboost, PlayerId = 0, Subject = EntityRef.From(e) });
  Console.WriteLine("OnSpellboost后 实体X=" + (e.Counters.TryGetValue(xKey, out var xv2) ? xv2 : -1)
      + " 玩家X=" + st.ReadCounter(0, xKey));
  foreach (var ab in def.Abilities)
    Console.WriteLine("  event=" + ab.Trigger?.Event + " zone=" + ab.Trigger?.ActiveZone);
}}