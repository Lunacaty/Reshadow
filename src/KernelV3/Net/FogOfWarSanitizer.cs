using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Net;

/// <summary>
/// 防作弊核心 2：战争迷雾状态裁剪器（防读牌 / 防透视）。
///
/// 权威端绝不把完整 BattleState 发给客户端，而是针对每个观察者
/// 生成一份脱敏快照：
///   · 牌库只发剩余张数，客户端内存里根本不存在牌序 —— 「算牌器」无从下手；
///   · 对手未公开的手牌 CardId 抹成 -1，抓包/内存读取都看不到；
///   · 对手正在做的选择（如发现三选一）候选列表抹空。
/// </summary>
public static class FogOfWarSanitizer
{
    public static NetStateSnapshot BuildForViewer(
        BattleState state, MatchRunner runner, int viewerPlayerId, int eventLogFromIdx)
    {
        var snap = new NetStateSnapshot
        {
            ViewerPlayerId = viewerPlayerId,
            ActivePlayer = state.ActivePlayer,
            FirstPlayer = state.FirstPlayer,
            TurnNumber = state.TurnNumber,
            Phase = runner.Phase.ToString(),
            Winner = runner.Winner(),
            EventStartIndex = eventLogFromIdx,
        };

        for (int pid = 0; pid < 2; pid++)
        {
            bool isSelf = pid == viewerPlayerId;
            var p = state.Players[pid];
            var dto = new NetPlayerDto
            {
                PlayerId = pid,
                // 只发张数，绝不暴露牌库内容。
                DeckCount = p.Deck.Count,
                CemeteryCount = p.Cemetery.Count,
                BanishedCount = p.Banished.Count,
                EpUnlocked = p.EpUnlocked,
                SepUnlocked = p.SepUnlocked,
                ManualEvolvedThisTurn = p.ManualEvolvedThisTurn,
                UsedEarlyExtraPp = p.UsedEarlyExtraPp,
                UsedLateExtraPp = p.UsedLateExtraPp,
                UsedExtraPpThisTurn = p.UsedExtraPpThisTurn,
                // 后手额外能量点独立槽位：客机镜像必须收到，否则界面显示与实际可用 PP 不符。
                BonusPp = p.BonusPp,
                Leader = ToDto(p.Leader, true),
            };

            foreach (var (name, _, _) in GameSchema.BuiltinCounterDefs)
                dto.Counters[name] = state.ReadCounter(pid, state.Schema.Counter(name));
            foreach (var pc in state.PoolCounterDefs ?? Array.Empty<Packs.PoolCounterDef>())
                if (!string.IsNullOrWhiteSpace(pc?.Name))
                    dto.Counters[pc.Name] = state.ReadCounter(pid, state.Schema.Counter(pc.Name));

            foreach (var e in p.Board) dto.Board.Add(ToDto(e, true));
            foreach (var e in p.CrestArea) dto.Crests.Add(ToDto(e, true));

            // 手牌：己方或已被效果公开才下发真实 CardId。
            foreach (var e in p.Hand)
                dto.Hand.Add(ToDto(e, isSelf || e.IsRevealed));

            snap.Players[pid] = dto;
        }

        // 增量事件日志同样脱敏：对手抽牌事件抹去具体卡号。
        for (int i = eventLogFromIdx; i < state.EventLog.Count; i++)
        {
            var ev = state.EventLog[i];
            // 快照里没有 IsRevealed（它是活体实体的运行期状态），
            // 所以按「抽牌方是不是观察者」判断：对手抽牌一律不告诉他抽到了什么。
            bool hideSubj = ev.Kind == CoreEvent.OnDraw
                            && ev.PlayerId >= 0
                            && ev.PlayerId != viewerPlayerId;
            snap.NewEvents.Add(new NetEventDto
            {
                Kind = ev.Kind.ToString(),
                PlayerId = ev.PlayerId,
                SubjectEntityId = ev.Subject?.EntityId ?? -1,
                SubjectCardId = hideSubj ? -1 : (ev.Subject?.Snapshot?.CardId ?? 0),
                SubjectPlayerId = ev.Subject?.Snapshot?.PlayerId ?? -1,
                SubjectZone = ev.Subject?.Snapshot?.Zone.ToString() ?? "",
                AbilityIndex = (ev.ActivatedAbility != null && ev.Subject?.Snapshot?.Def?.Abilities != null)
                    ? GetAbilityIndex(ev.Subject.Value.Snapshot.Def.Abilities, ev.ActivatedAbility)
                    : -1,
                ObjectEntityId = ev.Object?.EntityId ?? -1,
                Amount = ev.Amount,
                SecondaryAmount = ev.SecondaryAmount,
                CounterName = ev.CounterName ?? "",
                KnockedBackBySuperEvolve = ev.KnockedBackBySuperEvolve,
            });
        }

        // 挂起选择：对手正在选（例如发现三选一）时，候选列表抹空防偷看。
        var pending = runner.PendingChoice;
        if (pending != null && runner.IsWaitingForInput)
        {
            bool isMyChoice = pending.PlayerId == viewerPlayerId;
            snap.Pending = new NetPendingChoiceDto
            {
                ChoiceId = pending.ChoiceId,
                PlayerId = pending.PlayerId,
                SourceEntityId = runner.PreAction?.Subject?.EntityId
                    ?? runner.Vm?.Frames?.LastOrDefault()?.Scope?.Source?.EntityId ?? -1,
                SourceCardId = runner.PreAction?.Subject?.Def?.CardId
                    ?? runner.Vm?.Frames?.LastOrDefault()?.Scope?.Source?.Snapshot?.CardId ?? 0,
                Kind = pending.Kind.ToString(),
                MinSelect = pending.MinSelect,
                MaxSelect = pending.MaxSelect,
                CanCancel = isMyChoice && pending.CanCancel,
                Prompt = pending.Prompt ?? "",
                Candidates = isMyChoice ? pending.Candidates.ToList() : new List<int>(),
            };
        }

        return snap;
    }

    private static int GetAbilityIndex(IReadOnlyList<AbilityDef> list, AbilityDef item)
    {
        if (list == null || item == null) return -1;
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    private static NetEntityDto ToDto(RuntimeEntity e, bool revealSecret)
    {
        if (e == null) return null;

        if (!revealSecret)
        {
            // 战争迷雾暗牌：只给 EntityId / Zone，其余全部隐藏。
            return new NetEntityDto
            {
                EntityId = e.EntityId,
                EntrySerial = e.EntrySerial,
                PlayerId = e.PlayerId,
                Zone = e.Zone.ToString(),
                CardId = -1,
                IsHidden = true,
            };
        }

        return new NetEntityDto
        {
            EntityId = e.EntityId,
            EntrySerial = e.EntrySerial,
            PlayerId = e.PlayerId,
            Zone = e.Zone.ToString(),
            CardId = e.Def?.CardId ?? 0,
            ArtCardId = e.Def?.ArtCardId ?? 0,
            Cost = e.Cost,
            Attack = e.Attack,
            Life = e.Life,
            MaxLife = e.MaxLife,
            IsEvolved = e.IsEvolved,
            IsSuperEvolved = e.IsSuperEvolved,
            IsSilenced = e.IsSilenced,
            IsRevealed = e.IsRevealed,
            EnteredTurn = e.EnteredTurn,
            AttacksThisTurn = e.AttacksThisTurn,
            AttackedThisTurn = e.AttackedThisTurn,
            EngagedThisTurn = e.EngagedThisTurn,
            FusedThisTurn = e.FusedThisTurn,
            IsFaith = e.IsFaith,
            Keywords = e.Keywords.Select(k => k.Name).ToList(),
            KeywordValues = e.KeywordValues.ToDictionary(kv => kv.Key.Name, kv => kv.Value),
            Traits = e.Traits.Select(t => t.Name).ToList(),
            Counters = e.Counters.ToDictionary(kv => kv.Key.Name, kv => kv.Value),
        };
    }
}
