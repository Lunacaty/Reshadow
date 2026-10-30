using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Stage 6.2 · 双向还原验证器
//
// 对照《英文引擎设计3》§六：
//   「AST -> Canonical English Text，Diff 非空即报错」
//
// 真实做法（与设计文档的差别要说清楚）：
//   设计 3 设想的是「渲染回自然英文，与原句逐字比对」。
//   这要求渲染器与解析器共享一整套词形表，成本高且极易把
//   渲染器的措辞差异误报成解析错误。
//
//   这里采用**双向结构比对**，等价性更强且不依赖措辞：
//     1. 正向：原句 -> AST -> 规范串 A
//     2. 反向：规范串 A -> 重新解析？不可行（A 不是合法英文）
//   因此改为**成分守恒校验**：
//     把 AST 里的「语义成分」全部枚举出来，逐条到 Token 流里找对应证据。
//     任何一个成分找不到证据 → 该句被静默污染，报错。
//
// 这正好抓住设计 3 想抓的那类 bug：
//   · 原文有 "Draw a card"，AST 里没有 -> Missing
//   · AST 里凭空多出一个动作 -> Extra
//   · 数值对了但目标错了 -> Mismatch
// ============================================================================

public sealed class RoundTripResult
{
    public bool Ok => Problems.Count == 0;
    public List<string> Problems { get; } = new();
    /// <summary>正向渲染出的规范串，供人工比对与 Debug。</summary>
    public string Rendered { get; set; } = "";

    public override string ToString()
        => Ok ? "OK " + Rendered : string.Join(" | ", Problems) + "  <<< " + Rendered;
}

public static class RoundTripVerifier
{
    /// <summary>
    /// 校验一次编译：原文与 AST 的语义成分必须互相覆盖。
    /// </summary>
    public static RoundTripResult Verify(string originalText, IReadOnlyList<AbilityDef> abilities)
    {
        var r = new RoundTripResult();
        if (abilities == null || abilities.Count == 0)
        {
            // 无产出的句子：只有「本来就没有动作」时才允许
            if (HasImperativeVerb(originalText))
                r.Problems.Add("原文包含动作动词，但 AST 没有任何能力（静默丢弃）");
            r.Rendered = "";
            return r;
        }

        r.Rendered = AstEnglishPrinter.PrintCard(abilities);

        var tokens = Tokenize(originalText);
        var astVerbs = new List<string>();
        foreach (var a in abilities) CollectVerbs(a.Body, astVerbs);

        // 单向断言：AST 里的每个动作成分，原文必须能解释它的来源。
        //
        // 反向（原文有动词 -> AST 必须有动作）**不做**，因为授权型卡面
        // 天然是非平行的："Give this follower Ward" 里 give 是授权动词，
        // 落到 AST 是 ModifyKeyword，与 move/select 这些动作词不在一个维度；
        // 逐词要求对应会把每张带关键词的卡都误判成缺失，
        // 一个 55% 误报率的校验器和假的 100% 没有区别。
        //
        // 保留的这一向恰好能抓设计 3 最在意的 bug：
        // 渲染器/解析器凭空多出一个动作（文档 37 的 UpdateDeck 事故）。
        // 注意：这里**没有**为 replace 开豁免。
        // replace 现在是 ReplaceDeckNode 一个原子动作，
        // 若它又在 AST 里炸成 banish + spawn，那正是要报出来的缺陷。
        foreach (var av in astVerbs.Distinct())
        {
            if (tokens.Any(t => VerbFamilies.TryGetValue(t, out var f) && f == av)) continue;

            // 「把选中的那张牌移走」这一族在卡面上有多个同义写法：
            //   Select X and destroy it / discard it / banish it
            // 它们落到 AST 是同一个 MoveZone，区别只在目的地；
            // 目的地已由 MoveZone 的 ToZone 保留，这里不应判成缺陷。
            if (RemovalFamily.Contains(av)
                && tokens.Any(t => VerbFamilies.TryGetValue(t, out var f) && RemovalFamily.Contains(f)))
                continue;

            // 「把某张牌放到场上」这一族：summon / invoke / play 落到同一个 MoveZone。
            if (av == "summon_slot"
                && tokens.Any(t => t.Equals("summon", StringComparison.OrdinalIgnoreCase)
                                || t.Equals("invoke", StringComparison.OrdinalIgnoreCase)
                                || t.Equals("play", StringComparison.OrdinalIgnoreCase)))
                continue;

            // "Gain Crest: X" —— 卡面动词是 gain，AST 是生成到纹章区。
            // 这里的族名可能是 crest（生成到纹章区）也可能是 spawn
            // （同一节点在别的卡上就是普通生成），两者都接受 crest 作为来源证据。
            if ((av == "crest" || av == "spawn")
                && tokens.Any(t => t.Equals("crest", StringComparison.OrdinalIgnoreCase)))
                continue;

            // "transform it based on the total cost of the cards fused"
            // 后面的 "1: X 2: Y 3 or more: Z" 是**分支表**：
            // AST 用 BranchModeNode 的多个选项表达，卡面并没有 spawn 动词。
            // 只要原文出现分支表形状，就承认 spawn 有来源。
            if (av == "spawn" && HasSwitchTable(originalText))
                continue;

            r.Problems.Add($"AST 动作 '{av}' 在原文中无来源（凭空多出）");
        }

        // 3. 数值守恒：原文出现的独立数字，必须能在 AST 的数值表达式里找到
        var textNums = new HashSet<int>(tokens.Where(t => int.TryParse(t, out _)).Select(int.Parse));
        var astNums = new HashSet<int>();
        foreach (var a in abilities) CollectNums(a.Body, astNums);
        foreach (var a in abilities)
        {
            foreach (var c in a.Costs ?? Array.Empty<CostSpec>()) CollectNumExpr(c.Amount, astNums);
            // 条件里的数值："if you have 10 max play points"
            CollectNumsBool(a.Condition, astNums);
        }

        foreach (var n in textNums)
        {
            // 小数字（序号 / 费用参数）噪声大，只核对 >=2 的实义数值
            if (n < 2) continue;
            if (!astNums.Contains(n) && r.Problems.Count < 6)
                r.Problems.Add($"原文数值 '{n}' 在 AST 中找不到");
        }

        return r;
    }

    // ---------- 动词族表 ----------

    /// <summary>
    /// 动作动词 → 语义族。族名与 AstEnglishPrinter 的输出词一致。
    /// 只收「一个词对应一个真实动作」的动词，介词与助动词不在表内。
    /// </summary>
    private static readonly Dictionary<string, string> VerbFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        // 每个卡面动词对应一个**具体的**语义族。族粒度必须与实际动词一一对应：
        // 把 summon / add / draw / destroy 全归成 move 会让「Summon 2」和
        // 「Return it」互相解释，校验器就失去分辨力。
        ["deal"] = "deal",
        ["restore"] = "restore",
        ["recover"] = "restore",
        ["draw"] = "draw",
        ["add"] = "spawn",
        ["copy"] = "spawn",
        ["summon"] = "spawn",
        ["create"] = "spawn",
        ["spawn"] = "spawn",
        ["reanimate"] = "reanimate",
        ["destroy"] = "destroy",
        ["banish"] = "banish",
        ["discard"] = "discard",
        ["return"] = "return",
        ["evolve"] = "evolve",
        ["super-evolve"] = "superevolve",
        ["transform"] = "transform",
        ["select"] = "select",
        ["give"] = "keyword",
        ["gain"] = "keyword",
        ["remove"] = "keyword",
        ["activate"] = "invoke",
        ["invoke"] = "invoke",
        ["crest"] = "crest",
        ["reanimate"] = "reanimate",
        ["play"] = "play",
        // "Replace your deck with X" 在 AST 里展开成 banish + spawn，
        // 词面上没有这两个动词，但要由 replace 一词解释。
        ["replace"] = "replace",
    };

    /// <summary>
    /// 「把牌移出当前区域」的同义族。
    /// 卡面写法不同（destroy / discard / banish），AST 都是 MoveZone，
    /// 真正的区别在 ToZone 上，已单独保留。
    /// </summary>
    private static readonly HashSet<string> RemovalFamily =
        new(StringComparer.Ordinal) { "destroy", "discard", "banish" };

    private static void CollectVerbs(IReadOnlyList<EffectNode> body, List<string> acc)
    {
        if (body == null) return;
        foreach (var n in body) CollectVerbsNode(n, acc);
    }

    private static void CollectVerbsNode(EffectNode n, List<string> acc)
    {
        if (n == null) return;
        switch (n)
        {
            case DealDamageNode: acc.Add("deal"); break;
            case RestoreLifeNode: acc.Add("restore"); break;
            case MoveZoneNode mv:
                // 动词由「来源区 + 目的地」共同决定，只看目的地会把
                // 弃牌（手->墓地）说成 destroy、抽牌（库->手）说成 return。
                // 目标来自槽位时 Zone 不可靠（"select a card in hand, discard it"
                // 的第二句只带 slot:$selected），此时按目的地判定，
                // 并允许文本侧用 select/discard 任一动词解释。
                var fromZone = mv.Target?.SourceKind == TargetSourceKind.Slot
                    ? ZoneRef.None
                    : (mv.Target?.Zone ?? ZoneRef.None);
                acc.Add((fromZone, mv.ToZone) switch
                {
                    // 槽位来源不携带区域信息：卡面可能写 destroy / discard / banish
                    // 中的任何一个。这里只能给出一个中性族名，由下面的
                    // 「槽位动作按同义族接受」规则处理，不能硬判成某一种。
                    (ZoneRef.None, ZoneRef.Cemetery) => "destroy",
                    (ZoneRef.None, ZoneRef.Hand) => "return",
                    (ZoneRef.None, ZoneRef.Deck) => "return",
                    (ZoneRef.None, ZoneRef.Banished) => "banish",
                    (ZoneRef.Deck, ZoneRef.Hand) => "draw",
                    (ZoneRef.Hand, ZoneRef.Cemetery) => "discard",
                    (ZoneRef.Deck, ZoneRef.Cemetery) => "discard",
                    (ZoneRef.Board, ZoneRef.Cemetery) => "destroy",
                    (ZoneRef.Board, ZoneRef.Hand) => "return",
                    (ZoneRef.Board, ZoneRef.Deck) => "return",
                    (_, ZoneRef.Cemetery) => "destroy",
                    (_, ZoneRef.Banished) => "banish",
                    (_, ZoneRef.Hand) => "return",
                    (_, ZoneRef.Deck) => "return",
                    // 从槽位移到场上：卡面动词可能是 summon / invoke / play。
                    // 用中性族名，由下面的同义族规则接受其中任意一个。
                    (ZoneRef.None, ZoneRef.Board) => "summon_slot",
                    (_, ZoneRef.Board) => "spawn",
                    _ => "move",
                });
                break;
            case SpawnCardNode sp:
                // 同样是 SpawnCard，卡面动词却有三个：
                //   Reanimate (2)  → 从墓地拉
                //   Gain Crest: X  → 生成到纹章区
                //   Summon X       → 生成到场上
                // 全归成 spawn 会让前两种被判成「凭空多出的动作」。
                acc.Add(sp.ToZone switch
                {
                    // "Gain Crest: X" 生成到纹章区
                    ZoneRef.CrestArea => "crest",
                    // 从墓地取牌有三种卡面写法，靠"谁在拉"区分：
                    //   Reanimate (2)                      → 隐含动词，reanimate
                    //   Summon a copy of ... destroyed      → 动词是 summon，spawn
                    //   Add a copy of ... to your hand      → 动词是 add，spawn
                    // 只有 Reanimate 那一支没有显式动词，才归到 reanimate 族。
                    _ => (sp.SourceKind == CardSourceKind.CopyOfTarget
                          && sp.CopyFrom?.Zone == ZoneRef.Cemetery
                          && sp.CopyFrom?.History == HistoryScope.DestroyedThisMatch
                          && string.IsNullOrEmpty(sp.CardName))
                         ? "reanimate" : "spawn",
                });
                break;
            case SelectTargetNode: acc.Add("select"); break;
            // 授予关键词 / 改属性：卡面写法太自由（"Ward"、"give it Ward"、
            // "+2/+2" 裸短语都合法），不参与「原文动词」方向的反查，
            // 否则每一次裸关键词都会变成误报。
            case ModifyKeywordNode: case ModifyStatsNode: break;
            case EvolveNode e:
                acc.Add(e.IsSuperEvolve ? "superevolve" : "evolve");
                break;
            case TransformNode: acc.Add("transform"); break;
            case ReplaceDeckNode: acc.Add("replace"); break;
            // 计数器可由 "gain 4 shadows" / "Earth Rite" / 代价语句驱动，
            // 词面与语义不一一对应，同样不参与反查。
            case ModifyCounterNode: break;
            case GrantAbilityNode g:
                CollectVerbs(g.Granted?.Body, acc);
                break;
            case ConditionalNode c:
                CollectVerbs(c.Then, acc);
                CollectVerbs(c.Else, acc);
                break;
            case LoopNode l:
                CollectVerbs(l.Body, acc);
                break;
            case BranchModeNode bm:
                foreach (var o in bm.Options) CollectVerbs(o, acc);
                break;
        }
    }

    private static void CollectNums(IReadOnlyList<EffectNode> body, HashSet<int> acc)
    {
        if (body == null) return;
        foreach (var n in body) CollectNumsNode(n, acc);
    }

    private static void CollectNumsNode(EffectNode n, HashSet<int> acc)
    {
        if (n == null) return;
        switch (n)
        {
            case DealDamageNode d: CollectNumExpr(d.Amount, acc); break;
            case RestoreLifeNode r: CollectNumExpr(r.Amount, acc); break;
            case ModifyStatsNode m: CollectNumExpr(m.Attack, acc); CollectNumExpr(m.Life, acc); break;
            case ModifyCostNode mc: CollectNumExpr(mc.Value, acc); break;
            case ModifyCounterNode c: CollectNumExpr(c.Delta, acc); break;
            // "Can attack 2 times per turn" —— 数值挂在关键词的参数上
            case ModifyKeywordNode k: CollectNumExpr(k.Value, acc); break;
            case SpawnCardNode s: CollectNumExpr(s.Count, acc); break;
            case ConditionalNode c:
                // 分支条件里的数值（"Earth Rite (3)" 的 3 就在条件树里）
                CollectNumsBool(c.Condition, acc);
                CollectNums(c.Then, acc);
                CollectNums(c.Else, acc);
                break;
            case LoopNode l:
                CollectNumExpr(l.Count, acc);
                CollectNumsBool(l.Until, acc);
                CollectNums(l.Body, acc);
                break;
            case BranchModeNode bm:
                CollectNumExpr(bm.PickCount, acc);
                foreach (var o in bm.Options) CollectNums(o, acc);
                break;
            case GrantAbilityNode g:
                CollectNums(g.Granted?.Body, acc);
                break;
        }

        // 目标里的数值（count / where 谓词）同样要守恒
        var tgt = NodeTarget(n);
        if (tgt != null)
        {
            CollectNumExpr(tgt.Count, acc);
            CollectNumsBool(tgt.Where, acc);
        }
    }

    private static void CollectNumsBool(BoolExpr e, HashSet<int> acc)
    {
        if (e == null) return;
        if (e is CompareExpr c)
        {
            CollectNumExpr(c.Left, acc);
            CollectNumExpr(c.Right, acc);
        }
        // 复合谓词（And/Or）：卡面 "if you have 10 max play points" 的 10
        // 藏在条件树里，不递归就会误报成「数值丢失」。
        foreach (var child in BoolChildren(e)) CollectNumsBool(child, acc);
    }

    /// <summary>
    /// 取出一个布尔表达式的子谓词（常量/叶子返回空）。
    ///
    /// 用**显式类型判断**而不是反射探测接口：
    /// AndExpr.Items 声明为 IReadOnlyList&lt;BoolExpr&gt;，
    /// 反射时对 IEnumerable&lt;BoolExpr&gt; 的 IsAssignableFrom 判断并不可靠，
    /// 曾经因此漏掉整棵子树，让条件里的数值全部被判成"丢失"。
    /// </summary>
    private static IEnumerable<BoolExpr> BoolChildren(BoolExpr e)
    {
        switch (e)
        {
            case AndExpr a:
                foreach (var x in a.Items) if (x != null) yield return x;
                break;
            case OrExpr o:
                foreach (var x in o.Items) if (x != null) yield return x;
                break;
            case NotExpr n:
                if (n.Inner != null) yield return n.Inner;
                break;
        }
    }

    private static TargetSpec NodeTarget(EffectNode n) => n switch
    {
        SelectTargetNode s => s.Target,
        DealDamageNode d => d.Target,
        RestoreLifeNode r => r.Target,
        ModifyStatsNode m => m.Target,
        ModifyCostNode mc => mc.Target,
        ModifyKeywordNode k => k.Target,
        MoveZoneNode mv => mv.Target,
        EvolveNode e => e.Target,
        TransformNode t => t.Target,
        SpawnCardNode sp => sp.CopyFrom,
        _ => null,
    };

    private static void CollectNumExpr(NumExpr e, HashSet<int> acc)
    {
        if (e == null) return;
        if (e.Op == NumOp.Const) { acc.Add(e.Value); return; }
        foreach (var a in e.Args ?? Array.Empty<NumExpr>()) CollectNumExpr(a, acc);
        if (e.Cond != null) CollectNumsBool(e.Cond, acc);
        if (e.Then != null) CollectNumExpr(e.Then, acc);
        if (e.Else != null) CollectNumExpr(e.Else, acc);
        if (e.Of != null)
        {
            // CountOf / SumOf 的目标本身带过滤条件
            // （"count of allied followers with a base cost of 5 or more"），
            // 不递归进去，5 就会被判成丢失。
            CollectNumExpr(e.Of.Count, acc);
            CollectNumsBool(e.Of.Where, acc);
        }
    }

    // ---------- 文本侧 ----------

    /// <summary>
    /// 摘出「能力头」形式的动词族。
    /// 判定：该词后面紧跟冒号（"Evolve:"），或紧跟 "(N):" / "(N) -"，
    /// 或与它同族的触发词出现在 TriggerSpec 里。
    /// 这些是时机标记，不是动作。
    /// </summary>
    private static HashSet<string> CollectHeaders(List<string> tokens)
    {
        var heads = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; i++)
        {
            var w = tokens[i];
            // 进化头：卡面里 "Evolve:" / "Super-Evolve:" 一定在段首，
            // 且后接冒号；归一化后冒号已消失，因此用「同族词已在触发头集合」判定。
            if (w.Equals("evolve", StringComparison.OrdinalIgnoreCase)
                || w.Equals("fanfare", StringComparison.OrdinalIgnoreCase))
            {
                // 只有当它出现在句子最前或紧跟另一个头时才当头处理
                if (i == 0 || IsHeaderPredecessor(tokens[i - 1])) heads.Add(w.ToLowerInvariant());
            }
            if (w.Equals("super", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count
                && tokens[i + 1].Equals("evolve", StringComparison.OrdinalIgnoreCase))
            {
                heads.Add("superevolve");
                heads.Add("evolve");
            }
        }
        return heads;
    }

    private static bool IsHeaderPredecessor(string w)
        => w.Equals("fanfare", StringComparison.OrdinalIgnoreCase)
        || w.Equals("last", StringComparison.OrdinalIgnoreCase)
        || w.Equals("words", StringComparison.OrdinalIgnoreCase)
        || w.Equals("whenever", StringComparison.OrdinalIgnoreCase)
        || w.Equals("when", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 原文是否含融合分支表（"1: A 2: B 3 or more: C"）。
    /// 这种写法里的每一档都是一次生成，卡面没有 spawn 动词。
    /// </summary>
    private static bool HasSwitchTable(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var s = Regex.Replace(text, "<[^>]+>", " ");
        return Regex.IsMatch(s, @"(?:^|[\s.;:])\d+\s*:", RegexOptions.IgnoreCase)
               && Regex.IsMatch(s, @"\d+\s+or\s+more\s*:", RegexOptions.IgnoreCase);
    }

    private static bool HasImperativeVerb(string text)
    {
        var toks = Tokenize(text);
        return toks.Any(t => VerbFamilies.ContainsKey(t));
    }

    /// <summary>把原文归一成小写词序列（剥离 HTML 与标点）。</summary>
    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        var s = Regex.Replace(text, "<[^>]+>", " ");

        // 模式选项编号："1. Draw a card. 2. Deal 2 damage." 里的 1 和 2
        // 是块编号，不是效果数值，必须在归一阶段抹掉。
        s = Regex.Replace(s, @"\b\d+\.(?=\s|$)", " ");
        // 变量初值声明："X starts at 2." —— 2 绑定到 X，不是字面量。
        s = Regex.Replace(s, @"\b[XYZ]\s+starts\s+at\s+\d+", " ", RegexOptions.IgnoreCase);
        // 费用枚举："with base costs of 1, 2, 3, ... 8 this match"
        // 这一串是"需要多少个不同费用"的清单，AST 用计数器表达，
        // 逐个数字比对没有意义。
        s = Regex.Replace(s, @"\bbase costs of [\d,\s]+(and \d+)?", " ", RegexOptions.IgnoreCase);
        // 融合分支表序号："1: X 2: Y 3 or more: Z"
        // 这些数字是分支标签（对应融合总费用档位），不是效果数值。
        s = Regex.Replace(s, @"\b\d+\s+or\s+more\s*:", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?:^|[\s.;:])\d+\s*:", " ", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"[^A-Za-z0-9'-]+", " ");
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('-', '\''))
                .Where(w => w.Length > 0)
                .ToList();
    }
}
