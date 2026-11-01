using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Diy;

// ============================================================================
// 表单向导式 DIY —— 扁平表单 <-> CardDef 双向
//
// 定位：给完全不想理解表达式树的人用。
// 所以这里刻意**不支持**任意嵌套：一张卡 = 基础信息 + 若干条能力，
// 每条能力 = 触发时机 + 生效区域 + 可选门槛 + 若干动作行。
//
// 代价是它表达不了积木/脚本能表达的一切。这不是缺陷，是分层：
//   表单能造的卡 → 一定能转成积木与脚本（升级路径）；
//   反过来不成立，此时 TryCardDefToForm 返回 false 并说明原因，
//   让玩家「升级到积木模式」而不是让表单静默丢效果。
// ============================================================================

/// <summary>表单里可选的动作类型。</summary>
public enum FormActionKind
{
    Damage = 0,
    SplitDamage,
    Restore,
    FullRestore,
    Draw,
    Summon,
    AddToHand,
    Destroy,
    Banish,
    Discard,
    Bounce,
    Buff,
    SetStats,
    SwapStats,
    GrantKeyword,
    RemoveKeyword,
    Silence,
    Evolve,
    SuperEvolve,
    ModifyCounter,
    GainCounter,
    GainCrest,
    Transform,
    Reanimate,
}

/// <summary>数值来源：固定数字，或引擎已支持的几种统计。</summary>
public enum FormNumberSource
{
    Const = 0,
    HandCount,
    ComboCount,
    CemeteryCount,
    CounterValue,
    BoardCount,
    AttackProp,
    LifeProp,
}

/// <summary>目标筛选（扁平版 tgt_query）。</summary>
public sealed class TargetFilterForm
{
    public string Owner { get; set; } = "Self";     // Self / Opponent / Any
    public string Zone { get; set; } = "Board";     // Board / Hand / Deck / Cemetery
    public string Kind { get; set; } = "Any";       // Any / Follower / Spell / Amulet
    public string Trait { get; set; }               // 兵种（可选）
    /// <summary>选法：All / Random / Choose / First。</summary>
    public string Mode { get; set; } = "All";
    public int Count { get; set; } = 1;
    public bool ExcludeSelf { get; set; }
    /// <summary>属性区间筛选（≤，0 表示不筛）。</summary>
    public int MaxAttack { get; set; }
    public int MaxCost { get; set; }
    /// <summary>是否使用寄存器槽位（$self / $selected …）。空表示按上面的筛选。</summary>
    public string Slot { get; set; }
}

/// <summary>一行动作。</summary>
public sealed class ActionFormRow
{
    public FormActionKind Kind { get; set; } = FormActionKind.Damage;

    /// <summary>动作的目标（Draw / Silence 等无目标动作为 null）。</summary>
    public TargetFilterForm Target { get; set; }

    /// <summary>数值来源。</summary>
    public FormNumberSource NumberSource { get; set; } = FormNumberSource.Const;
    public int Number { get; set; }
    /// <summary>CounterValue / GrantKeyword / Transform / GainCrest 用的名字。</summary>
    public string Name { get; set; }

    /// <summary>Buff / SetStats 的第二个数值。</summary>
    public FormNumberSource NumberSource2 { get; set; } = FormNumberSource.Const;
    public int Number2 { get; set; }
}

/// <summary>一张表单能力卡片。</summary>
public sealed class AbilityFormItem
{
    /// <summary>触发时机（与 FormTrigger 表对应）。</summary>
    public string Trigger { get; set; } = "OnPlay";
    public string Zone { get; set; } = "Board";
    /// <summary>门槛：None / Combo / Necromancy / EarthRite / Enhance / Overflow。</summary>
    public string Gate { get; set; } = "None";
    public int GateAmount { get; set; }
    /// <summary>进化域条件（手动进化才生效）。</summary>
    public bool ManualOnly { get; set; }

    public List<ActionFormRow> Actions { get; set; } = new();
}

/// <summary>整张卡的表单。</summary>
public sealed class CardFormModel
{
    public int CardId { get; set; }
    public string NameChs { get; set; } = "";
    public string NameEng { get; set; } = "";
    public string Kind { get; set; } = "Follower";   // Follower / Spell / Amulet
    public string Clan { get; set; }
    public List<string> Traits { get; set; } = new();
    public int Cost { get; set; }
    public int Attack { get; set; }
    public int Life { get; set; }
    public List<string> Keywords { get; set; } = new();
    /// <summary>初始吟唱（0 表示无）。</summary>
    public int Countdown { get; set; }

    public List<AbilityFormItem> Abilities { get; set; } = new();
}
/// <summary>表单 -> CardDef / CardDef -> 表单。</summary>
public sealed class DiyFormCompiler
{
    private readonly GameSchema _schema;
    private readonly Lang.Lexicon _lex;
    public List<string> Errors { get; } = new();

    public DiyFormCompiler(GameSchema schema, Lang.Lexicon lexicon = null)
    {
        _schema = schema;
        _lex = lexicon;
    }

    /// <summary>
    /// 卡名 → 卡号。DoSpawn 只认 CardId、DoTransform 只认 IntoCardId，
    /// 缺了它们生成的是一张「卡面正确、运行时空操作」的卡。
    /// </summary>
    private int CardIdOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _lex == null) return 0;
        return _lex.TryGetCardId(name.Trim(), out var id) ? id : 0;
    }

    /// <summary>触发时机下拉项（UI 直接绑定）。</summary>
    public static readonly (string key, string labelChs, string labelEng)[] TriggerChoices =
    {
        ("OnPlay",             "入场曲",       "Fanfare"),
        ("OnDestroy",          "谢幕曲",       "Last Words"),
        ("OnEvolve",           "进化时",       "Evolve"),
        ("OnSuperEvolve",      "超进化时",     "Super-Evolve"),
        ("OnAttack",           "攻击时",       "Strike"),
        ("OnClash",            "交战时",       "Clash"),
        ("OnEngage",           "启动",         "Engage"),
        ("TurnStart",          "回合开始时",   "At the start of your turn"),
        ("TurnEnd",            "回合结束时",   "At the end of your turn"),
        ("OnEnterField",       "友方随从进场时", "Whenever an allied follower enters the field"),
        ("OnDraw",             "抽牌时",       "Whenever you draw"),
        ("OnSpellboost",       "魔力增幅时",   "On Spellboost"),
        ("OnFuse",             "融合时",       "Whenever you Fuse"),
        ("OnDamaged",          "受到伤害时",   "Whenever this takes damage"),
    };

    /// <summary>门槛下拉项。</summary>
    public static readonly (string key, string labelChs)[] GateChoices =
    {
        ("None", "无"),
        ("Combo", "连击 Combo(N)"),
        ("Necromancy", "唤灵 Necromancy(N)"),
        ("EarthRite", "土之秘术 Earth Rite(N)"),
        ("Enhance", "爆能强化 Enhance(N)"),
        ("Overflow", "觉醒 Overflow"),
    };

    // ---------------------------------------------------------------- 表单 → CardDef

    public CardDef FormToCardDef(CardFormModel form)
    {
        Errors.Clear();
        if (form == null) { Errors.Add("表单为空"); return null; }

        // 表单级防呆：这些是「填错了不会报错、只会造出一张废卡」的组合。
        var kind = form.Kind switch
        {
            "Spell" => CardKind.Spell,
            "Amulet" => CardKind.Amulet,
            "Crest" => CardKind.Crest,
            _ => CardKind.Follower,
        };

        if (kind == CardKind.Spell && (form.Attack != 0 || form.Life != 0))
            Errors.Add("法术卡不能填攻击力/生命值");
        if (kind == CardKind.Amulet && form.Attack != 0)
            Errors.Add("护符不能填攻击力");
        if (form.Cost < 0) Errors.Add("费用不能为负");
        if (form.Cost > 20) Errors.Add("费用上限为 20");
        if (string.IsNullOrWhiteSpace(form.NameChs)) Errors.Add("卡名不能为空");

        var abilities = new List<AbilityDef>();

        foreach (var kw in (form.Keywords ?? new List<string>())
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            abilities.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Body = new List<EffectNode>
                {
                    new ModifyKeywordNode { Target = TargetSpec.Self, Op = KeywordOp.Grant, Keyword = _schema.Keyword(kw) },
                },
            });
        }

        // 初始吟唱：与官方卡同一编码（PassiveAura + Countdown 代价）
        if (form.Countdown > 0)
        {
            abilities.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Costs = new List<CostSpec>
                {
                    new CostSpec { Kind = CostKind.Countdown, Amount = NumExpr.Const(form.Countdown) },
                },
                Body = new List<EffectNode>(),
            });
        }

        foreach (var item in form.Abilities ?? new List<AbilityFormItem>())
        {
            var ab = FormAbilityToDef(item);
            if (ab != null) abilities.Add(ab);
        }

        if (Errors.Count > 0) return null;

        return new CardDef
        {
            CardId = form.CardId,
            Name = string.IsNullOrWhiteSpace(form.NameChs) ? form.NameEng : form.NameChs,
            Kind = kind,
            BaseCost = form.Cost,
            BaseAttack = kind == CardKind.Spell ? 0 : form.Attack,
            BaseLife = kind == CardKind.Spell ? 0 : (kind == CardKind.Amulet ? 0 : form.Life),
            Abilities = abilities,
            Traits = (form.Traits ?? new List<string>()).Select(t => _schema.Trait(t)).ToList(),
            Clan = string.IsNullOrEmpty(form.Clan) ? SymbolId<ClanTag>.Invalid : _schema.Clan(form.Clan),
        };
    }

    private AbilityDef FormAbilityToDef(AbilityFormItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Trigger)) return null;

        var ev = ParseEvent(item.Trigger);
        var costs = new List<CostSpec>();

        switch (item.Gate)
        {
            case "Combo": costs.Add(Cost(CostKind.TurnPlayCount, item.GateAmount, "combo")); break;
            case "Necromancy": costs.Add(Cost(CostKind.Necromance, item.GateAmount, "necromancy")); break;
            case "EarthRite": costs.Add(Cost(CostKind.DestroyAlly, item.GateAmount, "earth_rite")); break;
            case "Enhance": costs.Add(Cost(CostKind.UsePp, item.GateAmount, "enhance")); break;
            default: break;
        }

        // 觉醒不是代价而是条件。
        BoolExpr cond = BoolExpr.True;
        // 门槛 -> 条件（与 EnglishCompiler 的 AndCosts 保持一致）
        BoolExpr? gateCond = item.Gate switch
        {
            "Combo" => CtrGe("combo", item.GateAmount),
            "Necromancy" => CtrGe("shadow", item.GateAmount),
            "EarthRite" => CtrGe("earth_sigil", item.GateAmount),
            "Enhance" => CtrGe("pp_at_play", item.GateAmount),
            _ => null,
        };
        if (gateCond != null) cond = new AndExpr(new List<BoolExpr> { cond, gateCond });

        if (item.Gate == "Overflow")
        {
            cond = new CompareExpr
            {
                Left = NumExpr.Ctr(_schema.Counter("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(7),
            };
        }

        // 进化域条件与官方卡一致（<ev>/<sev> 段都带这一条）。
        if (ev == CoreEvent.OnEvolve || ev == CoreEvent.OnSuperEvolve)
        {
            var st = ev == CoreEvent.OnSuperEvolve ? Core.UnitState.SuperEvolved : Core.UnitState.Evolved;
            cond = new AndExpr(new List<BoolExpr>
            {
                cond,
                new HasStateExpr { State = st, Of = TargetSpec.Self },
            });
        }

        var body = new List<EffectNode>();
        foreach (var row in item.Actions ?? new List<ActionFormRow>())
        {
            // 表单里「选 1 个敌方随从并打它」是一行（玩家理解的单位），
            // 而 AST 里是两条节点：先 Select 存进 $selected，后续动作引用槽位。
            // 少展开这一步，四端产物就不一致 —— 而卡面中文读起来几乎一样，
            // 正是「看起来一样、实际不同」的典型。
            if (row?.Target != null && row.Target.Mode == "Choose" && string.IsNullOrEmpty(row.Target.Slot))
            {
                body.Add(new SelectTargetNode
                {
                    Target = TargetOf(row.Target),
                    OutSlot = TargetSpec.SlotSelected,
                });
                var row2 = new ActionFormRow
                {
                    Kind = row.Kind,
                    Target = new TargetFilterForm { Slot = TargetSpec.SlotSelected },
                    NumberSource = row.NumberSource, Number = row.Number, Name = row.Name,
                    NumberSource2 = row.NumberSource2, Number2 = row.Number2,
                };
                var n2 = RowToNode(row2);
                if (n2 != null) body.Add(n2);
                continue;
            }

            var n = RowToNode(row);
            if (n != null) body.Add(n);
        }

        return new AbilityDef
        {
            Trigger = new TriggerSpec
            {
                Event = ev,
                ManualEvolveOnly = item.ManualOnly
                                   || ev == CoreEvent.OnEvolve
                                   || ev == CoreEvent.OnSuperEvolve,
                ActiveZone = item.Zone switch
                {
                    "Hand" => ActiveZoneScope.Hand,
                    "Deck" => ActiveZoneScope.Deck,
                    _ => ActiveZoneScope.Board,
                },
            },
            Condition = cond,
            Costs = costs,
            Body = body,
        };
    }

    /// <summary>
    /// 造一个代价。Label 必须填：TurnRunner.EnhanceTiers 只认 Label=="enhance"，
    /// 留空的话用表单造的爆能强化卡永远只扣基础费。
    /// </summary>
    /// <summary>计数器 >= N 的条件。</summary>
    private BoolExpr CtrGe(string counter, int n)
        => new CompareExpr
           {
               Left = NumExpr.Ctr(_schema.Counter(counter)),
               Op = CompareOp.Ge,
               Right = NumExpr.Const(Math.Max(0, n)),
           };

    private static CostSpec Cost(CostKind k, int amt, string label)
        => new CostSpec { Kind = k, Amount = NumExpr.Const(Math.Max(0, amt)), Label = label };

    private EffectNode RowToNode(ActionFormRow row)
    {
        if (row == null) return null;

        var tgt = TargetOf(row.Target);
        var num = NumberOf(row.NumberSource, row.Number, row.Name);
        var num2 = NumberOf(row.NumberSource2, row.Number2, row.Name);

        switch (row.Kind)
        {
            case FormActionKind.Damage:
                return new DealDamageNode { Target = tgt, Amount = num, IsAbility = true };
            case FormActionKind.SplitDamage:
                return new DealDamageNode { Target = tgt, Amount = num, IsAbility = true, IsSplit = true };
            case FormActionKind.Restore:
                return new RestoreLifeNode { Target = tgt, Amount = num };
            case FormActionKind.FullRestore:
                return new RestoreLifeNode { Target = tgt, FullRestore = true };

            case FormActionKind.Draw:
                return new MoveZoneNode
                {
                    Target = new TargetSpec
                    {
                        SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                        Zone = ZoneRef.Deck, Mode = SelectMode.First, Count = num,
                    },
                    ToZone = ZoneRef.Hand,
                };

            case FormActionKind.Summon:
                return new SpawnCardNode
                {
                    CardId = CardIdOf(row.Name), CardName = row.Name ?? "",
                    Count = num, ToZone = ZoneRef.Board,
                };
            case FormActionKind.AddToHand:
                return new SpawnCardNode
                {
                    CardId = CardIdOf(row.Name), CardName = row.Name ?? "",
                    Count = num, ToZone = ZoneRef.Hand,
                };
            case FormActionKind.GainCrest:
                return new SpawnCardNode
                {
                    IsCrest = true,
                    // 纹章是独立实体，不带主人卡号；落点是纹章区，不是战场。
                    CardId = 0, CardName = row.Name ?? "",
                    ToZone = ZoneRef.CrestArea, ToOwner = ZoneScope.Self,
                };

            case FormActionKind.Destroy:
                return new MoveZoneNode { Target = tgt, ToZone = ZoneRef.Cemetery, IsDestroy = true };
            case FormActionKind.Banish:
                return new MoveZoneNode { Target = tgt, ToZone = ZoneRef.Banished };
            case FormActionKind.Discard:
                return new MoveZoneNode { Target = tgt, ToZone = ZoneRef.Cemetery };
            case FormActionKind.Bounce:
                return new MoveZoneNode { Target = tgt, ToZone = ZoneRef.Hand };

            case FormActionKind.Buff:
                return new ModifyStatsNode { Target = tgt, Op = StatModOp.Add, Attack = num, Life = num2 };
            case FormActionKind.SetStats:
                return new ModifyStatsNode { Target = tgt, Op = StatModOp.Set, Attack = num, Life = num2 };
            case FormActionKind.SwapStats:
                return new ModifyStatsNode { Target = tgt, Op = StatModOp.Swap };

            case FormActionKind.GrantKeyword:
                return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Grant, Keyword = _schema.Keyword(row.Name ?? "") };
            case FormActionKind.RemoveKeyword:
                return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.Remove, Keyword = _schema.Keyword(row.Name ?? "") };
            case FormActionKind.Silence:
                return new ModifyKeywordNode { Target = tgt, Op = KeywordOp.SilenceAll };

            case FormActionKind.Evolve:
                return new EvolveNode { Target = tgt };
            case FormActionKind.SuperEvolve:
                return new EvolveNode { Target = tgt, IsSuperEvolve = true };

            case FormActionKind.GainCounter:
                return new ModifyCounterNode
                {
                    Target = null, Owner = ZoneScope.Self,
                    Counter = _schema.Counter(row.Name ?? ""), Op = CounterOp.Add, Delta = num,
                };
            case FormActionKind.ModifyCounter:
            {
                var d = num;
                if (row.Number < 0) d = NumExpr.Sub(NumExpr.Const(0), NumExpr.Const(-row.Number));
                return new ModifyCounterNode
                {
                    Target = tgt, Owner = ZoneScope.Self,
                    Counter = _schema.Counter(row.Name ?? ""), Op = CounterOp.Add, Delta = d,
                };
            }

            case FormActionKind.Transform:
                // DoTransform 只认 IntoCardId：没有它变身是彻底的空操作。
                return new TransformNode
                {
                    Target = tgt,
                    IntoCardId = CardIdOf(row.Name), IntoCardName = row.Name ?? "",
                };
            case FormActionKind.Reanimate:
                // 用唯一标准构造：手写会漏掉 SourceKind=CopyOfTarget 与 BaseCost 上限，
                // 漏了就是召出一个 CardId=0 的白壳，且费用筛选完全失效。
                return ReanimateHelper.Build(num);

            default:
                Errors.Add("未知动作：" + row.Kind);
                return null;
        }
    }

    private TargetSpec TargetOf(TargetFilterForm f)
    {
        if (f == null) return TargetSpec.Self;
        if (!string.IsNullOrEmpty(f.Slot))
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = f.Slot };

        var parts = new List<BoolExpr>();
        if (f.Kind != null && f.Kind != "Any")
        {
            parts.Add(new HasKindExpr
            {
                Kind = f.Kind switch
                {
                    "Spell" => CardKind.Spell,
                    "Amulet" => CardKind.Amulet,
                    _ => CardKind.Follower,
                },
                Of = TargetSpec.Item,
            });
        }
        if (!string.IsNullOrEmpty(f.Trait))
            parts.Add(new HasTraitExpr { Trait = _schema.Trait(f.Trait), Of = TargetSpec.Item });
        if (f.MaxAttack > 0)
            parts.Add(new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Item),
                Op = CompareOp.Le,
                Right = NumExpr.Const(f.MaxAttack),
            });
        if (f.MaxCost > 0)
            parts.Add(new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                Op = CompareOp.Le,
                Right = NumExpr.Const(f.MaxCost),
            });
        if (f.ExcludeSelf)
            parts.Add(new NotSameEntityExpr
            {
                ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf, Of = TargetSpec.Item,
            });

        var mode = f.Mode switch
        {
            "Random" => SelectMode.Random,
            "Choose" => SelectMode.Choose,
            "First" => SelectMode.First,
            _ => SelectMode.All,
        };

        return new TargetSpec
        {
            SourceKind = TargetSourceKind.Zone,
            Owner = f.Owner switch
            {
                "Opponent" => ZoneScope.Opponent,
                "Any" => ZoneScope.Any,
                _ => ZoneScope.Self,
            },
            Zone = f.Zone switch
            {
                "Hand" => ZoneRef.Hand,
                "Deck" => ZoneRef.Deck,
                "Cemetery" => ZoneRef.Cemetery,
                "Leader" => ZoneRef.Leader,
                _ => ZoneRef.Board,
            },
            Mode = mode,
            Count = mode == SelectMode.All ? null : NumExpr.Const(Math.Max(1, f.Count)),
            Where = parts.Count == 0 ? BoolExpr.True
                  : parts.Count == 1 ? parts[0]
                  : new AndExpr(parts),
        };
    }

    private NumExpr NumberOf(FormNumberSource src, int v, string name)
    {
        switch (src)
        {
            case FormNumberSource.HandCount:
                return NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                    Zone = ZoneRef.Hand, Mode = SelectMode.All,
                });
            case FormNumberSource.ComboCount: return NumExpr.Ctr(_schema.Counter("combo"));
            case FormNumberSource.CemeteryCount:
                return NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                    Zone = ZoneRef.Cemetery, Mode = SelectMode.All,
                });
            case FormNumberSource.BoardCount:
                return NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                    Zone = ZoneRef.Board, Mode = SelectMode.All,
                });
            case FormNumberSource.CounterValue:
                return NumExpr.Ctr(_schema.Counter(string.IsNullOrEmpty(name) ? "mode_count" : name));
            case FormNumberSource.AttackProp: return NumExpr.Prop(StatKey.Attack, TargetSpec.Item);
            case FormNumberSource.LifeProp: return NumExpr.Prop(StatKey.Life, TargetSpec.Item);
            default: return NumExpr.Const(v);
        }
    }

    private static CoreEvent ParseEvent(string s) => s switch
    {
        "OnPlay" => CoreEvent.OnPlay,
        "OnDestroy" => CoreEvent.OnDestroy,
        "OnEvolve" => CoreEvent.OnEvolve,
        "OnSuperEvolve" => CoreEvent.OnSuperEvolve,
        "OnAttack" => CoreEvent.OnAttack,
        "OnClash" => CoreEvent.OnClash,
        "OnEngage" => CoreEvent.OnEngage,
        "TurnStart" => CoreEvent.TurnStart,
        "TurnEnd" => CoreEvent.TurnEnd,
        "OnEnterField" => CoreEvent.OnEnterField,
        "OnDraw" => CoreEvent.OnDraw,
        "OnSpellboost" => CoreEvent.OnSpellboost,
        "OnFuse" => CoreEvent.OnFuse,
        "OnDamaged" => CoreEvent.OnDamaged,
        _ => CoreEvent.OnPlay,
    };

    // ---------------------------------------------------------------- CardDef → 表单

    /// <summary>
    /// 把 CardDef 反填回表单。
    ///
    /// 返回 false 表示这张卡超出了表单的表达能力（例如模式分支、嵌套循环）。
    /// 这时候**不能**返回一个「少了几个效果」的表单 —— 那会让玩家
    /// 在表单里存一次，把复杂卡悄悄降级成简单卡，而界面上看不出任何异常。
    /// </summary>
    public bool TryCardDefToForm(CardDef def, out CardFormModel form, out string reason)
    {
        form = null;
        reason = "";
        if (def == null) { reason = "CardDef 为空"; return false; }

        // 含激奏/结晶/纹章/信仰/融合的卡超出表单表达能力。
        // 静默丢弃这些子结构还返回 true，会让玩家在表单里存一次就
        // 把整条融合链/结晶效果删掉，而界面上看不出任何异常。
        if (def.AccelerateDef != null || def.CrystallizeDef != null
            || def.CrestDef != null || def.FaithDef != null || def.HasFuse)
        {
            reason = "该卡含激奏/结晶/纹章/信仰/融合，超出表单表达能力（请改用积木或脚本模式）";
            return false;
        }

        var f = new CardFormModel
        {
            CardId = def.CardId,
            NameChs = def.Name ?? "",
            NameEng = def.Name ?? "",
            Kind = def.Kind.ToString(),
            Clan = def.Clan.IsValid ? def.Clan.Name : null,
            Traits = (def.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(t => t.Name).ToList(),
            Cost = def.BaseCost,
            Attack = def.BaseAttack,
            Life = def.BaseLife,
        };

        foreach (var ab in def.Abilities ?? Array.Empty<AbilityDef>())
        {
            // 裸关键词 → 勾选项
            if (ab?.Trigger?.Event == CoreEvent.PassiveAura
                && (ab.Costs == null || ab.Costs.Count == 0)
                && ab.Body != null && ab.Body.Count == 1
                && ab.Body[0] is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant)
            {
                f.Keywords.Add(mk.Keyword.Name);
                continue;
            }

            // 初始吟唱
            if (ab?.Trigger?.Event == CoreEvent.PassiveAura
                && ab.Costs != null && ab.Costs.Count == 1
                && ab.Costs[0].Kind == CostKind.Countdown
                && (ab.Body == null || ab.Body.Count == 0))
            {
                f.Countdown = ab.Costs[0].Amount != null && ab.Costs[0].Amount.Op == NumOp.Const
                    ? ab.Costs[0].Amount.Value : 0;
                continue;
            }

            var item = DefToFormAbility(ab, out var why);
            if (item == null) { reason = why; return false; }
            f.Abilities.Add(item);
        }

        form = f;
        return true;
    }

    private AbilityFormItem DefToFormAbility(AbilityDef ab, out string reason)
    {
        reason = "";
        if (ab?.Trigger == null) { reason = "能力缺少触发规格"; return null; }

        // 表单的触发时机只有一个下拉框，装不下「回合归属」「并列事件」
        // 「每回合次数」「主体过滤」这些限定词。
        // 静默丢掉它们会让 Ghost 的「我方回合结束时」变成「回合结束时」，
        // 卡面中文看不出差别，实战里双方回合都触发。
        if (ab.Trigger.DuringTurn != TurnOwnerScope.Any
            || ab.Trigger.SecondaryEvent != null
            || ab.Trigger.MaxTriggersPerTurn > 0
            || ab.Trigger.SubjectFilter != null)
        {
            reason = "该能力带回合归属/并列事件/每回合次数/主体过滤，超出表单表达能力"
                     + "（请改用积木或脚本模式）";
            return null;
        }

        var item = new AbilityFormItem
        {
            Trigger = ab.Trigger.Event.ToString(),
            Zone = ab.Trigger.ActiveZone.ToString(),
            ManualOnly = ab.Trigger.ManualEvolveOnly,
        };

        // 代价 → 门槛
        foreach (var c in ab.Costs ?? Array.Empty<CostSpec>())
        {
            int amt = c.Amount != null && c.Amount.Op == NumOp.Const ? c.Amount.Value : 0;
            switch (c.Kind)
            {
                case CostKind.TurnPlayCount: item.Gate = "Combo"; item.GateAmount = amt; break;
                case CostKind.Necromance: item.Gate = "Necromancy"; item.GateAmount = amt; break;
                case CostKind.DestroyAlly: item.Gate = "EarthRite"; item.GateAmount = amt; break;
                case CostKind.UsePp: item.Gate = "Enhance"; item.GateAmount = amt; break;
                default: break;
            }
        }

        // 条件：只接受空的 / 恒真 / 觉醒（overflows）/ 进化域
        if (ab.Condition != null)
        {
            var cleaned = StripFormCondition(ab.Condition, ab.Trigger.Event, item.Gate);
            if (cleaned != null)
            {
                // 觉醒：max_pp >= N
                if (cleaned is CompareExpr cmp
                    && cmp.Left?.Op == NumOp.Counter && cmp.Left.Counter.Name == "max_pp"
                    && cmp.Right?.Op == NumOp.Const)
                {
                    item.Gate = "Overflow";
                }
                else
                {
                    reason = "该能力的条件超出表单表达能力：" + cleaned.GetType().Name
                             + "（请改用积木或脚本模式）";
                    return null;
                }
            }
        }

        // 表单把「选 1 个敌方随从并打它」看成一行，而 AST 是两条节点
        // （Select 存进 $selected + 后续动作引用槽位）。
        // 反填时必须把这对合并回一行，否则表单永远认不出自己造的卡 ——
        // 「四端同构」就成了「三端能升级、表单不能」。
        var bodyList = ab.Body ?? Array.Empty<EffectNode>();
        for (int i = 0; i < bodyList.Count; i++)
        {
            var n = bodyList[i];

            if (n is SelectTargetNode st && i + 1 < bodyList.Count
                && TargetUsesSlot(bodyList[i + 1], st.OutSlot))
            {
                var merged = NodeToRow(bodyList[i + 1], out var why2);
                if (merged == null) { reason = why2; return null; }
                merged.Target = Filter(st.Target);
                // 选法要保留原始 Mode（Choose / Random / All），
                // 否则「选 1 个」会被表单默认成「所有」。
                if (merged.Target != null) merged.Target.Mode = st.Target?.Mode.ToString() ?? "Choose";
                item.Actions.Add(merged);
                i++;                                  // 跳过被合并掉的那条
                continue;
            }

            var row = NodeToRow(n, out var why);
            if (row == null) { reason = why; return null; }
            item.Actions.Add(row);
        }

        return item;
    }

    /// <summary>剥掉恒真项与进化域条件；剩空返回 null。</summary>
    private static BoolExpr StripFormCondition(BoolExpr e, CoreEvent ev, string gate = null)
    {
        // 门槛对应的计数器名。表单正向生成的条件必须在这里剥掉 ——
        // 否则 TryCardDefToForm 会把残留的 CompareExpr 判成「超出表达能力」，
        // 于是表单自己造出来的带连击/唤灵/土之秘术/爆能强化的卡，连表单自己都读不回来。
        string gateCtr = gate switch
        {
            "Combo" => "combo",
            "Necromancy" => "shadow",
            "EarthRite" => "earth_sigil",
            "Enhance" => "pp_at_play",
            _ => null,
        };

        var items = e is AndExpr a ? a.Items.ToList() : new List<BoolExpr> { e };
        var kept = new List<BoolExpr>();
        foreach (var x in items)
        {
            if (x is ConstBoolExpr cb && cb.Value) continue;
            bool isEvolveDomain = x is HasStateExpr hs
                && ((ev == CoreEvent.OnEvolve && hs.State == Core.UnitState.Evolved)
                    || (ev == CoreEvent.OnSuperEvolve && hs.State == Core.UnitState.SuperEvolved));
            if (isEvolveDomain) continue;
            if (gateCtr != null && x is CompareExpr gcmp
                && gcmp.Left != null && gcmp.Left.Op == NumOp.Counter
                && string.Equals(gcmp.Left.Counter.Name, gateCtr, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(x);
        }
        if (kept.Count == 0) return null;
        if (kept.Count == 1) return kept[0];
        return new AndExpr(kept);
    }

    private ActionFormRow NodeToRow(EffectNode n, out string reason)
    {
        reason = "";
        switch (n)
        {
            case DealDamageNode d:
                return Row(d.IsSplit ? FormActionKind.SplitDamage : FormActionKind.Damage,
                           d.Target, d.Amount, null, out reason);

            case RestoreLifeNode r:
                if (r.FullRestore)
                    return new ActionFormRow { Kind = FormActionKind.FullRestore, Target = Filter(r.Target) };
                return Row(FormActionKind.Restore, r.Target, r.Amount, null, out reason);

            case MoveZoneNode m:
            {
                if (m.ToZone == ZoneRef.Hand && m.Target?.Zone == ZoneRef.Deck && m.Target.Mode == SelectMode.First)
                {
                    var row = new ActionFormRow { Kind = FormActionKind.Draw };
                    if (!SrcOf(m.Target.Count, out var s, out var v, out var cn, out reason)) return null;
                    row.NumberSource = s; row.Number = v; if (cn != null) row.Name = cn;
                    return row;
                }
                var k = m.ToZone switch
                {
                    ZoneRef.Cemetery => m.IsDestroy ? FormActionKind.Destroy : FormActionKind.Discard,
                    ZoneRef.Banished => FormActionKind.Banish,
                    ZoneRef.Hand => FormActionKind.Bounce,
                    _ => (FormActionKind)(-1),
                };
                if ((int)k < 0) { reason = "不支持的移区：" + m.ToZone; return null; }
                return new ActionFormRow { Kind = k, Target = Filter(m.Target) };
            }

            case SpawnCardNode sp:
            {
                if (ReanimateHelper.IsReanimate(sp))
                {
                    // 费用上限在 CopyFrom.Where 里，Count 恒为 1 —— 拿 Count 顶替会把
                    // Reanimate(8) 变成 Reanimate(1)。
                    var reRow = new ActionFormRow { Kind = FormActionKind.Reanimate };
                    if (ReanimateHelper.TryExtractCostCap(sp, out var cap)
                        && SrcOf(cap, out var rs, out var rv, out var rcn, out reason))
                    {
                        reRow.NumberSource = rs; reRow.Number = rv; if (rcn != null) reRow.Name = rcn;
                    }
                    return reRow;
                }
                var row = new ActionFormRow
                {
                    Kind = sp.IsCrest ? FormActionKind.GainCrest
                         : sp.ToZone == ZoneRef.Hand ? FormActionKind.AddToHand : FormActionKind.Summon,
                    Name = sp.CardName,
                };
                if (!SrcOf(sp.Count, out var s, out var v, out var cn, out reason)) return null;
                row.NumberSource = s; row.Number = v; if (cn != null) row.Name = cn;
                return row;
            }

            case ModifyStatsNode ms:
            {
                var k = ms.Op switch
                {
                    StatModOp.Set => FormActionKind.SetStats,
                    StatModOp.Swap => FormActionKind.SwapStats,
                    _ => FormActionKind.Buff,
                };
                var row = new ActionFormRow { Kind = k, Target = Filter(ms.Target) };
                if (ms.Attack != null)
                {
                    if (!SrcOf(ms.Attack, out var s1, out var v1, out var cn1, out reason)) return null;
                    row.NumberSource = s1; row.Number = v1; if (cn1 != null) row.Name = cn1;
                }
                if (ms.Life != null)
                {
                    if (!SrcOf(ms.Life, out var s2, out var v2, out var cn2, out reason)) return null;
                    row.NumberSource2 = s2; row.Number2 = v2; if (cn2 != null) row.Name = cn2;
                }
                return row;
            }

            case ModifyKeywordNode mw:
            {
                var k = mw.Op switch
                {
                    KeywordOp.Remove => FormActionKind.RemoveKeyword,
                    KeywordOp.SilenceAll => FormActionKind.Silence,
                    _ => FormActionKind.GrantKeyword,
                };
                return new ActionFormRow { Kind = k, Target = Filter(mw.Target), Name = mw.Keyword.Name };
            }

            case EvolveNode e:
                return new ActionFormRow
                {
                    Kind = e.IsSuperEvolve ? FormActionKind.SuperEvolve : FormActionKind.Evolve,
                    Target = Filter(e.Target),
                };

            case ModifyCounterNode mc:
            {
                bool gain = mc.Target == null;
                var row = new ActionFormRow
                {
                    Kind = gain ? FormActionKind.GainCounter : FormActionKind.ModifyCounter,
                    Target = mc.Target != null ? Filter(mc.Target) : null,
                    Name = mc.Counter.Name,
                };
                if (!SrcOf(mc.Delta, out var s, out var v, out var cn, out reason)) return null;
                row.NumberSource = s; row.Number = v; if (cn != null) row.Name = cn;
                return row;
            }

            case TransformNode t:
                return new ActionFormRow
                {
                    Kind = FormActionKind.Transform, Target = Filter(t.Target), Name = t.IntoCardName,
                };

            default:
                reason = "该动作超出表单表达能力：" + n?.GetType().Name + "（请改用积木或脚本模式）";
                return null;
        }
    }

    private ActionFormRow Row(FormActionKind kind, TargetSpec t, NumExpr num, string name, out string reason)
    {
        reason = "";
        var row = new ActionFormRow { Kind = kind, Target = Filter(t), Name = name };
        if (num != null)
        {
            if (!SrcOf(num, out var s, out var v, out var cn, out reason)) return null;
            row.NumberSource = s; row.Number = v; if (cn != null) row.Name = cn;
        }
        return row;
    }

    /// <summary>数值表达式 → 表单的「数值来源」。</summary>
    /// <summary>
    /// 数值表达式 → 表单的「数值来源」。
    ///
    /// counterName 必须回传：表单里「数值来源 = 自定义计数器」时，
    /// 具体是哪个计数器由 Name 承载。不回传就会退化成 mode_count，
    /// 于是玩家造的卡读的是别人的计数器。
    /// </summary>
    private bool SrcOf(NumExpr e, out FormNumberSource src, out int val, out string counterName, out string reason)
    {
        src = FormNumberSource.Const;
        val = 0;
        counterName = null;
        reason = "";
        if (e == null) return true;

        if (e.Op == NumOp.Const) { val = e.Value; return true; }

        // 「0 - N」是取负的编码方式（英文编译器与表单正向都这么生成）。
        // 不认它，任何「减 N」的数值反填都会报「超出表单表达能力」。
        if (e.Op == NumOp.Arith && e.Arith == ArithOp.Sub && e.Args.Count == 2
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0
            && e.Args[1].Op == NumOp.Const)
        {
            src = FormNumberSource.Const;
            val = -e.Args[1].Value;
            return true;
        }
        if (e.Op == NumOp.Counter)
        {
            if (e.Counter.Name == "combo") { src = FormNumberSource.ComboCount; return true; }
            src = FormNumberSource.CounterValue;
            counterName = e.Counter.Name;
            return true;
        }
        if (e.Op == NumOp.Property)
        {
            src = e.Stat == StatKey.Life ? FormNumberSource.LifeProp : FormNumberSource.AttackProp;
            return true;
        }
        if (e.Op == NumOp.Count)
        {
            var t = e.Of;
            if (t != null && t.SourceKind == TargetSourceKind.Zone && t.Owner == ZoneScope.Self)
            {
                if (t.Zone == ZoneRef.Hand) { src = FormNumberSource.HandCount; return true; }
                if (t.Zone == ZoneRef.Cemetery) { src = FormNumberSource.CemeteryCount; return true; }
                if (t.Zone == ZoneRef.Board) { src = FormNumberSource.BoardCount; return true; }
            }
        }

        reason = "该数值来源超出表单表达能力（请改用积木或脚本模式）";
        return false;
    }

    /// <summary>该节点的主目标是否引用了指定槽位。</summary>
    private static bool TargetUsesSlot(EffectNode n, string slot)
    {
        var t = n switch
        {
            DealDamageNode d => d.Target,
            RestoreLifeNode r => r.Target,
            MoveZoneNode m => m.Target,
            ModifyStatsNode ms => ms.Target,
            ModifyKeywordNode mk => mk.Target,
            EvolveNode e => e.Target,
            TransformNode tn => tn.Target,
            _ => null,
        };
        return t != null && t.SourceKind == TargetSourceKind.Slot && t.Slot == slot;
    }

    private TargetFilterForm Filter(TargetSpec t)
    {
        if (t == null) return null;

        if (t.SourceKind == TargetSourceKind.Slot)
            return new TargetFilterForm { Slot = t.Slot };

        var f = new TargetFilterForm
        {
            Owner = t.Owner.ToString(),
            Zone = t.Zone.ToString(),
            Kind = "Any",
            Mode = t.Mode.ToString(),
            Count = t.Count != null && t.Count.Op == NumOp.Const ? t.Count.Value : 1,
        };

        var items = t.Where is AndExpr a ? a.Items.ToList()
                    : t.Where == null ? new List<BoolExpr>()
                    : new List<BoolExpr> { t.Where };

        foreach (var x in items)
        {
            switch (x)
            {
                case HasKindExpr hk:
                    f.Kind = hk.Kind switch
                    {
                        CardKind.Follower => "Follower",
                        CardKind.Spell => "Spell",
                        CardKind.Amulet => "Amulet",
                        _ => "Any",
                    };
                    break;
                case HasTraitExpr ht: f.Trait = ht.Trait.Name; break;
                case NotSameEntityExpr: f.ExcludeSelf = true; break;
                case CompareExpr cmp when cmp.Left?.Op == NumOp.Property && cmp.Right?.Op == NumOp.Const:
                    if (cmp.Left.Stat == StatKey.Attack) f.MaxAttack = cmp.Right.Value;
                    if (cmp.Left.Stat == StatKey.Cost) f.MaxCost = cmp.Right.Value;
                    break;
                default: break;
            }
        }
        return f;
    }
}
