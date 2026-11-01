using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// DIY 四端造卡的五道硬核门禁
//
// 这五道门禁按「从弱到强」排列，每一道都在前一道路上加了约束：
//   1. 中文渲染         —— 能读懂
//   2. 积木往返         —— 结构不丢
//   3. 四端同构         —— 四条路产出同一张卡
//   4. 真机对战         —— 装进引擎真的能跑
//   5. 常规三大门禁     —— 构建/回归/静态检查
//
// 为什么必须有第 3 道：四端各自的「能编译」只说明各自能跑通自己那条路。
// 只有当四条路的产物在 AST 与中文渲染上**逐字相同**，
// 才谈得上「玩家用哪种方式造卡都一样」。
// ============================================================================
public static partial class Regress
{
    public static string RunDiy()
    {
        var log = new StringBuilder();
        int pass = 0, total = 0;

        void Check(string name, Func<(bool ok, string detail)> run)
        {
            total++;
            bool ok;
            string detail;
            try { (ok, detail) = run(); }
            catch (Exception ex) { ok = false; detail = "异常 " + ex.GetType().Name + ": " + ex.Message; }
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        Check("DIY-1 30 张真卡中文渲染零洋泾浜", GateChineseRendering);
        Check("DIY-2 30 张真卡积木往返无损", GateBlockRoundTrip);
        Check("DIY-3 自定义词条 + 四端同构等价", GateFourWayEquivalence);
        Check("DIY-4 DIY 卡真机对战效用", GateLiveBattle);

        log.AppendLine();
        log.AppendLine("=== DIY 门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    // ---------------------------------------------------------------- 夹具

    private static DiyWorkspace DiyCtx(int seed = 1, bool withTestset = true)
    {
        var ws = DiyWorkspace.Create(seed);
        ws.Context.Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier");
        if (withTestset) TestsetLoader.LoadInto(ws.Context);
        return ws;
    }

    // ---------------------------------------------------------------- 门禁 1

    private static (bool, string) GateChineseRendering()
    {
        var ws = DiyCtx();
        var cards = TestsetLoader.LoadInto(ws.Context);
        if (cards.Count != 30) return (false, "测试集不是 30 张：" + cards.Count);

        // 洋泾浜的判据：英文语法词残留。
        // 这些词一旦出现在中文卡面里，说明渲染器退回了「逐词替换」，
        // 而那不是难读的问题 —— 它掩盖了 AST 是否真的对齐。
        string[] forbidden =
        {
            "add ", "return ", " and ", " or ", "to your hand", "to the field",
            "Follower", "Spell", "Amulet", "under some condition", "+0/+0",
            "Deal ", "Damage ", "Select ", "Give ", "at random",
        };

        var bad = new List<string>();
        int labelled = 0;

        foreach (var c in cards)
        {
            var zh = ws.Printer.PrintCard(c.Def);
            if (string.IsNullOrWhiteSpace(zh)) { bad.Add(c.CardId + " 渲染为空"); continue; }


            foreach (var f in forbidden)
            {
                // 卡名本身可能是英文（DIY 卡没有中文名），只查能力行。
                var lines = zh.Split('\n').Where(l => !l.Contains("《") && !l.Contains("【关联"));
                if (lines.Any(l => l.Contains(f, StringComparison.Ordinal)))
                    bad.Add(c.CardId + " 残留「" + f.Trim() + "」");
            }

            // 「有中文能力标签」：任意一个【…】形式的触发头即可。
            // 触发头有二十多种，只列几个已知的会让正确渲染被判成失败。
            bool anyLabel = System.Text.RegularExpressions.Regex.IsMatch(
                zh, "【[^】]*(曲|时|启动|融合|结晶|激奏|纹章|信仰|场|牌|回合|阶段)[^】]*】");
            if (anyLabel) labelled++;
        }

        if (bad.Count > 0) return (false, string.Join(" | ", bad.Take(4)) + "（共 " + bad.Count + " 处）");
        // 判据不是「含某几个固定标签」，而是「含任意一个中文能力标签」：
        // 触发头有二十多种（含【我方回合结束时】【进入战场时】【在手牌中】……），
        // 只列几个已知的会让正确渲染被判成失败 —— 假红同样是假。
        if (labelled < 30) return (false, "带能力标签的只有 " + labelled + "/30 张");
        return (true, "30 张全部中文渲染，零英文残留，带标签 " + labelled + " 张");
    }

    // ---------------------------------------------------------------- 门禁 2

    private static (bool, string) GateBlockRoundTrip()
    {
        var ws = DiyCtx();
        var cards = TestsetLoader.LoadInto(ws.Context);
        var dec = new DiyBlockDecompiler(ws.Schema);

        int ok = 0;
        var bad = new List<string>();

        foreach (var c in cards)
        {
            var blocks = dec.Decompile(c.Def);
            var derrs = dec.Errors.ToList();
            if (derrs.Count > 0) { bad.Add(c.CardId + " 反编译失败：" + derrs[0]); continue; }

            var res = new DiyBlockCompiler(ws.Schema).Compile(blocks);
            if (!res.Ok) { bad.Add(c.CardId + " 重编译失败：" + res.Errors.FirstOrDefault()); continue; }

            string diff = "";
            if (!DiyWorkspace.StructurallyEqual(c.Def, res.Def, out diff))
            {
                bad.Add(c.CardId + " 不等价：" + diff.Split('\n')[0]);
                continue;
            }
            ok++;
        }

        if (bad.Count > 0) return (false, string.Join(" | ", bad.Take(3)) + "（共 " + bad.Count + " 张）");
        return (true, ok + "/30 反编译→重编译全部等价");
    }
    // ---------------------------------------------------------------- 门禁 3

    /// <summary>
    /// 四端同构：CNL / 脚本 / 积木 / 表单各造一次同一张《御剑机甲师》。
    ///
    /// 判据是 ChineseCardPrinter 与 AstEnglishPrinter 的输出逐字相同。
    /// 只比 CardDef 的字段不够 —— 字段可以相同而能力树不同（例如节点顺序、
    /// 目标筛选的写法），而那正是不同编译器最容易分叉的地方。
    /// </summary>
    private static (bool, string) GateFourWayEquivalence()
    {
        var ws = DiyCtx(seed: 7, withTestset: false);
        ws.DefineTrait("Mecha", "机甲");
        ws.DefineCounter("sword_intent", "剑意", 0, 10);
        ws.DefineClan("Swordcraft", "皇家");

        const int id = 910001;
        const string nameChs = "御剑机甲师";
        const string nameEng = "Blademech Adept";
        ws.DefineCardName(nameEng, id);

        var made = new List<(string way, CardDef def, string err)>();

        // ① 受限英文 CNL
        {
            // 用引擎已支持的 CNL 句式（与脚本/积木/表单三端表达同一张卡）：
            //   · "Gain 2 <counter>"        = 计数器 +2
            //   · "deal it <counter> damage" = 伤害值取计数器
            // 官方卡池里等价的说法是 "damage equal to your X"，但那条句式
            // 目前不在 CNL 的语法里 —— 补它属于扩充官方英文语法，不在 DIY 范围内。
            // 用引擎已支持的 CNL 句式，且与脚本/积木/表单三端表达同一张卡：
            //   "Gain 2 <counter>"            → 计数器 +2
            //   "deal it X damage. X is your <counter>" → 伤害值取该计数器
            // 官方卡池里更常见的 "damage equal to your X" 不在当前 CNL 语法内
            // （补它属于扩充官方英文语法，超出 DIY 范围）。
            var text = "<b><color=Keyword>Ward</color></b>\n"
                     + "<hr><b><color=Keyword>Fanfare</color></b>: "
                     + "Gain 2 <b><color=Keyword>sword_intent</color></b>. "
                     + "Select an enemy follower on the field and deal it X damage. "
                     + "X is your <b><color=Keyword>sword_intent</color></b>.";
            var def = ws.CompileFromEnglishCnl(new DiyWorkspace.CnlCardSpec
            {
                CardId = id, Name = nameEng, NameChs = nameChs,
                Kind = CardKind.Follower, Cost = 3, Attack = 2, Life = 3,
                Clan = "Swordcraft", Traits = new[] { "Mecha" },
                EngText = text,
            }, out var errs);
            made.Add(("CNL", def, errs != null && errs.Count > 0 ? errs[0] : null));
        }

        // ② ReshadowScript
        {
            var script = "card " + id + " \"" + nameChs + "\" (Follower, cost: 3, atk: 2, life: 3, clan: Swordcraft, traits: [Mecha]) {\n"
                       + "    keywords: [ward]\n"
                       + "    on play {\n"
                       + "        gain_counter(sword_intent, 2)\n"
                       + "        select $selected = target(enemy, board, follower, count: 1)\n"
                       + "        damage($selected, counter(sword_intent))\n"
                       + "    }\n"
                       + "}\n";
            var comp = new ReshadowScriptCompiler(ws);
            var list = comp.Compile(script);
            made.Add(("Script",
                list.FirstOrDefault(),
                comp.Errors.Count > 0 ? comp.Errors[0] : null));
        }

        // ③ Scratch 积木
        {
            var doc = new CardBlockWorkspace
            {
                CardId = id, Name = nameChs, Kind = "Follower",
                Cost = 3, Attack = 2, Life = 3, Clan = "Swordcraft",
                Traits = new List<string> { "Mecha" },
                Keywords = new List<string> { "ward" },
            };
            doc.Hats.Add(new Block
            {
                Opcode = "hat_self_event",
                Text = "OnPlay",
                Stacks = new List<Block>
                {
                    new Block
                    {
                        Opcode = "act_mod_counter",
                        Text = "sword_intent",
                        Number = new Block { Opcode = "num_const", IntA = 2 },
                    },
                    new Block
                    {
                        Opcode = "act_select",
                        Text = TargetSpec.SlotSelected,
                        Target = new Block
                        {
                            Opcode = "tgt_query", Text = "Follower", EnumA = "Choose",
                            IntA = 0, IntB = 1,
                            Number = new Block { Opcode = "num_const", IntA = 1 },
                        },
                    },
                    new Block
                    {
                        Opcode = "act_damage",
                        Target = new Block { Opcode = "tgt_slot", Text = TargetSpec.SlotSelected },
                        Number = new Block { Opcode = "num_counter", Text = "sword_intent" },
                    },
                },
            });
            var res = new DiyBlockCompiler(ws.Schema).Compile(doc);
            made.Add(("Blocks", res.Def, res.Ok ? null : res.Errors.FirstOrDefault()));
        }

        // ④ 表单
        {
            var form = new CardFormModel
            {
                CardId = id, NameChs = nameChs, NameEng = nameEng,
                Kind = "Follower", Clan = "Swordcraft",
                Traits = new List<string> { "Mecha" },
                Cost = 3, Attack = 2, Life = 3,
                Keywords = new List<string> { "ward" },
                Abilities = new List<AbilityFormItem>
                {
                    new AbilityFormItem
                    {
                        Trigger = "OnPlay",
                        Actions = new List<ActionFormRow>
                        {
                            new ActionFormRow
                            {
                                Kind = FormActionKind.GainCounter,
                                Name = "sword_intent",
                                NumberSource = FormNumberSource.Const,
                                Number = 2,
                            },
                            new ActionFormRow
                            {
                                Kind = FormActionKind.Damage,
                                Target = new TargetFilterForm
                                {
                                    Owner = "Opponent", Zone = "Board", Kind = "Follower",
                                    Mode = "Choose", Count = 1,
                                },
                                NumberSource = FormNumberSource.CounterValue,
                                Name = "sword_intent",
                            },
                        },
                    },
                },
            };
            var fc = new DiyFormCompiler(ws.Schema);
            var def = fc.FormToCardDef(form);
            made.Add(("Form", def, fc.Errors.Count > 0 ? fc.Errors[0] : null));
        }

        // ---- 逐个校验 ----
        var bad = new List<string>();
        foreach (var (way, def, err) in made)
        {
            if (err != null) bad.Add(way + " 编译失败：" + err);
            else if (def == null) bad.Add(way + " 产出 null");
        }
        if (bad.Count > 0) return (false, string.Join(" | ", bad));

        // 四端在两种渲染下的输出必须逐字相同。
        string en0 = AstEnglishPrinter.PrintCard(made[0].def.Abilities);
        string zh0 = ws.Printer.PrintCard(made[0].def);
        foreach (var (way, def, _) in made.Skip(1))
        {
            string en = AstEnglishPrinter.PrintCard(def.Abilities);
            string zh = ws.Printer.PrintCard(def);
            if (en != en0) bad.Add(way + " 的英文 AST 与 CNL 不同：\n    " + en + "\n    vs " + en0);
            if (zh != zh0) bad.Add(way + " 的中文卡面与 CNL 不同：\n    " + zh.Replace("\n", " / ") + "\n    vs " + zh0.Replace("\n", " / "));
        }

        if (bad.Count > 0) return (false, string.Join(" | ", bad.Take(2)));
        return (true, "四端产物在两种渲染下逐字相同；中文=" + zh0.Replace("\n", " / "));
    }
    // ---------------------------------------------------------------- 门禁 4

    /// <summary>
    /// 真机对战：把门禁 3 造的卡放进真实引擎打出，并配一张
    /// 「Whenever an allied Mecha follower enters the field」的支援卡。
    ///
    /// 这一道不能被前三道替代：前三道都是**结构**校验，
    /// 而「AST 结构对」与「装进引擎真的会打 2 点」是两件事。
    /// </summary>
    private static (bool, string) GateLiveBattle()
    {
        var ws = DiyCtx(seed: 11, withTestset: false);
        ws.DefineTrait("Mecha", "机甲");
        var swordIntent = ws.DefineCounter("sword_intent", "剑意", 0, 10);
        ws.DefineClan("Swordcraft", "皇家");
        var st = ws.State;

        // 主战者
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 5;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(1, st.Schema.Counter("pp"), 20);

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        var tr = new TurnRunner(st, st.Schema);

        // ---- DIY 卡：御剑机甲师（入场曲：剑意+2，选 1 个敌方随从打「剑意」）----
        const int swordId = 920001;
        ws.DefineCardName("Blademech Adept", swordId);
        var script =
            "card " + swordId + " \"御剑机甲师\" (Follower, cost: 3, atk: 2, life: 3, traits: [Mecha]) {\n" +
            "    keywords: [ward]\n" +
            "    on play {\n" +
            "        gain_counter(sword_intent, 2)\n" +
            "        select $selected = target(enemy, board, follower, count: 1)\n" +
            "        damage($selected, counter(sword_intent))\n" +
            "    }\n" +
            "}\n";
        var comp = new ReshadowScriptCompiler(ws);
        var crafted = comp.Compile(script).FirstOrDefault();
        if (crafted == null) return (false, "DIY 卡解析失败：" + string.Join(" | ", comp.Errors));
        st.RegisterCard(crafted);

        // ---- DIY 支援卡：每当友方 Mecha 随从进场，抽 1 ----
        const int supportId = 920002;
        ws.DefineCardName("Mech Workshop", supportId);
        var supportScript =
            "card " + supportId + " \"机甲工坊\" (Amulet, cost: 1) {\n" +
            "    on enter on(target(ally, board, follower, trait: Mecha)) {\n" +
            "        draw(1)\n" +
            "    }\n" +
            "}\n";
        var support = comp.Compile(supportScript).FirstOrDefault();
        if (support == null) return (false, "支援卡解析失败：" + string.Join(" | ", comp.Errors));
        st.RegisterCard(support);

        for (int i = 0; i < 10; i++)
        {
            st.RegisterCard(new CardDef
            {
                CardId = 930000 + i, Name = "Fill" + i, Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 1,
            });
            st.SpawnCard(930000 + i, ZoneRef.Deck, 0);
        }

        // 敌方 3 血随从作为伤害落点
        st.RegisterCard(new CardDef
        {
            CardId = 940001, Name = "Target", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 9, BaseCost = 1,
        });
        var foe = st.SpawnCard(940001, ZoneRef.Board, 1);

        // 支援卡先进场（这样它才能看到后面的 Mecha 随从进场）
        var supportCard = st.SpawnCard(supportId, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, supportCard, vm);
        vm.StepUntilBlocked();
        if (supportCard.Zone != ZoneRef.Board) return (false, "支援卡没进场：" + supportCard.Zone);

        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();

        // 打出 DIY 卡
        var card = st.SpawnCard(swordId, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        bool played = tr.PlayCard(0, card, vm);
        int guard = 0;
        while (vm.Pending != null && guard++ < 8)
        {
            vm.SubmitChoice(vm.Pending.ChoiceId, new[] { foe.EntityId });
            vm.StepUntilBlocked();
        }
        vm.StepUntilBlocked();

        bool onBoard = card.Zone == ZoneRef.Board;
        bool ward = card.Keywords.Contains(st.Schema.Keyword("ward"));
        int intent = st.ReadCounter(0, swordIntent);
        bool counterOk = intent == 2;
        bool damaged = foe.Life == 7;          // 9 - 2
        int handAfter = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool supportFired = handAfter >= handBefore;   // 打出 -1，支援抽 +1 → 净持平或更多

        bool ok = played && onBoard && ward && counterOk && damaged && supportFired;
        var detail = "打出=" + played + " 在场=" + onBoard + " 守护=" + ward
                     + " 剑意=" + intent + "（期望2）"
                     + " 敌方=" + foe.Life + "（期望7）"
                     + " Mecha联动抽牌=" + supportFired + "（手牌 " + handBefore + "->" + handAfter + "）";
        return (ok, detail);
    }
}
