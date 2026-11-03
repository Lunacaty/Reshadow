using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.KernelV3.Net;

/// <summary>联机消息类型。</summary>
public enum NetMsgKind
{
    Hello = 0,
    HelloAck,
    DeckSubmit,
    DeckAck,
    StartMatch,
    Action,
    Snapshot,
    Ping,
    Pong,
    Error,
}

/// <summary>客机可发送的操作意图。权威端只认这些，不认任何状态字段。</summary>
public enum NetActionKind
{
    PlayCard = 0,
    Attack,
    Evolve,
    EngageAmulet,
    UseExtraPp,
    SubmitChoice,
    CancelPending,
    EndTurn,
}

public sealed class NetActionPacket
{
    public NetActionKind Kind { get; set; }
    public int EntityId { get; set; } = -1;
    public int TargetId { get; set; } = -1;
    public bool Flag { get; set; }
    public int ChoiceId { get; set; } = -1;
    public int[] Selected { get; set; } = Array.Empty<int>();
}

public sealed class NetEntityDto
{
    public int EntityId { get; set; }
    public int EntrySerial { get; set; }
    public int PlayerId { get; set; }
    public string Zone { get; set; } = "";
    public int CardId { get; set; }
    public int Cost { get; set; }
    public int Attack { get; set; }
    public int Life { get; set; }
    public int MaxLife { get; set; }
    public bool IsEvolved { get; set; }
    public bool IsSuperEvolved { get; set; }
    public bool IsSilenced { get; set; }
    public bool IsRevealed { get; set; }
    public bool IsHidden { get; set; }
    public int EnteredTurn { get; set; }
    public int AttacksThisTurn { get; set; }
    public bool AttackedThisTurn { get; set; }
    public bool EngagedThisTurn { get; set; }
    public bool FusedThisTurn { get; set; }
    public bool IsFaith { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Traits { get; set; } = new();
    public Dictionary<string, int> Counters { get; set; } = new();
}

public sealed class NetPlayerDto
{
    public int PlayerId { get; set; }
    public int DeckCount { get; set; }
    public int CemeteryCount { get; set; }
    public int BanishedCount { get; set; }
    public bool EpUnlocked { get; set; }
    public bool SepUnlocked { get; set; }
    public bool ManualEvolvedThisTurn { get; set; }
    public bool UsedEarlyExtraPp { get; set; }
    public bool UsedLateExtraPp { get; set; }
    public bool UsedExtraPpThisTurn { get; set; }
    public NetEntityDto Leader { get; set; }
    public List<NetEntityDto> Board { get; set; } = new();
    public List<NetEntityDto> Crests { get; set; } = new();
    public List<NetEntityDto> Hand { get; set; } = new();
    public Dictionary<string, int> Counters { get; set; } = new();
}

public sealed class NetEventDto
{
    public string Kind { get; set; } = "";
    public int PlayerId { get; set; }
    public int SubjectEntityId { get; set; } = -1;
    public int SubjectCardId { get; set; }
    public int ObjectEntityId { get; set; } = -1;
    public int Amount { get; set; }
    public int SecondaryAmount { get; set; }
    public string CounterName { get; set; } = "";
    public bool KnockedBackBySuperEvolve { get; set; }
}

public sealed class NetPendingChoiceDto
{
    public int ChoiceId { get; set; }
    public int PlayerId { get; set; }
    public string Kind { get; set; } = "";
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; }
    public bool CanCancel { get; set; }
    public string Prompt { get; set; } = "";
    public List<int> Candidates { get; set; } = new();
}

public sealed class NetStateSnapshot
{
    public int ViewerPlayerId { get; set; }
    public int ActivePlayer { get; set; }
    public int FirstPlayer { get; set; }
    public int TurnNumber { get; set; }
    public string Phase { get; set; } = "";
    public int Winner { get; set; } = -1;
    public int EventStartIndex { get; set; }
    public NetPlayerDto[] Players { get; set; } = new NetPlayerDto[2];
    public List<NetEventDto> NewEvents { get; set; } = new();
    public NetPendingChoiceDto Pending { get; set; }
}

/// <summary>
/// 防作弊核心 1：卡池与规则完整性哈希。
///
/// 握手阶段双方各自算一遍，不一致就当场拒绝开局 ——
/// 用记事本把 1 费妖精改成 99/99 会被这一关拦住。
/// </summary>
public static class PoolIntegrityHasher
{
    /// <summary>
    /// 对局用哈希：按 126 轮要求覆盖**启用卡包清单 + 全部规则属性 + 池内全部卡**。
    /// 任何一项不一致（哪怕只改了 MaxBoardSize）都会让握手当场失败。
    /// </summary>
    public static string ComputePoolHash(Packs.CardPoolRuntime pool)
    {
        if (pool == null) return "";
        var sb = new StringBuilder();
        sb.Append("pool:").Append(pool.PoolId).Append('\n');

        // 启用卡包清单（排序后拼接，顺序不影响结果）。
        sb.Append("packs:")
          .Append(string.Join(",",
              (pool.EnabledPackIds ?? new HashSet<string>())
                  .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
          .Append('\n');

        // 全部规则属性（反射按名字排序，新增规则自动进哈希）。
        var rules = pool.Rules ?? MatchRules.CreateStandard();
        foreach (var prop in typeof(MatchRules).GetProperties()
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            sb.Append(prop.Name).Append('=').Append(prop.GetValue(rules)).Append(';');
        }
        sb.Append('\n');

        AppendCards(sb, pool.AllCards?.Values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>旧签名：普通 CardPool 视图。保留给非对局路径（DIY 预览等）。</summary>
    public static string ComputePoolHash(CardPool pool)
    {
        if (pool == null) return "";
        var sb = new StringBuilder();
        sb.Append("pool:").Append(pool.PoolId).Append('\n');

        var rules = pool.BaseRules ?? MatchRules.CreateStandard();
        foreach (var prop in typeof(MatchRules).GetProperties()
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            sb.Append(prop.Name).Append('=').Append(prop.GetValue(rules)).Append(';');
        }
        sb.Append('\n');

        AppendCards(sb, pool.Catalog?.AllCards?.Values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void AppendCards(StringBuilder sb, IEnumerable<CardDef> cards)
    {
        foreach (var c in (cards ?? Enumerable.Empty<CardDef>()).OrderBy(c => c.CardId))
        {
            sb.Append(c.CardId).Append('|')
              .Append((int)c.Kind).Append('|')
              .Append(c.BaseCost).Append('|')
              .Append(c.BaseAttack).Append('|')
              .Append(c.BaseLife).Append('|')
              .Append(c.HasFuse).Append('|')
              .Append(AstEnglishPrinter.PrintCard(c.Abilities)).Append('\n');
        }
    }
}
