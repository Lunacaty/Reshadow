using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
public class Program {
  static GameSchema _schema; static Dictionary<int,string> _raw=new();
  public static void Main(){
    Console.OutputEncoding=System.Text.Encoding.UTF8;
    _schema=GameSchema.CreateDefault(); var parser=new AstCanonicalParser(_schema);
    var root=JsonDocument.Parse(File.ReadAllText("data/packs/official-sv/pack.json")).RootElement;
    foreach(var c in root.GetProperty("cards").EnumerateArray()) _raw[c.GetProperty("card_id").GetInt32()]=c.GetProperty("text").GetString();
    var w=new MemoryWorld(7); var ev=new VmEval(w);
    var scope=new VmScope{Owner=ZoneScope.Self,Controller=0};
    var srcDef=parser.ParseCard(_raw[10132120]);
    var src=w.Create(10132120,ZoneRef.Board,0,atk:3,life:3,cost:3); src.Def=srcDef; scope.Source=EntityRef.From(src);
    var golemDef=parser.ParseCard("card 88999991 \"g\" (Follower, clan=巫师, cost=2, atk=2, life=2):\n    traits: [巨像]\n    fanfare:\n        draw 1");
    var spellDef=parser.ParseCard("card 88999992 \"s\" (Spell, clan=巫师, cost=1):\n    on_play:\n        draw 1");
    // 2 巨像随从 + 1 带巨像兵种的法术 + 1 普通随从
    var g1=w.Create(88999991,ZoneRef.Board,0,atk:2,life:2,cost:2);g1.Def=golemDef;
    var g2=w.Create(88999991,ZoneRef.Board,0,atk:2,life:2,cost:2);g2.Def=golemDef;
    var am=w.Create(88999992,ZoneRef.Board,0,cost:1);am.Def=spellDef;am.Traits.Add(_schema.Trait("巨像"));
    var plain=w.Create(88999992,ZoneRef.Board,0,atk:1,life:1,cost:1);plain.Def=golemDef;plain.Traits.Clear();
    Console.WriteLine("=== 10132120 奇迹女巫·爱蜜儿 (Follower 主语) 现场 ===");
    Console.WriteLine("  世界: 2 巨像随从 + 1 带巨像兵种的「法术」 + 1 普通随从 => 正确 X=2（巨像·随从）");
    string tmp="card 88999999 \"t\" (Spell, clan=中立, cost=1):\n    on_play:\n        deal count(self:board<Follower> All where has trait 巨像) damage to opponent:leader";
    var td=parser.ParseCard(tmp);
    var dd=(DealDamageNode)td.Abilities.SelectMany(a=>a.Body??Array.Empty<EffectNode>()).First(n=>n is DealDamageNode);
    Console.WriteLine("  EvalNum(count(self:board<Follower> All where has trait 巨像)) = "+ev.EvalNum(dd.Amount,scope));
    var orig=dd.Amount.Of;
    var fixedOf=new TargetSpec{SourceKind=orig.SourceKind,Owner=orig.Owner,Zone=orig.Zone,Mode=orig.Mode,Where=Fix(orig.Where),History=orig.History};
    Console.WriteLine("  修正 HasKind.Of=$item 后 = "+ev.EvalNum(new NumExpr{Op=NumOp.Count,Of=fixedOf},scope));
    Console.WriteLine("  Where 原式 = "+Shape(orig.Where));
  }
  static BoolExpr Fix(BoolExpr b){
    if(b==null)return null;
    if(b is AndExpr a)return new AndExpr(a.Items.Select(Fix).ToList());
    if(b is HasKindExpr hk&&hk.Of?.Slot==TargetSpec.SlotSelf)return new HasKindExpr{Kind=hk.Kind,Of=TargetSpec.Item};
    return b;
  }
  static string Shape(BoolExpr b){
    if(b==null)return "null";
    if(b is AndExpr a)return "And["+string.Join(",",a.Items.Select(Shape))+"]";
    if(b is HasKindExpr k)return "HasKind("+k.Kind+",Of="+(k.Of?.Slot??"null")+")";
    if(b is HasTraitExpr t)return "HasTrait("+t.Trait.Name+",Of="+(t.Of?.Slot??"null")+")";
    return b.GetType().Name;
  }
}
