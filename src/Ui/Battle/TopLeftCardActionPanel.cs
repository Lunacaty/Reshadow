using System;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 左上角卡牌详情与操作弹窗（X:18, Y:82, 宽 336）。
///
/// 单击任意手牌 / 场上棋子 / 纹章时在这里展示效果，
/// 并在底部给出【进化】【超进化】【启动】【融合】按钮 ——
/// 原版战斗界面的核心操作入口就是这个左上角窗，不是右键菜单。
/// </summary>
/// 基类必须是 Control 而不是 PanelContainer：
/// PanelContainer 是布局容器，子节点可见性一变就会把它们的 Position
/// 强制重置回 (0,0) 并铺满自身 —— 二级弹窗刚设好的右侧坐标下一微秒就被拉回去，
/// 表现就是「点开历史条目，弹窗糊在主弹窗脸上」。
public partial class TopLeftCardActionPanel : Control
{
    private BattleSessionController _ctrl;
    private RuntimeEntity _bound;

    /// <summary>
    /// 由 BattleScreenRoot 注入的阻塞门禁判定。
    ///
    /// 三层动画架构里，进化/超进化属于「阻塞段」——
    /// 演出没播完之前玩家不该改状态。左上角弹窗最初没有查这个门禁，
    /// 于是当场上有阻塞动画时，玩家仍能从这里强行点进化，
    /// 后端立刻结算而演出还排着队，视觉与状态彻底脱节。
    /// </summary>
    public Func<bool> IsInputBlocked { get; set; }

    private Label _nameLbl;
    private Label _metaLbl;
    private RichTextLabel _effectRich;
    private HBoxContainer _actionRow;

    /// <summary>「曾受能力影响」列表容器。</summary>
    private VBoxContainer _historyList;

    /// <summary>主详情窗：用选中卡的立绘中心填充做背景。</summary>
    private ArtCenterBannerBox _mainCardBox;
    private VBoxContainer _mainCardInner;

    /// <summary>点开某条历史后在右侧展开的二级来源详情。</summary>
    private PanelContainer _secondaryPopup;
    private Label _secondaryTitle;
    private RichTextLabel _secondaryRich;

    /// <summary>当前展开的那条历史；用于再次点击时收起。</summary>
    private AppliedEffectRecord _activeRecord;

    public event Action<RuntimeEntity> OnEntityChanged;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        Visible = false;
        Position = new Vector2(18, 82);
        Size = new Vector2(336, 0);
        CustomMinimumSize = new Vector2(336, 0);
        ZIndex = 65;

        // 主弹窗不能是纯色块：需求要的是「弹窗背景用选中卡的立绘中心填充」。
        // 外层本身透明无边框，视觉全部由内部的 ArtCenterBannerBox 提供。
        // Control 没有 panel 样式，不需要清理；背景由内部 _mainCardBox 提供。

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        AddChild(root);

        _mainCardBox = new ArtCenterBannerBox
        {
            DimAlpha = 0.78f,
            CustomMinimumSize = new Vector2(336, 0),
        };
        _mainCardBox.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        root.AddChild(_mainCardBox);

        // 主窗内容装在内层容器里，叠在立绘背景之上。
        _mainCardInner = new VBoxContainer();
        _mainCardInner.AddThemeConstantOverride("separation", 6);
        _mainCardBox.AddChild(_mainCardInner);

        var head = new HBoxContainer();
        _mainCardInner.AddChild(head);

        _nameLbl = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameLbl.AddThemeFontSizeOverride("font_size", 16);
        _nameLbl.AddThemeColorOverride("font_color", new Color("FEF08A"));
        _nameLbl.AddThemeConstantOverride("outline_size", 4);
        _nameLbl.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        head.AddChild(_nameLbl);

        var close = new Button { Text = "✕" };
        SvWbTheme.StyleButton(close);
        close.Pressed += HidePanel;
        head.AddChild(close);

        _metaLbl = new Label();
        _metaLbl.AddThemeFontSizeOverride("font_size", 12);
        _metaLbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        _mainCardInner.AddChild(_metaLbl);

        _effectRich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(310, 0),
        };
        _effectRich.AddThemeFontSizeOverride("normal_font_size", 12);
        _effectRich.AddThemeFontSizeOverride("bold_font_size", 12);
        _mainCardInner.AddChild(_effectRich);

        _actionRow = new HBoxContainer();
        _actionRow.AddThemeConstantOverride("separation", 8);
        _mainCardInner.AddChild(_actionRow);
        // ── 曾受能力影响（点击展开来源）──
        _historyList = new VBoxContainer();
        _historyList.AddThemeConstantOverride("separation", 4);
        _historyList.Visible = false;
        root.AddChild(_historyList);

        BuildSecondaryPopup();
    }

    /// <summary>
    /// 延迟到下一帧再重建面板与战场。
    ///
    /// 进化在按钮自己的 Pressed 回调里执行，若当场重建，
    /// 正在派发信号的按钮会被 QueueFree —— 界面卡死。
    /// </summary>
    private RuntimeEntity _pendingRefresh;

    private void DeferRefresh(RuntimeEntity entity)
    {
        // CallDeferred 只能传 Godot 的 Variant 参数，RuntimeEntity 是纯 C# 对象，
        // 因此把待刷新实体存在字段里，延迟调用自己不带参数。
        _pendingRefresh = entity;
        CallDeferred(nameof(DoRefresh));
    }

    private void DoRefresh()
    {
        var entity = _pendingRefresh;
        _pendingRefresh = null;
        if (entity == null) { HidePanel(); return; }
        ShowForEntity(entity);
        OnEntityChanged?.Invoke(entity);
    }

    /// <summary>当前是否处于阻塞状态（进化演出 / Choose 挂起 / 终局）。</summary>
    private bool Blocked => IsInputBlocked != null && IsInputBlocked();

    public void ShowForEntity(RuntimeEntity entity)
    {
        if (entity?.Def == null || _ctrl == null) { HidePanel(); return; }
        _bound = entity;
        var def = entity.Def;

        string silPart = entity.IsSilenced ? " 【沉默】" : "";
        _nameLbl.Text = DeckEditorViewModel.DisplayName(def)
            + (entity.IsSuperEvolved ? " 【超进化】" : entity.IsEvolved ? " 【进化】" : "")
            + silPart;

        string stat = def.Kind == CardKind.Follower
            ? $"费用 {entity.Cost}  |  ⚔ {entity.Attack}  |  【盾】{entity.Life}/{entity.MaxLife}"
            : $"费用 {entity.Cost}  |  {def.Kind}";

        // 兵种显示**运行时实体**的完整列表，不是卡面印刷的那一个。
        // 被亡者召还的随从同时拥有原兵种与「亡者」，只看 def.Traits 会完全漏掉。
        string traitPart = "";
        try
        {
            var traitList = (entity.Traits != null && entity.Traits.Count > 0
                                ? entity.Traits.AsEnumerable()
                                : (def.Traits ?? Array.Empty<SymbolId<TraitTag>>()).AsEnumerable())
                            .Where(t => t.IsValid && !string.IsNullOrWhiteSpace(t.Name))
                            .Select(t =>
                            {
                                try { return _ctrl.Store.CardPrinter.TraitZh(t.Name) ?? t.Name; }
                                catch { return t.Name; }
                            })
                            .Distinct()
                            .ToList();
            if (traitList.Count > 0) traitPart = "·" + string.Join("·", traitList);
        }
        catch { }

        var dynamicStats = new System.Collections.Generic.List<string>();
        foreach (var kv in entity.Counters)
        {
            if (!kv.Key.IsValid || kv.Value <= 0) continue;
            string cName = kv.Key.Name;
            string zh = cName switch
            {
                "spellboost" => $"⚡魔力增幅: {kv.Value}",
                "skybound_gauge" => $"⚔奥义槽: {kv.Value}",
                "fuse_count" => $"◆融合数: {kv.Value}",
                // 179 号文档 D4：漏了这两项，玩家看不到已融合的**种类数**与总费，
                // 而融合变身表正是按总费决定变出哪一档。
                "fuse_kinds" => $"◆融合种类: {kv.Value}",
                "fuse_cost" => $"◆融合总费: {kv.Value}",
                "countdown" => $"⏳倒计时: {kv.Value}",
                "earth_sigil" => $"🪨土之印: {kv.Value}",
                "clues" => $"🔍线索: {kv.Value}",
                _ => $"{cName}: {kv.Value}"
            };
            dynamicStats.Add(zh);
        }
        string dynPart = dynamicStats.Count > 0 ? "  |  " + string.Join("  ", dynamicStats) : "";
        _metaLbl.Text = $"【{SvWbTheme.ClanDisplayZh(def.Clan.IsValid ? def.Clan.Name : null)}{traitPart}】 {stat}{dynPart}";

        // 必须走 FormatFullCardText：只打本体的话，玩家点开带纹章 / 信仰 /
        // 激奏 / 结晶的卡完全看不到那些子卡的效果（179 号文档 D4）。
        _effectRich.Text = SvWbTheme.FormatFullCardText(def, _ctrl.Store.CardPrinter);

        // 主窗背景换成这张卡的立绘中心填充。
        if (_mainCardBox != null)
        {
            // 分开填：CardId 是这张卡本身，ArtCardId 是立绘来源。
            // 塞进同一个字段会让 _Draw 里的回落判断失去意义。
            _mainCardBox.CardId = def.CardId;
            _mainCardBox.ArtCardId = def.ArtCardId;
            _mainCardBox.QueueRedraw();
        }

        // 不能在按钮自己的 Pressed 回调里销毁按钮本身。
        // Godot 的信号派发持有当前控件的引用，边派发边 QueueFree
        // 会把「信号发出者」在回调中途拆掉 —— 表现为点击后整个界面卡死。
        // 这里延到下一帧再清，回调结束时控件仍然有效。
        foreach (var c in _actionRow.GetChildren())
        {
            _actionRow.RemoveChild(c);
            c.QueueFree();
        }

        if (_ctrl.CanEvolveUnit(entity))
        {
            var b = new Button { Text = "✦ 进化 (+2/+2)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentGold);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEvolve(entity, false)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (_ctrl.CanSuperEvolveUnit(entity))
        {
            var b = new Button { Text = "★ 超进化 (+3/+3)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEvolve(entity, true)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (_ctrl.CanEngageUnit(entity, out int cost))
        {
            var b = new Button { Text = $"⚡ 启动 ({cost}费)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEngage(entity)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (entity.Zone == ZoneRef.Hand && _ctrl.CanStartFuse(entity))
        {
            var b = new Button { Text = "◆ 进行融合" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
            b.Pressed += () => { if (Blocked) return; _ctrl.StartFuseMode(entity); DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }

        if (_ctrl.IsInFuseMode && ReferenceEquals(_ctrl.FuseBaseCard, entity))
        {
            var ok = new Button
            {
                Text = $"✔ 确认融合 (已选 {_ctrl.SelectedFuseMaterials.Count} 张)",
                Disabled = _ctrl.SelectedFuseMaterials.Count == 0,
            };
            SvWbTheme.StyleButton(ok, isPrimary: true, customAccent: SvWbTheme.StatusValid);
            ok.Pressed += () => { _ctrl.ConfirmFuse(); DeferRefresh(entity); };
            _actionRow.AddChild(ok);

            var cancel = new Button { Text = "取消融合" };
            SvWbTheme.StyleButton(cancel);
            cancel.Pressed += () => { _ctrl.CancelFuseMode(); DeferRefresh(entity); };
            _actionRow.AddChild(cancel);
        }

        Visible = true;

        // 历史区随实体切换重建；二级弹窗在 RebuildHistory 里先收起。
        RebuildHistory(entity);
    }

    // ---------------------------------------------------------------- 历史溯源

    private void BuildSecondaryPopup()
    {
        _secondaryPopup = new PanelContainer();
        _secondaryPopup.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.05f, 0.07f, 0.13f, 0.97f), new Color("D4AF37"), 8, 2, 10));
        _secondaryPopup.CustomMinimumSize = new Vector2(300, 0);
        _secondaryPopup.Visible = false;
        _secondaryPopup.ZIndex = 70;
        AddChild(_secondaryPopup);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        _secondaryPopup.AddChild(v);

        _secondaryTitle = new Label();
        _secondaryTitle.AddThemeFontSizeOverride("font_size", 13);
        _secondaryTitle.AddThemeColorOverride("font_color", new Color("FEF08A"));
        v.AddChild(_secondaryTitle);

        _secondaryRich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(286, 0),
        };
        _secondaryRich.AddThemeFontSizeOverride("normal_font_size", 11);
        v.AddChild(_secondaryRich);
    }

    /// <summary>
    /// 汇总该实体受过的全部影响：历史记录在前，当前生效的常驻光环补在后面。
    ///
    /// 光环必须实时扫描 —— 它不在 EffectHistory 里（没有「某次触发」这个时点），
    /// 而是「因为场上还站着一张卡所以一直生效」，随光环源离场而消失。
    /// </summary>
    private System.Collections.Generic.List<AppliedEffectRecord> CollectAllEffects(RuntimeEntity entity)
    {
        var list = new System.Collections.Generic.List<AppliedEffectRecord>();
        if (entity == null) return list;

        foreach (var r in entity.EffectHistory) list.Add(r);

        var st = _ctrl?.State;
        if (st == null) return list;
        foreach (var (node, scope) in st.QueryDamageModifiers(entity))
        {
            var snap = scope.Source?.Snapshot;
            var def = snap?.Def ?? st.CardDefOf(snap?.CardId ?? 0);
            if (def == null) continue;
            bool dup = false;
            foreach (var old in list)
                if (ReferenceEquals(old.SourceCard, def)) { dup = true; break; }
            if (dup) continue;
            list.Insert(0, new AppliedEffectRecord
            {
                SourceCard = def,
                SourceAbility = scope.SourceAbility,
                Tag = "光环",
            });
        }

        // 关键词光环（「当本卡在场时，我方随从获得【守护】」）不在
        // QueryDamageModifiers 里 —— 那条路只扫 DamageModifierNode。
        // 不单独扫一遍，这类光环在溯源面板上完全不出现。
        for (int pid = 0; pid < 2; pid++)
        {
            var srcs = new System.Collections.Generic.List<ShadowverseFan.KernelV3.Vm.RuntimeEntity>();
            srcs.AddRange(st.Players[pid].Board);
            srcs.AddRange(st.Players[pid].CrestArea);
            foreach (var src in srcs)
            {
                if (src?.Def?.Abilities == null || src.IsSilenced) continue;
                if (src.EntityId == entity.EntityId) continue;
                foreach (var ab in src.Def.Abilities)
                {
                    if (ab?.Trigger == null || ab.Trigger.Event != ShadowverseFan.KernelV3.Core.CoreEvent.PassiveAura) continue;
                    if (ab.Body == null) continue;
                    bool grants = false;
                    foreach (var n in ab.Body)
                        if (n is ShadowverseFan.KernelV3.Ast.ModifyKeywordNode mk
                            && mk.Op == ShadowverseFan.KernelV3.Ast.KeywordOp.Grant
                            && st.HasEffectiveKeyword(entity, mk.Keyword))
                        { grants = true; break; }
                    if (!grants) continue;
                    bool dup2 = false;
                    foreach (var old in list)
                        if (ReferenceEquals(old.SourceCard, src.Def)) { dup2 = true; break; }
                    if (dup2) continue;
                    list.Insert(0, new AppliedEffectRecord
                    {
                        SourceCard = src.Def,
                        SourceAbility = ab,
                        Tag = "光环",
                    });
                }
            }
        }
        return list;
    }

    private void RebuildHistory(RuntimeEntity entity)
    {
        if (_historyList == null) return;
        foreach (var c in _historyList.GetChildren())
        {
            _historyList.RemoveChild(c);
            c.QueueFree();
        }
        _secondaryPopup.Visible = false;
        _activeRecord = null;

        var records = CollectAllEffects(entity);
        if (records.Count == 0) { _historyList.Visible = false; return; }

        _historyList.Visible = true;
        var header = new Label { Text = "▾ 受到的能力影响 (" + records.Count + ")" };
        header.AddThemeFontSizeOverride("font_size", 11);
        header.AddThemeColorOverride("font_color", new Color("FDE68A"));
        _historyList.AddChild(header);

        foreach (var rec in records)
        {
            if (rec?.SourceCard == null) continue;
            var captured = rec;
            var box = new ArtCenterBannerBox
            {
                ArtCardId = rec.SourceCard.ArtCardId,
                CardId = rec.SourceCard.CardId,
                DimAlpha = 0.62f,
                CustomMinimumSize = new Vector2(310, 38),
                MouseFilter = MouseFilterEnum.Stop,
                MouseDefaultCursorShape = CursorShape.PointingHand,
            };
            box.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            box.AddChild(margin);

            var title = new Label
            {
                // 严格只显示来源卡的中文卡名，不拼内部 Tag（Grant ward / TakenDelta 这类英文枚举）。
                Text = DeckEditorViewModel.DisplayName(rec.SourceCard),
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            title.AddThemeFontSizeOverride("font_size", 13);
            title.AddThemeColorOverride("font_color", Colors.White);
            title.AddThemeConstantOverride("outline_size", 4);
            title.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
            margin.AddChild(title);

            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed
                    && mb.ButtonIndex == MouseButton.Left)
                {
                    ShowSecondary(captured, box);
                    box.AcceptEvent();
                }
            };
            _historyList.AddChild(box);
        }
    }

    private void ShowSecondary(AppliedEffectRecord rec, Control anchor)
    {
        if (ReferenceEquals(_activeRecord, rec) && _secondaryPopup.Visible)
        {
            _secondaryPopup.Visible = false;
            _activeRecord = null;
            return;
        }
        _activeRecord = rec;
        _secondaryTitle.Text = "《" + DeckEditorViewModel.DisplayName(rec.SourceCard) + "》";
        // 高亮那一行已经是 ▶ 开头的纯文本；转 RichText 前先把它包成
        // 金底亮字，否则在长卡面里只有一个箭头，根本定位不到。
        string raw = _ctrl.Store.CardPrinter.PrintCardWithHighlight(rec.SourceCard, rec.SourceAbility);
        var sb = new System.Text.StringBuilder();
        foreach (var ln in raw.Replace("\r", "").Split('\n'))
        {
            if (ln.StartsWith("▶ "))
                sb.Append("[bgcolor=#451A03][color=#FEF08A][b]").Append(ln).Append("[/b][/color][/bgcolor]").Append('\n');
            else if (!string.IsNullOrWhiteSpace(ln))
                sb.Append("[color=#94A3B8]").Append(SvWbTheme.FormatRichCardText(ln)).Append("[/color]").Append('\n');
        }
        _secondaryRich.Text = sb.ToString();
        _secondaryPopup.Visible = true;
        // 基类改成 Control 后不会再篡改子节点坐标。
        // X 固定在主窗宽度右侧 + 10px；Y 用**全局**坐标差值对齐被点的这一行 ——
        // anchor.Position.Y 是它在 _historyList 内部的相对值，直接用会错位。
        float rightX = (_mainCardBox != null && _mainCardBox.Size.X > 0
                        ? _mainCardBox.Size.X : 336f) + 10f;
        float alignY = anchor != null
            ? Mathf.Max(0f, anchor.GlobalPosition.Y - GlobalPosition.Y) : 0f;
        _secondaryPopup.Position = new Vector2(rightX, alignY);
    }


    /// <summary>
    /// 这个全局坐标是否真的落在弹窗的可见内容上。
    ///
    /// 不能用 GetGlobalRect()：本类继承 Control，Size.Y 恒为 0
    /// （Control 不会因为子节点 VBox 撑高而自动改自身尺寸）。
    /// 用它做命中检测会让「点弹窗内的进化/启动按钮」判成点在窗外，
    /// 于是 _Input 先把弹窗关掉，按钮永远点不中。
    /// 改为逐个子控件判矩形。
    /// </summary>
    public bool HasGlobalPoint(Vector2 globalPos)
    {
        if (!Visible) return false;
        if (_mainCardBox != null && IsInstanceValid(_mainCardBox) && _mainCardBox.Visible
            && _mainCardBox.GetGlobalRect().HasPoint(globalPos)) return true;
        if (_historyList != null && IsInstanceValid(_historyList) && _historyList.Visible
            && _historyList.GetGlobalRect().HasPoint(globalPos)) return true;
        if (_secondaryPopup != null && IsInstanceValid(_secondaryPopup) && _secondaryPopup.Visible
            && _secondaryPopup.GetGlobalRect().HasPoint(globalPos)) return true;
        return false;
    }

    public void HidePanel()
    {
        // 融合选材期间关面板必须一并退出融合模式（179 号文档 D3）。
        // 否则「确认融合 / 取消融合」按钮随着面板消失，而 _ctrl.IsInFuseMode
        // 仍为 true —— 玩家既拖不出牌、手牌也一直卡在选材态，只能重开一局。
        if (_ctrl?.IsInFuseMode == true) _ctrl.CancelFuseMode();
        _bound = null;
        if (_secondaryPopup != null) _secondaryPopup.Visible = false;
        _activeRecord = null;
        Visible = false;
    }

    public RuntimeEntity BoundEntity => _bound;
}

/// <summary>
/// 用指定 CardId 的立绘中心裁切填充背景、并叠暗色可读遮罩与金边的矩形容器。
///
/// 既当主弹窗背景，也当「受能力影响」每一条的底图 ——
/// 用立绘而不是纯色块，玩家扫一眼就能认出「这张卡影响过我」。
/// </summary>
public partial class ArtCenterBannerBox : PanelContainer
{
    public int CardId { get; set; }
    public int ArtCardId { get; set; }
    public bool Selected { get; set; }
    public float DimAlpha { get; set; } = 0.76f;

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (rect.Size.X <= 1f || rect.Size.Y <= 1f) return;

        // 纹章/信仰没有自己的立绘，回落到主人卡。
        var tex = ShadowverseFan.Ui.CardArtLoader.Load(ArtCardId > 0 ? ArtCardId : CardId);
        if (tex != null)
        {
            // 取立绘正中心偏上（y=0.38 是角色面部/胸像的黄金区）做等比裁切填充。
            var ts = tex.GetSize();
            float boxAspect = rect.Size.X / rect.Size.Y;
            float texAspect = ts.X / Mathf.Max(1f, ts.Y);
            Rect2 src;
            if (texAspect > boxAspect)
            {
                float sw = ts.Y * boxAspect;
                src = new Rect2((ts.X - sw) * 0.5f, 0f, sw, ts.Y);
            }
            else
            {
                float sh = ts.X / boxAspect;
                float sy = Mathf.Clamp(ts.Y * 0.38f - sh * 0.5f, 0f, Mathf.Max(0f, ts.Y - sh));
                src = new Rect2(0f, sy, ts.X, sh);
            }
            DrawTextureRectRegion(tex, rect, src);
        }
        else
        {
            DrawRect(rect, new Color("141C2E"));
        }

        // 暗化遮罩：保证前景卡名在任意立绘上都清晰可读。
        DrawRect(rect, new Color(0.04f, 0.06f, 0.12f, DimAlpha));
        DrawRect(rect, Selected ? new Color("FDE047") : new Color("D4AF37"),
                 false, Selected ? 2.5f : 1.5f);
    }
}
