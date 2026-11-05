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


/// <summary>整张卡的表单。</summary>
/// <summary>表单里的一行构筑期修正。</summary>
public sealed class DeckModRow
{
    /// <summary>size / leader_life / hand / ep / sep / self_copy / allow_clan_fixed / allow_clan_choose</summary>
    public string Kind { get; set; } = "size";
    public int Value { get; set; }
    /// <summary>allow_clan_fixed 时的职业名；其余种类可留空。</summary>
    public string Arg { get; set; } = "";
    /// <summary>互斥组：同组只生效一次。</summary>
    public string ExclusiveGroup { get; set; } = "";
    public bool StackPerCopy { get; set; }
}

public sealed class CardFormModel
{
    /// <summary>
    /// 构筑期修正（对局中不生效）。表单三行：种类 / 数值 / 备注（互斥组或职业）。
    /// </summary>
    public List<DeckModRow> DeckModRows { get; set; } = new();
    public int CardId { get; set; }
    public string NameChs { get; set; } = "";
    public string NameEng { get; set; } = "";
    public string Kind { get; set; } = "Follower";   // Follower / Spell / Amulet
    public string Clan { get; set; }
    public List<string> Traits { get; set; } = new();
    public int Cost { get; set; }
    public int Attack { get; set; }
    public int Life { get; set; }
    /// <summary>
    /// 关键词列表。UI 用一个输入框收集，玩家按英文逗号分隔（Ward, Storm, Rush）。
    /// </summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>初始吟唱（0 表示无）。</summary>
    public int Countdown { get; set; }
    /// <summary>稀有度（0 = 未指定）。卡面框色与分解价值用，不影响对局。</summary>
    public int Rarity { get; set; }

    // 注意：表单**不含** Abilities。
    // 基础信息（卡名/费用/身材/职业/稀有度/关键词）只在这里填，
    // 效果交给积木 / AST 规范语法 / 官方英文三端。
    // 表单编不了复杂效果，硬塞进去势必超纲并静默丢动作。
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

        // 关键词是卡牌定义属性，写进 InnateKeywords，不生成 PassiveAura 能力 ——
        // 与 EnglishCompiler 同口径（那边也不再把裸关键词编成能力）。
        // 生成能力会让它在 Abilities 里多存一份，卡面还会多出一条空的 aura: 段。
        // 关键词分两种编码，与 EnglishCompiler / DiyBlockCompiler 同口径：
        //   无参（Ward …）           -> CardDef.InnateKeywords
        //   带参（max_attacks_per_turn(2)）-> PassiveAura + ModifyKeywordNode(Value=N)
        // InnateKeywords 是 List<SymbolId>，存不下数值；一律塞进去等于把
        // 「可攻击 2 次」降级成「可攻击 1 次」，而且卡面看不出区别。
        var innateKeywords = new List<SymbolId<KeywordTag>>();
        foreach (var rawKw in (form.Keywords ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            string kwName = rawKw.Trim();
            NumExpr kwVal = null;
            int lp = kwName.IndexOf('(');
            if (lp > 0 && kwName.EndsWith(")"))
            {
                if (int.TryParse(kwName.Substring(lp + 1, kwName.Length - lp - 2), out int n))
                    kwVal = NumExpr.Const(n);
                kwName = kwName.Substring(0, lp).Trim();
            }

            var kwSym = _schema.Keyword(kwName);
            if (!kwSym.IsValid) continue;

            if (kwVal == null)
            {
                if (!innateKeywords.Contains(kwSym)) innateKeywords.Add(kwSym);
                continue;
            }

            abilities.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Condition = BoolExpr.True,
                Body = new List<EffectNode>
                {
                    new ModifyKeywordNode
                    {
                        Target = TargetSpec.Self, Op = KeywordOp.Grant,
                        Keyword = kwSym, Value = kwVal,
                    },
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

        // 表单不编效果：效果由积木 / AST 规范语法 / 官方英文三端负责。

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
            InnateKeywords = innateKeywords,
            Rarity = form.Rarity,
            Traits = (form.Traits ?? new List<string>()).Select(t => _schema.Trait(t)).ToList(),
            Clan = string.IsNullOrEmpty(form.Clan) ? SymbolId<ClanTag>.Invalid : _schema.Clan(form.Clan),
            // 构筑期修正：对局中不生效，单独成一组，不进 Abilities。
            DeckModifiers = (form.DeckModRows ?? new List<DeckModRow>())
                .Where(r => r != null)
                .Select(RowToDeckMod)
                .Where(m => m != null)
                .ToList(),
        };
    }

    /// <summary>构筑修正行 -> DeckBuildModifier。</summary>
    private DeckBuildModifier RowToDeckMod(DeckModRow r)
    {
        if (r == null) return null;
        var kind = r.Kind switch
        {
            "size" => DeckModKind.ModifyDeckSize,
            "leader_life" => DeckModKind.ModifyLeaderStartLife,
            "hand" => DeckModKind.ModifyStartingHandSize,
            "ep" or "sep" => DeckModKind.ModifyStartEvolvePoints,
            "self_copy" => DeckModKind.OverrideSelfCopyLimit,
            "allow_clan_fixed" => DeckModKind.AllowSpecificClan,
            "allow_clan_choose" => DeckModKind.AllowChosenClans,
            _ => DeckModKind.ModifyDeckSize,
        };
        return new DeckBuildModifier
        {
            Kind = kind,
            Value = r.Value,
            TargetClan = r.Arg ?? "",
            ExclusiveGroup = r.ExclusiveGroup ?? "",
            StackPerCopy = r.StackPerCopy,
        };
    }

    /// <summary>DeckBuildModifier -> 构筑修正行。</summary>
    private static DeckModRow DeckModToRow(DeckBuildModifier m)
    {
        if (m == null) return null;
        return new DeckModRow
        {
            Kind = m.Kind switch
            {
                DeckModKind.ModifyDeckSize => "size",
                DeckModKind.ModifyLeaderStartLife => "leader_life",
                DeckModKind.ModifyStartingHandSize => "hand",
                DeckModKind.ModifyStartEvolvePoints => "ep",
                DeckModKind.OverrideSelfCopyLimit => "self_copy",
                DeckModKind.AllowSpecificClan => "allow_clan_fixed",
                DeckModKind.AllowChosenClans => "allow_clan_choose",
                _ => "size",
            },
            Value = m.Value,
            Arg = m.TargetClan ?? "",
            ExclusiveGroup = m.ExclusiveGroup ?? "",
            StackPerCopy = m.StackPerCopy,
        };
    }

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
            // 构筑期修正反译成表单行。不处理的话，一张带扩容的卡
            // 用表单读回再存，扩容效果就悄悄没了 —— 而卡面中文看不出区别。
            DeckModRows = (def.DeckModifiers ?? new List<DeckBuildModifier>())
                .Where(m => m != null)
                .Select(DeckModToRow)
                .Where(r => r != null)
                .ToList(),
        };

        // InnateKeywords 是卡牌定义属性，不在 Abilities 里。
        // 不拷过来，一张只有常驻关键词的卡切到表单端会显示成「零关键词」，
        // 玩家再存一次就把关键词全弄丢了 —— 而卡面中文看不出区别。
        foreach (var ik in def.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
            if (ik.IsValid && !string.IsNullOrWhiteSpace(ik.Name) && !f.Keywords.Contains(ik.Name))
                f.Keywords.Add(ik.Name);

        foreach (var ab in def.Abilities ?? Array.Empty<AbilityDef>())
        {
            // 关键词 → 勾选项。带参的要把 (N) 一起带上，否则读回后次数没了。
            if (ab?.Trigger?.Event == CoreEvent.PassiveAura
                && (ab.Costs == null || ab.Costs.Count == 0)
                && ab.Body != null && ab.Body.Count == 1
                && ab.Body[0] is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant)
            {
                string kwEntry = mk.Value is NumExpr { Op: NumOp.Const } kv
                    ? mk.Keyword.Name + "(" + kv.Value + ")"
                    : mk.Keyword.Name;
                if (!f.Keywords.Contains(kwEntry)) f.Keywords.Add(kwEntry);
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

            // 除关键词与吟唱外的任何能力，表单都表达不了 ——
            // 表单只负责基础信息，效果一律交给效果端。
            reason = "该卡含效果能力，表单只处理基础信息；请在积木 / AST / 英文端编辑效果";
            return false;
        }

        form = f;
        return true;
    }
}