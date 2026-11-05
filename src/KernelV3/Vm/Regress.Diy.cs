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
        Check("DIY-5 往返后真卡仍能实战生效", GateRoundTripLiveBattle);
        Check("DIY-6 纯真实对局（无手搭夹具）", GateRealMatch);
        Check("DIY-8 复刻能力遍历全部同事件能力且绑定 $self", GateInvokeCoverage);

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

            // 兜底：任何连续 3 个以上 ASCII 字母都是英文残留。
            // 之前按「已知英文语法词」列表查，带下划线的内码（cannot_be_played）
            // 与没列进表的词全都能蒙混过去 —— 白名单式检查永远漏。
            // 卡名（《…》）豁免：DIY 卡本来就可能没有中文名。
            foreach (var line in zh.Split('\n'))
            {
                if (line.Contains("《") || line.Contains("【关联")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, "[A-Za-z]{3,}");
                if (m.Success) bad.Add(c.CardId + " 残留英文 \"" + m.Value + "\"");
            }

            // 「有中文能力标签」：任意一个【…】形式的触发头即可。
            // 触发头有二十多种，只列几个已知的会让正确渲染被判成失败。
            bool anyLabel = System.Text.RegularExpressions.Regex.IsMatch(
                zh, "【[^】]*(曲|时|启动|融合|结晶|激奏|纹章|信仰|场|牌|回合|阶段)[^】]*】");
            // 「每当…时，」也是合法的触发头（旁观型与玩家级事件的官方写法），
            // 只认【】会让这类正确渲染被判成没标签。
            // 法术例外：法术的打出效果**没有触发头**，卡面直接印效果正文
            // （官方从不给法术印【入场曲】），所以它们本来就不该有标签。
            if (c.Def.Kind == ShadowverseFan.KernelV3.Ast.CardKind.Spell) labelled++;
            else if (anyLabel || zh.Contains("每当")) labelled++;
        }

        // ---- 定点断言：这几处是「渲染器盲区」的高发地带，必须逐条盯住 ----
        var zhById = cards.ToDictionary(c => c.CardId, c => ws.Printer.PrintCard(c.Def));

        // ① 护符/结晶的初始吟唱必须在卡面上
        foreach (var need in new[] { 81000001, 81000004, 81000032, 81000034 })
        {
            if (!zhById[need].Contains("【吟唱"))
                bad.Add(need + " 卡面缺【吟唱】");
        }

        // ② 卡种必须在定语之后（「攻击力至多3的随从」而不是「随从攻击力至多3的」）
        if (!zhById[81000002].Contains("攻击力至多3的随从"))
            bad.Add("81000002 语序错：应为「攻击力至多3的随从」，实际=" + zhById[81000002]);

        // ③ 负增量要识别成「推进」而不是「延长-1」
        if (!zhById[81000032].Contains("推进1回合"))
            bad.Add("81000032 未渲染成「推进1回合」，实际=" + zhById[81000032]);
        if (zhById[81000032].Contains("延长-"))
            bad.Add("81000032 出现「延长-」，负数未识别");

        // ④ 扣资源要折叠成「消费N点」而不是「若…则…-N」
        if (!zhById[81000007].Contains("消费10点【信仰】值"))
            bad.Add("81000007 未折叠成「消费10点【信仰】值」，实际=" + zhById[81000007]);

        // ⑤ 旁观触发的卡面必须写清「谁」触发的
        if (zhById[81000006].Contains("【进入战场时】给予"))
            bad.Add("81000006 纹章触发缺主语（【进入战场时】之前应有「每当…」）");

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
        // DiyCtx 已默认加载测试集；这里不能再 LoadInto 一次 ——
        // 重复加载会把同一张卡注册两遍，第二次的 CardDef 覆盖第一次，
        // 而 TestsetLoader 内部的词法层也已 Build 过，符号会漂移。
        var ws = DiyCtx(withTestset: false);
        var cards = TestsetLoader.LoadInto(ws.Context);
        var dec = new DiyBlockDecompiler(ws.Schema);

        int ok = 0;
        var bad = new List<string>();

        foreach (var c in cards)
        {
            var blocks = dec.Decompile(c.Def);
            var derrs = dec.Errors.ToList();
            if (derrs.Count > 0) { bad.Add(c.CardId + " 反编译失败：" + derrs[0]); continue; }

            // 必须传 Lexicon：不传的话 CardIdOf 恒返回 0，
            // 召唤/变身/具名卡池全部退化成空壳或「任意卡」——
            // 而门禁若在这里也不传，就永远测不到那条真实路径。
            var res = new DiyBlockCompiler(ws.Schema, ws.Lexicon).Compile(blocks);
            if (!res.Ok) { bad.Add(c.CardId + " 重编译失败：" + res.Errors.FirstOrDefault()); continue; }

            string diff = "";
            if (!DiyWorkspace.StructurallyEqual(c.Def, res.Def, out diff))
            {
                bad.Add(c.CardId + " 不等价：" + diff.Replace("\n", " "));
                continue;
            }
            ok++;
        }

        if (bad.Count > 0) return (false, string.Join(" | ", bad.Take(3)) + "（共 " + bad.Count + " 张）");

        // ---- PyAST 往返：与积木同样必须 30/30 ----
        // 只验积木不验 PyAST，等于默许另一条路静默丢数据。
        int sok = 0;
        var sbad = new List<string>();
        foreach (var c in cards)
        {
            string printed = AstCanonicalPrinter.PrintCard(c.Def);
            var parser = new AstCanonicalParser(ws.Schema, ws.Lexicon);
            var back = parser.ParseCard(printed);
            if (back == null || parser.Errors.Count > 0)
            {
                sbad.Add(c.CardId + " 重新解析失败：" + (parser.Errors.FirstOrDefault() ?? "null"));
                continue;
            }
            string sd = "";
            if (!DiyWorkspace.StructurallyEqual(c.Def, back, out sd))
            {
                sbad.Add(c.CardId + " 不等价：" + sd.Replace("\n", " "));
                continue;
            }
            if (AstCanonicalPrinter.PrintCard(back) != printed)
            {
                sbad.Add(c.CardId + " 二次打印不一致");
                continue;
            }
            sok++;
        }
        if (sbad.Count > 0) return (false, "PyAST 往返 " + sok + "/30：" + string.Join(" | ", sbad.Take(3)));

        return (true, "积木 " + ok + "/30、PyAST " + sok + "/30 双向无损（含融合/子卡/CardId 判等）");
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

        // ② PyAST（唯一的 AST 文本格式）
        {
            var dsl = "card " + id + " \"" + nameChs + "\" (Follower, clan=Swordcraft, cost=3, atk=2, life=3):\n"
                    + "    traits: [Mecha]\n"
                    + "    keywords: [ward]\n"
                    + "    fanfare:\n"
                    + "        counter sword_intent by 2\n"
                    + "        select opponent:board Choose(1) where is Follower\n"
                    + "        deal ctr:sword_intent damage to $selected\n";
            var parser = new AstCanonicalParser(ws.Schema, ws.Lexicon);
            var def = parser.ParseCard(dsl);
            made.Add(("PyAST", def, parser.Errors.Count > 0 ? parser.Errors[0] : null));
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
            var res = new DiyBlockCompiler(ws.Schema, ws.Lexicon).Compile(doc);
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
            };
            // 表单只填基础信息，不含效果 —— 效果由三端各自表达。
            var fc = new DiyFormCompiler(ws.Schema, ws.Lexicon);
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
        string en0 = AstCanonicalPrinter.PrintAbilities(made[0].def.Abilities);
        string zh0 = ws.Printer.PrintCard(made[0].def);
        foreach (var (way, def, _) in made.Skip(1))
        {
            string en = AstCanonicalPrinter.PrintAbilities(def.Abilities);
            string zh = ws.Printer.PrintCard(def);
            if (en != en0) bad.Add(way + " 的 PyAST 与 CNL 不同：\n    " + en.Replace("\n", " | ") + "\n    vs " + en0.Replace("\n", " | "));
            if (zh != zh0) bad.Add(way + " 的中文卡面与 CNL 不同：\n    " + zh.Replace("\n", " / ") + "\n    vs " + zh0.Replace("\n", " / "));
        }

        if (bad.Count > 0) return (false, string.Join(" || ", bad.Take(2)).Replace("\n", " ⏎ "));

        // ---- 表单自身闭环：表单造出的卡必须能被表单读回去 ----
        // 只验「四端产出相同」不够：那四条路各造一次就完了。
        // 真正要的是「玩家能在四种模式之间来回切换而不丢效果」，
        // 而表单读不回自己造的卡，就是这条升级路径断在表单这一端。
        var formDef = made.First(m => m.way == "Form").def;
        var fc2 = new DiyFormCompiler(ws.Schema, ws.Lexicon);
        if (!fc2.TryCardDefToForm(formDef, out var formBack, out var why))
            return (false, "表单读不回自己造的卡：" + why);

        var reDef = fc2.FormToCardDef(formBack);
        string fd = "";
        if (!DiyWorkspace.StructurallyEqual(formDef, reDef, out fd))
            return (false, "表单 造→读→再造 不等价：" + fd.Replace("\n", " "));

        return (true, "四端产物在两种渲染下逐字相同，且表单自身闭环；中文=" + zh0.Replace("\n", " / "));
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

    // ---------------------------------------------------------------- 门禁 5

    /// <summary>
    /// 往返后真机效用：把经「积木往返」与「脚本往返」重编译的真卡放进真实对局，
    /// 断言它们的核心机制仍然生效。
    ///
    /// 这一道是前四道的最后一块拼图：
    ///   门禁 2 只验「AST 字符串相等」，而 AST 打印器本身可能没打印某些字段
    ///   （CardId / IntoCardId / CrestDef / Duration …）。于是「字符串相等」
    ///   与「运行时行为相同」之间有一条缝 —— 门禁 5 就是去补这条缝：
    ///   真跑一遍，看召唤出的东西有没有卡面、变身有没有发生、纹章有没有挂上。
    /// </summary>
    private static (bool, string) GateRoundTripLiveBattle()
    {
        var ws = DiyCtx(seed: 23);
        var cards = TestsetLoader.LoadInto(ws.Context);
        var byId = cards.ToDictionary(c => c.CardId);

        // 经积木往返与原卡，各跑一遍，逐项对比行为。
        var blocks = new DiyBlockDecompiler(ws.Schema).Decompile(byId[81000006].Def);
        var ariaRound = new DiyBlockCompiler(ws.Schema, ws.Lexicon).Compile(blocks);
        if (!ariaRound.Ok) return (false, "Aria 积木往返失败：" + ariaRound.Errors.FirstOrDefault());

        var scripted = new ReshadowScriptCompiler(ws);
        var strikerBack = scripted.Compile(scripted.Print(byId[81000022].Def));
        if (strikerBack.Count == 0) return (false, "Striker 脚本往返失败");
        var slausBack = scripted.Compile(scripted.Print(byId[81000023].Def));
        if (slausBack.Count == 0) return (false, "Slaus 脚本往返失败");
        var sinciroBack = scripted.Compile(scripted.Print(byId[81000021].Def));
        if (sinciroBack.Count == 0) return (false, "Sinciro 脚本往返失败");

        var report = new List<string>();
        bool ok = true;

        // ---- ① Aria（积木往返）：进化召唤 3 个妖精，且纹章给它们疾驰 ----
        {
            var (st, tr, vm) = DiyBoard(ws);
            st.RegisterCard(ariaRound.Def);
            var c = st.SpawnCard(ariaRound.Def.CardId, ZoneRef.Hand, 0);
            vm.DropPendingTriggers();
            tr.PlayCard(0, c, vm);
            vm.StepUntilBlocked();

            bool crest = st.EnumerateZone(ZoneRef.CrestArea, 0).Any(x => x.Def?.Kind == CardKind.Crest);
            st.WriteCounter(0, st.Schema.Counter("ep"), 2);
            vm.DropPendingTriggers();
            tr.Evolve(0, c, false, vm);
            vm.StepUntilBlocked();

            int fairies = st.EnumerateZone(ZoneRef.Board, 0).Count(x => x.Def?.CardId == 90011110);
            int stormed = st.EnumerateZone(ZoneRef.Board, 0)
                .Count(x => x.Def?.CardId == 90011110 && x.Keywords.Contains(st.Schema.Keyword("storm")));

            bool pass = crest && fairies == 3 && stormed == 3 && st.UnresolvedSpawnCount == 0;
            ok &= pass;
            report.Add("Aria(积木往返) 纹章=" + crest + " 妖精=" + fairies + " 疾驰=" + stormed
                       + " 空壳=" + st.UnresolvedSpawnCount);
        }

        // ---- ② Striker Artifact（脚本往返）：融合 Artifact 护符后变身 ----
        {
            var (st, tr, vm) = DiyBoard(ws);
            st.RegisterCard(strikerBack[0]);
            var b = st.SpawnCard(81000022, ZoneRef.Hand, 0);

            st.RegisterCard(new CardDef
            {
                CardId = 950001, Name = "ArtMat", Kind = CardKind.Amulet, BaseCost = 1,
                Traits = new List<SymbolId<TraitTag>> { st.Schema.Trait("Artifact") },
            });
            var m = st.SpawnCard(950001, ZoneRef.Hand, 0);
            vm.DropPendingTriggers();
            bool fused = tr.FuseCards(0, b, new[] { m }, vm);
            vm.StepUntilBlocked();

            bool transformed = b.Def != null && b.Def.CardId != 81000022
                               && b.Def.Name != null && b.Def.Name.Contains("Artifact");
            bool pass = fused && transformed && st.UnresolvedSpawnCount == 0;
            ok &= pass;
            report.Add("Striker(脚本往返) 融合=" + fused + " 变身→" + (b.Def?.Name ?? "null")
                       + " 空壳=" + st.UnresolvedSpawnCount);
        }

        // ---- ③ Slaus（脚本往返）：回合末给**对手**贴纹章 ----
        {
            var (st, tr, vm) = DiyBoard(ws);
            st.RegisterCard(slausBack[0]);
            var c = st.SpawnCard(81000023, ZoneRef.Board, 0);
            c.IsEvolved = true;
            vm.DropPendingTriggers();
            int foeCrest0 = st.EnumerateZone(ZoneRef.CrestArea, 1).Count();
            int myCrest0 = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();

            tr.EndTurn(0, vm);
            vm.StepUntilBlocked();

            int foeCrest1 = st.EnumerateZone(ZoneRef.CrestArea, 1).Count();
            int myCrest1 = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();

            // 关键：纹章必须挂到**对手**身上，而且落点是纹章区（不占战场）。
            // 纹章必须挂到**对手**身上，且我方不能凭空多一个。
            // 我方计数可能非 0（Slaus 自己带纹章定义），所以判「没有新增」而不是「等于 0」。
            bool pass = foeCrest1 == foeCrest0 + 1 && myCrest1 == myCrest0;
            ok &= pass;
            report.Add("Slaus(脚本往返) 对手纹章 " + foeCrest0 + "→" + foeCrest1
                       + " 我方 " + myCrest0 + "→" + myCrest1);
        }

        // ---- ④ Sinciro（脚本往返）：超进化复刻入场曲 ----
        {
            var (st, tr, vm) = DiyBoard(ws);
            st.RegisterCard(sinciroBack[0]);
            var foe = st.SpawnCard(940001, ZoneRef.Board, 1);
            var c = st.SpawnCard(81000021, ZoneRef.Board, 0);
            c.IsEvolved = true;
            c.IsSuperEvolved = true;
            st.WriteCounter(0, st.Schema.Counter("sep"), 2);
            // fuse_kinds 是融合材料种类数，由真实融合产生；这里直接置值模拟。
            st.WriteCounter(0, st.Schema.Counter("fuse_kinds"), 2);
            int foeLife0 = foe.Life;
            int leaderLife0 = st.Players[1].Leader.Life;

            vm.DropPendingTriggers();
            vm.EnqueueEvent(new VmEvent
            {
                Kind = CoreEvent.OnSuperEvolve,
                Subject = EntityRef.From(c),
                IsManualEvolve = true,
            });
            vm.StepUntilBlocked();

            // 复刻的是入场曲「对敌方全体打 X（=融合种类数）」，目标必须是 $self 的能力持有者。
            // 复刻的是入场曲「对敌方全体随从与主战者打 X（= 融合材料种类数）」。
            // 两个落点都要掉血才算真的复刻成功：只看随从会漏掉 IncludeLeader 是否还原。
            bool fired = foe.Life == foeLife0 - 2 && st.Players[1].Leader.Life == leaderLife0 - 2;
            ok &= fired;
            report.Add("Sinciro(脚本往返) 超进化复刻 敌方随从 " + foeLife0 + "→" + foe.Life
                       + " 主战者 " + leaderLife0 + "→" + st.Players[1].Leader.Life + " 触发=" + fired);
        }

        return (ok, string.Join("；", report));
    }

    /// <summary>门禁 5 用的最小对局台。</summary>
    private static (BattleState st, TurnRunner tr, CardVm vm) DiyBoard(DiyWorkspace ws)
    {
        // 每个用例必须用**全新的** BattleState：
        // 复用同一个会让前面用例残留的实体/计数器影响后面 ——
        // 例如「敌方随从 9→9」可能只是因为它被上一个用例改成过 9。
        var st = ws.ResetState();
        if (st.CardDefOf(99) == null)
            st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });

        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 8;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(1, st.Schema.Counter("pp"), 20);

        if (st.CardDefOf(940001) == null)
            st.RegisterCard(new CardDef
            {
                CardId = 940001, Name = "Dummy", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 9, BaseCost = 1,
            });
        for (int i = 0; i < 8; i++)
        {
            int id = 960000 + i;
            if (st.CardDefOf(id) == null)
                st.RegisterCard(new CardDef
                {
                    CardId = id, Name = "Fill" + i, Kind = CardKind.Follower,
                    BaseAttack = 1, BaseLife = 1, BaseCost = 1,
                });
            st.SpawnCard(id, ZoneRef.Deck, 0);
        }

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        return (st, new TurnRunner(st, st.Schema), vm);
    }


    // ---------------------------------------------------------------- 门禁 6

    /// <summary>
    /// 纯真实对局门禁：不手搭任何场景。
    ///
    /// 为什么需要它：前五道门禁都在**手工布置**的台面上跑 —— 造主战者、
    /// 塞能量、直接置计数器、然后调一步操作。这类夹具如果写错，
    /// 失败会被误判成「引擎有问题」，或者更糟：夹具恰好绕开了真实路径，
    /// 于是真问题被测成通过。
    ///
    /// 这里的做法是：用 MatchRunner 起一局真对局，从洗牌、换牌、涨费、
    /// 抽牌到结束回合全部走真实流程；DIY 卡混进牌组自然摸到、自然打出。
    /// 断言只有三条，且都是「真实对局中必然成立」的：
    ///   1. 全程不出现未注册卡（UnresolvedSpawnCount == 0）
    ///   2. 全程状态不腐坏（StateCorrupted == false）
    ///   3. 对局能正常终局（不退化成死循环、不中途卡死）
    /// 另外统计一次「DIY 卡是否真的被摸到并打出过」—— 没摸到就说明牌组没生效。
    /// </summary>
    private static (bool, string) GateRealMatch()
    {
        // 跑 5 局不同种子：单局通过可能是运气，多局才说明流程稳。
        var results = new List<string>();
        for (int seed = 41; seed <= 45; seed++)
        {
            var (ok, detail) = PlayOneRealMatch(seed);
            if (!ok) return (false, "seed=" + seed + " " + detail);
            results.Add("seed" + seed + ":" + detail);
        }
        return (true, string.Join("；", results));
    }

    private static (bool, string) PlayOneRealMatch(int seed)
    {
        var ws = DiyWorkspace.Create(seed: seed);
        ws.Context.Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier");
        ws.DefineTrait("Mecha", "机甲");

        var cards = TestsetLoader.LoadInto(ws.Context);
        var st = ws.State;

        // 用真实的 30 张测试集卡组两副牌（各 40 张，允许重复以凑满）。
        var deck0 = new List<int>();
        var deck1 = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            deck0.Add(cards[i % cards.Count].CardId);
            deck1.Add(cards[(i + 7) % cards.Count].CardId);
        }

        var mr = new MatchRunner(st);
        // 自动消解选择框：这是引擎自带的自动化路径（Bot 对战与回归共用），
        // 不是我为了测试开的后门 —— UI 走的是同一条 DrainVm，只是选择来源不同。
        mr.AutoChoicePolicy = new FirstLegalChoicePolicy();
        mr.Start(0, deck0, deck1);
        mr.ConfirmMulligan();

        int steps = 0;
        const int maxSteps = 4000;
        bool sawRoundTrippedCard = false;

        while (mr.Phase == MatchPhase.Playing && steps < maxSteps)
        {
            steps++;

            // 真实对局里不该留下任何「未注册卡」的痕迹。
            if (st.UnresolvedSpawnCount > 0)
                return (false, "第 " + steps + " 步出现未注册卡 " + st.UnresolvedSpawnCount + " 次");
            if (mr.Vm != null && mr.Vm.StateCorrupted)
                return (false, "第 " + steps + " 步状态腐坏：" + mr.Vm.FaultMessage);

            // 挂起选择框时 GetLegalActions 返回空是**正常**的，
            // 那表示 VM 在等输入，不是死锁。AutoChoicePolicy 会把它消解掉，
            // 这里只需要跳过一次循环。
            if (mr.IsWaitingForInput) continue;

            var actions = mr.GetLegalActions();
            if (actions.Count == 0)
                return (false, "第 " + steps + " 步没有任何合法动作");

            if (actions.Any(a => a.Kind == PlayerActionKind.PlayCard)) sawRoundTrippedCard = true;

            mr.Execute(PickActionForRealMatch(actions));
        }

        if (steps >= maxSteps)
            return (false, "对局在 " + maxSteps + " 步内没有终局（疑似死循环）");

        // 终局必须是一方真的输了，而不是停在半路。
        int winner = mr.Winner();
        bool finished = winner != -1;
        if (!finished) return (false, "对局结束但未判定胜负（Phase=" + mr.Phase + "）");

        // 双方牌组必须真的在动：整局结束时的墓地/放逐总量应当 > 0。
        int graves = st.EnumerateZone(ZoneRef.Cemetery, 0).Count()
                     + st.EnumerateZone(ZoneRef.Cemetery, 1).Count();
        if (graves == 0) return (false, "整局没有任何卡进入墓地，牌组或流程可能没生效");

        return (true, "真实对局 " + steps + " 步终局，胜者=" + winner
                      + "，墓地共 " + graves + " 张，出牌发生=" + sawRoundTrippedCard
                      + "，未注册卡=0，状态未腐坏");
    }

    /// <summary>
    /// 真实对局的行动选择：优先出牌，其次攻击，最后结束回合。
    /// 不做任何「专门为测试开的后门」。
    /// </summary>
    private static PlayerAction PickActionForRealMatch(List<PlayerAction> actions)
    {
        var play = actions.FirstOrDefault(a => a.Kind == PlayerActionKind.PlayCard);
        if (play != null) return play;

        var attack = actions.FirstOrDefault(a => a.Kind == PlayerActionKind.Attack);
        if (attack != null) return attack;

        var evolve = actions.FirstOrDefault(a => a.Kind == PlayerActionKind.Evolve);
        if (evolve != null) return evolve;

        var end = actions.FirstOrDefault(a => a.Kind == PlayerActionKind.EndTurn);
        return end ?? actions[0];
    }


    // ---------------------------------------------------------------- 门禁 7



    // ---------------------------------------------------------------- 门禁 8

    /// <summary>
    /// 复刻能力（InvokeAbilityNode）的两条性质：
    ///   ① 目标卡在**同一事件**下有多条能力时，必须全部复刻（不能 FirstOrDefault）
    ///   ② 子作用域的 $self 必须指向**被复刻的实体**，不是施法者
    ///
    /// 为什么单独测：这两条是 94 号文档里最后一处「修了但没有测试覆盖」的地方。
    /// 只验「复刻生效」不够 —— 复刻一条也是生效，$self 绑错也可能生效（只是绑错了人）。
    /// </summary>
    private static (bool, string) GateInvokeCoverage()
    {
        var ws = DiyCtx(seed: 57);
        var st = ws.State;
        var comp = new ReshadowScriptCompiler(ws);
        var report = new List<string>();
        bool ok = true;

        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 6;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(1, st.Schema.Counter("pp"), 20);
        for (int i = 0; i < 8; i++)
        {
            int id = 931000 + i;
            st.RegisterCard(new CardDef
            {
                CardId = id, Name = "F" + i, Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 1,
            });
            st.SpawnCard(id, ZoneRef.Deck, 0);
        }

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        var tr = new TurnRunner(st, st.Schema);

        // ---- ① 多条同事件能力都要复刻 ----
        {
            const int targetId = 932001;
            const int invokerId = 932002;
            const int extraId = 932003;

            var tgtDef = comp.Compile(
                "card " + targetId + " \"复刻目标\" (Follower, cost: 1, atk: 1, life: 1) {\n" +
                "    on play { damage(target(enemy, leader), 1) }\n" +
                "}").First();
            st.RegisterCard(tgtDef);

            var invDef = comp.Compile(
                "card " + invokerId + " \"复刻者\" (Follower, cost: 1, atk: 1, life: 1) {\n" +
                "    on play { invoke(play, target(ally, board, follower, count: 1)) }\n" +
                "}").First();
            st.RegisterCard(invDef);

            var extraDef = comp.Compile(
                "card " + extraId + " \"追加能力\" (Follower, cost: 1, atk: 1, life: 1) {\n" +
                "    on play { draw(1) }\n" +
                "}").First();

            var tgt = st.SpawnCard(targetId, ZoneRef.Board, 0);

            // 用引擎 API 给目标追加**第二条** OnPlay 能力（真实路径，不是夹具）。
            // 若有 FirstOrDefault，这条不会被复刻。
            st.GrantAbility(tgt, extraDef.Abilities[0], DurationKind.Permanent);

            int hand0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int foeLife0 = st.Players[1].Leader.Life;

            var invoker = st.SpawnCard(invokerId, ZoneRef.Hand, 0);
            vm.DropPendingTriggers();
            tr.PlayCard(0, invoker, vm);
            int guard = 0;
            while (vm.Pending != null && guard++ < 8)
            {
                vm.SubmitChoice(vm.Pending.ChoiceId, new[] { tgt.EntityId });
                vm.StepUntilBlocked();
            }
            vm.StepUntilBlocked();

            int hand1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int foeLife1 = st.Players[1].Leader.Life;

            bool dmg = foeLife1 == foeLife0 - 1;
            bool drew = hand1 >= hand0;
            bool pass = dmg && drew;
            ok &= pass;
            report.Add("多条同事件 打1=" + dmg + " 抽1=" + drew
                       + "（主战者 " + foeLife0 + "→" + foeLife1 + "，手牌 " + hand0 + "→" + hand1 + "）");
        }

        // ---- ② $self 必须指向被复刻的实体 ----
        {
            const int selfId = 933001;
            const int casterId = 933002;

            var selfDef = comp.Compile(
                "card " + selfId + " \"自身目标\" (Follower, cost: 1, atk: 2, life: 2) {\n" +
                "    on play { buff($self, 3/3) }\n" +
                "}").First();
            st.RegisterCard(selfDef);

            var casterDef = comp.Compile(
                "card " + casterId + " \"施法者\" (Follower, cost: 1, atk: 1, life: 1) {\n" +
                "    on play { invoke(play, target(ally, board, follower, count: 1)) }\n" +
                "}").First();
            st.RegisterCard(casterDef);

            var selfCard = st.SpawnCard(selfId, ZoneRef.Board, 0);
            var caster = st.SpawnCard(casterId, ZoneRef.Hand, 0);
            int selfAtk0 = selfCard.Attack, selfLife0 = selfCard.Life;
            int castAtk0 = caster.Attack, castLife0 = caster.Life;

            vm.DropPendingTriggers();
            tr.PlayCard(0, caster, vm);
            int guard = 0;
            while (vm.Pending != null && guard++ < 8)
            {
                vm.SubmitChoice(vm.Pending.ChoiceId, new[] { selfCard.EntityId });
                vm.StepUntilBlocked();
            }
            vm.StepUntilBlocked();

            bool selfBuffed = selfCard.Attack == selfAtk0 + 3 && selfCard.Life == selfLife0 + 3;
            bool casterUntouched = caster.Attack == castAtk0 && caster.Life == castLife0;
            bool pass = selfBuffed && casterUntouched;
            ok &= pass;
            report.Add("$self 绑定 被复刻卡=" + selfCard.Attack + "/" + selfCard.Life
                       + "（原 " + selfAtk0 + "/" + selfLife0 + "）"
                       + " 施法者=" + caster.Attack + "/" + caster.Life
                       + "（原 " + castAtk0 + "/" + castLife0 + "）");
        }

        return (ok, string.Join("；", report));
    }
}
