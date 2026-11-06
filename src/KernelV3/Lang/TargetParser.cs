using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 目标短语解析器
//
// 英语名词短语的槽位顺序（语言学事实）：
//   [量词] [随机] [其他] [归属] [状态] [兵种/职业] 核心名词 [区域] [后置定语]
//
// 例：all random other allied evolved Ward followers on the field with 3 defense or less
//      量词 随机  其他  归属   状态  兵种   核心      区域        后置定语
//
// 全程只碰 TokenCursor，不碰原文字符串。
// ============================================================================

public sealed class TargetParser
{
    private readonly Lexicon _lex;

    /// <summary>
    /// 代词 it / them 默认指向的槽位。null 表示 $selected。
    /// 编译 "When/Whenever <主语> ... " 触发器时应置为 $trigger，直到本句出现 Select 再切回 $selected。
    /// </summary>
    public string DefaultPronounSlot { get; set; }

    /// <summary>
    /// 变量量词 X / Y / Z 的解析器。
    ///
    /// 「X is the number of crests you have」这种定义句可能出现在动作**之后**，
    /// 由 EnglishCompiler 提前扫描登记，这里按需回查。
    /// 没接的时候退化成 ctr:X（读一个不存在的计数器，恒为 0）。
    /// </summary>
    public Func<string, NumExpr> VariableResolver { get; set; }

    /// <summary>
    /// 当前句内已经产生绑定的槽位集合（R-4 / 设计3 5.3）。
    /// 设计3 的强制校验：遇到 it / them 但对应槽位为空时，
    /// 必须直接抛编译异常，绝不静默绑到 $selected。
    /// </summary>
    public HashSet<string> BoundSlots { get; } = new HashSet<string>();

    /// <summary>
    /// 槽位绑定顺序。末尾 = 最近一次绑定。
    ///
    /// 代词必须遵循「最近焦点原则」：
    ///   "Select an enemy follower and give it Ward. Evolve this follower and give it Storm"
    /// 前半句的 it 指敌方随从，后半句的 it 指本卡 —— 只有顺序能区分。
    /// 早先 BoundSlots 是无序 HashSet，ResolvePronounTarget 又按
    /// $selected -> $created -> $self 的死顺序返回，导致
    /// 《亚修雷&莉缇雅》把 Storm 给了敌方随从。
    /// </summary>
    private readonly List<string> _slotHistory = new();

    /// <summary>最近一次绑定的槽位（无边时返回 null）。</summary>
    public string MostRecentSlot => _slotHistory.Count > 0 ? _slotHistory[^1] : null;

    /// <summary>当前句是否已出现 Select / Summon / Add / 触发器等可绑定的前序动作。</summary>
    public void MarkBound(string slot)
    {
        if (string.IsNullOrEmpty(slot)) return;
        BoundSlots.Add(slot);
        // 移到末尾：同一槽位被重新绑定也算「最近焦点」。
        _slotHistory.Remove(slot);
        _slotHistory.Add(slot);
    }

    /// <summary>本句开始时清空登记。</summary>
    public void ResetSlots()
    {
        BoundSlots.Clear();
        _slotHistory.Clear();
        SawHandAndDeck = false;
    }

    /// <summary>是否启用设计3 5.3 的代词强制校验。整卡为 true，单句语料为 false。</summary>
    public bool StrictPronounCheck { get; set; } = true;

    /// <summary>
    /// 最近一次 TryParse 是否遇到了「in your hand and deck」这种**双区域**短语。
    /// TargetSpec 只能装一个 Zone，调用方（ParseTransform）据此补发第二条节点。
    /// </summary>
    public bool SawHandAndDeck { get; private set; } = false;

    /// <summary>
    /// 最近一次 TryParse 是否遇到 "allied and enemy" 这种双阵营短语。
    /// 命中时 ParseSelect 要展开成两次选择（先己方后敌方，累加到同一个槽位）。
    /// </summary>
    public bool SawAlliedAndEnemy { get; private set; } = false;

    /// <summary>
    /// 正在解析 "When/Whenever ..." 从句的**主语**本身。
    ///
    /// 必须与「触发器从句下的效果体」区分开，否则两种 other 会被混为一谈：
    ///   · 主语里的 another —— "Whenever **another** allied follower enters the field, give it Rush"
    ///     是「本卡以外的随从」，排除的是**监听者自己**（$self）。若改成排除事件主体，
    ///     过滤条件就恒不成立，这条能力永远不触发。
    ///   · 效果体里的 other —— "…, deal 1 damage to all **other** followers"
    ///     排除的是**刚进场的那个**（$trigger）。
    /// 主语解析发生在 EnglishCompiler 设定 DefaultPronounSlot 之前（前缀先剥、后定上下文），
    /// 那时刻的值还是上一句留下的，不能拿它当判据 —— 否则同一张卡里
    /// 前面的触发句会污染后面的主语。
    /// </summary>
    public bool InTriggerSubject { get; set; }

    public TargetParser(Lexicon lex) => _lex = lex;

    /// <summary>诊断用：上一次 TryParse 在后置定语循环里消费了多 token。</summary>
    public static int LastPostfixConsumed;

    /// <summary>
    /// "other / another" 该排除哪个槽位 —— 由**当前句所处的上下文**决定，不能写死。
    ///
    /// 三种情形，缺一不可：
    ///   1. 触发器从句的**主语**："Whenever another allied follower enters the field, …"
    ///      → another = 本卡以外，排除 $self。不能待定：主语是「谁算数」的判据，
    ///        与事件主体是同一个东西，拿它去排除事件主体会让条件恒假。
    ///   2. 触发器从句的**效果体**："…, deal 1 damage to all other followers"
    ///      → other = 刚进场的那个以外，排除 $trigger。
    ///   3. 普通动作上下文："Fanfare: Deal 2 damage to another enemy follower."
    ///      → another = 本卡以外，排除 $self（旧行为，逐字不变）。
    ///
    /// 情形 1 与 2 的判据不能都用 DefaultPronounSlot：主语解析发生在前缀剥离阶段，
    /// 那时 EnglishCompiler 还没根据本句设置该字段，读到的是上一句的残留值。
    /// 因此情形 1 用显式的 InTriggerSubject（由 PrefixParser 在解析主语时置位），
    /// 情形 2 才看 DefaultPronounSlot。
    /// </summary>
    private string OtherExcludeSlot()
    {
        if (InTriggerSubject) return TargetSpec.SlotSelf;
        return DefaultPronounSlot == TargetSpec.SlotTrigger
            ? TargetSpec.SlotExcludeTriggerOrSelf
            : TargetSpec.SlotSelf;
    }

    /// <summary>尝试从 cursor 当前位置解析一个目标。解析不了返回 null（cursor 不动）。</summary>
    /// <summary>
    /// 统一代词解析入口：it / them / its / their 都走这里。
    ///
    /// 优先级（设计3 5.3 ScopeRegister）：
    ///   $selected → $created → $counter → $self → $opposing → 触发器初始值
    ///
    /// 为什么必须统一：早先 its / their 写死返回 $selected，
    /// 于是 "Draw a follower and set its cost to 0" 里的 its
    /// 指向空的 $selected 而不是刚抽到的 $created —— 那张牌减不了费。
    /// </summary>
    public TargetSpec ResolvePronounTarget()
    {
        string pronSlot;

        // 最近焦点优先：谁最后被绑定，it/them 就指谁。
        // 不能用固定的 $selected -> $created -> $self 顺序 ——
        // "Evolve this follower and give it Storm" 里 it 必须指本卡，
        // 而死顺序会让它指向前半句选中的敌方随从。
        if (MostRecentSlot != null)
            pronSlot = MostRecentSlot;
        else if (DefaultPronounSlot != null)
            pronSlot = DefaultPronounSlot;
        else if (StrictPronounCheck)
        {
            // 强制校验：代词必须有前序绑定（设计3 5.3）。
            //
            // 这条**不能放宽**。「Fanfare: Give it Ward.」这种没有前置
            // Select/Summon 的句子必须当场报错，否则它会静默绑到空槽位，
            // 卡在对局里表现为「打出去什么都不发生」—— 比编译失败更难查。
            throw new LangException(
                "代词 it 缺少前序绑定：本句既没有 Select，也没有 Summon / Add / Draw 或触发器主体");
        }
        else
        {
            pronSlot = TargetSpec.SlotSelected;
        }

        return new TargetSpec
        {
            SourceKind = TargetSourceKind.Slot,
            Slot = pronSlot,
            Mode = SelectMode.All,
        };
    }

    public TargetSpec TryParse(TokenCursor cur)
    {
        SawAlliedAndEnemy = false;
        SawHandAndDeck = false;
        int start = cur.Position;
        var owner = ZoneScope.Self;
        bool ownerSet = false;
        var zone = ZoneRef.Board;
        var mode = SelectMode.All;
        var wheres = new List<BoolExpr>();
        string slot = null;
        int cardId = 0;
        string cardName = "";
        SortKey sort = SortKey.None;
        bool desc = false;

        // "the first/second/Nth selected follower" 出现在**宾语首位**时，
        // 后置定语循环根本没机会看到它（那个循环在名词之后才跑）。
        // 必须在入口处先认一次。
        // 同时支持直接写 $choose2 / choose2 的形态。
        {
            int preSave = cur.Position;
            int preOrd = 0;
            if (cur.MatchWord("the")) preOrd = ParseOrdinalWord(cur);
            if (preOrd > 0 && cur.MatchWord("selected"))
            {
                cur.TryMatchAnyWord("follower", "followers", "card", "cards", "amulet", "amulets", "target", "targets");
                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotChoose(preOrd),
                    Mode = SelectMode.All,
                };
            }
            cur.Seek(preSave);
            if (TargetSpec.TryParseChooseSlotIndex(cur.Current.Text, out int preIdx))
            {
                cur.Advance();
                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotChoose(preIdx),
                    Mode = SelectMode.All,
                };
            }
        }
        bool distinct = false;
        NumExpr count = NumExpr.Const(1);
        bool countSet = false;
        bool matched = false;
        bool hasCardType = false;
        bool includeLeader = false;
        // 历史池："... have entered the field this match" / "destroyed this match"。
        // 不识别的话，这类条件会退回「当前活在场上的」——与卡面的
        // 「本局入场过的 / 本局被破坏的」完全不是一回事。
        var history = Core.HistoryScope.None;
        bool duplicatesOnly = false;
        bool surfacedAll = false;   // 原文出现过 all / each（区别于默认的 All）   // 显式声明「只要重复的那些」

        // ---- -1. 特殊整体指代（最高优先级拦截）----

        // "all leaders with the highest/lowest defense"
        if (cur.MatchPhrase("all", "leaders") || cur.MatchPhrase("the", "leaders"))
        {
            // "all leaders with the lowest defense" —— highest/lowest 必须落到 Sort 上。
            // 直接吞掉的话，All 模式会打双方主战者各 3 点，
            // 与「防御力最低的」完全相反（10552310）。
            var leaderSort = SortKey.None;
            bool leaderDesc = false;
            if (cur.MatchPhrase("with", "the"))
            {
                leaderDesc = cur.Current.IsWord("highest");
                leaderSort = SortKey.Life;
                while (!cur.IsAtEnd && cur.Current.IsAnyWord("highest", "lowest", "defense", "base", "cost"))
                    cur.Advance();
            }
            return new TargetSpec
            {
                Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All,
                IncludeLeader = true, Sort = leaderSort, SortDescending = leaderDesc,
            };
        }

        // "both leaders" / "all leaders"
        if (cur.MatchPhrase("both", "leaders") || cur.MatchPhrase("all", "leaders"))
            return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };

        // "the opposing follower" / "the opposing amulet"
        if (cur.MatchPhrase("the", "opposing", "follower") || cur.MatchPhrase("the", "opposing", "amulet"))
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotOpposing, Mode = SelectMode.All };

        // "the exact copy" / "the exact copies" / "the copy"
        if (cur.MatchPhrase("the", "exact", "copy") || cur.MatchPhrase("the", "exact", "copies")
            || cur.MatchPhrase("the", "copy"))
            return TargetSpec.Created;

        // "yourself"
        if (cur.MatchWord("yourself"))
        {
            // "yourself and your opponent" → 双方主战者
            if (cur.MatchPhrase("and", "your", "opponent"))
                return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };
            return TargetSpec.SelfLeader;
        }

        // "your leader's defense" / "your leader"
        if (cur.MatchPhrase("your", "leader's"))
        {
            cur.TryMatchAnyWord("defense", "life", "attack");
            return TargetSpec.SelfLeader;
        }
        if (cur.MatchPhrase("the", "enemy", "leader's"))
        {
            cur.TryMatchAnyWord("defense", "life", "attack");
            return TargetSpec.OpponentLeader;
        }
        if (cur.MatchPhrase("your", "leader"))
        {
            if (cur.MatchPhrase("and", "your", "opponent") || cur.MatchPhrase("and", "the", "enemy", "leader"))
                return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.Leader, Mode = SelectMode.All, IncludeLeader = true };
            return TargetSpec.SelfLeader;
        }

        // "your opponent" / "the enemy leader" / "an enemy leader"
        if (cur.MatchPhrase("your", "opponent") || cur.MatchPhrase("the", "enemy", "leader")
            || cur.MatchPhrase("an", "enemy", "leader"))
            return TargetSpec.OpponentLeader;

        // "your hand"
        if (cur.MatchPhrase("your", "hand"))
            return new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.Hand, Mode = SelectMode.All };

        // "all your crests" = 我方全部纹章；"all crests" = 双方全部纹章。
        // 早先两者共用一个分支并把 Owner 写死成 Self，于是 "Banish all crests."
        // 只消了我方纹章（174 号文档 BUG 2）。
        if (cur.MatchPhrase("all", "your", "crests"))
            return new TargetSpec { Owner = ZoneScope.Self, Zone = ZoneRef.CrestArea, Mode = SelectMode.All };
        if (cur.MatchPhrase("all", "crests"))
            return new TargetSpec { Owner = ZoneScope.Any, Zone = ZoneRef.CrestArea, Mode = SelectMode.All };

        // "your Crest: <Name>"
        if (cur.MatchPhrase("your", "crest"))
        {
            cur.TryMatch(TokKind.Colon);
            // 纹章名可能是一个词，也可能是多词（"Crest: Corruption" / "Crest: Kukishiro Mistbloom"）。
            // 只取一个 token 会让后半截留在流里报错。
            var nameParts = new List<string>();
            // 遇到 by / and / to 就停：
            //   "Delay the count of your Crest: Majestic Conquest by 2"
            // 不拦的话 "by 2" 会被并进纹章名，名字变成 "Majestic Conquest by 2"，
            // 运行期匹配不到任何纹章。
            while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Period
                   && cur.Current.Kind != TokKind.Comma
                   && !cur.Current.IsAnyWord("by", "and", "to"))
            {
                nameParts.Add(cur.Current.Text);
                cur.Advance();
            }
            string cn = string.Join(" ", nameParts).Trim();
            var spec = new TargetSpec
            {
                Owner = ZoneScope.Self, Zone = ZoneRef.CrestArea,
                CardName = cn, Mode = SelectMode.All,
            };
            // 必须挂 Where 名称过滤：只写 CardName 字段不会参与
            // "destroy your Crest: X" 的候选筛选，于是一张毁灭纹章的卡
            // 把玩家身上**所有**纹章一起毁掉。
            if (!string.IsNullOrEmpty(cn))
                spec = spec.WithWhere(new HasCardNameExpr
                {
                    CardName = cn,
                    Of = TargetSpec.Item,
                });
            return spec;
        }

        // ---- 0. 槽位代词 ----
        if (cur.Current.IsAnyWord("it", "them"))
        {
            cur.Advance();
            // ⚠ 这里**不再**私吞 "and this follower" / "and the enemy leader"。
            // 私吞会让 ParseGive / ParseEvolve / ParseMove 里的
            // `while (cur.Current.IsWord("and"))` 根本看不到 and，
            // 第二个目标被静默丢弃（10353110 / 10432120 / 10472110 /
            // 10512110 / 10672110 全部如此）。
            // 并列目标一律交给动作层用 and 展开成多条节点。
            // 代词 it 的指向取决于当前上下文（R-4 / Stage 4 代词绑定）：
            //   · 前面有 Select <Target>      → $selected
            //   · 前面是 Summon / Add / Draw   → $created
            //   · 处于 When/Whenever <主语> enters/evolves/attacks 触发器且本句未 Select → $trigger
            // 之前死写 $selected，导致 30 张
            // "Whenever X enters the field, give it ..." 在触发时去读空的 $selected。
            // 设计3 5.3 的 ScopeRegister 优先级：
            //   Select         -> $selected（被消费 1 次即直接合并）
            //   Summon / Add   -> $created（指向刚入场的实体）
            //   Whenever/When  -> $trigger
            // 触发器绑定的优先级最高（DefaultPronounSlot 已置为 $trigger 时直接采纳），
            // 其次才看本句内既有的绑定。
            // 优先级：本句内新产生的绑定 > 触发器初始值。
            //
            // 触发器槽位只是**初始值**，句内一旦出现 Select / Summon / Add，
            // 代词就该指向那个更新的目标（U-5 裁决：「swap this follower's attack
            // and defense and give it +1/+0」里的 it 指本卡，不指触发源）。
            // 之前的实现让 DefaultPronounSlot 无条件最高优先，
            // 于是 BoundSlots 的 $selected / $created 分支永远走不到。
            return ResolvePronounTarget();
        }
        // "activate all of them instead" / "use all of them"：
        // 这是模式抉择的元声明，them 指「上面的所有选项」，不是一个实体集合。
        // 交给 MetaModeWeaver 处理，这里返回一个占位的自指目标即可 ——
        // 若走到代词解析，StrictPronounCheck 会当场报错（模式句本来就没有前置绑定）。
        if (cur.Current.IsAnyWord("them"))
        {
            // "activate all of them instead"：这是模式抉择的元声明，
            // them 指「上面列出的所有选项」，不是一个实体集合。
            // 必须在 Advance 之前判断，否则 Peek 的基准已经越过了 them。
            bool isAllOfThem = cur.Peek(-2).IsWord("all") && cur.Peek(-1).IsWord("of");

            cur.Advance();

            if (isAllOfThem)
                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotSelected,
                    Mode = SelectMode.All,
                };

            return ResolvePronounTarget();
        }
        if (cur.Current.IsAnyWord("its", "their"))
        {
            cur.Advance();
            // its defense / its attack → 指代上一条动作的目标
            // 不能这里偷吃属性名！
            // "if its cost is 4" 里的 cost 是条件要读的属性，
            // 被这里吃掉后，条件层只剩 "is 4"，
            // 只能盲猜一个默认属性（Life），于是费用变成生命值。
            //
            // 走统一解析而不是写死 $selected：
            //   "Draw a follower and set its cost to 0." 里的 its 应指向 $created
            //   （刚抽到的那张牌），写死 $selected 会让这张牌减不了费。
            return ResolvePronounTarget();
        }
        if (cur.Current.IsAnyWord("itself"))
        {
            cur.Advance();
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelf, Mode = SelectMode.All };
        }

        // "this follower" / "this card"
        if (cur.Current.IsAnyWord("this"))
        {
            cur.Advance();
            // `crest` 必须在内：`Destroy this crest` 漏了它就会把 crest 留在句尾报错。
            cur.TryMatchAnyWord("follower", "card", "amulet", "crest");
            return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = TargetSpec.SlotSelf, Mode = SelectMode.All };
        }

        // ---- 1. 量词 ----
        // 数字后面紧跟 '-' 时它是**费用定语**（1-cost / 1-base-cost），不是数量。
        // 当成量词吃掉，游标会停在 '-' 上，第 3 步修饰词循环直接 break，
        // 后面 "-base-cost allied cards on the field" 整段被丢 ——
        // 「at least two 1-base-cost allied cards」于是退化成「场上任意 2 张卡」。
        if (cur.Current.Kind == TokKind.Number && cur.Peek().Kind != TokKind.Dash)
        {
            int n = (int)cur.Current.Value;
            cur.Advance();
            count = NumExpr.Const(n);
            countSet = true;
            mode = SelectMode.Choose;
            matched = true;
            // "2 leftmost" → 按顺序取前 N 个
            if (cur.Current.IsWord("leftmost"))
            {
                cur.Advance();
                mode = SelectMode.First;
            }
        }
        // "the leftmost"
        else if (cur.Current.IsWord("leftmost"))
        {
            cur.Advance();
            mode = SelectMode.First;
            count = NumExpr.Const(1);
            countSet = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("all"))
        {
            // "activate all of them instead" —— 模式抉择的元声明。
            //
            // them 指的是上面列出的**所有选项**，不是一个实体集合。
            // 若照常吞掉 all 再让 of them 往下走，them 会掉进代词解析，
            // 而模式句本来就没有前置绑定，StrictPronounCheck 当场报错 ——
            // 三张 Mode 卡（10771310 / 10912310 / 10962310）就是这么挂的。
            //
            // 直接在这里整体消费掉，交给 MetaModeWeaver 处理。
            if (cur.Peek().IsWord("of") && cur.Peek(2).IsAnyWord("them", "them."))
            {
                cur.Advance();          // all
                cur.Advance();          // of
                cur.Advance();          // them
                cur.TryMatchAnyWord("instead");

                return new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotSelected,
                    Mode = SelectMode.All,
                };
            }

            cur.Advance();
            mode = SelectMode.All;
            // 记下「全体」是原文明确写的。后置的 another/other 只排除自身，
            // 不得据此把全体缩成单选。
            surfacedAll = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("a", "an"))
        {
            cur.Advance();
            count = NumExpr.Const(1);
            countSet = true;
            mode = SelectMode.Choose;
        }
        // 变量量词：「give X random enemy followers」。
        // X 后面不是连字符（不是「X-cost」）时，既没被量词步吃掉、
        // 也没被修饰词循环吃掉，游标会卡死在 X 上直接返回 null。
        else if (cur.Current.IsAnyWord("x", "y", "z") && cur.Peek().Kind != TokKind.Dash)
        {
            string vn = cur.Current.Text.ToUpperInvariant();
            cur.Advance();
            // 优先用编译器登记好的定义（`X is the number of crests you have`），
            // 没登记才退回按名字造计数器。
            count = VariableResolver != null
                ? VariableResolver(vn)
                : NumExpr.Ctr(_lex.Counter(vn));
            countSet = true;
            mode = SelectMode.Choose;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("each"))
        {
            cur.Advance();
            mode = SelectMode.All;
            surfacedAll = true;
            matched = true;
        }

        // ---- 2. and 是并列边界，在此停止（必须在修饰词循环之前判定）----
        if (cur.Current.IsWord("and"))
        {
            cur.Seek(start);
            return null;
        }

        // ---- 3. 统一前置修饰词循环 ----
        // 英语里这些是同层修饰语，顺序不固定：
        //   all random other allied evolved Ward followers with 3 defense or less
        //   the 2 leftmost / another random / a random unselected enemy / 3-base-cost allied
        // 旧实现把它们散落在循环外，导致一旦顺序与预期不同就永远匹配不到。
        while (true)
        {
            // 冠词
            if (cur.Current.IsAnyWord("the", "a", "an")) { cur.Advance(); continue; }

            // [N] leftmost / leftmost
            if (cur.Current.Kind == TokKind.Number && cur.Peek().IsWord("leftmost"))
            {
                count = NumExpr.Const(cur.Current.Value); countSet = true;
                cur.Advance(); cur.Advance();
                mode = SelectMode.First; matched = true; continue;
            }

            if (cur.Current.IsWord("leftmost"))
            {
                cur.Advance(); mode = SelectMode.First;
                if (!countSet) { count = NumExpr.Const(1); countSet = true; }
                matched = true; continue;
            }

            // random / differently named / exact copy / unselected / another·other
            if (cur.MatchWord("random"))
            {
                mode = SelectMode.Random;
                if (!countSet) { count = NumExpr.Const(1); countSet = true; }
                matched = true; continue;
            }
            if (cur.MatchPhrase("differently", "named")) { distinct = true; matched = true; continue; }

            if (cur.MatchPhrase("exact", "copy") || cur.MatchPhrase("exact", "copies")) { matched = true; continue; }
            if (cur.MatchWord("unselected"))
            {
                wheres.Add(BoolExpr.Not(new IsPreviousExpr { Slot = TargetSpec.SlotSelected }));
                matched = true; continue;
            }
            if (cur.MatchAnyWord("another", "other"))
            {
                // "another" 是**限定词**，不是量词 —— 它本身不表示「选几个」。
                //
                // 「Select another allied card」与「all other followers」的分界在于
                // 前面有没有出现 all/each：
                //   · "Select another allied card" —— 没有量词，单指一个，交玩家选；
                //   · "all other followers" —— all 已定下全体，another 只是排除自身。
                // 之前一律不改 mode（停在默认 All），前者会直接作用于全场的其他卡；
                // 而一律改成 Choose 又会把后者的「全体」缩成「让玩家挑一个」。
                // surfacedAll 记录「前面出现过 all/each」，是两者的唯一区分依据。
                if (!countSet && !surfacedAll)
                {
                    count = NumExpr.Const(1);
                    countSet = true;
                    mode = SelectMode.Choose;
                }
                wheres.Add(new NotSameEntityExpr { ExcludeSlot = OtherExcludeSlot(), Of = TargetSpec.Item });
                matched = true; continue;
            }

            // N-cost / N-base-cost / X-cost，以及费用枚举
            // 「1-, 3-, or 5-cost card」——分词器把每种写法都拆成 数字 / - / cost。
            if ((cur.Current.Kind == TokKind.Number || cur.Current.IsAnyWord("x", "y", "z"))
                && cur.Peek().Kind == TokKind.Dash)
            {
                var costExprs = new List<NumExpr>();
                while ((cur.Current.Kind == TokKind.Number || cur.Current.IsAnyWord("x", "y", "z"))
                       && cur.Peek().Kind == TokKind.Dash)
                {
                    costExprs.Add(cur.Current.Kind == TokKind.Number
                        ? NumExpr.Const(cur.Current.Value)
                        : NumExpr.Var(_lex.Counter(cur.Current.Text.ToUpperInvariant())));
                    cur.Advance();               // 数字
                    cur.Advance();               // '-'
                    if (cur.Current.Kind == TokKind.Comma) cur.Advance();
                    cur.TryMatchAnyWord("and", "or");
                }

                bool isBase = cur.MatchWord("base");
                cur.TryMatch(TokKind.Dash);
                cur.TryMatchAnyWord("cost", "costs");

                var stat = isBase ? StatKey.BaseCost : StatKey.Cost;
                var cmps = costExprs.Select(ce => (BoolExpr)new CompareExpr
                {
                    Left = NumExpr.Prop(stat, TargetSpec.Item),
                    Op = CompareOp.Eq,
                    Right = ce,
                }).ToArray();

                wheres.Add(cmps.Length == 1 ? cmps[0] : BoolExpr.Or(cmps));
                matched = true; continue;
            }

            // non-<Trait>（分词器拆成 non / - / Encroacher）
            if (cur.Current.IsWord("non") && cur.Peek().Kind == TokKind.Dash)
            {
                cur.Advance(); cur.Advance();
                var trNon = cur.Current.Text; cur.Advance();
                wheres.Add(BoolExpr.Not(new HasTraitExpr { Trait = _lex.Trait(trNon), Of = TargetSpec.Item }));
                matched = true; continue;
            }

            // 归属
            // "allied and enemy follower" -> Any
            if (cur.Current.IsAnyWord("allied", "allies") && cur.Peek().IsWord("and")
                && cur.Peek(2).IsAnyWord("enemy", "enemies"))
            {
                cur.Advance(); cur.Advance(); cur.Advance();
                // 记下这是「双方各一个」的双目标短语，由 ParseSelect 展开成两次选择。
                owner = ZoneScope.Self; ownerSet = true; SawAlliedAndEnemy = true;
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("enemy"))
            {
                cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true; matched = true;
                while (cur.Current.IsAnyWord("a", "an")) cur.Advance();
                continue;
            }
            if (cur.Current.IsAnyWord("allied"))
            {
                cur.Advance();
                owner = ZoneScope.Self; ownerSet = true; matched = true;
                while (cur.Current.IsAnyWord("a", "an")) cur.Advance();
                continue;
            }
            if (cur.Current.IsAnyWord("your") && cur.Peek().IsWord("opponent"))
            {
                cur.Advance(); cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true; matched = true;
                continue;
            }
            // "your" 后面若是 leader/hand/deck 等已知目标，留给专用分支处理
            if (cur.Current.IsAnyWord("your") && !cur.Peek().IsAnyWord("leader", "hand", "deck", "cemetery", "crest"))
            {
                cur.Advance();
                owner = ZoneScope.Self; ownerSet = true; matched = true;
                continue;
            }

            // "a Fused follower"：融合产物（fuse_count > 0 的随从）。
            // 早先这个词靠「未知修饰词静默跳过」被吞掉，条件退化成「任一随从」；
            // 删掉吞词循环后必须正式建模，否则整句解析直接失败。
            if (cur.Current.IsAnyWord("fused"))
            {
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Ctr(_lex.Counter("FUSE_COUNT")),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(1),
                });
                matched = true; continue;
            }

            // 状态：super-evolved / evolved / unevolved / damaged
            if (cur.Current.IsAnyWord("super") && cur.Peek().Kind == TokKind.Dash)
            {
                cur.Advance(); cur.Advance(); cur.TryMatchAnyWord("evolved");
                wheres.Add(new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("super-evolved"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("unevolved"))
            {
                cur.Advance();
                wheres.Add(BoolExpr.Not(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Item }));
                matched = true; continue;
            }
            if (cur.Current.IsAnyWord("evolved"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Item });
                matched = true; continue;
            }
            // "an Enhanced card"：以爆能强化档位打出的卡。
            // 早先这里没有分支，"Enhanced" 落进后面的未知修饰词循环被静默吞掉，
            // 条件退化成「打出任何一张卡」——《古旧天剑》的信仰因此被打成过牌机。
            if (cur.Current.IsAnyWord("enhanced"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.Enhanced, Of = TargetSpec.Item });
                matched = true; continue;
            }
            // "damaged" 必须后接名词，否则可能是 "Destroy all damaged enemy followers" 里的修饰
            if (cur.Current.IsAnyWord("damaged"))
            {
                cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.Damaged, Of = TargetSpec.Item });
                matched = true; continue;
            }

            // 前置的 destroyed："... destroyed followers this match"。
            // 官方两种写法都有（destroyed 在前 / 在后），只认后置会漏掉这一半。
            // 这里只登记「被破坏过」这一事实，兵种与卡种由后续 token 继续收，
            // 归属（allied / enemy）由前面的 owner 分支决定 —— 一个都不吞。
            if (cur.Current.IsAnyWord("destroyed"))
            {
                cur.Advance();
                zone = ZoneRef.Cemetery;
                history = Core.HistoryScope.DestroyedThisMatch;
                matched = true; continue;
            }

            // 前置的 entered the field："... entered the field this match" 里的前半
            if (cur.MatchPhrase("entered", "the", "field"))
            {
                zone = ZoneRef.Board;
                history = Core.HistoryScope.EnteredThisMatch;
                matched = true; continue;
            }

            // 兵种（可能连续多个）
            if (cur.Current.Kind == TokKind.Trait || _lex.IsTrait(cur.Current.Text))
            {
                var tr = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasTraitExpr { Trait = _lex.Trait(tr), Of = TargetSpec.Item });
                matched = true; continue;
            }

            // 职业
            if (cur.Current.Kind == TokKind.Clan)
            {
                var cn = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasClanExpr { Clan = _lex.Clan(cn), Of = TargetSpec.Item });
                matched = true; continue;
            }

            // "other"（除自身外）
            if (cur.Current.IsAnyWord("another", "other"))
            {
                cur.Advance();
                wheres.Add(new NotSameEntityExpr { ExcludeSlot = OtherExcludeSlot(), Of = TargetSpec.Item });
                matched = true; continue;
            }

            break;
        }

        // unselected
        if (cur.MatchWord("unselected"))
        {
            wheres.Add(BoolExpr.Not(new IsPreviousExpr { Slot = TargetSpec.SlotSelected }));
            matched = true;
        }

        // "all N-, N-, and N-cost cards"（费用枚举）
        if (cur.Current.IsAnyWord("all") && cur.Peek().Kind == TokKind.Number)
        {
            int saveE = cur.Position;
            cur.Advance();
            var costs = new List<int>();
            while (cur.Current.Kind == TokKind.Number)
            {
                costs.Add(cur.Current.Value);
                cur.Advance();
                if (cur.Current.Kind == TokKind.Dash) cur.Advance();
                bool sawSep = false;
                if (cur.Current.Kind == TokKind.Comma) { cur.Advance(); sawSep = true; }
                if (cur.Current.IsWord("and")) { cur.Advance(); sawSep = true; }
                if (!sawSep) break;
                if (cur.Current.Kind != TokKind.Number) break;
            }
            if (costs.Count > 0 && cur.MatchWord("cost"))
            {
                var ors = new List<BoolExpr>();
                foreach (var cc in costs)
                    ors.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                        Op = CompareOp.Eq,
                        Right = NumExpr.Const(cc),
                    });
                wheres.Add(ors.Count == 1 ? ors[0] : BoolExpr.Or(ors.ToArray()));
                matched = true;
            }
            else cur.Seek(saveE);
        }

        // "an allied and enemy follower" -> Any
        if ((cur.Current.IsAnyWord("allied", "allies")) && cur.Peek().IsWord("and")
            && cur.Peek(2).IsAnyWord("enemy", "enemies"))
        {
            cur.Advance(); cur.Advance(); cur.Advance();
            owner = ZoneScope.Self; ownerSet = true; SawAlliedAndEnemy = true; matched = true;
        }

        // ---- 6c. 未知修饰词：已彻底删除 ----
        //
        // 早先这里有一个 while 循环，把所有「不是词典词条的单词」静默跳过，
        // 意图是容忍未注册的兵种名（Mysteria 之类）。代价远大于收益：
        // 《古旧天剑》的 "an Enhanced card" 被吞成 "a card"，条件彻底失效，
        // 而且这类 bug 只能靠玩家反馈才发现 —— 编译不报错、卡面也不异常。
        //
        // 现在的策略是**不吞**：未识别的修饰词会自然落入后续匹配，
        // 若最终整句无法解析，编译器会给出明确报错而不是静默生成错误语义。

        // ---- 7. 卡种 / 卡名 ----
        if (cur.MatchPhrase("ally", "or", "enemy") || cur.MatchPhrase("allies", "or", "enemies"))
        {
            owner = ZoneScope.Any; ownerSet = true; includeLeader = true; matched = true;
        }
        else if (cur.Current.IsAnyWord("duplicate", "duplicates"))
        {
            // 必须留痕：只吃掉词而不留标记，候选池会退化成「区域里的全部卡」，
            // "Banish all duplicates from your deck" 一句就把整副牌库放逐光。
            cur.Advance(); duplicatesOnly = true; matched = true;
        }
        else
        if (cur.Current.Kind == TokKind.CardName)
        {
            cardId = cur.Current.CardId;
            cardName = cur.Current.Text;
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("follower", "followers"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("amulet", "amulets"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Amulet, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("spell", "spells"))
        {
            cur.Advance();
            wheres.Add(new HasKindExpr { Kind = CardKind.Spell, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        // crest / crests —— 纹章不在战场上，必须切到纹章区。
        // 漏掉这一支时 "Destroy an enemy crest" / "all enemy crests" 会解析失败或指错区域。
        else if (cur.Current.IsAnyWord("crest", "crests"))
        {
            cur.Advance();
            zone = ZoneRef.CrestArea;
            wheres.Add(new HasKindExpr { Kind = CardKind.Crest, Of = TargetSpec.Item });
            hasCardType = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("enemies", "enemy") && !cur.Peek().IsAnyWord("follower", "followers"))
        {
            cur.Advance();
            if (!ownerSet) { owner = ZoneScope.Opponent; ownerSet = true; }
            includeLeader = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("allies", "ally"))
        {
            cur.Advance();
            if (!ownerSet) { owner = ZoneScope.Self; ownerSet = true; }
            includeLeader = true;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("card", "cards"))
        {
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("leader", "leaders"))
        {
            cur.Advance();
            matched = true;
        }
        else if (cur.Current.IsAnyWord("copy", "copies"))
        {
            cur.Advance();
            cur.TryMatchAnyWord("of");
            // copies of <卡名>
            if (cur.Current.Kind == TokKind.CardName)
            {
                cardId = cur.Current.CardId;
                cardName = cur.Current.Text;
                cur.Advance();
            }
            else if (cur.Current.IsAnyWord("it", "them"))
            {
                // "Banish all enemy copies of it from the field"（10334110）：
                // 要的是「场上与 $selected 同名的所有敌方卡」，不是那个已被放逐的实体本身。
                // 直接返回 Slot 会把一个已离场的单实体当目标，什么都放逐不到。
                cur.Advance();
                cur.TryMatchAnyWord("from", "on");
                cur.TryMatchAnyWord("the");
                cur.TryMatchAnyWord("field");
                var pron = ResolvePronounTarget();
                wheres.Add(new HasCardNameExpr { Of = pron });
                matched = true;
            }
            matched = true;
        }

        // ---- 8. 区域 ----
        if (cur.Current.IsAnyWord("on") && cur.Peek().IsAnyWord("the") && cur.Peek(2).IsAnyWord("field"))
        {
            cur.Advance(); cur.Advance(); cur.Advance();
            zone = ZoneRef.Board;
            matched = true;
        }
        else if (cur.Current.IsAnyWord("in", "from") && cur.Peek().IsAnyWord("your"))
        {
            cur.Advance(); cur.Advance();
            // "in your opponent's hand"
            if (cur.Current.IsAnyWord("opponent's", "opponent") )
            {
                cur.Advance();
                owner = ZoneScope.Opponent; ownerSet = true;
            }
            var z = cur.Current.Text.ToLowerInvariant();
            cur.Advance();
            zone = z.StartsWith("hand") ? ZoneRef.Hand
                 : z.StartsWith("deck") ? ZoneRef.Deck
                 : z.StartsWith("cemetery") ? ZoneRef.Cemetery
                 : ZoneRef.Board;
            matched = true;
        }

        // ---- 8b. 更多区域短语 ----
        if (cur.MatchPhrase("from", "your", "deck")) { zone = ZoneRef.Deck; }
        else if (cur.MatchPhrase("from", "your", "hand")) { zone = ZoneRef.Hand; }
        else if (cur.MatchPhrase("from", "your", "cemetery")) { zone = ZoneRef.Cemetery; }
        else if (cur.MatchPhrase("from", "the", "field") || cur.MatchPhrase("on", "the", "field"))
            zone = ZoneRef.Board;
        else if (cur.MatchPhrase("in", "your", "opponent's", "deck")) { zone = ZoneRef.Deck; owner = ZoneScope.Opponent; ownerSet = true; }
        else if (cur.MatchPhrase("in", "your", "opponent's", "hand")) { zone = ZoneRef.Hand; owner = ZoneScope.Opponent; ownerSet = true; }
        else if (cur.MatchPhrase("in", "your", "hand", "and", "deck"))
        {
            // "Transform all copies of X in your hand and deck into ..."
            // 双区域。TargetSpec 只能装一个 Zone，这里先记 Hand，
            // 由 ParseTransform 读 sawHandAndDeck 再补发一条 Deck 的节点。
            zone = ZoneRef.Hand;
            SawHandAndDeck = true;
        }
        else if (cur.MatchPhrase("in", "your", "deck")) { zone = ZoneRef.Deck; }
        else if (cur.MatchPhrase("in", "your", "hand")) { zone = ZoneRef.Hand; }
        else if (cur.MatchPhrase("in", "your", "cemetery")) { zone = ZoneRef.Cemetery; }
        else if (cur.MatchPhrase("destroyed", "this", "match"))
        {
            // 只设 zone 不设 history 会让「本局被破坏」退化成「墓地里的全部」——
            // 历史上死掉又被召回的卡、以及开局就在墓地的卡都会被算进去。
            zone = ZoneRef.Cemetery;
            if (history == Core.HistoryScope.None) history = Core.HistoryScope.DestroyedThisMatch;
        }

        // ---- 9. 后置定语 ----
        int postLoopStart = cur.Position;
        while (true)
        {
            // "not named <CardName>" / "named <CardName>"
            // `discard all cards not named Great Testimony` 与
            // `Fuse: A and B` 都靠这个谓词表达。
            {
                int np = cur.Position;
                bool neg = cur.MatchPhrase("not", "named");
                if (neg || cur.MatchWord("named"))
                {
                    if (cur.Current.Kind == TokKind.CardName)
                    {
                        var names = new List<(int Id, string Name)>();
                        names.Add((cur.Current.CardId, cur.Current.Text));
                        cur.Advance();
                        while (cur.Current.IsAnyWord("and", "or"))
                        {
                            cur.Advance();
                            if (cur.Current.Kind != TokKind.CardName) break;
                            names.Add((cur.Current.CardId, cur.Current.Text));
                            cur.Advance();
                        }

                        BoolExpr expr = names.Count == 1
                            ? new HasCardNameExpr { CardId = names[0].Id, CardName = names[0].Name, Of = TargetSpec.Item }
                            : BoolExpr.Or(names.Select(x => (BoolExpr)new HasCardNameExpr
                                { CardId = x.Id, CardName = x.Name, Of = TargetSpec.Item }).ToArray());

                        wheres.Add(neg ? BoolExpr.Not(expr) : expr);
                        matched = true; continue;
                    }
                    cur.Seek(np);
                }
            }

            // 区域短语可能出现在后置定语之后（"that costs 3 or less from your deck"）
            if (cur.MatchPhrase("from", "your", "deck")) { zone = ZoneRef.Deck; matched = true; continue; }
            if (cur.MatchPhrase("from", "your", "hand")) { zone = ZoneRef.Hand; matched = true; continue; }
            if (cur.MatchPhrase("from", "your", "cemetery")) { zone = ZoneRef.Cemetery; matched = true; continue; }
            if (cur.MatchPhrase("from", "the", "field")) { zone = ZoneRef.Board; matched = true; continue; }
            if (cur.MatchPhrase("in", "your", "hand")) { zone = ZoneRef.Hand; matched = true; continue; }
            if (cur.MatchPhrase("in", "your", "deck")) { zone = ZoneRef.Deck; matched = true; continue; }
            // "whose cost has been changed"
            if (cur.MatchPhrase("whose", "cost", "has", "been", "changed"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.CostModified, Of = TargetSpec.Item });
                matched = true; continue;
            }
            // "drawn"（Summon any Forestcraft followers drawn）
            if (cur.MatchWord("drawn"))
            {
                slot = TargetSpec.SlotCreated; matched = true; continue;
            }
            // ⚠ 这里**不再**私吞 "and the enemy leader"。
            // 把它并进同一个 TargetSpec 会让主战者在 Where 的 is Follower
            // 过滤里被筛掉，或挤占随机随从的名额。
            // 并列主战者伤害统一由 ActionParser.ParseDeal 展开成独立节点。

            // 历史池限定语（"... entered the field this match" / "... destroyed this match"）。
            // 关系代词 that 必须先吃掉：它在后置循环的停用词表里，
            // 「followers **that** entered the field this match」会就此收尾，
            // 整句剩下的部分被当成无法解析的尾缀。
            {
                int rp = cur.Position;
                cur.TryMatchAnyWord("that", "which");
                bool hit = cur.MatchPhrase("have", "entered", "the", "field", "this", "match")
                        || cur.MatchPhrase("entered", "the", "field", "this", "match");
                if (hit) { history = Core.HistoryScope.EnteredThisMatch; matched = true; continue; }
                cur.Seek(rp);
            }
            if (cur.MatchPhrase("have", "entered", "the", "field", "this", "match")
                || cur.MatchPhrase("entered", "the", "field", "this", "match"))
            {
                history = Core.HistoryScope.EnteredThisMatch; matched = true; continue;
            }
            // 「this match」/「this turn」只是**限定语**，本身不决定历史池的种类。
            //
            // 之前这里无条件把裸 this match 改写成 DestroyedThisMatch ——
            // 于是「a card added to your hand this turn」里的 this turn
            // 也被当成「本回合被破坏」，整句去墓地找一个根本不存在的历史池。
            // 判定只认「前面已经出现了 destroyed / entered the field 这类动词」，
            // 那才是历史池成立的依据；没有动词就只是时间状语，吃掉即可。
            // 只有**已经进入历史池**（前面见到了 destroyed / entered the field）
            // 才把 this match / this turn 当作历史限定语。
            // 裸的时间状语（"a card added to your hand this turn"）若也被换成
            // DestroyedThisTurn，整句会去墓地找一个根本不存在的历史池。
            if (history != Core.HistoryScope.None && cur.MatchPhrase("this", "match"))
            {
                if (history == Core.HistoryScope.DestroyedThisTurn)
                    history = Core.HistoryScope.DestroyedThisMatch;
                matched = true; continue;
            }
            if (history != Core.HistoryScope.None && cur.MatchPhrase("this", "turn"))
            {
                if (history == Core.HistoryScope.DestroyedThisMatch)
                    history = Core.HistoryScope.DestroyedThisTurn;
                matched = true; continue;
            }

            // 后置的单独 destroyed（"... followers destroyed this match"）。
            // 前置循环已经认得它，这里再兜一次：官方两种语序都出现过，
            // 只认前置会让 "allied Shikigami followers destroyed this match" 的
            // destroyed 落到卡种分支之后没人吃，history 整个丢失。
            if (cur.Current.IsAnyWord("destroyed"))
            {
                cur.Advance();
                zone = ZoneRef.Cemetery;
                if (history == Core.HistoryScope.None) history = Core.HistoryScope.DestroyedThisMatch;
                matched = true; continue;
            }

            if (cur.MatchPhrase("destroyed", "this", "match"))
            {
                history = Core.HistoryScope.DestroyedThisMatch; matched = true; continue;
            }
            if (cur.MatchPhrase("destroyed", "this", "turn"))
            {
                history = Core.HistoryScope.DestroyedThisTurn; matched = true; continue;
            }

            // ⚠ 同上："and both leaders" 不再并入随从目标，
            // 否则原本的 Owner = Opponent 会被强行改成 Any，
            // 「all enemy followers and both leaders」连己方主战者也打了。
            // "with Fuse"（先判断再消耗，避免 MatchWord 抢先吞掉 with）
            // "with Fuse" 是「这张卡带有 Fuse 能力」，不是「命中上一个融合材料」。
            // 之前借用 IsPreviousExpr，打印出来是 where is selected，
            // 语义完全错位。
            if (cur.Current.IsWord("with") && cur.Peek().IsWord("fuse"))
            {
                cur.Advance(); cur.Advance();
                wheres.Add(new HasStateExpr { State = UnitState.HasFuse, Of = TargetSpec.Item });
                matched = true; continue;
            }

            // "or the enemy leader"
            if (cur.MatchPhrase("or", "the", "enemy", "leader") || cur.MatchPhrase("or", "an", "enemy", "leader"))
            {
                includeLeader = true;
                matched = true;
                continue;
            }
            // "other than it" / "other than the attack target"
            // "other than it" 的语义是「排除那张具体的卡（不是同一实体）」，
            // 不是「某槽位非空」。
            // IsPreviousExpr 在 VmEval 里只检查槽位是否非空，
            // 拿它当「不是同一张卡」用会让每个候选都通过（10503210 大游戏世界）。
            // "the first/1st/second/2nd/... selected follower" → $choose{K}
            //   "Deal damage to the first selected follower equal to
            //    the second selected follower's attack"
            {
                int ordSave = cur.Position;
                int ord = 0;
                if (cur.MatchWord("the"))
                {
                    ord = ParseOrdinalWord(cur);
                    if (ord > 0 && cur.MatchWord("selected"))
                    {
                        cur.TryMatchAnyWord("follower", "followers", "card", "cards", "amulet", "amulets", "target", "targets");
                        slot = TargetSpec.SlotChoose(ord);
                        matched = true;
                        continue;
                    }
                }
                else if (TargetSpec.TryParseChooseSlotIndex(cur.Current.Text, out int ci))
                {
                    cur.Advance();
                    slot = TargetSpec.SlotChoose(ci);
                    matched = true;
                    continue;
                }
                cur.Seek(ordSave);
            }

            // "other than the first selected follower" / "other than $choose1"
            if (cur.Current.IsWord("other") && cur.Peek().IsWord("than"))
            {
                int otSave = cur.Position;
                cur.Advance(); cur.Advance();
                if (TargetSpec.TryParseChooseSlotIndex(cur.Current.Text, out int cIdx))
                {
                    cur.Advance();
                    wheres.Add(new NotSameEntityExpr
                    {
                        ExcludeSlot = TargetSpec.SlotChoose(cIdx),
                        Of = TargetSpec.Item,
                    });
                    matched = true;
                    continue;
                }
                if (cur.MatchWord("the"))
                {
                    int ord2 = ParseOrdinalWord(cur);
                    if (ord2 > 0 && cur.MatchWord("selected"))
                    {
                        cur.TryMatchAnyWord("follower", "followers", "card", "cards", "amulet", "amulets", "target", "targets");
                        wheres.Add(new NotSameEntityExpr
                        {
                            ExcludeSlot = TargetSpec.SlotChoose(ord2),
                            Of = TargetSpec.Item,
                        });
                        matched = true;
                        continue;
                    }
                }
                cur.Seek(otSave);
            }

            if (cur.MatchPhrase("other", "than", "it"))
            {
                // 排除哪一格由执行上下文决定，编译期不能定死：
                //   "Whenever you play another card, if there's a card on the field
                //    other than it ..."      -> 排除 $trigger（被使用的那张）
                //   "destroy all other followers" -> 排除 $self（本卡）
                // 用 ResolvePronounTarget() 在纯条件编译时还会因为「无前序绑定」直接抛错。
                wheres.Add(new NotSameEntityExpr
                {
                    ExcludeSlot = TargetSpec.SlotExcludeTriggerOrSelf,
                    Of = TargetSpec.Item,
                });
                matched = true;
                continue;
            }
            if (cur.MatchPhrase("other", "than", "the", "attack", "target"))
            {
                wheres.Add(new NotSameEntityExpr
                {
                    ExcludeSlot = TargetSpec.SlotOpposing,
                    Of = TargetSpec.Item,
                });
                matched = true;
                continue;
            }

            // "with the same base cost" / "with the same cost"。
            // 必须放在**后置定语循环**：英文原文里它出现在
            // "a card on the field other than it" **之后**，
            // 放前置循环里永远遇不到（10503210 大游戏世界）。
            {
                bool sameBase = cur.MatchPhrase("with", "the", "same", "base", "cost");
                if (sameBase || cur.MatchPhrase("with", "the", "same", "cost"))
                {
                    var stKey = sameBase ? StatKey.BaseCost : StatKey.Cost;
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(stKey, TargetSpec.Item),
                        Op = CompareOp.Eq,
                        Right = NumExpr.Prop(stKey, TargetSpec.Trigger),
                    });
                    matched = true;
                    continue;
                }
            }
            // "that didn't attack this turn"
            if (cur.MatchPhrase("that", "didn't", "attack", "this", "turn"))
            {
                wheres.Add(BoolExpr.Not(new HasStateExpr { State = UnitState.AttackedThisTurn, Of = TargetSpec.Item }));
                matched = true;
                continue;
            }
            // "with N defense/attack or less/more"
            if (cur.Current.IsWord("with") && cur.Peek().Kind == TokKind.Number
                && cur.Peek(2).IsAnyWord("attack", "defense", "life"))
            {
                cur.Advance();
                int fn = cur.Current.Value; cur.Advance();
                var fstat = cur.Current.Text.ToLowerInvariant().StartsWith("attack") ? StatKey.Attack : StatKey.Life;
                cur.Advance();
                // "with 1 defense"（没有 or less/more）是**精确等值**，
                // 不是 Life >= 1。旧实现无条件 Ge，且推进游标时把句末的词也吃掉了，
                // 「with 1 defense.」被读成「生命 >= 1」（等于不过滤）。
                var fop = CompareOp.Eq;
                if (cur.TryMatchWord("or"))
                {
                    fop = cur.Current.IsAnyWord("less", "fewer") ? CompareOp.Le : CompareOp.Ge;
                    cur.Advance();
                }
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(fstat, TargetSpec.Item),
                    Op = fop,
                    Right = NumExpr.Const(fn),
                });
                matched = true; continue;
            }

            // "with On Spellboost"
            if (cur.MatchPhrase("with", "on", "spellboost"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.HasOnSpellboost, Of = TargetSpec.Item });
                matched = true;
                continue;
            }
            // "with Last Words [and a base cost of N or less]"
            if (cur.MatchPhrase("with", "last", "words"))
            {
                wheres.Add(new HasStateExpr { State = UnitState.HasLastWords, Of = TargetSpec.Item });
                if (cur.MatchPhrase("and", "a", "base", "cost", "of") && cur.Current.Kind == TokKind.Number)
                {
                    int bn = cur.Current.Value; cur.Advance();
                    cur.TryMatchAnyWord("or"); cur.TryMatchAnyWord("less");
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = CompareOp.Le,
                        Right = NumExpr.Const(bn),
                    });
                }
                matched = true;
                continue;
            }

            // with the highest/lowest X
            if (cur.Current.IsAnyWord("with") && cur.Peek().IsAnyWord("the")
                && cur.Peek(2).IsAnyWord("highest", "lowest"))
            {
                cur.Advance(); cur.Advance();
                desc = cur.Current.IsAnyWord("highest");
                cur.Advance();
                bool isBaseCost = cur.MatchWord("base");
                var key = cur.Current.Text.ToLowerInvariant();
                cur.Advance();
                sort = key.StartsWith("attack") ? SortKey.Attack
                     : key.StartsWith("cost") ? (isBaseCost ? SortKey.BaseCost : SortKey.Cost)
                     : SortKey.Life;
                matched = true;
                continue;
            }
            // with <Keyword>
            if (cur.Current.IsAnyWord("with") && cur.Peek().Kind == TokKind.Keyword)
            {
                cur.Advance();
                var kw = cur.Current.Text;
                cur.Advance();
                wheres.Add(new HasKeywordExpr { Keyword = _lex.Keyword(kw), Of = TargetSpec.Item });
                matched = true;
                continue;
            }
            // "with base costs of N or less"
            if (cur.MatchPhrase("with", "base", "costs", "of"))
            {
                if (cur.Current.Kind == TokKind.Number)
                {
                    int cn2 = cur.Current.Value;
                    cur.Advance();
                    cur.TryMatchAnyWord("or");
                    var cop2 = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                    cur.Advance();
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = cop2,
                        Right = NumExpr.Const(cn2),
                    });
                    matched = true;
                    continue;
                }
            }

            // with a base cost of N or more/less
            if (cur.MatchPhrase("with", "a", "base", "cost", "of"))
            {
                if (cur.Current.Kind == TokKind.Number)
                {
                    int cn = cur.Current.Value;
                    cur.Advance();
                    cur.TryMatchAnyWord("or");
                    var cop = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                    cur.Advance();
                    wheres.Add(new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = cop,
                        Right = NumExpr.Const(cn),
                    });
                    matched = true;
                    continue;
                }
            }

            // with N defense/attack or less/more
            if (cur.Current.IsAnyWord("with") && cur.Peek().Kind == TokKind.Number
                && cur.Peek(2).IsAnyWord("attack", "defense", "life"))
            {
                cur.Advance();
                int n = (int)cur.Current.Value;
                cur.Advance();
                var stat = cur.Current.Text.ToLowerInvariant().StartsWith("attack") ? StatKey.Attack : StatKey.Life;
                cur.Advance();
                cur.TryMatchAnyWord("or");
                var op = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(stat, TargetSpec.Item),
                    Op = op,
                    Right = NumExpr.Const(n),
                });
                matched = true;
                continue;
            }
            // that costs N or less
            if (cur.Current.IsAnyWord("that", "costs", "cost") && cur.Peek().IsAnyWord("costs", "cost")
                && cur.Peek(2).Kind == TokKind.Number)
            {
                cur.Advance(); cur.Advance();
                int n = (int)cur.Current.Value;
                cur.Advance();
                cur.TryMatchAnyWord("or");
                var op = cur.Current.IsAnyWord("less") ? CompareOp.Le : CompareOp.Ge;
                cur.Advance();
                wheres.Add(new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                    Op = op,
                    Right = NumExpr.Const(n),
                });
                matched = true;
                continue;
            }
            break;
        }
        LastPostfixConsumed = cur.Position - postLoopStart;

        if (!matched && !hasCardType && wheres.Count == 0 && cardId == 0)
        {
            cur.Seek(start);
            return null;
        }

        BoolExpr where = wheres.Count switch
        {
            0 => BoolExpr.True,
            1 => wheres[0],
            _ => BoolExpr.And(wheres.ToArray()),
        };

        // 战场与主战者是**公共博弈区**：卡面没写 allied 也没写 enemy 时
        // （"all followers" / "a follower on the field" / "6 other random followers"）
        // 默认归属是双方。手牌 / 牌库 / 墓地 / 纹章区是私有区，未指明才是己方。
        var resolvedZone = history == Core.HistoryScope.None ? zone
                         : history == Core.HistoryScope.EnteredThisMatch ? ZoneRef.Board
                         : ZoneRef.Cemetery;
        // 纹章区同样按「公共博弈区」处理："all crests" 这类没写归属的
        // 说法应指向双方（与 "all followers" 一致），否则 "Banish all crests."
        // 又会退化成只消我方。
        var defaultOwner = (resolvedZone == ZoneRef.Board || resolvedZone == ZoneRef.Leader
                         || resolvedZone == ZoneRef.CrestArea)
            ? ZoneScope.Any
            : ZoneScope.Self;

        return new TargetSpec
        {
            // 历史池是**决定性**的：既然说的是「本局/本回合被破坏过的」，
            // 候选就一定来自历史记录，不该指向任何具体区域。
            // zone 在解析过程中会被卡种 / 归属短语反复改写（followers 会把它设成
            // Board），若不在最后归一，"allied Shikigami followers destroyed
            // this match" 就会去 Board 上数 —— 那里一个被破坏的都没有。
            // slot 优先："drawn" 之类会写入 $created
            SourceKind = slot != null ? TargetSourceKind.Slot
                       : cardId > 0 ? TargetSourceKind.CardId
                       : TargetSourceKind.Zone,
            Slot = slot ?? "",
            Owner = ownerSet ? owner : defaultOwner,
            Zone = resolvedZone,
            CardId = cardId,
            CardName = cardName,
            Where = where,
            Mode = mode,
            Count = count,
            Sort = sort,
            SortDescending = desc,
            DistinctByName = distinct,
            DuplicatesOnly = duplicatesOnly,
            IncludeLeader = includeLeader,
            History = history,
        };
    }

    /// <summary>解析序数词：first/1st/second/2nd/…/ninth/9th；认不出返回 0。</summary>
    private static int ParseOrdinalWord(TokenCursor cur)
    {
        if (cur.IsAtEnd) return 0;
        string t = (cur.Current.Text ?? "").ToLowerInvariant();
        int n = t switch
        {
            "first" or "1st" => 1,
            "second" or "2nd" => 2,
            "third" or "3rd" => 3,
            "fourth" or "4th" => 4,
            "fifth" or "5th" => 5,
            "sixth" or "6th" => 6,
            "seventh" or "7th" => 7,
            "eighth" or "8th" => 8,
            "ninth" or "9th" => 9,
            _ => 0,
        };
        if (n > 0) { cur.Advance(); return n; }
        // 分词把 "2nd" 拆成 Number(2) + Word("nd") 时也要认。
        if (cur.Current.Kind == TokKind.Number && cur.Peek().IsAnyWord("st", "nd", "rd", "th"))
        {
            n = cur.Current.Value;
            cur.Advance(); cur.Advance();
            return n;
        }
        return 0;
    }

}
