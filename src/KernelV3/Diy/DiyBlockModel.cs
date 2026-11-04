using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Diy;

// ============================================================================
// Scratch 积木式 DIY —— 积木树 <-> CardDef 双向无损
//
// 为什么积木需要「形状」这个强类型：
//   拖拽界面最常见的错误不是「拼错了」，而是「把数值塞进了条件槽」。
//   如果积木不区分形状，这种错误要到编译期才暴露，反馈太晚；
//   而形状不匹配在拖拽瞬间就能高亮 —— 这是积木式交互的全部价值。
//
// 五种形状（与 UI 视觉一一对应）：
//   Hat    事件帽：能力顶端，下面挂一串动作
//   C      控制流：中间包一段子序列（if / loop / mode）
//   Stack  动作：可上下拼接
//   Pill   目标/数值胶囊：只能嵌进孔位
//   Hex    布尔与数值表达式
//
// 双向无损同样是硬要求：30 张真卡必须能 Decompile → Compile 回来，
// 否则「积木能造卡」只是能造**简单**卡，复杂卡一到就散架。
// ============================================================================

/// <summary>积木形状。槽位只接受对应形状，这是拖拽期校验的依据。</summary>
public enum BlockShape
{
    Hat,
    C,
    Stack,
    Pill,
    Hex,
}

/// <summary>槽位类型：决定这个孔能插什么。</summary>
public enum SlotKind
{
    None,
    /// <summary>一串动作积木（可空）。</summary>
    StackSeq,
    /// <summary>一个目标胶囊。</summary>
    Target,
    /// <summary>一个数值表达式积木。</summary>
    Number,
    /// <summary>一个布尔条件积木。</summary>
    Condition,
    /// <summary>若干并列分支，每个分支是一串动作。</summary>
    StackSeqList,
}

/// <summary>一颗积木。字段按 opcode 复用（积木语言天然如此）。</summary>
public sealed class Block
{
    /// <summary>稳定唯一 id，用于错误提示与拖拽定位。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    public string Opcode { get; init; } = "";

    public BlockShape Shape => DiyBlockModel.ShapeOf(Opcode);

    // ---- 通用字段 ----
    public string Text { get; set; }
    /// <summary>次要文本（构筑期修正的互斥组名等）。</summary>
    public string Text2 { get; set; }          // 卡名 / 关键词名 / 兵种名 / 事件名
    public int IntA { get; set; }             // 常量 / 计数器初值 / 序号
    public int IntB { get; set; }             // 副常量
    public bool FlagA { get; set; }           // 各种开关（IsCrest / IsExact / manualOnly …）
    public bool FlagB { get; set; }
    public bool FlagC { get; set; }
    public bool FlagD { get; set; }
    public bool FlagE { get; set; }
    public int IntC { get; set; }
    public string EnumC { get; set; }
    public string EnumA { get; set; }         // 枚举字面量（Zone / Owner / Mode / Op …）
    public string EnumB { get; set; }
    public string CardNameA { get; set; }     // 具名卡池（card("X", …)）

    // ---- 槽位 ----
    public List<Block> Stacks { get; set; } = new();          // StackSeq
    public List<Block> ElseStacks { get; set; } = new();      // if-else 的 else 支
    public List<List<Block>> Branches { get; set; } = new();  // mode 的多分支
    public Block Target { get; set; }                          // Target
    public Block Target2 { get; set; }                         // 第二个目标（变身/赋予等）
    public Block Number { get; set; }                          // Number
    public Block Number2 { get; set; }                         // 第二个数值
    public Block Condition { get; set; }                       // Condition

    // ---- 代价 / 触发附加 ----
    public List<(string kind, int amount)> Costs { get; set; } = new();
    public string ActiveZone { get; set; }
    public string DuringTurn { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Opcode);
}

/// <summary>一份积木文档 = 一张卡。</summary>
public sealed class CardBlockWorkspace
{
    /// <summary>构筑期修正积木（不是能力，不挂在任何事件帽下）。</summary>
    public List<Block> DeckModBlocks { get; set; } = new();
    public int CardId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Follower";
    public int Cost { get; set; }
    public int Attack { get; set; }
    public int Life { get; set; }
    public string Clan { get; set; }
    public List<string> Traits { get; set; } = new();

    /// <summary>裸关键词（Ward / Rush …），编译成 PassiveAura 上的 Grant。</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>是否声明了融合（Fuse: …）。丢了它 Sinciro / Striker / Gear 全都不能融合。</summary>
    public bool HasFuse { get; set; }

    /// <summary>融合材料筛选（可空 = 任意卡）。</summary>
    public Block FuseFilter { get; set; }

    public List<Block> Hats { get; set; } = new();

    public int AccelerateCost { get; set; } = -1;
    public List<Block> AccelerateHats { get; set; } = new();
    public int CrystallizeCost { get; set; } = -1;
    public List<Block> CrystallizeHats { get; set; } = new();
    public List<Block> CrestHats { get; set; } = new();
    public List<Block> FaithHats { get; set; } = new();
}
/// <summary>积木形状表 + 面板目录 + 槽位校验。</summary>
public static class DiyBlockModel
{
    /// <summary>opcode → 形状。新增积木必须在这里登记，否则拖拽校验会静默放行。</summary>
    private static readonly Dictionary<string, BlockShape> Shapes = new(StringComparer.Ordinal)
    {
        // ---- 事件帽 ----
        ["hat_self_event"] = BlockShape.Hat,
        ["hat_watch_event"] = BlockShape.Hat,
        ["hat_passive"] = BlockShape.Hat,

        // ---- 控制流 ----
        ["c_if_else"] = BlockShape.C,
        ["c_repeat"] = BlockShape.C,
        ["c_mode"] = BlockShape.C,
        ["c_for_each"] = BlockShape.C,

        // ---- 动作 ----
        ["act_select"] = BlockShape.Stack,
        ["act_damage"] = BlockShape.Stack,
        ["act_split_damage"] = BlockShape.Stack,
        ["act_restore"] = BlockShape.Stack,
        ["act_full_restore"] = BlockShape.Stack,
        ["act_draw"] = BlockShape.Stack,
        ["act_summon"] = BlockShape.Stack,
        ["act_add_hand"] = BlockShape.Stack,
        ["act_copy"] = BlockShape.Stack,
        ["act_destroy"] = BlockShape.Stack,
        ["act_banish"] = BlockShape.Stack,
        ["act_discard"] = BlockShape.Stack,
        ["act_bounce"] = BlockShape.Stack,
        ["act_return_deck"] = BlockShape.Stack,
        ["act_move_to_board"] = BlockShape.Stack,
        ["act_buff"] = BlockShape.Stack,
        ["act_set_stats"] = BlockShape.Stack,
        ["act_swap_stats"] = BlockShape.Stack,
        ["act_mod_cost"] = BlockShape.Stack,
        ["act_grant_kw"] = BlockShape.Stack,
        // 118 轮新增的两个动作块与随机数胶囊
        ["act_reveal"] = BlockShape.Stack,
        ["act_damage_aura"] = BlockShape.Stack,
        ["num_rand"] = BlockShape.Pill,
        ["act_remove_kw"] = BlockShape.Stack,
        ["act_silence"] = BlockShape.Stack,
        ["act_evolve"] = BlockShape.Stack,
        ["act_super_evolve"] = BlockShape.Stack,
        ["act_mod_counter"] = BlockShape.Stack,
        ["act_transform"] = BlockShape.Stack,
        ["act_reanimate"] = BlockShape.Stack,
        ["act_gain_crest"] = BlockShape.Stack,
        ["act_replace_deck"] = BlockShape.Stack,
        ["act_invoke"] = BlockShape.Stack,
        ["act_grant_ability"] = BlockShape.Stack,
        ["act_grant_entity_ability"] = BlockShape.Stack,
        // 构筑期修正与卡池检索：需要参数但没有子动作，用胶囊形。
        ["act_deck_mod"] = BlockShape.Pill,
        ["act_discover"] = BlockShape.Stack,

        // ---- 胶囊 ----
        ["tgt_slot"] = BlockShape.Pill,
        ["tgt_leader"] = BlockShape.Pill,
        ["tgt_query"] = BlockShape.Pill,

        // ---- 表达式 ----
        ["bool_compare"] = BlockShape.Hex,
        ["bool_and"] = BlockShape.Hex,
        ["bool_or"] = BlockShape.Hex,
        ["bool_not"] = BlockShape.Hex,
        ["bool_has_kw"] = BlockShape.Hex,
        ["bool_has_trait"] = BlockShape.Hex,
        // 不登记的话 IsKnown 返回 false，形状退化成 Stack、UI 认不出，
        // 而编译/反编译逻辑其实都在 —— 表现为「积木面板里放不出来」。
        ["bool_has_pack"] = BlockShape.Hex,
        ["bool_has_clan"] = BlockShape.Hex,
        ["bool_has_state"] = BlockShape.Hex,
        ["bool_has_card_name"] = BlockShape.Hex,
        ["bool_exists"] = BlockShape.Hex,
        ["bool_true"] = BlockShape.Hex,

        ["num_const"] = BlockShape.Pill,
        ["num_counter"] = BlockShape.Pill,
        ["num_prop"] = BlockShape.Pill,
        ["num_count"] = BlockShape.Pill,
        ["num_max_group"] = BlockShape.Pill,
        ["num_arith"] = BlockShape.Hex,
    };

    public static BlockShape ShapeOf(string opcode)
        => Shapes.TryGetValue(opcode ?? "", out var s) ? s : BlockShape.Stack;

    public static bool IsKnown(string opcode) => Shapes.ContainsKey(opcode ?? "");

    /// <summary>
    /// 积木面板目录。
    ///
    /// 兵种 / 计数器 / 关键词 / 职业的候选项**实时从 schema 读**，
    /// 这样玩家用 DefineTrait 注册的新兵种会立刻出现在下拉里；
    /// 硬编码一份名单的话，DIY 出来的词永远选不到。
    /// </summary>
    public static BlockPalette GetAvailableBlocks(GameSchema schema)
    {
        return new BlockPalette
        {
            Traits = Names(schema?.Traits),
            Counters = Names(schema?.Counters),
            Keywords = Names(schema?.Keywords),
            Clans = Names(schema?.Clans),
            Opcodes = Shapes.Keys.OrderBy(x => x).ToList(),
        };
    }

    private static List<string> Names<T>(Core.SymbolRegistry<T> reg)
        => reg == null
           ? new List<string>()
           : reg.All.Select(x => x.Name)
                  .Where(x => !string.IsNullOrEmpty(x))
                  .Distinct()
                  .OrderBy(x => x, StringComparer.Ordinal)
                  .ToList();
}

/// <summary>面板目录（UI 直接绑定它生成下拉项）。</summary>
public sealed class BlockPalette
{
    public List<string> Traits { get; set; } = new();
    public List<string> Counters { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public List<string> Clans { get; set; } = new();
    public List<string> Opcodes { get; set; } = new();
}

/// <summary>编译结果（错误信息精确到积木 id，便于 UI 高亮那一块）。</summary>
public sealed class BlockCompileResult
{
    public CardDef Def { get; set; }
    public List<string> Errors { get; } = new();

    /// <summary>
    /// 构筑期修正积木收集到的词条。
    ///
    /// 它们不对应任何 EffectNode（对局中不生效），
    /// 所以不能混进能力体，必须由这里单独回传给 CardDef.DeckModifiers。
    /// </summary>
    public List<DeckBuildModifier> DeckModsOut { get; } = new();

    public bool Ok => Errors.Count == 0 && Def != null;

    public void Fail(Block b, string msg)
        => Errors.Add("积木[" + (b?.Id ?? "?") + " " + (b?.Opcode ?? "?") + "]：" + msg);
}
/// <summary>积木树 → CardDef。</summary>
public sealed class DiyBlockCompiler
{
    private readonly GameSchema _schema;
    private readonly Lang.Lexicon _lex;
    private readonly BlockCompileResult _res = new();

    /// <summary>本次编译出的子卡，供「获得纹章」节点在构造时内嵌。</summary>
    private CardDef _pendingCrest;
    private CardDef _pendingFaith;

    public DiyBlockCompiler(GameSchema schema, Lang.Lexicon lexicon = null)
    {
        _schema = schema;
        _lex = lexicon;
    }

    /// <summary>
    /// 卡名 → 卡号。
    ///
    /// 这一条是**运行期能否生效**的关键：DoSpawn 只认 n.CardId，
    /// DoTransform 只认 n.IntoCardId。只写 CardName 时卡面中文完全正确、
    /// 编译零错误，但打出去会召唤一个 CardId=0 的白板空壳。
    /// </summary>
    private int CardIdOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _lex == null) return 0;
        return _lex.TryGetCardId(name.Trim(), out var id) ? id : 0;
    }

    public BlockCompileResult Compile(CardBlockWorkspace doc)
    {
        if (doc == null) { _res.Errors.Add("文档为空"); return _res; }

        // 先编纹章/信仰子卡：本体的「获得纹章」节点需要在构造时就把 CrestDef 填进去。
        // SpawnCardNode.CrestDef 是 init-only，构造后补不了 —— 而缺了它，
        // 运行期 GainCrest 建出的是一个没有任何能力的同名声望壳。
        _pendingCrest = BuildSub(0, doc.CrestHats, CardKind.Crest, doc, force: true);
        _pendingFaith = BuildSub(0, doc.FaithHats, CardKind.Faith, doc, force: true);

        // 构筑期修正块先处理：它们不是能力，只往 DeckModsOut 收集。
        foreach (var blk in doc.DeckModBlocks ?? new List<Block>())
        {
            if (blk?.Opcode != "act_deck_mod") continue;
            CompileDeckMod(blk);
        }

        var abilities = new List<AbilityDef>();

        // 裸关键词：编译成 PassiveAura 上的 Grant（与官方卡同一套编码）
        foreach (var kw in (doc.Keywords ?? new List<string>())
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .Select(k => k.Contains('(') ? k.Substring(0, k.IndexOf('(')) : k)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            abilities.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Body = new List<EffectNode>
                {
                    new ModifyKeywordNode
                    {
                        Target = TargetSpec.Self, Op = KeywordOp.Grant, Keyword = _schema.Keyword(kw),
                    },
                },
            });
        }

        foreach (var hat in doc.Hats ?? new List<Block>())
        {
            var ab = CompileHat(hat);
            if (ab != null) abilities.Add(ab);
        }

        var def = new CardDef
        {
            CardId = doc.CardId,
            Name = doc.Name,
            Kind = ParseKind(doc.Kind),
            BaseCost = doc.Cost,
            BaseAttack = doc.Attack,
            BaseLife = doc.Life,
            Abilities = abilities,
            Traits = (doc.Traits ?? new List<string>()).Select(t => _schema.Trait(t)).ToList(),
            Clan = string.IsNullOrEmpty(doc.Clan) ? SymbolId<ClanTag>.Invalid : _schema.Clan(doc.Clan),
            HasFuse = doc.HasFuse,
            FuseMaterialFilter = doc.HasFuse ? CompileTarget(doc.FuseFilter) : null,
            AccelerateDef = BuildSub(doc.AccelerateCost, doc.AccelerateHats, CardKind.Spell, doc),
            CrystallizeDef = BuildSub(doc.CrystallizeCost, doc.CrystallizeHats, CardKind.Amulet, doc),
            CrestDef = _pendingCrest,
            FaithDef = _pendingFaith,
            // 构筑期修正词条：编译期由 act_deck_mod 收集，不在能力体里。
            DeckModifiers = new List<DeckBuildModifier>(_res.DeckModsOut),
        };

        _res.Def = _res.Errors.Count == 0 ? def : null;
        return _res;
    }

    private CardDef BuildSub(int cost, List<Block> hats, CardKind kind, CardBlockWorkspace doc, bool force = false)
    {
        if (!force && (hats == null || hats.Count == 0)) return null;
        if (force && (hats == null || hats.Count == 0)) return null;

        var abil = new List<AbilityDef>();
        foreach (var h in hats)
        {
            var ab = CompileHat(h);
            if (ab != null) abil.Add(ab);
        }
        return new CardDef
        {
            CardId = 0, Name = doc.Name, Kind = kind, BaseCost = Math.Max(0, cost),
            Abilities = abil,
            Traits = (doc.Traits ?? new List<string>()).Select(t => _schema.Trait(t)).ToList(),
        };
    }

    /// <summary>把一块 act_deck_mod 积木转成 DeckBuildModifier 并收集。</summary>
    private void CompileDeckMod(Block b)
    {
        var kind = b.Text switch
        {
            "size" => DeckModKind.ModifyDeckSize,
            "leader_life" => DeckModKind.ModifyLeaderStartLife,
            "hand" => DeckModKind.ModifyStartingHandSize,
            "ep" => DeckModKind.ModifyStartEvolvePoints,
            "sep" => DeckModKind.ModifyStartEvolvePoints,
            "self_copy" => DeckModKind.OverrideSelfCopyLimit,
            "allow_clan_fixed" => DeckModKind.AllowSpecificClan,
            "allow_clan_choose" => DeckModKind.AllowChosenClans,
            _ => DeckModKind.ModifyDeckSize,
        };

        bool isSep = b.Text == "sep";
        _res.DeckModsOut.Add(new DeckBuildModifier
        {
            Kind = kind,
            Value = isSep ? 0 : b.IntA,
            Value2 = isSep ? b.IntA : 0,
            TargetClan = b.CardNameA ?? "",
            StackPerCopy = b.FlagA,
            ExclusiveGroup = b.Text2 ?? "",
        });
    }

    private static CardKind ParseKind(string s) => s switch
    {
        "Spell" => CardKind.Spell,
        "Amulet" => CardKind.Amulet,
        "Crest" => CardKind.Crest,
        "Faith" => CardKind.Faith,
        _ => CardKind.Follower,
    };

    // ---------------------------------------------------------------- 帽子

    private AbilityDef CompileHat(Block hat)
    {
        if (hat == null || hat.IsEmpty) return null;

        CoreEvent ev;
        TargetSpec subject = null;

        switch (hat.Opcode)
        {
            case "hat_self_event":
                ev = EventOf(hat.Text);
                break;
            case "hat_watch_event":
                ev = EventOf(hat.Text);
                subject = CompileTarget(hat.Target);
                break;
            case "hat_passive":
                ev = CoreEvent.PassiveAura;
                break;
            default:
                _res.Fail(hat, "不是事件帽积木");
                return null;
        }

        var costs = new List<CostSpec>();
        foreach (var (kind, amount) in hat.Costs ?? new List<(string, int)>())
        {
            costs.Add(new CostSpec
            {
                Kind = kind switch
                {
                    "necromancy" => CostKind.Necromance,
                    "earth_rite" => CostKind.DestroyAlly,
                    "combo" => CostKind.TurnPlayCount,
                    "rally" => CostKind.MatchPlayCount,
                    "countdown" => CostKind.Countdown,
                    _ => CostKind.UsePp,
                },
                Amount = NumExpr.Const(amount),
                // Label 决定「爆能强化」与「启动」的区别：
                // TurnRunner.EnhanceTiers 只认 Label=="enhance"，
                // 丢了它，Enhance(4) 会被当成普通启动费，爆能强化整条机制失效。
                Label = kind == "enhance" ? "enhance"
                      : kind == "engage" ? "engage"
                      : kind == "necromancy" ? "necromancy"
                      : kind == "earth_rite" ? "earth_rite"
                      : kind == "combo" ? "combo"
                      : kind == "rally" ? "rally"
                      : kind == "countdown" ? "countdown"
                      : "",
            });
        }

        var trig = new TriggerSpec
        {
            Event = ev,
            SubjectFilter = subject,
            ManualEvolveOnly = hat.FlagA,
            // 并列事件（打出**或融合**）与每回合次数上限都是运行期判定依据：
            // 丢了 SecondaryEvent，Octrice 在融合时不再推进吟唱；
            // 丢了 MaxTriggersPerTurn，「每回合一次」会变成每次触发。
            SecondaryEvent = string.IsNullOrEmpty(hat.EnumB) ? null : EventOf(hat.EnumB),
            // 缺省是 0（= 不限制）。写成 1 会把所有能力都限制成「每回合一次」，
            // 而卡面文字完全看不出差别。
            MaxTriggersPerTurn = hat.IntA,
            ActiveZone = hat.ActiveZone switch
            {
                "Hand" => ActiveZoneScope.Hand,
                "Deck" => ActiveZoneScope.Deck,
                "ExArea" => ActiveZoneScope.ExArea,
                _ => ActiveZoneScope.Board,
            },
            DuringTurn = hat.DuringTurn switch
            {
                "Self" => TurnOwnerScope.Self,
                "Opponent" => TurnOwnerScope.Opponent,
                _ => TurnOwnerScope.Any,
            },
        };

        var body = CompileStackList(hat.Stacks);
        var cond = CompileCond(hat.Condition);

        return new AbilityDef
        {
            Trigger = trig,
            Condition = cond ?? BoolExpr.True,
            Costs = costs,
            Body = body,
        };
    }

    private List<EffectNode> CompileStackList(List<Block> blocks)
    {
        var list = new List<EffectNode>();
        foreach (var b in blocks ?? new List<Block>())
        {
            var n = CompileStack(b);
            if (n != null) list.Add(n);
        }
        return list;
    }

    private EffectNode CompileStack(Block b)
    {
        if (b == null || b.IsEmpty) return null;

        switch (b.Opcode)
        {
            case "c_if_else":
            {
                var cond = CompileCond(b.Condition);
                if (cond == null) { _res.Fail(b, "if 缺少条件"); return null; }
                return new ConditionalNode
                {
                    Condition = cond,
                    Then = CompileStackList(b.Stacks),
                    Else = CompileStackList(b.ElseStacks),
                };
            }
            case "c_repeat":
            {
                var n = CompileNumber(b.Number);
                return new LoopNode
                {
                    Kind = LoopKind.Times,
                    Count = n ?? NumExpr.Const(1),
                    MaxIterations = n != null && n.Op == NumOp.Const ? Math.Max(1, n.Value) : 30,
                    Body = CompileStackList(b.Stacks),
                };
            }
            case "c_for_each":
            {
                var coll = CompileTarget(b.Target);
                return new LoopNode
                {
                    Kind = LoopKind.ForEach,
                    Collection = coll ?? TargetSpec.Self,
                    Body = CompileStackList(b.Stacks),
                    MaxIterations = 30,
                };
            }
            case "c_mode":
            {
                var opts = new List<IReadOnlyList<EffectNode>>();
                foreach (var br in b.Branches ?? new List<List<Block>>())
                    opts.Add(CompileStackList(br));
                return new BranchModeNode
                {
                    Kind = b.EnumA switch
                    {
                        "Random" => ModeDispatchKind.Random,
                        "Sequence" => ModeDispatchKind.Sequence,
                        "RandomUnused" => ModeDispatchKind.RandomUnused,
                        "SequenceUnused" => ModeDispatchKind.SequenceUnused,
                        _ => ModeDispatchKind.Choose,
                    },
                    // 优先读 Number 槽位：PickCount 可能是动态表达式
                    // （「发动 Y 个，Y 为此卡费用」），只读 IntA 会把它压成常数。
                    PickCount = CompileNumber(b.Number) ?? NumExpr.Const(Math.Max(1, b.IntA)),
                    Options = opts,
                };
            }

            case "act_select":
            {
                var t = CompileTarget(b.Target);
                if (t == null) { _res.Fail(b, "select 缺少目标胶囊"); return null; }
                return new SelectTargetNode { Target = t, OutSlot = b.Text ?? TargetSpec.SlotSelected };
            }

            case "act_damage":
                return new DealDamageNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Amount = CompileNumber(b.Number) ?? NumExpr.Const(0),
                    IsAbility = true,
                };

            case "act_split_damage":
                return new DealDamageNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Amount = CompileNumber(b.Number) ?? NumExpr.Const(0),
                    IsSplit = true,
                    IsAbility = true,
                };

            case "act_restore":
                return new RestoreLifeNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Amount = CompileNumber(b.Number) ?? NumExpr.Const(0),
                };

            case "act_full_restore":
                return new RestoreLifeNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    FullRestore = true,
                };

            case "act_draw":
                return new MoveZoneNode
                {
                    Target = new TargetSpec
                    {
                        SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self, Zone = ZoneRef.Deck,
                        Mode = SelectMode.First, Count = CompileNumber(b.Number) ?? NumExpr.Const(1),
                    },
                    ToZone = ZoneRef.Hand,
                };

            case "act_summon":
            case "act_add_hand":
            case "act_gain_crest":
            {
                bool isCrest = b.Opcode == "act_gain_crest";
                // ToOwner 必须来自积木：Slaus 的「给**对手**贴纹章」写成固定 Self，
                // 往返后就成了「给自己贴纹章」，而卡面中文一模一样。
                var owner = b.EnumA switch
                {
                    "Opponent" => ZoneScope.Opponent,
                    "Any" => ZoneScope.Any,
                    _ => ZoneScope.Self,
                };
                return new SpawnCardNode
                {
                    // 纹章/信仰是**独立实体**，不继承主人卡的卡号：
                    // 给了主人卡号，运行期会按那个卡号去建实体，拿到的却是主人卡本身。
                    CardId = isCrest ? 0 : (b.IntB != 0 ? b.IntB : CardIdOf(b.Text)),
                    CardName = b.Text ?? "",
                    Count = CompileNumber(b.Number) ?? NumExpr.Const(1),
                    // 纹章落在纹章区，不是战场。写成 Board 会让它在战场上占一个格子，
                    // 而且同名唯一/区域上限这些纹章规则全部失效。
                    ToZone = isCrest ? ZoneRef.CrestArea
                           : b.Opcode == "act_add_hand" ? ZoneRef.Hand : ZoneRef.Board,
                    IsCrest = isCrest,
                    ToOwner = owner,
                    // 纹章节点内嵌子卡定义，否则运行期建出的纹章没有能力。
                    CrestDef = isCrest
                        ? (b.Text == (_pendingFaith?.Name ?? "") ? _pendingFaith : _pendingCrest)
                        : null,
                };
            }

            case "act_destroy":
                return MoveTo(b, ZoneRef.Cemetery, true);
            case "act_banish":
                return MoveTo(b, ZoneRef.Banished, false);
            case "act_discard":
                return MoveTo(b, ZoneRef.Cemetery, false);
            case "act_bounce":
                return MoveTo(b, ZoneRef.Hand, false);
            case "act_return_deck":
                return MoveTo(b, ZoneRef.Deck, false);
            case "act_move_to_board":
                return MoveTo(b, ZoneRef.Board, false);

            case "act_buff":
                return new ModifyStatsNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = StatModOp.Add,
                    Attack = CompileNumber(b.Number),
                    Life = CompileNumber(b.Number2),
                    Duration = ParseDuration(b.EnumB),
                };

            case "act_set_stats":
                return new ModifyStatsNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = StatModOp.Set,
                    Attack = CompileNumber(b.Number),
                    Life = CompileNumber(b.Number2),
                    Duration = ParseDuration(b.EnumB),
                };

            case "act_swap_stats":
                return new ModifyStatsNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = StatModOp.Swap,
                };

            case "act_mod_cost":
                return new ModifyCostNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = b.EnumA switch
                    {
                        "Halve" => CostModOp.Halve,
                        "Set" => CostModOp.Set,
                        _ => CostModOp.Add,
                    },
                    Value = CompileNumber(b.Number) ?? NumExpr.Const(0),
                    Duration = ParseDuration(b.EnumB),
                };

            case "act_reveal":
                return new RevealCardNode { Target = CompileTarget(b.Target) ?? TargetSpec.Self };

            case "act_damage_aura":
            {
                System.Enum.TryParse<DamageAuraKind>(b.EnumA ?? "", out var dk);
                System.Enum.TryParse<DamageSourceMask>(b.EnumB ?? "", out var dsm);
                if (dsm == DamageSourceMask.None) dsm = DamageSourceMask.All;
                return new DamageModifierNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Kind = dk,
                    SourceMask = dsm,
                    Amount = CompileNumber(b.Number),
                    IsReduction = b.EnumC != "0",
                };
            }

            case "act_grant_kw":
                return new ModifyKeywordNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = KeywordOp.Grant,
                    Keyword = _schema.Keyword(b.Text ?? ""),
                    Value = CompileNumber(b.Number),
                    Duration = ParseDuration(b.EnumB),
                };

            case "act_remove_kw":
                return new ModifyKeywordNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = KeywordOp.Remove,
                    Keyword = _schema.Keyword(b.Text ?? ""),
                };

            case "act_silence":
                return new ModifyKeywordNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = KeywordOp.SilenceAll,
                };

            case "act_evolve":
            case "act_super_evolve":
                return new EvolveNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    IsSuperEvolve = b.Opcode == "act_super_evolve",
                };

            case "act_mod_counter":
            {
                var delta = CompileNumber(b.Number) ?? NumExpr.Const(1);
                if (b.FlagA) delta = NumExpr.Sub(NumExpr.Const(0), delta);
                return new ModifyCounterNode
                {
                    Target = b.Target != null ? CompileTarget(b.Target) : null,
                    Owner = ZoneScope.Self,
                    Counter = _schema.Counter(b.Text ?? ""),
                    // Op 必须保留：「信仰初始值为 0」是 Set，「+1」是 Add。
                    // 把 Set 统一写成 Add，初值语义就变了（Set 0 表示清空，Add 0 什么都不做）。
                    Op = b.EnumA switch
                    {
                        "Set" => CounterOp.Set,
                        "Fill" => CounterOp.Fill,
                        _ => CounterOp.Add,
                    },
                    Delta = delta,
                };
            }

            case "act_transform":
                // DoTransform 只认 IntoCardId：没有它，变身是彻底的空操作。
                return new TransformNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    IntoCardId = b.IntB != 0 ? b.IntB : CardIdOf(b.Text),
                    IntoCardName = b.Text ?? "",
                };

            case "act_reanimate":
                // 用唯一标准构造：手写这条节点会漏掉 9 个字段里的某几个，
                // 而漏掉的后果是召唤出一个 CardId=0 的白壳。
                return ReanimateHelper.Build(CompileNumber(b.Number) ?? NumExpr.Const(0));

            case "act_replace_deck":
                return new ReplaceDeckNode { DeckName = b.Text ?? "", Owner = ZoneScope.Self };

            case "act_mark_victory":
                // Mjerrabaine 纹章的胜负改判：把牌库标记为「含胜利的卡牌」。
                return new MarkVictoryCardNode { Owner = ZoneScope.Self };

            case "act_invoke":
                // Target 缺了就是空操作：DoInvoke 第一行 Resolve(n.Target)。
                return new InvokeAbilityNode
                {
                    Event = EventOf(b.Text),
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                };

            case "act_copy":
                // 复制体：SourceKind 必须是 CopyOfTarget，否则运行期会去 SpawnCard(0)。
                return new SpawnCardNode
                {
                    SourceKind = CardSourceKind.CopyOfTarget,
                    CopyFrom = CompileTarget(b.Target) ?? TargetSpec.Self,
                    IsExactCopy = b.FlagA,
                    Count = CompileNumber(b.Number) ?? NumExpr.Const(1),
                    ToZone = b.EnumA == "Hand" ? ZoneRef.Hand : ZoneRef.Board,
                    ToOwner = ZoneScope.Self,
                };

            case "act_discover":
            {
                return new SpawnCardNode
                {
                    SourceKind = CardSourceKind.PoolQuery,
                    CopyFrom = CompileTarget(b.Target) ?? new TargetSpec
                    {
                        Zone = ZoneRef.CardPool, Mode = SelectMode.Choose,
                    },
                    ToZone = b.EnumA switch
                    {
                        "board" => ZoneRef.Board,
                        "deck" => ZoneRef.Deck,
                        "exarea" => ZoneRef.ExArea,
                        _ => ZoneRef.Hand,
                    },
                    ToOwner = ZoneScope.Self,
                    Count = NumExpr.Const(b.IntA > 0 ? b.IntA : 1),
                    OfferCount = b.IntB > 0 ? b.IntB : 3,
                };
            }

            case "act_grant_entity_ability":
            {
                // 给**实体**授予能力（不是给计数器）。
                // 与 GrantCounterAbilityNode 是两种节点，不能合并。
                return new GrantAbilityNode
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Granted = CompileGrantedAbility(b),
                    Duration = b.EnumA switch
                    {
                        "ThisTurn" => DurationKind.ThisTurn,
                        "UntilEndOfOpponentTurn" => DurationKind.UntilEndOfOpponentTurn,
                        _ => DurationKind.Permanent,
                    },
                };
            }

            case "act_grant_ability":
            {
                return new GrantCounterAbilityNode
                {
                    Counter = _schema.Counter(b.Text ?? ""),
                    Granted = CompileGrantedAbility(b),
                };
            }

            default:
                _res.Fail(b, "未知动作积木");
                return null;
        }
    }

    /// <summary>
    /// Duration 字面量 → DurationKind。
    ///
    /// 限时效果（直到回合结束 / 直到敌方回合结束）与永久效果在 AST 上
    /// 只差这一个字段；丢了它就全变成永久，而卡面中文看不出区别。
    /// </summary>
    private static DurationKind ParseDuration(string s) => s switch
    {
        "ThisTurn" => DurationKind.ThisTurn,
        "UntilEndOfOpponentTurn" => DurationKind.UntilEndOfOpponentTurn,
        "ThisMatch" => DurationKind.ThisMatch,
        _ => DurationKind.Permanent,
    };

    /// <summary>还原被授予的能力：优先按整条事件帽解析，兼容旧的纯动作写法。</summary>
    private AbilityDef CompileGrantedAbility(Block b)
    {
        if (b?.Stacks != null && b.Stacks.Count == 1 && b.Stacks[0].Shape == BlockShape.Hat)
        {
            var ab = CompileHat(b.Stacks[0]);
            if (ab != null) return ab;
        }
        return new AbilityDef
        {
            Trigger = new TriggerSpec { Event = EventOf(b?.Text ?? "PassiveAura") },
            Body = b?.Stacks != null && b.Stacks.Count > 0
                ? CompileStackList(b.Stacks)
                : new List<EffectNode>(),
        };
    }

    private EffectNode MoveTo(Block b, ZoneRef zone, bool destroy)
        => new MoveZoneNode
        {
            Target = CompileTarget(b.Target) ?? TargetSpec.Self,
            ToZone = zone,
            IsDestroy = destroy,
        };

    // ---------------------------------------------------------------- 胶囊

    private TargetSpec CompileTarget(Block b)
    {
        if (b == null || b.IsEmpty) return null;

        switch (b.Opcode)
        {
            case "tgt_slot":
                return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = b.Text ?? TargetSpec.SlotSelf };

            case "tgt_leader":
                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = b.EnumA switch
                    {
                        "Opponent" => ZoneScope.Opponent,
                        "Any" => ZoneScope.Any,
                        _ => ZoneScope.Self,
                    },
                    Zone = ZoneRef.Leader,
                    Mode = SelectMode.All,
                };

            case "tgt_query":
            {
                var parts = new List<BoolExpr>();
                var cond = CompileCond(b.Condition);
                if (cond != null && !(cond is ConstBoolExpr cb0 && cb0.Value)) parts.Add(cond);

                // 卡种、排除自己、附加条件三者是并列的，必须都能回到 Where。
                if (!string.IsNullOrEmpty(b.Text))
                {
                    parts.Add(new HasKindExpr
                    {
                        Kind = b.Text switch
                        {
                            "Follower" => CardKind.Follower,
                            "Spell" => CardKind.Spell,
                            "Amulet" => CardKind.Amulet,
                            _ => CardKind.Follower,
                        },
                        Of = TargetSpec.Item,
                    });
                }
                if (b.FlagA)
                {
                    parts.Add(new NotSameEntityExpr
                    {
                        ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf,
                        Of = TargetSpec.Item,
                    });
                }

                BoolExpr where = parts.Count == 0 ? BoolExpr.True
                    : parts.Count == 1 ? parts[0] : new AndExpr(parts);

                var mode = b.EnumA switch
                {
                    "Random" => SelectMode.Random,
                    "Choose" => SelectMode.Choose,
                    "First" => SelectMode.First,
                    _ => SelectMode.All,
                };

                return new TargetSpec
                {
                    SourceKind = string.IsNullOrEmpty(b.CardNameA) ? TargetSourceKind.Zone : TargetSourceKind.CardId,
                    // 卡号必须填：CardId==0 时历史池筛选会退化成「任意卡」。
                    CardId = string.IsNullOrEmpty(b.CardNameA) ? 0 : CardIdOf(b.CardNameA),
                    CardName = b.CardNameA ?? "",
                    IncludeLeader = b.FlagB,
                    DuplicatesOnly = b.FlagC,
                    Sort = string.IsNullOrEmpty(b.EnumC) ? SortKey.None
                         : (Enum.TryParse<SortKey>(b.EnumC, true, out var sk2) ? sk2 : SortKey.None),
                    SortDescending = b.FlagD,
                    DistinctByName = b.FlagE,
                    TopN = b.IntC,
                    History = b.EnumB switch
                    {
                        "DestroyedThisMatch" => HistoryScope.DestroyedThisMatch,
                        "DestroyedThisTurn" => HistoryScope.DestroyedThisTurn,
                        "EnteredThisMatch" => HistoryScope.EnteredThisMatch,
                        _ => HistoryScope.None,
                    },
                    Owner = (b.IntB & 1) == 1 ? ZoneScope.Opponent
                          : (b.IntB & 2) == 2 ? ZoneScope.Any : ZoneScope.Self,
                    Zone = b.IntA switch
                    {
                        1 => ZoneRef.Hand,
                        2 => ZoneRef.Deck,
                        3 => ZoneRef.Cemetery,
                        4 => ZoneRef.Banished,
                        5 => ZoneRef.Leader,
                        6 => ZoneRef.CrestArea,
                        7 => ZoneRef.ExArea,
                        // 外部池：缺这两支会静默回落成 Board。
                        8 => ZoneRef.CardPool,
                        9 => ZoneRef.GlobalPool,
                        _ => ZoneRef.Board,
                    },
                    Mode = mode,
                    Count = mode == SelectMode.All ? null : CompileNumber(b.Number),
                    Where = where,
                };
            }

            default:
                _res.Fail(b, "不是目标胶囊");
                return null;
        }
    }

    private BoolExpr CompileCond(Block b)
    {
        if (b == null || b.IsEmpty) return null;

        switch (b.Opcode)
        {
            case "bool_true": return BoolExpr.True;
            case "bool_compare":
                return new CompareExpr
                {
                    Left = CompileNumber(b.Number),
                    Op = b.EnumA switch
                    {
                        "Le" => CompareOp.Le,
                        "Eq" => CompareOp.Eq,
                        "Gt" => CompareOp.Gt,
                        "Lt" => CompareOp.Lt,
                        _ => CompareOp.Ge,
                    },
                    Right = CompileNumber(b.Number2),
                };
            case "bool_and":
            case "bool_not":
            case "bool_or":
            {
                var items = new List<BoolExpr>();
                foreach (var x in b.Stacks ?? new List<Block>())
                {
                    var c = CompileCond(x);
                    if (c != null) items.Add(c);
                }
                if (b.Opcode == "bool_not")
                    return items.Count > 0 ? new NotExpr(items[0]) : null;
                if (items.Count == 0) return null;
                return b.Opcode == "bool_and" ? new AndExpr(items) : new OrExpr(items);
            }
            case "bool_has_kw":
                return new HasKeywordExpr
                {
                    Keyword = _schema.Keyword(b.Text ?? ""),
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };
            case "bool_has_trait":
                return new HasTraitExpr
                {
                    Trait = _schema.Trait(b.Text ?? ""),
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };
            // 发现的卡包/职业筛选。缺这两支会让积木端造不出
            // 「只从某卡包/某职业发现」的卡 —— 而脚本端与表单端都能造。
            case "bool_has_card_name":
                // 具名卡过滤（"Fuse: Ominous Artifact β" / "not named X"）。
                // 不支持的话这类卡切到积木视图会直接报「积木不支持条件」。
                return new HasCardNameExpr
                {
                    CardId = b.IntA != 0 ? b.IntA : CardIdOf(b.Text),
                    CardName = b.Text ?? "",
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };

            case "bool_has_pack":
                return new HasPackExpr
                {
                    PackId = b.Text ?? "",
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };
            case "bool_has_clan":
            {
                var txt = b.Text ?? "";
                if (string.Equals(txt, "self", StringComparison.OrdinalIgnoreCase))
                    return new HasClanExpr { IsSelfClan = true, Of = CompileTarget(b.Target) ?? TargetSpec.Item };

                var names = txt.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (names.Length > 1)
                    return new HasClanExpr
                    {
                        Clans = names.Select(x => _schema.Clan(x.Trim())).ToList(),
                        Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                    };

                return new HasClanExpr
                {
                    Clan = _schema.Clan(txt),
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };
            }
            case "bool_has_state":
                return new HasStateExpr
                {
                    State = b.EnumA switch
                    {
                        "Evolved" => Core.UnitState.Evolved,
                        "SuperEvolved" => Core.UnitState.SuperEvolved,
                        "Damaged" => Core.UnitState.Damaged,
                        "HasFuse" => Core.UnitState.HasFuse,
                        "HasLastWords" => Core.UnitState.HasLastWords,
                        "HasOnSpellboost" => Core.UnitState.HasOnSpellboost,
                        _ => Core.UnitState.Damaged,
                    },
                    // Of 必须还原：能力级条件判的是 $self，where 里判的是 $item。
                    // 一律写 $item，会让「本卡是否已进化」在触发时恒为 false ——
                    // 因为触发那一刻作用域里只有 $self，$item 是空的。
                    Of = CompileTarget(b.Target) ?? TargetSpec.Item,
                };
            case "bool_exists":
                return new ExistsExpr
                {
                    Target = CompileTarget(b.Target) ?? TargetSpec.Self,
                    Op = CompareOp.Ge,
                    Count = CompileNumber(b.Number) ?? NumExpr.Const(1),
                };
            default:
                _res.Fail(b, "未知条件积木");
                return null;
        }
    }

    private NumExpr CompileNumber(Block b)
    {
        if (b == null || b.IsEmpty) return null;

        switch (b.Opcode)
        {
            case "num_const": return NumExpr.Const(b.IntA);
            case "num_rand":
                // IntA = 下界, IntB = 上界, IntC = 变量槽(0=X 1=Y 2=Z)
                return NumExpr.RandRange(b.IntA, b.IntB, b.IntC);
            case "num_counter": return NumExpr.Ctr(_schema.Counter(b.Text ?? ""));
            case "num_prop":
            {
                // 用 TryParse 覆盖**全部** StatKey：只列三种会让 BaseCost /
                // BaseAttack / Countdown 等经过往返后被篡改成「攻击力」，
                // 而卡面中文完全一样（都读「攻击力」以外的词）。
                var sk = Enum.TryParse<StatKey>(b.EnumA, true, out var parsedSk)
                    ? parsedSk : StatKey.Attack;
                // Of 必须还原：缺省是 $item（筛选上下文），但「X = 本卡攻击力」用的是 $self。
                return NumExpr.Prop(sk, CompileTarget(b.Target) ?? TargetSpec.Item);
            }
            case "num_count":
                return NumExpr.CountOf(CompileTarget(b.Target) ?? TargetSpec.Self);
            case "num_max_group":
                return new NumExpr { Op = NumOp.MaxGroupSize, Of = CompileTarget(b.Target) ?? TargetSpec.Self };
            case "num_arith":
            {
                var l = CompileNumber(b.Number);
                var r = CompileNumber(b.Number2);
                if (l == null) return r;
                if (r == null) return l;
                return b.EnumA switch
                {
                    "Sub" => NumExpr.Sub(l, r),
                    "Mul" => NumExpr.Mul(l, r),
                    "Div" => NumExpr.Div(l, r),
                    _ => NumExpr.Add(l, r),
                };
            }
            default:
                _res.Fail(b, "不是数值积木");
                return null;
        }
    }

    private static CoreEvent EventOf(string s) => s switch
    {
        "OnPlay" => CoreEvent.OnPlay,
        "OnEnterField" => CoreEvent.OnEnterField,
        "OnLeaveField" => CoreEvent.OnLeaveField,
        "OnDestroy" => CoreEvent.OnDestroy,
        "OnBanish" => CoreEvent.OnBanish,
        "OnDiscard" => CoreEvent.OnDiscard,
        "OnSummon" => CoreEvent.OnSummon,
        "OnEvolve" => CoreEvent.OnEvolve,
        "OnSuperEvolve" => CoreEvent.OnSuperEvolve,
        "OnAttack" => CoreEvent.OnAttack,
        "OnClash" => CoreEvent.OnClash,
        "OnDamaged" => CoreEvent.OnDamaged,
        "OnHealed" => CoreEvent.OnHealed,
        "OnEngage" => CoreEvent.OnEngage,
        "OnFuse" => CoreEvent.OnFuse,
        "OnSpellboost" => CoreEvent.OnSpellboost,
        "OnDraw" => CoreEvent.OnDraw,
        "OnReturnToHand" => CoreEvent.OnReturnToHand,
        "OnAddToHand" => CoreEvent.OnAddToHand,
        "OnTransform" => CoreEvent.OnTransform,
        "OnEarthRite" => CoreEvent.OnEarthRite,
        "OnSelectMode" => CoreEvent.OnSelectMode,
        "OnCountdownExpire" => CoreEvent.OnCountdownExpire,
        "PassiveAura" => CoreEvent.PassiveAura,
        "BattleStart" => CoreEvent.BattleStart,
        "TurnStart" => CoreEvent.TurnStart,
        "TurnEnd" => CoreEvent.TurnEnd,
        _ => CoreEvent.OnPlay,
    };
}
/// <summary>
/// CardDef → 积木树。
///
/// 必须覆盖全部节点：30 张真卡里既有模式分支、历史池统计，也有纹章与信仰子块。
/// 遇到不认识的节点**必须报错**，不能静默丢 —— 静默丢会让「积木能造这张卡」
/// 变成一句谎话，而玩家打开编辑器只会看到一张缺了效果的空卡。
/// </summary>
public sealed class DiyBlockDecompiler
{
    private readonly GameSchema _schema;
    public List<string> Errors { get; } = new();

    public DiyBlockDecompiler(GameSchema schema) { _schema = schema; }

    /// <summary>
    /// 把构筑期修正还原成积木块。
    ///
    /// 它们不是能力，不能混进任何事件帽的能力体，
    /// 所以单独成块并挂在文档上，由 Compile 端收集回 DeckModsOut。
    /// </summary>
    private List<Block> DeckModBlocks(CardDef card)
    {
        var list = new List<Block>();
        if (card?.DeckModifiers == null) return list;
        foreach (var m in card.DeckModifiers)
        {
            if (m == null) continue;
            string word = m.Kind switch
            {
                DeckModKind.ModifyDeckSize => "size",
                DeckModKind.ModifyLeaderStartLife => "leader_life",
                DeckModKind.ModifyStartingHandSize => "hand",
                DeckModKind.ModifyStartEvolvePoints => m.Value2 != 0 ? "sep" : "ep",
                DeckModKind.OverrideSelfCopyLimit => "self_copy",
                DeckModKind.AllowSpecificClan => "allow_clan_fixed",
                DeckModKind.AllowChosenClans => "allow_clan_choose",
                _ => "size",
            };
            int num = m.Kind == DeckModKind.ModifyStartEvolvePoints && m.Value2 != 0 ? m.Value2 : m.Value;
            list.Add(new Block
            {
                Opcode = "act_deck_mod",
                Text = word,
                Text2 = m.ExclusiveGroup ?? "",
                CardNameA = m.TargetClan ?? "",
                IntA = num,
                FlagA = m.StackPerCopy,
            });
        }
        return list;
    }

    public CardBlockWorkspace Decompile(CardDef def)
    {
        Errors.Clear();
        if (def == null) { Errors.Add("CardDef 为空"); return null; }

        var ws = new CardBlockWorkspace
        {
            CardId = def.CardId,
            Name = def.Name ?? "",
            Kind = def.Kind.ToString(),
            Cost = def.BaseCost,
            Attack = def.BaseAttack,
            Life = def.BaseLife,
            Clan = def.Clan.IsValid ? def.Clan.Name : null,
            Traits = (def.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(t => t.Name).ToList(),
            HasFuse = def.HasFuse,
            FuseFilter = def.HasFuse ? Target(def.FuseMaterialFilter) : null,
            // 构筑期修正必须单独装：它们不是能力，混进事件帽会让
            // 对局中跑出「牌组张数 +20」这种根本不该发生的动作。
            DeckModBlocks = DeckModBlocks(def),
        };

        foreach (var ab in def.Abilities ?? Array.Empty<AbilityDef>())
        {
            if (IsBareKeyword(ab))
            {
                var mk = (ModifyKeywordNode)ab.Body[0];
                ws.Keywords.Add(mk.Value != null && mk.Value.Op == NumOp.Const
                    ? mk.Keyword.Name + "(" + mk.Value.Value + ")"
                    : mk.Keyword.Name);
                continue;
            }
            var hat = Hat(ab);
            if (hat != null) ws.Hats.Add(hat);
        }

        if (def.AccelerateDef != null)
        {
            ws.AccelerateCost = def.AccelerateDef.BaseCost;
            ws.AccelerateHats = Hats(def.AccelerateDef.Abilities);
        }
        if (def.CrystallizeDef != null)
        {
            ws.CrystallizeCost = def.CrystallizeDef.BaseCost;
            ws.CrystallizeHats = Hats(def.CrystallizeDef.Abilities);
        }
        if (def.CrestDef != null) ws.CrestHats = Hats(def.CrestDef.Abilities);
        if (def.FaithDef != null) ws.FaithHats = Hats(def.FaithDef.Abilities);

        return ws;
    }

    private static bool IsBareKeyword(AbilityDef ab)
        => ab?.Trigger?.Event == CoreEvent.PassiveAura
           && (ab.Costs == null || ab.Costs.Count == 0)
           && ab.Condition is null or ConstBoolExpr
           && ab.Body != null && ab.Body.Count == 1
           && ab.Body[0] is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant;

    private List<Block> Hats(IReadOnlyList<AbilityDef> abilities)
    {
        var list = new List<Block>();
        foreach (var ab in abilities ?? Array.Empty<AbilityDef>())
        {
            // 只跳「裸关键词」，不能把 PassiveAura 整体跳过。
            // 子卡（结晶/纹章/信仰）的常驻项正是 PassiveAura：
            //   · 初始吟唱 Countdown (3)
            //   · 信仰的「初始值为 0」
            // 跳过它们，结晶护符进场后永远不会自己消失，信仰也没初值。
            if (IsBareKeyword(ab)) continue;
            var h = Hat(ab);
            if (h != null) list.Add(h);
        }
        return list;
    }

    /// <summary>
    /// 把被授予的能力整条装进事件帽积木。
    ///
    /// 被授予的能力可以是完整能力（带 SubjectFilter / DuringTurn / Condition / Costs），
    /// 只存 Event 名会让这些限定词全部丢失 —— 而卡面中文读起来完全一样。
    /// </summary>
    private List<Block> WrapGranted(AbilityDef granted)
    {
        var list = new List<Block>();
        if (granted == null) return list;
        var h = Hat(granted);
        if (h != null) list.Add(h);
        return list;
    }

    private Block Hat(AbilityDef ab)
    {
        if (ab?.Trigger == null) return null;

        // PassiveAura + 有代价（吟唱）或有效果体（计数器初值）时，必须当作常驻帽保留，
        // 否则子卡最关键的「吟唱 N」「初始值为 N」直接人间蒸发。
        string opcode = ab.Trigger.SubjectFilter != null ? "hat_watch_event"
                      : ab.Trigger.Event == CoreEvent.PassiveAura ? "hat_passive"
                      : "hat_self_event";

        var hat = new Block
        {
            Opcode = opcode,
            Text = ab.Trigger.Event.ToString(),
            FlagA = ab.Trigger.ManualEvolveOnly,
            EnumB = ab.Trigger.SecondaryEvent?.ToString(),
            IntA = ab.Trigger.MaxTriggersPerTurn,
            ActiveZone = ab.Trigger.ActiveZone == ActiveZoneScope.Board ? null : ab.Trigger.ActiveZone.ToString(),
            DuringTurn = ab.Trigger.DuringTurn == TurnOwnerScope.Any ? null : ab.Trigger.DuringTurn.ToString(),
            Target = opcode == "hat_watch_event" ? Target(ab.Trigger.SubjectFilter) : null,
            Condition = Cond(ab.Condition),
        };

        foreach (var c in ab.Costs ?? Array.Empty<CostSpec>())
        {
            // 优先用 Label 还原具体种类：UsePp 既可能是「爆能强化」也可能是「启动」，
            // 光看 Kind 分不出来，而两者的运行期行为完全不同。
            // Label 可能带空格（EnglishCompiler 写的是 "earth rite"），
            // 归一成下划线才能与脚本/积木的关键字对上。
            string labelKey = c.Label?.ToLowerInvariant().Replace(' ', '_');
            string kind = !string.IsNullOrEmpty(labelKey) ? labelKey
                        : c.Kind switch
                        {
                            CostKind.Necromance => "necromancy",
                            CostKind.DestroyAlly => "earth_rite",
                            CostKind.TurnPlayCount => "combo",
                            CostKind.MatchPlayCount => "rally",
                            CostKind.Countdown => "countdown",
                            _ => "enhance",
                        };
            int amt = c.Amount != null && c.Amount.Op == NumOp.Const ? c.Amount.Value : 1;
            hat.Costs.Add((kind, amt));
        }

        hat.Stacks = Stacks(ab.Body);
        return hat;
    }

    private List<Block> Stacks(IReadOnlyList<EffectNode> nodes)
    {
        var list = new List<Block>();
        foreach (var n in nodes ?? Array.Empty<EffectNode>())
        {
            var b = Stack(n);
            if (b != null) list.Add(b);
        }
        return list;
    }

    private Block Stack(EffectNode n)
    {
        switch (n)
        {
            case RevealCardNode rv:
                return new Block { Opcode = "act_reveal", Target = Target(rv.Target) };

            case DamageModifierNode dm:
                return new Block
                {
                    Opcode = "act_damage_aura",
                    Target = Target(dm.Target),
                    EnumA = dm.Kind.ToString(),
                    EnumB = dm.SourceMask.ToString(),
                    EnumC = dm.IsReduction ? "1" : "0",
                    Number = Num(dm.Amount),
                };

            case SelectTargetNode st:
                return new Block { Opcode = "act_select", Text = st.OutSlot, Target = Target(st.Target) };

            case DealDamageNode d:
                return new Block
                {
                    Opcode = d.IsSplit ? "act_split_damage" : "act_damage",
                    Target = Target(d.Target), Number = Num(d.Amount),
                };

            case RestoreLifeNode r:
                return r.FullRestore
                    ? new Block { Opcode = "act_full_restore", Target = Target(r.Target) }
                    : new Block { Opcode = "act_restore", Target = Target(r.Target), Number = Num(r.Amount) };

            case MoveZoneNode m:
            {
                if (m.ToZone == ZoneRef.Hand && m.Target?.Zone == ZoneRef.Deck && m.Target.Mode == SelectMode.First)
                    return new Block { Opcode = "act_draw", Number = Num(m.Target.Count) };

                string op = m.ToZone switch
                {
                    ZoneRef.Cemetery => m.IsDestroy ? "act_destroy" : "act_discard",
                    ZoneRef.Banished => "act_banish",
                    ZoneRef.Hand => "act_bounce",
                    ZoneRef.Deck => "act_return_deck",
                    ZoneRef.Board => "act_move_to_board",
                    _ => null,
                };
                if (op == null) { Errors.Add("不支持的移区：" + m.ToZone); return null; }
                return new Block { Opcode = op, Target = Target(m.Target) };
            }

            case SpawnCardNode sp:
            {
                // 亡者召还：费用上限在 CopyFrom.Where 里，不在 Count。
                // Count 恒为 1（「召一个」），拿它当上限会让 Reanimate(8) 变成 Reanimate(1)。
                if (ReanimateHelper.IsReanimate(sp))
                {
                    ReanimateHelper.TryExtractCostCap(sp, out var cap);
                    return new Block { Opcode = "act_reanimate", Number = Num(cap) };
                }

                // 卡池检索：漏这一支会让带 discover 的卡掉进 act_add_hand，
                // 变成「把一张空名卡加入手牌」，条件与区域全丢。
                if (sp.SourceKind == CardSourceKind.PoolQuery)
                {
                    return new Block
                    {
                        Opcode = "act_discover",
                        Target = Target(sp.CopyFrom),
                        IntA = sp.Count != null && sp.Count.Op == NumOp.Const ? sp.Count.Value : 1,
                    IntB = sp.OfferCount,
                        EnumA = sp.ToZone switch
                        {
                            ZoneRef.Board => "board",
                            ZoneRef.Deck => "deck",
                            ZoneRef.ExArea => "exarea",
                            _ => "hand",
                        },
                    };
                }

                // 复制体与召唤是两种节点：SourceKind=CopyOfTarget 时没有 CardId，
                // 落到下面的 act_summon 分支会导出成「召唤一张空名卡」，
                // CopyFrom 与 IsExactCopy 全部丢失。
                if (sp.SourceKind == CardSourceKind.CopyOfTarget)
                {
                    return new Block
                    {
                        Opcode = "act_copy",
                        Target = Target(sp.CopyFrom),
                        FlagA = sp.IsExactCopy,
                        Number = Num(sp.Count),
                        EnumA = sp.ToZone == ZoneRef.Hand ? "Hand" : "Board",
                    };
                }
                string op = sp.IsCrest ? "act_gain_crest"
                          : sp.ToZone == ZoneRef.Hand ? "act_add_hand" : "act_summon";
                // IntB 存真实卡号：卡名可能重名或被改名，卡号不会。
                return new Block
                {
                    Opcode = op, Text = sp.CardName, Number = Num(sp.Count),
                    IntB = sp.CardId, EnumA = sp.ToOwner.ToString(),
                };
            }

            case ModifyStatsNode ms:
            {
                string op = ms.Op switch
                {
                    StatModOp.Set => "act_set_stats",
                    StatModOp.Swap => "act_swap_stats",
                    _ => "act_buff",
                };
                return new Block
                {
                    Opcode = op, Target = Target(ms.Target),
                    Number = Num(ms.Attack), Number2 = Num(ms.Life),
                    EnumB = ms.Duration == DurationKind.Permanent ? null : ms.Duration.ToString(),
                };
            }

            case ModifyCostNode mc:
                return new Block
                {
                    Opcode = "act_mod_cost", Target = Target(mc.Target),
                    EnumA = mc.Op.ToString(), Number = Num(mc.Value),
                    EnumB = mc.Duration == DurationKind.Permanent ? null : mc.Duration.ToString(),
                };

            case ModifyKeywordNode mk:
            {
                if (mk.Op == KeywordOp.SilenceAll)
                    return new Block { Opcode = "act_silence", Target = Target(mk.Target) };
                return new Block
                {
                    Opcode = mk.Op == KeywordOp.Remove ? "act_remove_kw" : "act_grant_kw",
                    Target = Target(mk.Target), Text = mk.Keyword.Name, Number = Num(mk.Value),
                    EnumB = mk.Duration == DurationKind.Permanent ? null : mk.Duration.ToString(),
                };
            }

            case EvolveNode e:
                return new Block
                {
                    Opcode = e.IsSuperEvolve ? "act_super_evolve" : "act_evolve",
                    Target = Target(e.Target),
                };

            case ModifyCounterNode c:
            {
                bool neg = c.Delta != null && c.Delta.Op == NumOp.Arith
                           && c.Delta.Arith == ArithOp.Sub && c.Delta.Args.Count > 1
                           && c.Delta.Args[0].Op == NumOp.Const && c.Delta.Args[0].Value == 0;
                var delta = neg ? c.Delta.Args[1] : c.Delta;
                return new Block
                {
                    Opcode = "act_mod_counter", Text = c.Counter.Name,
                    Target = c.Target != null ? Target(c.Target) : null,
                    Number = Num(delta), FlagA = neg,
                    EnumA = c.Op.ToString(),
                };
            }

            case TransformNode t:
                return new Block { Opcode = "act_transform", Target = Target(t.Target), Text = t.IntoCardName, IntB = t.IntoCardId };

            case ConditionalNode cd:
                return new Block
                {
                    Opcode = "c_if_else",
                    Condition = Cond(cd.Condition),
                    Stacks = Stacks(cd.Then),
                    ElseStacks = Stacks(cd.Else),
                };

            case LoopNode lp:
                if (lp.Kind == LoopKind.ForEach)
                    return new Block { Opcode = "c_for_each", Target = Target(lp.Collection), Stacks = Stacks(lp.Body) };
                return new Block { Opcode = "c_repeat", Number = Num(lp.Count), Stacks = Stacks(lp.Body) };

            case BranchModeNode bm:
            {
                var blk = new Block
                {
                    Opcode = "c_mode",
                    EnumA = bm.Kind.ToString(),
                    IntA = bm.PickCount != null && bm.PickCount.Op == NumOp.Const ? bm.PickCount.Value : 1,
                    // 同时保留动态表达式：只写 IntA 会让 prop(cost, $self)
                    // 在往返一次后退化成常数 1，积木端再也看不出原本的语义。
                    Number = Num(bm.PickCount),
                };
                foreach (var opt in bm.Options ?? Array.Empty<IReadOnlyList<EffectNode>>())
                    blk.Branches.Add(Stacks(opt));
                return blk;
            }

            case MarkVictoryCardNode _:
                return new Block { Opcode = "act_mark_victory" };

            case ReplaceDeckNode rd:
                return new Block { Opcode = "act_replace_deck", Text = rd.DeckName };

            case InvokeAbilityNode ia:
                return new Block { Opcode = "act_invoke", Text = ia.Event.ToString(), Target = Target(ia.Target ?? TargetSpec.Self) };

            case GrantAbilityNode ga:
            {
                var gb = new Block
                {
                    Opcode = "act_grant_entity_ability",
                    Target = Target(ga.Target),
                    EnumA = ga.Duration == DurationKind.Permanent ? null : ga.Duration.ToString(),
                };
                // 被授予的能力必须**整条**装进事件帽：
                // 只存 Event 名会丢掉 SubjectFilter / DuringTurn / Condition / Costs ——
                // 「每当友方随从进化」会退化成「常驻」、「若…则…」会变成无条件。
                gb.Stacks = WrapGranted(ga.Granted);
                return gb;
            }

            case GrantCounterAbilityNode gc:
            {
                var blk = new Block { Opcode = "act_grant_ability", Text = gc.Counter.Name };
                blk.Stacks = WrapGranted(gc.Granted);
                return blk;
            }

            default:
                Errors.Add("积木不支持节点：" + n.GetType().Name);
                return null;
        }
    }

    private Block Target(TargetSpec t)
    {
        if (t == null) return null;

        if (t.SourceKind == TargetSourceKind.Slot)
            return new Block { Opcode = "tgt_slot", Text = t.Slot };

        // 具名卡池（card("X", … history=…)）不能走特例：
        // 特例只带三个字段，会丢掉 CardName 与 History，
        // 于是「本局已进入战场的同名卡」反编译成「当前场上的所有卡」。
        if (t.Zone == ZoneRef.Leader && t.SourceKind == TargetSourceKind.Zone)
            return new Block { Opcode = "tgt_leader", EnumA = t.Owner.ToString() };

        // 卡种 / 排除自己 / 历史池 / 具名卡——这些都必须单独承载。
        // 之前一律塞进 Condition，结果 Condition 只认布尔积木，
        // 「is Follower」「other than itself」「history=Entered」全部被判为不支持而丢弃，
        // 于是「所有敌方随从」反编译成「所有敌方卡」，效果范围被悄悄放大。
        var (kindText, restWhere) = SplitKind(t.Where);
        bool excludeSelf = HasNotSame(restWhere, out var restWhere2);

        return new Block
        {
            Opcode = "tgt_query",
            IntA = ZoneCode(t.Zone),
            IntB = OwnerCode(t.Owner),
            EnumA = t.Mode.ToString(),
            Text = kindText,
            FlagA = excludeSelf,
            FlagB = t.IncludeLeader,
            FlagC = t.DuplicatesOnly,
            EnumB = t.History == HistoryScope.None ? null : t.History.ToString(),
            CardNameA = t.CardName,
            // 排序与去重是筛选语义的一部分：「基础费用最高的 1 个」与「任意 1 个」
            // 出天差地别，而 AstEnglishPrinter 不打印它们时会被静默丢掉。
            EnumC = t.Sort == SortKey.None ? null : t.Sort.ToString(),
            FlagD = t.SortDescending,
            FlagE = t.DistinctByName,
            IntC = t.TopN,
            Number = t.Mode == SelectMode.All ? null : Num(t.Count),
            Condition = Cond(restWhere2),
        };
    }

    /// <summary>从 Where 里抽出「卡种」文字，返回剩余条件。</summary>
    private static (string kind, BoolExpr rest) SplitKind(BoolExpr w)
    {
        if (w == null) return (null, null);
        if (w is HasKindExpr hk)
            return (hk.Kind switch
            {
                CardKind.Follower => "Follower",
                CardKind.Spell => "Spell",
                CardKind.Amulet => "Amulet",
                _ => null,
            }, null);

        if (w is AndExpr a)
        {
            string kind = null;
            var kept = new List<BoolExpr>();
            foreach (var x in a.Items)
            {
                if (x is HasKindExpr k2 && kind == null)
                {
                    kind = k2.Kind switch
                    {
                        CardKind.Follower => "Follower",
                        CardKind.Spell => "Spell",
                        CardKind.Amulet => "Amulet",
                        _ => null,
                    };
                    continue;
                }
                kept.Add(x);
            }
            if (kept.Count == 0) return (kind, null);
            if (kept.Count == 1) return (kind, kept[0]);
            return (kind, new AndExpr(kept));
        }
        return (null, w);
    }

    /// <summary>从条件里检出并剥掉「排除自己」，返回是否命中。</summary>
    private static bool HasNotSame(BoolExpr w, out BoolExpr rest)
    {
        rest = w;
        if (w is NotSameEntityExpr) { rest = null; return true; }
        if (w is AndExpr a)
        {
            bool hit = false;
            var kept = new List<BoolExpr>();
            foreach (var x in a.Items)
            {
                if (x is NotSameEntityExpr) { hit = true; continue; }
                kept.Add(x);
            }
            if (!hit) return false;
            rest = kept.Count == 0 ? null : kept.Count == 1 ? kept[0] : new AndExpr(kept);
            return true;
        }
        return false;
    }

    /// <summary>把 has kind X 从条件里抽出来（tgt_query 的卡种由 Text 承载）。</summary>
    private BoolExpr StripKind(BoolExpr w)
    {
        if (w is AndExpr a)
        {
            var kept = a.Items.Where(x => x is not HasKindExpr).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return new AndExpr(kept);
        }
        return w is HasKindExpr ? null : w;
    }

    private static int ZoneCode(ZoneRef z) => z switch
    {
        ZoneRef.Hand => 1, ZoneRef.Deck => 2, ZoneRef.Cemetery => 3,
        ZoneRef.Banished => 4, ZoneRef.Leader => 5, ZoneRef.CrestArea => 6,
        ZoneRef.ExArea => 7,
        // 外部池必须有自己的编码：缺这两支会静默回落成 0(=战场)，
        // 「从当前卡池发现」于是变成「从战场选」。
        ZoneRef.CardPool => 8, ZoneRef.GlobalPool => 9,
        _ => 0,
    };

    private static int OwnerCode(ZoneScope o) => o switch
    {
        ZoneScope.Opponent => 1, ZoneScope.Any => 2, _ => 0,
    };

    private Block Cond(BoolExpr e)
    {
        if (e == null) return null;
        if (e is ConstBoolExpr cb) return cb.Value ? null : new Block { Opcode = "bool_true", FlagA = false };

        switch (e)
        {
            case CompareExpr c:
                return new Block
                {
                    Opcode = "bool_compare", Number = Num(c.Left), Number2 = Num(c.Right),
                    EnumA = c.Op.ToString(),
                };
            case AndExpr a:
            {
                var b = new Block { Opcode = "bool_and" };
                foreach (var x in a.Items)
                {
                    var cb2 = Cond(x);
                    if (cb2 != null) b.Stacks.Add(cb2);
                }
                return b.Stacks.Count == 0 ? null : b;
            }
            case OrExpr o:
            {
                var b = new Block { Opcode = "bool_or" };
                foreach (var x in o.Items)
                {
                    var cb2 = Cond(x);
                    if (cb2 != null) b.Stacks.Add(cb2);
                }
                return b.Stacks.Count == 0 ? null : b;
            }
            case NotExpr nt:
            {
                var b = new Block { Opcode = "bool_not" };
                var inner = Cond(nt.Inner);
                if (inner != null) b.Stacks.Add(inner);
                return b;
            }
            case HasKeywordExpr hk:
                return new Block { Opcode = "bool_has_kw", Text = hk.Keyword.Name, Target = Target(hk.Of) };
            case HasTraitExpr ht:
                return new Block { Opcode = "bool_has_trait", Text = ht.Trait.Name, Target = Target(ht.Of) };
            case HasCardNameExpr hcn:
                return new Block
                {
                    Opcode = "bool_has_card_name",
                    Text = hcn.CardName,
                    IntA = hcn.CardId,
                    Target = Target(hcn.Of),
                };
            case HasPackExpr hp:
                return new Block { Opcode = "bool_has_pack", Text = hp.PackId, Target = Target(hp.Of) };
            case HasClanExpr hcl:
            {
                string txt = hcl.IsSelfClan ? "self"
                    : (hcl.Clans != null && hcl.Clans.Count > 0
                        ? string.Join("|", hcl.Clans.Select(x => x.Name))
                        : hcl.Clan.Name);
                return new Block { Opcode = "bool_has_clan", Text = txt, Target = Target(hcl.Of) };
            }
            case HasStateExpr hs:
                // Of 必须存下来：它区分「本卡是否已进化」与「被筛的那张是否已进化」。
                return new Block { Opcode = "bool_has_state", EnumA = hs.State.ToString(), Target = Target(hs.Of) };
            case ExistsExpr ex:
                return new Block
                {
                    Opcode = "bool_exists", Target = Target(ex.Target), Number = Num(ex.Count),
                };
            default:
                Errors.Add("积木不支持条件：" + e.GetType().Name);
                return null;
        }
    }

    private Block Num(NumExpr e)
    {
        if (e == null) return null;
        switch (e.Op)
        {
            case NumOp.Const: return new Block { Opcode = "num_const", IntA = e.Value };
            case NumOp.RandomRange: return new Block
            {
                Opcode = "num_rand", IntA = e.Value, IntC = e.RollId,
                IntB = e.Args != null && e.Args.Count > 0 && e.Args[0].Op == NumOp.Const
                    ? e.Args[0].Value : e.Value,
            };
            case NumOp.Counter: return new Block { Opcode = "num_counter", Text = e.Counter.Name };
            case NumOp.Property:
                return new Block { Opcode = "num_prop", EnumA = e.Stat.ToString(), Target = Target(e.Of) };
            case NumOp.Count:
                return new Block { Opcode = "num_count", Target = Target(e.Of) };
            case NumOp.MaxGroupSize:
                return new Block { Opcode = "num_max_group", Target = Target(e.Of) };
            case NumOp.Arith:
            {
                if (e.Args.Count < 2) return Num(e.Args.FirstOrDefault());
                return new Block
                {
                    Opcode = "num_arith", EnumA = e.Arith.ToString(),
                    Number = Num(e.Args[0]), Number2 = Num(e.Args[1]),
                };
            }
            default:
                Errors.Add("积木不支持数值：" + e.Op);
                return null;
        }
    }
}
