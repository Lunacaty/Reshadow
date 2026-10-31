using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 战斗状态 —— IVmWorld 的真实实现
//
// 它持有两个绝对对等的 PlayerState，是唯一的规则权威。
// MemoryWorld 只是自测用的假世界，这个才是真正跑对局的世界。
//
// 三条不变量（写死在代码里，不是文档）：
//   1. 任何操作只影响 PlayerId 指定的那个玩家，绝不「顺带」改对手
//   2. 区域容量在进入时检查，超限直接去墓地/消失，不触发入场事件
//   3. 只有被「破坏」才触发 OnDestroy 并让 shadow+1；放逐/回手/变形都不算
// ============================================================================

public sealed class BattleState : IVmWorld
{
    public PlayerState[] Players { get; } = new PlayerState[2];

    /// <summary>当前回合玩家（0 或 1）。</summary>
    public int ActivePlayer { get; set; }

    /// <summary>回合数，先手第 1 回合开始计。</summary>
    public int TurnNumber { get; set; }

    public GameSchema Schema { get; }
    public Random Rng { get; }

    private readonly Dictionary<int, CardDef> _cardPool = new();
    private readonly List<RuntimeEntity> _all = new();
    private readonly List<(RuntimeEntity e, AbilityDef ab)> _granted = new();
    private readonly List<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> _grantedCounters = new();
    private int _nextEntityId = 1;

    public BattleState(GameSchema schema = null, int seed = 0)
    {
        Schema = schema ?? GameSchema.CreateDefault();
        Rng = seed == 0 ? new Random() : new Random(seed);
        Players[0] = new PlayerState { PlayerId = 0 };
        Players[1] = new PlayerState { PlayerId = 1 };
    }

    /// <summary>
    /// 开局扫描信仰（R-1.2 / c-2.1 第 2 步）。
    /// 牌组里每张「带信仰能力」的卡，在开局就在纹章区生成一个对应信仰实体，
    /// 初始信仰值 = 0。同名信仰只生成一个。
    /// 只有开局就在牌组里的卡才会生成信仰，对战中途加入的不算。
    /// </summary>
    public void SetupFaith()
    {
        var faithKey = Schema.Counter("faith");
        for (int pid = 0; pid < 2; pid++)
        {
            var p = Players[pid];
            var seen = new HashSet<int>();
            foreach (var card in p.Deck)
            {
                if (card.Def == null) continue;
                if (!DefinesFaith(card.Def)) continue;
                if (!seen.Add(card.Def.CardId)) continue;

                // 信仰实体用 Faith 卡种（不是 Crest），
                // 这样「纹章相关效果」会自然排除它。
                int fid = FaithEntityIdBase + card.Def.CardId;
                if (!_cardPool.ContainsKey(fid))
                {
                    // 优先用主人卡内嵌的定义（带 Abilities）：
                    // 卡面正文只写「信仰」，它的效果文本在主人卡的 alt_modes 里。
                    // 不接这条，开局生成的信仰就是个没有能力的空壳。
                    var fd = card.Def.FaithDef;
                    _cardPool[fid] = fd != null
                        ? new CardDef
                        {
                            CardId = fid,
                            Name = fd.Name ?? card.Def.Name,
                            Kind = CardKind.Faith,
                            BaseCost = fd.BaseCost,
                            Abilities = fd.Abilities,
                            InnateKeywords = fd.InnateKeywords,
                            InnateCounters = fd.InnateCounters,
                            SourceText = fd.SourceText,
                        }
                        : new CardDef
                        {
                            CardId = fid,
                            Name = card.Def.Name,
                            Kind = CardKind.Faith,
                            BaseCost = 0,
                        };
                }
                var fe = CreateEntity(fid, ZoneRef.CrestArea, pid);
                if (fe == null) continue;
                fe.IsFaith = true;
                fe.FaithOwner = pid;
                fe.Counters[faithKey] = 0;
            }
        }
    }

    /// <summary>该卡是否定义了信仰（卡面提到 faith）。</summary>
    private static bool DefinesFaith(CardDef def)
    {
        if (def.Abilities == null) return false;
        foreach (var ab in def.Abilities)
        {
            if (ab == null) continue;
            if (MentionsFaith(ab.Condition)) return true;
            foreach (var n in ab.Body ?? Array.Empty<EffectNode>())
                if (MentionsFaithNode(n)) return true;
        }
        return false;
    }

    private static bool MentionsFaithNode(EffectNode n)
        => n != null && MentionsFaithNum(ReflectNum(n));

    private static NumExpr ReflectNum(EffectNode n) => n switch
    {
        // ModifyCounterNode 的 Counter 就是目标计数器：指向 faith 就说明该卡用到信仰。
        // 不能去 Delta 里找 —— "Reduce your faith's value by 10" 的 Delta 是算术式。
        ModifyCounterNode mc => mc.Counter.Name == "faith"
            ? NumExpr.Ctr(mc.Counter)
            : mc.Delta,
        RestoreLifeNode r => r.Amount,
        _ => null,
    };

    private static bool MentionsFaithNum(NumExpr e)
    {
        if (e == null) return false;
        if (e.Op == NumOp.Counter && e.Counter.Name == "faith") return true;
        if (e.Args != null) foreach (var a in e.Args) if (MentionsFaithNum(a)) return true;
        return false;
    }

    private static bool MentionsFaith(BoolExpr b)
    {
        if (b is CompareExpr c) return MentionsFaithNum(c.Left) || MentionsFaithNum(c.Right);
        if (b is AndExpr a) return a.Items.Any(MentionsFaith);
        if (b is OrExpr o) return o.Items.Any(MentionsFaith);
        return false;
    }

    // ---------- 卡池 ----------    // ---------- 卡池 ----------

    public void RegisterCard(CardDef def) => _cardPool[def.CardId] = def;

    public CardDef CardDefOf(int cardId)
        => _cardPool.TryGetValue(cardId, out var d) ? d : null;

    // ---------- 计数器：一律按绝对玩家编号 ----------

    public int ReadCounter(int playerId, SymbolId<CounterTag> counter)
    {
        if (playerId < 0 || playerId > 1) return 0;

        // 信仰值住在信仰实体身上，不在玩家身上。
        // 这样做的好处：信仰占纹章区一格、同名唯一、而且可以单独被授予能力，
        // 同时不会被「纹章造成伤害」这类效果误伤。
        var faith = FindFaith(playerId);
        if (faith != null && counter.Name == "faith")
            return faith.Counters.TryGetValue(counter, out var fv) ? fv : 0;

        if (counter.Name == "earth_sigil")
        {
            var sigil = FindEarthSigil(playerId);
            if (sigil != null) return sigil.Counters.TryGetValue(counter, out var sv) ? sv : 0;
        }
        if (counter.Name == "clues")
        {
            var clue = FindClue(playerId);
            if (clue != null) return clue.Counters.TryGetValue(counter, out var cv) ? cv : 0;
        }
        // crest 作为玩家级读取时，语义是「你有几个纹章」——
        // 以纹章区里的实体个数为准，不读那个镜像计数器。
        //
        // **必须排除信仰**：信仰与纹章共用纹章区（两者都占一格、都同名唯一），
        // 但 76-c 第一章明写「在卡牌文本判定中，纹章不包括信仰」。
        // 不排除的话，3 纹章 + 1 信仰会读成 4，所有「你有 N 个纹章」都偏大。
        if (counter.Name == "crest")
            return Players[playerId].CrestArea.Count(e => e != null && !e.IsFaith);

        return Players[playerId].Counters.TryGetValue(counter.Id, out var v) ? v : 0;
    }

    public void WriteCounter(int playerId, SymbolId<CounterTag> counter, int value)
    {
        if (playerId < 0 || playerId > 1) return;

        var faith = FindFaith(playerId);
        if (faith != null && counter.Name == "faith")
        {
            faith.Counters[counter] = Schema.ClampCounter(counter, value);
            return;
        }

        // 土之印 / 线索的层数都住在场上的护符实体上（R-6.2）。
        // 写入时若没有护符且值 > 0，先召唤一个。
        if (counter.Name == "earth_sigil" || counter.Name == "clues")
        {
            bool isSigil = counter.Name == "earth_sigil";
            var holder = isSigil ? FindEarthSigil(playerId) : FindClue(playerId);
            int clamped = Schema.ClampCounter(counter, value);

            if (holder == null)
            {
                if (clamped <= 0) { Players[playerId].Counters[counter.Id] = 0; return; }
                holder = isSigil ? SummonEarthSigil(playerId) : SummonClue(playerId);
                if (holder == null) { Players[playerId].Counters[counter.Id] = clamped; return; }
            }

            holder.Counters[counter] = clamped;
            Players[playerId].Counters[counter.Id] = clamped;

            // 层数归 0：护符立即破坏
            if (clamped <= 0) DestroyEntity(holder, byRule: true);
            return;
        }

        // 钳制到该计数器自己的范围（pp 0..10 / ep 0..2 …）
        Players[playerId].Counters[counter.Id] = Schema.ClampCounter(counter, value);
    }

    /// <summary>Magic Sediment（土之印衍生护符，90031210）。
    /// 场上最多只存在 1 个；Gain N earth sigils 若场上已有，直接给它加层；
    /// 若没有，召唤一个层数为 N 的 Magic Sediment。</summary>
    public const int MagicSedimentCardId = 90031210;

    /// <summary>默认 Clue 护符：Divine Evidence（90001210）。</summary>
    public const int ClueDefaultCardId = 90001210;

    /// <summary>信仰实体的卡 ID 偏移量，避免与真实卡冲突。</summary>
    public const int FaithEntityIdBase = 900000;

    /// <summary>
    /// 获得一个纹章（R-6.2）。
    /// 纹章不是普通召唤：它自有同名唯一与区域上限两道门槛，
    /// 而且两道都是「安静失败」——不进墓地、不触发任何事件。
    /// </summary>
    public RuntimeEntity GainCrest(int cardId, string cardName, int playerId, CardDef crestDef = null)
    {
        if (playerId is < 0 or > 1) return null;

        CardDef def = null;

        // 优先用主人卡内嵌的定义：那是唯一带 Abilities 的来源。
        // 没有它才会退到卡池按名字查 —— 而那一步有两个已知陷阱：
        //   1. 查到的多半是空壳，纹章拿得到但没有能力；
        //   2. 若卡池里存在**同名随从**（Aria, Lady of the Woods 就是），
        //      会把随从本体当成纹章塞进纹章区，卡种、身材全错。
        if (crestDef != null)
        {
            // 树章必须有自己的号段 id，**绝不能沿用主人卡的 CardId**：
            // Aria 的随从卡与她的纹章同名不同物，共用 id 会让 CreateEntity
            // 从卡池取到随从定义 —— 纹章的 Abilities 直接变成空。
            // 一律走 ResolveCrestCardId 合成（它在同一卡名下是幂等的）。
            int synth = ResolveCrestCardId(crestDef.Name ?? cardName ?? CrestFallbackName);
            if (synth <= 0) return null;
            def = crestDef.CardId == synth
                ? crestDef
                : new CardDef
                {
                    CardId = synth,
                    Name = crestDef.Name,
                    Kind = crestDef.Kind,
                    BaseCost = crestDef.BaseCost,
                    BaseAttack = crestDef.BaseAttack,
                    BaseLife = crestDef.BaseLife,
                    Abilities = crestDef.Abilities,
                    InnateKeywords = crestDef.InnateKeywords,
                    InnateCounters = crestDef.InnateCounters,
                    SourceText = crestDef.SourceText,
                };
            cardId = synth;
            _cardPool[cardId] = def;
        }

        if (def == null && cardId > 0) _cardPool.TryGetValue(cardId, out def);
        if (def == null && !string.IsNullOrEmpty(cardName))
        {
            // 按名字兜底时**只认 Crest 卡种**：绝不能把同名随从当纹章。
            foreach (var kv in _cardPool)
            {
                if (kv.Value == null || kv.Value.Name != cardName) continue;
                if (kv.Value.Kind != CardKind.Crest && kv.Value.Kind != CardKind.Faith) continue;
                def = kv.Value; cardId = kv.Key; break;
            }
        }
        if (def == null)
        {
            // 卡池里没有这张纹章：按卡名合成一个 cardId，保证同名唯一能生效。
            // 绝不能用 string.GetHashCode()：.NET 默认开启字符串哈希随机化
            // （UseRandomizedStringHashAlgorithm），同一个卡名在两个进程里会得到
            // 不同的值，于是「同名唯一」「存档 / 回放 / 网络同步」全部失配。
            //
            // 旧实现另有两处隐患，这里一并收口：
            //   · '%' 在 C# 里可以为负，取负号之后基址丢失，id 会掉出保留号段；
            //   · 撞车时 if (!ContainsKey) 静默跳过写入，两个不同卡名共用同一 cardId。
            int synthId = ResolveCrestCardId(cardName ?? CrestFallbackName);
            if (synthId <= 0) return null;   // 号段耗尽：安静失败，绝不覆盖已有卡
            cardId = synthId;
            _cardPool.TryGetValue(cardId, out def);
        }
        if (def == null) return null;

        // 纹章必须以 Crest 卡种进入纹章区。
        var e = CreateEntity(cardId, ZoneRef.CrestArea, playerId);
        if (e == null) return null;
        if (e.Zone != ZoneRef.CrestArea) return null;   // 同名或满员：安静失败

        // 纹章带自己的计数器（初始 1）。
        // "Delay the counts of all your crests by 1" 改的就是它，
        // 而不是玩家级的 crest 计数器（那个是「你有几个纹章」）。
        var crestKey = Schema.Counter("crest");
        e.Counters[crestKey] = 1;

        // 「When you gain this crest」的触发点：纹章进入纹章区就算获得。
        // 不发这条事件，Mjerrabaine 那类「获得纹章时换牌库」永远不会执行。
        NotifySummoned(e);
        return e;
    }

    /// <summary>纹章实体的卡 ID 偏移量（旧号段，已废弃：只覆盖 1e5 个槽位，且原实现
    /// 依赖随机化的 string.GetHashCode）。仅为兼容既有引用保留；
    /// 新的合成走 <see cref="CrestSynthCardIdBase"/>。</summary>
    public const int CrestEntityIdBase = 970000;

    /// <summary>按卡名合成纹章 cardId 的保留号段起点，区间 [400000000, 499999999]。
    ///
    /// 号段选择依据（实测）：真实 card_id 只落在 [10001110, 90074320]；
    /// 既有保留常量是 FaithEntityIdBase = 900000、LeaderCardIdBase = 990000、
    /// MagicSedimentCardId = 90031210、ClueDefaultCardId = 90001210 —— 全都远低于 4 亿。
    /// 因此这个区间既不与真实卡号重叠，也不与任何既有保留号段重叠。</summary>
    public const int CrestSynthCardIdBase = 400000000;

    /// <summary>合成号段容量。1e8 个槽位下 FNV-1a 的碰撞概率可忽略，
    /// 但仍必须线性探测兜底（见 <see cref="ResolveCrestCardId"/>）。</summary>
    public const int CrestSynthSlots = 100000000;

    private const string CrestFallbackName = "Crest";

    /// <summary>卡名 → 纹章 cardId 的**确定性**映射。
    ///
    /// 哈希是自实现的 FNV-1a-64（按 UTF-16 码元逐字节），只依赖卡名本身，
    /// 与进程、平台、.NET 的字符串哈希随机化无关 —— 同一卡名在任何进程、
    /// 任何调用顺序下都得到同一个 id。
    ///
    /// 防碰撞：算出的槽位若已被**另一个**卡名占用，按 probe = 1, 2, … 换偏移基
    /// 重算，直到找到空槽或命中同名。全程绝不覆盖卡池里已有的定义，
    /// 也不会静默返回一个撞车的 id。
    ///
    /// 返回 0 表示号段探尽，调用方按安静失败处理。</summary>
    private int ResolveCrestCardId(string cardName)
    {
        if (string.IsNullOrEmpty(cardName)) cardName = CrestFallbackName;

        // 同一个卡名在本进程内已分配过：直接复用。
        if (_crestNameToCardId.TryGetValue(cardName, out int known)) return known;

        string ownerName = null;
        for (int probe = 0; probe < CrestSynthSlots; probe++)
        {
            int id = CrestSynthCardIdBase + (int)(CrestNameHash(cardName, probe) % (ulong)CrestSynthSlots);

            if (!_cardPool.TryGetValue(id, out var occupant))
            {
                _cardPool[id] = new CardDef { CardId = id, Name = cardName, Kind = CardKind.Crest };
                _crestNameToCardId[cardName] = id;
                return id;
            }

            // 槽位被占：同名则复用（可能是别处注册进来的定义），异名则继续探测。
            if (occupant != null && occupant.Name == cardName)
            {
                _crestNameToCardId[cardName] = id;
                return id;
            }
            ownerName = occupant?.Name;
        }

        // 号段探尽：不覆盖任何已有卡，交给调用方安静失败。
        LastCrestIdCollision = ownerName;
        return 0;
    }

    /// <summary>FNV-1a-64，按 UTF-16 码元（低字节在前）逐字节喂入。
    /// probe &gt; 0 时换一个偏移基，用于碰撞后的线性探测。
    /// 全程 ulong 运算，不依赖 string.GetHashCode()，因此跨进程稳定。</summary>
    private static ulong CrestNameHash(string name, int probe)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

        ulong h = FnvOffsetBasis ^ (unchecked((ulong)(uint)probe) * GoldenGamma);
        foreach (char c in name)
        {
            h = (h ^ (byte)(c & 0xFF)) * FnvPrime;
            h = (h ^ (byte)((c >> 8) & 0xFF)) * FnvPrime;
        }
        return h;
    }

    /// <summary>卡名 → 合成 cardId 的记忆（同一进程内复用，避免重复探测）。
    /// 跨进程不复用：跨进程靠的仍然是上面的确定性哈希。</summary>
    private readonly Dictionary<string, int> _crestNameToCardId = new();

    /// <summary>最近一次号段探尽时占住槽位的卡名，仅用于诊断。</summary>
    public string LastCrestIdCollision { get; private set; }

    /// <summary>
    /// 土之印与 Clue 护符的保护（R-6.2）：
    /// 它们不能被能力选中，也不能被能力破坏。
    /// 只有层数归 0 的规则性破坏能结束它们。
    /// </summary>
    public bool IsProtectedAmulet(RuntimeEntity e)
        => e?.Def != null
           && (e.Def.CardId == MagicSedimentCardId || e.Def.CardId == ClueDefaultCardId);

    /// <summary>取该玩家场上的 Clue 护符（最多 1 个）。</summary>
    public RuntimeEntity FindClue(int playerId)
    {
        if (playerId < 0 || playerId > 1) return null;
        var clueTrait = Schema.Trait("Clue");
        foreach (var e in Players[playerId].Board)
            if (e.Def != null && (e.Traits.Contains(clueTrait)
                                  || (e.Def.Traits != null && e.Def.Traits.Contains(clueTrait))))
                return e;
        return null;
    }

    /// <summary>
    /// 召唤一个 Clue 护符（默认 Divine Evidence）。
    /// 若场上已有 Clue：旧的那张被【消失 Banish】（会触发它自己的
    /// "When this card is banished from the field" 能力），层数累加到新的上面。
    /// </summary>
    private RuntimeEntity SummonClue(int playerId)
    {
        var oldClue = FindClue(playerId);
        int carried = 0;
        if (oldClue != null)
        {
            var key = Schema.Counter("clues");
            oldClue.Counters.TryGetValue(key, out carried);

            // Clue 的旧护符是被 Banish，不是被破坏——
            // 所以要发 OnBanish，但不加墓场、不进 DestroyedThisMatch。
            oldClue.Zone = ZoneRef.Banished;
            oldClue.EntrySerial++;
            Players[playerId].Banished.Add(oldClue);
            EmitEvent(new VmEvent { Kind = CoreEvent.OnBanish, Subject = EntityRef.From(oldClue) });
        }

        var e = SpawnCard(ClueDefaultCardId, ZoneRef.Board, playerId);
        if (e != null && carried > 0)
        {
            var key = Schema.Counter("clues");
            e.Counters[key] = carried;
        }
        return e;
    }

    /// <summary>取该玩家场上的土之印护符（最多 1 个）。</summary>
    public RuntimeEntity FindEarthSigil(int playerId)
    {
        if (playerId < 0 || playerId > 1) return null;
        foreach (var e in Players[playerId].Board)
            if (e.Def != null && e.Def.CardId == MagicSedimentCardId) return e;
        return null;
    }

    /// <summary>
    /// 召唤一个 Magic Sediment。
    /// 若场上已有另一个土之印，把它的层数合并过来后破坏它（场上始终只留 1 个）。
    /// </summary>
    private RuntimeEntity SummonEarthSigil(int playerId)
    {
        var oldSigil = FindEarthSigil(playerId);
        int carried = 0;
        if (oldSigil != null)
        {
            var key = Schema.Counter("earth_sigil");
            oldSigil.Counters.TryGetValue(key, out carried);
            RemoveQuietly(oldSigil);
        }

        var e = SpawnCard(MagicSedimentCardId, ZoneRef.Board, playerId);
        if (e != null && carried > 0)
        {
            var key = Schema.Counter("earth_sigil");
            e.Counters[key] = carried;
        }
        return e;
    }

    /// <summary>
    /// 可堆叠护符进场时的合并：土之印与 Clue 场上只保留 1 个，
    /// 新进场的那个继承旧的全部层数，旧的无声明移除。
    /// </summary>
    private void MergeStackableAmulet(RuntimeEntity fresh)
    {
        if (fresh?.Def == null) return;
        bool isSigil = fresh.Def.CardId == MagicSedimentCardId;
        bool isClue = fresh.Def.CardId == ClueDefaultCardId;
        if (!isSigil && !isClue) return;

        var key = Schema.Counter(isSigil ? "earth_sigil" : "clues");
        int carried = 0;
        foreach (var old in Players[fresh.PlayerId].Board.ToList())
        {
            if (ReferenceEquals(old, fresh) || old.Def == null) continue;
            if (old.Def.CardId != fresh.Def.CardId) continue;
            old.Counters.TryGetValue(key, out int cv);
            carried += cv;
            RemoveQuietly(old);
        }
        if (carried > 0)
        {
            fresh.Counters.TryGetValue(key, out int own);
            fresh.Counters[key] = own + carried;
        }
    }

    /// <summary>无声移除：不触发谢幕曲、不加墓场（用于合并）。</summary>
    private void RemoveQuietly(RuntimeEntity e)
    {
        foreach (var p in Players) { }
        Detach(e);
        e.EntrySerial++;
        e.Zone = ZoneRef.None;
        _all.Remove(e);
    }

    /// <summary>取该玩家的信仰实体（纹章区里带 IsFaith 的那个）。</summary>
    public RuntimeEntity FindFaith(int playerId)
    {
        if (playerId < 0 || playerId > 1) return null;
        foreach (var e in Players[playerId].CrestArea)
            if (e.IsFaith) return e;
        return null;
    }

    public int ReadEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter)
        => e != null && e.Counters.TryGetValue(counter, out var v) ? v : 0;

    public void WriteEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter, int value)
    {
        if (e != null) e.Counters[counter] = value;
    }

    // ---------- 区域查询 ----------

    public IEnumerable<RuntimeEntity> EnumerateZone(ZoneRef zone, int playerId)
    {
        if (zone == ZoneRef.Leader)
        {
            if (playerId < 0) return Players.Select(p => p.Leader).Where(x => x != null);
            return playerId is 0 or 1 && Players[playerId].Leader != null
                ? new[] { Players[playerId].Leader }
                : Array.Empty<RuntimeEntity>();
        }

        if (playerId < 0)
            return Players.SelectMany(p => p.Zone(zone) ?? new List<RuntimeEntity>()).ToList();

        if (playerId > 1) return Array.Empty<RuntimeEntity>();
        return Players[playerId].Zone(zone) ?? (IEnumerable<RuntimeEntity>)Array.Empty<RuntimeEntity>();
    }

    /// <summary>
    /// 历史池查询。BattleState 维护的是快照列表（PlayerState 里的 DestroyedThisMatch 等），
    /// 不是当前的 Cemetery —— 被复活或消失的卡不应该再算进去。
    /// </summary>
    public IEnumerable<EntitySnapshot> EnumerateHistory(HistoryScope scope, int playerId)
    {
        IEnumerable<PlayerState> ps = playerId < 0 ? Players : new[] { Players[playerId] };
        return scope switch
        {
            HistoryScope.DestroyedThisTurn => ps.SelectMany(p => p.DestroyedThisTurn).ToList(),
            HistoryScope.DestroyedThisMatch => ps.SelectMany(p => p.DestroyedThisMatch).ToList(),
            HistoryScope.EnteredThisMatch => ps.SelectMany(p => p.EnteredThisMatch).ToList(),
            _ => Array.Empty<EntitySnapshot>(),
        };
    }

    public IEnumerable<RuntimeEntity> FindByCardId(ZoneRef zone, int playerId, int cardId)
        => EnumerateZone(zone, playerId).Where(e => (e.Def?.CardId ?? 0) == cardId);

    public RuntimeEntity GetLive(int entityId, int entrySerial)
    {
        var e = _all.FirstOrDefault(x => x.EntityId == entityId);
        if (e == null) return null;
        if (e.EntrySerial != entrySerial) return null;   // 换过区，旧引用失效
        return e;
    }

    public int AllocateEntityId() => _nextEntityId++;

    public bool CheckDeckRule(int playerId, DeckRule rule)
    {
        if (playerId is < 0 or > 1) return true;
        var deck = Players[playerId].Deck;
        return rule switch
        {
            DeckRule.NoDuplicates => deck.Select(e => e.Def?.CardId ?? 0).Distinct().Count() == deck.Count,
            DeckRule.EvenCostOnly => deck.All(e => e.Cost % 2 == 0),
            DeckRule.OddCostOnly => deck.All(e => e.Cost % 2 == 1),
            _ => true,
        };
    }

    // ---------- 事件 ----------

    /// <summary>事件出口。VM 会把事件转成触发器，真正的入队由 CardVm 负责。</summary>
    public List<VmEvent> EventLog { get; } = new();

    /// <summary>
    /// 事件转发出口（IVmWorld.TriggerSink），由 CardVm 构造时注册。
    ///
    /// 之前这里只写 EventLog，VM 侧完全收不到 ——
    /// 回合初抽牌的 OnDraw、能力伤害的 OnDamaged、能力破坏的 OnDestroy
    /// 全都止步于日志，触发器一次都不会响应。
    /// 转发只负责入队，结算顺序仍由 CardVm.StepUntilBlocked 独占。
    /// </summary>
    public Action<VmEvent> TriggerSink { get; set; }

    public void EmitEvent(VmEvent ev)
    {
        if (ev == null) return;
        EventLog.Add(ev);
        TriggerSink?.Invoke(ev);
    }

    public void ClearEvents() => EventLog.Clear();

    // ---------- 实体生命周期 ----------

    /// <summary>造一个实体放进指定区域。这是所有实体的唯一出生点。</summary>
    public RuntimeEntity CreateEntity(int cardId, ZoneRef zone, int playerId, int atk = 0, int life = 0, int cost = -1)
    {
        if (playerId is < 0 or > 1) throw new ArgumentException("playerId 必须是 0 或 1");

        _cardPool.TryGetValue(cardId, out var def);
        var e = new RuntimeEntity
        {
            EntityId = AllocateEntityId(),
            EntrySerial = 1,
            Def = def,
            PlayerId = playerId,
            Zone = zone,
            Attack = atk != 0 ? atk : (def?.BaseAttack ?? 0),
            Life = life != 0 ? life : (def?.BaseLife ?? 0),
            Cost = cost >= 0 ? cost : (def?.BaseCost ?? 0),
        };
        e.MaxLife = e.Life;

        // 装配卡面自带的常驻属性。
        // 裸关键词（Ward / Storm / Rush）与 Countdown (N) 在编译器里被编成
        // PassiveAura 能力，但之前从来没有人在实体创建时把它们装进去，
        // 所以一张印着 Ward 的随从被召唤上场后 Keywords 是空的。
        if (def != null)
        {
            foreach (var ab in def.Abilities)
            {
                if (ab?.Trigger == null) continue;
                if (ab.Trigger.Event != Core.CoreEvent.PassiveAura) continue;

                foreach (var n in ab.Body ?? Array.Empty<Ast.EffectNode>())
                {
                    switch (n)
                    {
                        // 裸关键词（Ward / Storm / Rush / Can't be destroyed …）
                        case Ast.ModifyKeywordNode mk when mk.Op == Ast.KeywordOp.Grant:
                            if (!e.Keywords.Contains(mk.Keyword)) e.Keywords.Add(mk.Keyword);
                            if (mk.Value?.Op == Ast.NumOp.Const) e.KeywordValues[mk.Keyword] = mk.Value.Value;
                            break;

                        // 「X starts at N」的初始计数器。
                        // 之前这条分支不存在，PassiveAura 只被当成关键词装配器，
                        // 于是 X 计数器从未创建，`Deal X damage` 恒打 0 点。
                        // 只认 Op == Set：Increase X by 1 走触发路径，不能被入场重刷。
                        case Ast.ModifyCounterNode mc
                            when mc.Op == Ast.CounterOp.Set && mc.Delta?.Op == Ast.NumOp.Const:
                            e.Counters[mc.Counter] = mc.Delta.Value;
                            break;

                        // 真光环（PassiveAura + ModifyStats，如「所有友方随从 +2/+2」）。
                        // 只吃常量增量，非常量留给运行时求值。
                        case Ast.ModifyStatsNode ms when ms.Op == Ast.StatModOp.Add:
                            if (ms.Attack?.Op == Ast.NumOp.Const) e.Attack += ms.Attack.Value;
                            if (ms.Life?.Op == Ast.NumOp.Const)
                            {
                                e.Life += ms.Life.Value;
                                e.MaxLife += ms.Life.Value;
                            }
                            break;
                    }
                }

                foreach (var c in ab.Costs ?? Array.Empty<Ast.CostSpec>())
                {
                    if (c.Kind != Ast.CostKind.Countdown) continue;
                    if (c.Amount?.Op != Ast.NumOp.Const) continue;
                    e.Counters[Schema.Counter("countdown")] = c.Amount.Value;
                }
            }

            foreach (var k in def.InnateKeywords) if (!e.Keywords.Contains(k)) e.Keywords.Add(k);
            foreach (var kv in def.InnateCounters) e.Counters[kv.Key] = kv.Value;
        }

        _all.Add(e);
        PlaceInZone(e, zone, playerId);
        return e;
    }

    /// <summary>把实体放进区域，并执行容量铁律。</summary>
    private void PlaceInZone(RuntimeEntity e, ZoneRef zone, int playerId)
    {
        var p = Players[playerId];
        var list = p.Zone(zone);
        if (list == null) return;

        // 纹章唯一铁律：同名纹章不可重复持有。
        // 已有纹章 A 时再获得 A，直接忽略（连倒计时也不刷新）。
        if (zone == ZoneRef.CrestArea && e.Def != null && !e.IsFaith)
        {
            bool exists = p.CrestArea.Any(c => c.Def != null && !c.IsFaith && c.Def.CardId == e.Def.CardId);
            if (exists) { e.Zone = ZoneRef.None; _all.Remove(e); return; }
        }

        int cap = PlayerState.Capacity(zone);
        if (cap >= 0 && list.Count >= cap)
        {
            // 两条溢出铁律完全不同，不能合并处理：
            //
            // · 满场（Board ≥ 5）：新卡**直接凭空消失**。
            //   不进墓地、shadow 不变、不进 DestroyedThisMatch、
            //   不触发入场与消失时能力，也不能被亡者召还。
            //
            // · 爆牌（Hand ≥ 9）：第 10 张**进墓地**，shadow +1，
            //   但不触发谢幕曲、不触发 OnDiscard、不进 DestroyedThisMatch。
            //   之前把爆牌也当成凭空消失，墓场数就少加了。
            if (zone == ZoneRef.Board)
            {
                e.Zone = ZoneRef.None;
                _all.Remove(e);
                return;
            }
            if (zone == ZoneRef.Hand)
            {
                e.Zone = ZoneRef.Cemetery;
                e.EntrySerial++;
                p.Cemetery.Add(e);
                var shadowKey = Schema.Counter("shadow");
                var pid = playerId;
                Players[pid].Counters[shadowKey.Id] =
                    Schema.ClampCounter(shadowKey, ReadCounter(pid, shadowKey) + 1);
                return;
            }

            // 纹章区满 5 个：新纹章直接失效忽略，
            // 不顶替旧纹章、不进墓地、不触发任何事件（R-6.2）。
            // 之前这里没有 CrestArea 分支，两个 if 都不匹配，
            // 代码直接往下走执行 list.Add(e)，纹章可以无限塞入。
            if (zone == ZoneRef.CrestArea)
            {
                e.Zone = ZoneRef.None;
                _all.Remove(e);
                return;
            }
        }

        e.Zone = zone;
        e.PlayerId = playerId;
        list.Add(e);

        // 土之印 / Clue 进场即合并：场上永远只留 1 个，层数累加。
        // 合并逻辑原先只写在 SummonEarthSigil 里，于是「从手牌打出」
        // 与「能力直接 SpawnCard」这两条路径都绕过了它，场上堆出多个同类护符。
        // 放在这里是因为它是**所有**进场路径的唯一汇合点。
        if (zone == ZoneRef.Board) MergeStackableAmulet(e);

        if (zone == ZoneRef.Board)
        {
            // 召唤失调的唯一写入点。
            //
            // 此前只有 TurnRunner.PlayCard 会写 EnteredTurn，于是 Summon / Reanimate /
            // Invoke / 复制等**所有能力召唤路径**造出来的实体 EnteredTurn 恒为 0，
            // 而 TurnNumber 已经涨到 5+，CombatResolver.CanAttack 的
            // 「EnteredTurn >= TurnNumber」恒假 —— 召唤出来的随从当回合就能打脸。
            //
            // 进战场的路径不止一条（MoveEntity / CreateEntity 都经过这里），
            // 把盖章点放在这里才能覆盖全部；PlayCard 已经写过一次同样的值，重复无害。
            //
            // 基准取 TurnNumber：它在 TurnRunner.StartTurn 里**只有 player 0 回合**才 +1，
            // 所以它是「轮次」而不是「半步」。先手回合 N 与后手回合 N 同属一轮，
            // 这与「上回合入场的随从下一轮可攻击」的判定基准一致。
            e.EnteredTurn = TurnNumber;
            p.EnteredThisMatch.Add(e.TakeSnapshot());
        }
    }

    /// <summary>
    /// 清空某张卡名下停放的融合材料实体。
    /// 只清实体，卡上的 fuse_count / fuse_cost / fuse_kinds 全部保留。
    /// </summary>
    private void ClearFuseMaterials(RuntimeEntity baseCard)
    {
        if (baseCard == null) return;
        foreach (var p in Players)
        {
            for (int i = p.FuseMaterial.Count - 1; i >= 0; i--)
            {
                var m = p.FuseMaterial[i];
                if (m == null) { p.FuseMaterial.RemoveAt(i); continue; }
                if (m.FusedIntoId != baseCard.EntityId) continue;
                p.FuseMaterial.RemoveAt(i);
                m.Zone = ZoneRef.None;
                _all.Remove(m);
            }
        }
    }

    /// <summary>把实体从当前区域移除。</summary>
    private void Detach(RuntimeEntity e)
    {
        foreach (var p in Players)
        {
            if (p.Zone(e.Zone)?.Remove(e) == true) return;
            if (p.Leader == e) { p.Leader = null; return; }
        }
    }

    /// <summary>
    /// 换区。EntrySerial++ 让旧引用失效（回手再打出的识别依据）。
    /// 离开场上只发 OnLeaveField；是否发 OnDestroy 由 DestroyEntity 决定。
    /// </summary>
    public void MoveEntity(RuntimeEntity e, ZoneRef toZone, int toPlayerId)
    {
        if (e == null) return;
        if (e.Zone == toZone && e.PlayerId == toPlayerId) return;

        // 满场拦截必须是「移动之前」的判定：
        // 卡片效果召唤（"summon a X" / "move this card to the board"）都走这里，
        // 一旦交给 PlaceInZone 去处理，牌库/手牌里的那张已经被 Detach 出来了，
        // PlaceInZone 只能把它置 None —— 卡就从牌库消失了。
        if (toZone == ZoneRef.Board && RejectBoardMoveIfFull(e, toPlayerId)) return;

        bool leftBoard = e.Zone == ZoneRef.Board && toZone != ZoneRef.Board;
        Detach(e);
        e.EntrySerial++;
        PlaceInZone(e, toZone, toPlayerId);

        // 回到牌库的卡要洗回随机位置，且**保留全部状态**（减费 / Buff / 魔力增幅 / 奥义）。
        // 与回手相反：ResetToPrintedValues 只在 Board → Hand 那一条路径生效。
        if (toZone == ZoneRef.Deck) ShuffleZone(ZoneRef.Deck, toPlayerId);

        if (leftBoard)
            EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });

        // 迁入战场的路径不止 SpawnCard：能力把牌库/手牌/墓地的卡搬上场也走这里。
        // 不在这里通知，「进入战场时」与连携对这条路径一律失明。
        if (!leftBoard && toZone == ZoneRef.Board) NotifySummoned(e);

        // 融合素材区在卡离开手牌（打出 / 被变化 / 被烧毁 / 被弃牌）时清空。
        // 清的是**实体**，fuse_count / fuse_cost / fuse_kinds 三个计数器留在卡上 ——
        // 否则 Sinciro 打出时 Fanfare 读到 fuse_kinds = 0，整条逻辑断掉。
        if (toZone != ZoneRef.FuseMaterial && (toZone == ZoneRef.Board
            || toZone == ZoneRef.Cemetery || toZone == ZoneRef.Banished
            || toZone == ZoneRef.None || toZone == ZoneRef.Deck))
            ClearFuseMaterials(e);
    }

    /// <summary>
    /// 满场（Board ≥ 5）时的召唤铁律：**新卡凭空消失** ——
    /// 不进墓地、shadow 不变、不进 DestroyedThisMatch、不触发入场与消失时能力。
    ///
    /// 唯一的例外是「牌库 → 战场」：卡面写「回合开始时从牌组上场」的 Invoke（瞬念召唤）
    /// 在 R-6.2-8 是「满场时不发动」，也就是**留在牌库**，而不是消失。
    /// 把这条留在这里而不是调用方，是因为召唤路径太多（能力 / 直接召唤 / 复制），
    /// 每漏一条就是一张卡永久失踪。
    /// </summary>
    /// <returns>true 表示这次移动已被拦下，调用方不得再执行移动。</returns>
    private bool RejectBoardMoveIfFull(RuntimeEntity e, int toPlayerId)
    {
        var board = Players[toPlayerId].Board;
        if (board.Count < PlayerState.MaxBoard) return false;

        // 从牌库来的：不移动，原地不动（保留自己的区域与流水号）。
        if (e.Zone == ZoneRef.Deck) return true;

        // 从其它区域（无来源的 token / 手牌 / 墓地复活）：凭空消失。
        Detach(e);
        e.EntrySerial++;
        e.Zone = ZoneRef.None;
        _all.Remove(e);
        return true;
    }

    /// <summary>
    /// 洗某玩家的一个区域（Fisher-Yates，用世界随机源）。
    /// 与 MatchRunner.ShuffleDeck 同一算法；洗牌点不止开局与换牌，所以放在这里。
    /// </summary>
    public void ShuffleZone(ZoneRef zone, int playerId)
    {
        var list = Players[playerId].Zone(zone);
        if (list == null || list.Count < 2) return;
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// 破坏（Destroy）：进墓地 + 触发 OnDestroy + shadow+1。
    /// 只有真正的「被破坏」才调用它；放逐 / 回手 / 变形一律不要用。
    /// </summary>
    /// <summary>
    /// 统一伤害管线。能力伤害必须走这里，
    /// 否则屏障、超进化免伤、伤害上限全部被绕过。
    /// </summary>
    public void ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId)
    {
        if (target == null || amount <= 0) return;

        // CombatResolver 的伤害管线只负责数值与屏障，它的 vm 参数在这里是 null ——
        // 也就是说能力伤害过去既不进 EventLog、也不进触发队列，
        // 「当受到伤害时」对任何能力伤害一律失明。
        // 由这里补发一条 OnDamaged：EventLog 与触发队列都拿得到，
        // 而战斗伤害走 CombatResolver 自己的 vm 通道，不会重复。
        int dealt = new CombatResolver(this, Schema).ApplyDamage(target, amount, sourcePlayerId);
        if (dealt <= 0) return;   // 屏障吸收 / 超进化免伤：没有伤害就没有受伤事件

        EmitEvent(new VmEvent
        {
            Kind = CoreEvent.OnDamaged,
            Subject = EntityRef.From(target),
            Amount = dealt,
        });
    }

    /// <summary>IVmWorld 接口实现：能力破坏，受保护拦截。</summary>
    void IVmWorld.DestroyEntity(RuntimeEntity e) => DestroyEntity(e);

    public void DestroyEntity(RuntimeEntity e, bool byRule = false)
    {
        if (e == null) return;
        if (e.Zone != ZoneRef.Board) return;

        // 超进化铁律：自己的回合内，超进化随从不能被任何能力或效果破坏。
        // 这条规则之前完全没有实现，法术可以直接炸死刚超进化的随从。
        if (e.IsSuperEvolved && e.PlayerId == ActivePlayer) return;

        // 土之印 / Clue 护符不能被能力破坏（R-6.2）。
        // 它们只能因「层数归 0」而规则性破坏。
        // byRule = true 表示「规则性收尾」（层数归 0），不受保护拦截。
        if (!byRule && IsProtectedAmulet(e)) return;

        // 「不能被破坏」关键词（cannot_destroy）。
        // 这个关键词之前只有写入、从来没有读取点——
        // 卡面写着 Can't be destroyed 也照样一把炸掉。
        if (e.Keywords.Contains(Schema.Keyword("cannot_destroy"))) return;

        // 「以消失替代破坏」：先跑一次离场时触发链。
        // 卡面写 "When this card leaves the field, banish it." 的实体
        // （Ghost 是典型）会在这一步被搬到放逐区；此时它已不在战场，
        // 后面整条破坏流程自然失去目标。
        //
        // 这正是「替代」与「事后补救」的区别：先破坏再被 OnLeaveField 挪走，
        // shadow 已经加了、DestroyedThisMatch 已经记了、墓场已经走过一遭。
        if (ShouldBanishOnLeave(e))
        {
            BanishEntity(e);
            if (e.Zone != ZoneRef.Board) return;
        }

        int owner = e.PlayerId;
        var snap = e.TakeSnapshot();

        Detach(e);
        e.EntrySerial++;
        PlaceInZone(e, ZoneRef.Cemetery, owner);

        Players[owner].DestroyedThisMatch.Add(snap);
        Players[owner].DestroyedThisTurn.Add(snap);

        var shadow = Schema.Counter("shadow");
        WriteCounter(owner, shadow, ReadCounter(owner, shadow) + 1);

        EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
    }

    /// <summary>
    /// 该实体是否声明了「离场时改为放逐」（"When this card leaves the field, banish it."）。
    ///
    /// 判据是卡面能力本身，不是卡名：任何写成这句话的卡都适用，
    /// Ghost 只是其中一张（它同时还有回合末自我放逐）。
    /// 被沉默的实体没有能力，所以不享受这条替换。
    /// </summary>
    private static bool ShouldBanishOnLeave(RuntimeEntity e)
    {
        if (e?.Def?.Abilities == null || e.IsSilenced) return false;
        foreach (var ab in e.Def.Abilities)
        {
            if (ab?.Trigger == null || ab.Trigger.Event != CoreEvent.OnLeaveField) continue;
            if (ab.Body == null) continue;
            foreach (var n in ab.Body)
            {
                if (n is Ast.MoveZoneNode mz
                    && mz.ToZone == ZoneRef.Banished
                    && mz.Target != null
                    && mz.Target.SourceKind == Ast.TargetSourceKind.Slot
                    && (mz.Target.Slot == Ast.TargetSpec.SlotTrigger
                        || mz.Target.Slot == Ast.TargetSpec.SlotSelf))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 放逐（Banish）：进放逐区，不触发 OnDestroy，不加 shadow。
    ///
    /// 先发 OnLeaveField，再移区，最后发 OnBanish。
    /// 不能反过来：MoveEntity 内部也会对「离开战场」补发一次 OnLeaveField，
    /// 两条路径叠加时事件发两次，而 OnBanish 反而一次都没有——
    /// 于是「当被放逐时」类能力全体失明。
    /// </summary>
    public void BanishEntity(RuntimeEntity e)
    {
        if (e == null || e.Zone == ZoneRef.Banished) return;
        // OnLeaveField 由 MoveEntity 统一发一次（它只在「离开战场」时发）。
        // 这里手发会与之叠加成两次，而 OnBanish 反倒一次都没有。
        MoveEntity(e, ZoneRef.Banished, e.PlayerId);
        EmitEvent(new VmEvent { Kind = CoreEvent.OnBanish, Subject = EntityRef.From(e) });
    }

    public void RemoveEntity(RuntimeEntity e) => _all.Remove(e);

    /// <summary>
    /// 吟唱归 0 的纹章到期：移出纹章区，并让它的「谢幕曲」照常触发。
    ///
    /// 不能复用 DestroyEntity —— 它第一行就是 `if (e.Zone != Board) return;`，
    /// 纹章在 CrestArea，会被静默跳过（实测：纹章永远卡在纹章区，
    /// Octrice 的 Last Words 一次都不触发）。
    /// 也不能把它塞进墓地：纹章到期限不属于「被破坏」，不该污染墓地与 shadow。
    ///
    /// 做法：先移出纹章区（落点 Banished，与「失去纹章」语义一致），
    /// 再发 OnLeaveField / OnDestroy 让谢幕曲有机会响应；
    /// 实体本身留在放逐区可查，GetLive 仍能取到。
    /// </summary>
    public void ExpireCrest(RuntimeEntity e)
    {
        if (e == null || e.Zone != ZoneRef.CrestArea) return;
        MoveEntity(e, ZoneRef.Banished, e.PlayerId);
        EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
    }

    // ---------- 状态结算 SBA ----------

    /// <summary>
    /// 把 Life &lt;= 0 的场上实体破坏掉。
    /// 必须在每条原子指令后调用，否则死亡随从会留在场上继续挨刀。
    /// </summary>
    /// <summary>
    /// 状态基础行为：生命值 ≤ 0 的场上实体破坏。
    ///
    /// 注意与能力破坏的区别：
    /// 这里是「已经死了」的收尾，不是「能否破坏」的判定。
    /// 超进化免破坏只拦能力破坏，拦不住生命归零——
    /// 否则 Give -0/-3 这类属性修正把超进化随从打到 0 血也死不了。
    /// </summary>
    public int ProcessStateBasedActions()
    {
        int n = 0;
        foreach (var p in Players)
        {
            foreach (var e in p.Board.ToList())
            {
                // 「生命归零即破坏」只对**随从**成立。
                //
                // 护符没有生命值这个概念：它的 Life 恒为 0（BaseLife 没写就是 0），
                // 而这条判定原来对场上所有实体一视同仁 —— 于是任何护符一拍下来
                // 就被判成「已经死了」，第一个 SBA 就把它送进墓地。
                // 实测：Fanfare 打出普通护符后 ProcessStateBasedActions 立即
                // 把它变成 zone=Cemetery，全卡池的护符等于都不存在。
                //
                // 护符的死亡条件只有两条，都不在这里：
                //   · Countdown 归 0 —— TurnRunner.StepBoardCountdown
                //   · 土之印 / Clue 层数归 0 —— WriteCounter(byRule: true)
                // 卡面效果明确破坏走 DestroyEntity，受护符保护规则约束。
                //
                // 超进化随从的 Life<=0 仍然要在这里破坏：本方法不是「能否破坏」的
                // 判定，而是「已经死了」的收尾，所以它不经过 DestroyEntity 的
                // 超进化免破坏 / 护符保护拦截（见 DestroyByState）。
                if (e.Def?.Kind != Ast.CardKind.Follower) continue;
                if (e.Life > 0) continue;
                DestroyByState(e);
                n++;
            }
        }
        return n;
    }

    /// <summary>生命归零的破坏：不受超进化免破坏保护。</summary>
    private void DestroyByState(RuntimeEntity e)
    {
        if (e == null || e.Zone != ZoneRef.Board) return;

        int owner = e.PlayerId;
        var snap = e.TakeSnapshot();

        Detach(e);
        e.EntrySerial++;
        PlaceInZone(e, ZoneRef.Cemetery, owner);

        Players[owner].DestroyedThisMatch.Add(snap);
        Players[owner].DestroyedThisTurn.Add(snap);

        var shadow = Schema.Counter("shadow");
        WriteCounter(owner, shadow, ReadCounter(owner, shadow) + 1);

        EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
    }

    // ---------- 生成 / 变形 / 授予 ----------

    public RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, int toPlayerId)
    {
        var e = CreateEntity(cardId, toZone, toPlayerId);
        // 召唤进场的随从同样要发「进入战场时」并加连携。
        // 早期只在 TurnRunner.PlayCard 里手写发事件，于是 Summon / Reanimate /
        // Invoke / 复制这几条路径造出来的实体对「进入战场时」完全失明。
        NotifySummoned(e);
        return e;
    }

    /// <summary>
    /// 通知「一个实体刚刚进入战场」：连携 +1（仅随从），再发 OnEnterField / OnSummon。
    /// 幂等：e 不在战场时直接返回，所以可以在多条进场路径上重复调用。
    /// </summary>
    public void NotifySummoned(RuntimeEntity e)
    {
        if (e == null || e.Zone != ZoneRef.Board) return;
        if (e.Def?.Kind == Ast.CardKind.Follower)
        {
            var rallyKey = Schema.Counter("rally");
            WriteCounter(e.PlayerId, rallyKey, ReadCounter(e.PlayerId, rallyKey) + 1);
        }
        EmitEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(e) });
        EmitEvent(new VmEvent { Kind = CoreEvent.OnSummon, Subject = EntityRef.From(e) });
    }

    public void ReplaceDeck(int playerId, string deckName, IReadOnlyList<int> cardIds)
    {
        var p = Players[playerId];
        // 旧牌库直接离场：既不进 Banished 也不进 Cemetery，
        // 否则 BanishedCount / CemeteryCount 会被一次性打爆。
        p.Deck.Clear();
        foreach (var id in cardIds) CreateEntity(id, ZoneRef.Deck, playerId);
    }

    /// <summary>IVmWorld 接口实现：默认完整复制。</summary>
    RuntimeEntity IVmWorld.SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId)
        => SpawnCopy(source, toZone, toPlayerId, exact: true);

    /// <summary>
    /// 复制一个实体。
    ///
    /// 官方术语表把「复制」拆成两种，语义不是程度差别而是完全不同：
    ///   exact = true  （an exact copy of X）
    ///       继承源目标**当前**的攻击力 / 当前生命 / 生命上限 / 当前费用 /
    ///       已获得的关键词与授予能力 / 已进化与超进化状态。
    ///   exact = false （a copy of X）
    ///       只是把那张卡的**印刷白板**再拿出来一份。
    ///
    /// 两者都**不继承**「本回合已攻击 / 已启动」：复制体是一次全新入场，
    /// EnteredTurn 由 PlaceInZone 盖成当前回合，召唤失调照常生效。
    /// </summary>
    public RuntimeEntity SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId, bool exact = true)
    {
        var s = source.Snapshot;
        if (s == null) return null;

        // 白板复制：只用卡号生成，CreateEntity 按卡面印刷值装配。
        if (!exact) return CreateEntity(s.CardId, toZone, toPlayerId);

        var e = CreateEntity(s.CardId, toZone, toPlayerId);
        if (e == null) return null;

        // 状态必须**逐字段覆盖**，不能借 CreateEntity 的 atk/life/cost 形参：
        // 那里 0 表示「用印刷值」，一个 0 攻随从会被复制成它的印刷攻击力。
        e.Attack = s.Attack;
        e.Life = s.Life;
        // 上限单独搬 —— 之前只搬了 Life，CreateEntity 把 MaxLife 写成 Life，
        // 于是 7/6（上限 9）的复制体上限也变成 6，治疗量被永久吃掉。
        e.MaxLife = s.MaxLife;
        e.Cost = s.Cost;
        e.IsEvolved = s.IsEvolved;
        e.IsSuperEvolved = s.IsSuperEvolved;

        // 关键词与特性：CreateEntity 装的是**卡面印的**，exact 要的是**现在有的**。
        e.Keywords.Clear();
        foreach (var k in s.Keywords) e.Keywords.Add(k);
        e.Traits.Clear();
        foreach (var t in s.Traits) e.Traits.Add(t);

        // 授予能力不写在 CardDef 里（GrantAbility 单独记账），必须按实体逐条搬。
        foreach (var (ge, gab) in _granted.ToList())
            if (ge != null && ge.EntityId == s.EntityId) _granted.Add((e, gab));

        // 「本回合已攻击 / 已启动」显式清掉：新实体即使默认值正确，
        // 也不该依赖默认值来表达一条规则。
        e.AttackedThisTurn = false;
        e.AttacksThisTurn = 0;
        e.EngagedThisTurn = false;

        return e;
    }

    /// <summary>
    /// 变身：把实体换成另一张卡。
    ///
    /// 官方术语表有独立的「变身」条目，语义是**变成另一张卡**，
    /// 而不只是换个名字 —— 旧卡的关键词、计数器、进化状态、
    /// 临时修正都必须清掉，否则新卡会带着旧卡的全部状态。
    ///
    /// 变形不算破坏：不触发 OnDestroy，不加 shadow。
    /// </summary>
    public void TransformEntity(RuntimeEntity e, int intoCardId)
    {
        if (e == null) return;
        if (!_cardPool.TryGetValue(intoCardId, out var d)) return;

        e.Def = d;
        e.Attack = d.BaseAttack;
        e.Life = d.BaseLife;
        e.MaxLife = d.BaseLife;
        e.Cost = d.BaseCost;

        // 旧卡的状态一律不继承
        e.Keywords.Clear();
        e.KeywordValues.Clear();
        e.Counters.Clear();
        e.TempModifiers.Clear();
        e.IsEvolved = false;
        e.IsSuperEvolved = false;
        e.IsSilenced = false;
        e.AttacksThisTurn = 0;
        e.AttackedThisTurn = false;
        e.AttackedLeaderLastTurn = false;
    }

    public void GrantAbility(RuntimeEntity e, AbilityDef ability)
        => _granted.Add((e, ability));

    public IReadOnlyList<(RuntimeEntity e, AbilityDef ab)> GrantedAbilities => _granted;

    public void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, AbilityDef ability)
        => _grantedCounters.Add((playerId, counter, ability));

    public IReadOnlyList<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> GrantedCounterAbilities => _grantedCounters;

    /// <summary>
    /// 机制宏展开。返回空表示未实现 —— VM 会因此报错，
    /// 而不是让效果凭空消失。
    /// </summary>
    public IReadOnlyList<EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope)
        => Array.Empty<EffectNode>();

    // ---------- 抽牌 ----------

    /// <summary>
    /// 抽牌。
    ///
    /// 官方规则：牌组为 0 张时抽牌即失败 ——
    /// 但若自己牌组底部已有「胜利的卡牌」，则改为胜利。
    /// 之前这里只是 continue，抽空牌组不会有任何后果。
    /// </summary>
    public void DrawCards(int playerId, int count)
    {
        var p = Players[playerId];
        for (int i = 0; i < count; i++)
        {
            if (p.Deck.Count == 0)
            {
                if (p.HasVictoryCard)
                    p.WonByVictoryCard = true;
                else
                    p.LostByDeckOut = true;
                EmitEvent(new VmEvent { Kind = CoreEvent.OnDeckOut });
                return;
            }

            // 爆牌判定必须在移动之前取：手牌已满 9 张时，
            // MoveEntity 会把这张牌按容量铁律直接送进墓地 + shadow+1。
            bool handFull = p.Hand.Count >= PlayerState.MaxHand;
            var card = p.Deck[0];
            MoveEntity(card, ZoneRef.Hand, playerId);

            // 官方规则：爆掉的那张**不是抽到的牌** —— 它没进手牌，
            // 所以不发 OnDraw（也不发 OnDiscard / 谢幕曲，那两条由 PlaceInZone 的
            // 手牌溢出分支天然保证：它只 MoveEntity，不产生任何事件）。
            // 之前这里无条件发 OnDraw，「每当抽牌时」会被一张爆掉的牌骗到。
            if (handFull) continue;

            EmitEvent(new VmEvent { Kind = CoreEvent.OnDraw, Subject = EntityRef.From(card) });
        }
    }

    /// <summary>
    /// 撤销到期的临时修正。
    ///
    /// 官方术语表: until the end of the turn = 本回合结束撤销;
    /// until the end of your opponents turn = 到对手回合结束才撤销。
    ///
    /// 之前 Duration 只写进 AST, 运行时从不处理, 所有限时效果都永久生效。
    /// </summary>
    public int ExpireTemporaryModifiers(int endingPlayer)
    {
        int n = 0;
        foreach (var p in Players)
        {
            foreach (var list in new[] { p.Board, p.Hand, p.CrestArea, p.ExArea })
            {
                foreach (var e in list) n += ExpireOn(e, endingPlayer);
            }
        }
        return n;
    }

    private static int ExpireOn(RuntimeEntity e, int endingPlayer)
    {
        int n = 0;
        for (int i = e.TempModifiers.Count - 1; i >= 0; i--)
        {
            var m = e.TempModifiers[i];
            bool due = m.Duration switch
            {
                DurationKind.ThisTurn => true,
                // 到对手回合结束才到期
                DurationKind.UntilEndOfOpponentTurn => endingPlayer != e.PlayerId,
                DurationKind.ThisMatch => false,
                _ => false,
            };
            if (!due) continue;

            if (m.IsKeywordGrant)
            {
                e.Keywords.Remove(m.Keyword);
                e.KeywordValues.Remove(m.Keyword);
            }
            else if (!m.IsCounterMod)
            {
                e.Attack -= m.Attack;
                e.Life -= m.Life;
            }

            e.TempModifiers.RemoveAt(i);
            n++;
        }
        return n;
    }

    /// <summary>把牌组底部的卡变成「胜利的卡牌」（官方「胜利的卡牌」机制）。</summary>
    public void SetVictoryCard(int playerId)
    {
        if (playerId is < 0 or > 1) return;
        Players[playerId].HasVictoryCard = true;
    }
}
