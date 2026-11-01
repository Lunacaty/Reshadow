using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// 卡包浏览器 & 赛制卡池规则配置器。
///
/// 左：卡包列表 + 该包的卡；右：卡池的 MatchRules 全字段可视化。
/// 规则字段是直接按类型反射列出来的 —— 36 项手写一遍必然漏，
/// 而且以后 MatchRules 加字段时这里会自动跟上。
/// </summary>
public partial class PackAndPoolManagerView : Control
{
    public event Action OnBackToDecks;

    private GameSessionStore _store;
    private VBoxContainer _packList;
    private VBoxContainer _cardList;
    private VBoxContainer _ruleList;
    private Label _header;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        if (!_store.PoolConfigs.Any()) _store.Initialize();
        BuildUi();
        RefreshPacks();
        RefreshRules();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 58) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));
        root.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        var back = new Button { Text = "← 返回牌组一览" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBackToDecks?.Invoke();
        row.AddChild(back);

        _header = new Label { Text = "卡包 / 卡池管理" };
        _header.AddThemeFontSizeOverride("font_size", 22);
        _header.AddThemeColorOverride("font_color", new Color("FEF08A"));
        row.AddChild(_header);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        root.AddChild(body);

        // 左：卡包 + 卡
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        left.AddThemeConstantOverride("separation", 6);
        body.AddChild(left);

        var packTitle = new Label { Text = "物理卡包" };
        packTitle.AddThemeFontSizeOverride("font_size", 14);
        packTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        left.AddChild(packTitle);

        var packScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 110) };
        left.AddChild(packScroll);
        _packList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        packScroll.AddChild(_packList);

        var cardTitle = new Label { Text = "包内卡牌" };
        cardTitle.AddThemeFontSizeOverride("font_size", 14);
        cardTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        left.AddChild(cardTitle);

        var cardScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        left.AddChild(cardScroll);
        _cardList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cardScroll.AddChild(_cardList);

        // 右：卡池规则
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        body.AddChild(right);

        var ruleTitle = new Label { Text = "赛制卡池规则（MatchRules 全字段）" };
        ruleTitle.AddThemeFontSizeOverride("font_size", 14);
        ruleTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        right.AddChild(ruleTitle);

        var ruleScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(ruleScroll);
        _ruleList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ruleList.AddThemeConstantOverride("separation", 3);
        ruleScroll.AddChild(_ruleList);
    }

    private void RefreshPacks()
    {
        foreach (var c in _packList.GetChildren()) c.QueueFree();

        foreach (var kv in _store.Catalog.Packs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var pack = kv.Value;
            var btn = new Button
            {
                Text = kv.Key + "   (" + (pack.Cards?.Count ?? 0) + " 张)",
                Alignment = HorizontalAlignment.Left,
            };
            SvWbTheme.StyleButton(btn);
            string pid = kv.Key;
            btn.Pressed += () => ShowPackCards(pid);
            _packList.AddChild(btn);
        }
    }

    private void ShowPackCards(string packId)
    {
        foreach (var c in _cardList.GetChildren()) c.QueueFree();
        if (!_store.Catalog.Packs.TryGetValue(packId, out var pack)) return;

        _header.Text = "卡包 / 卡池管理 —— " + packId;

        foreach (var c in pack.Cards.OrderBy(x => x.CardId))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var idLbl = new Label { Text = c.CardId.ToString(), CustomMinimumSize = new Vector2(84, 0) };
            idLbl.AddThemeFontSizeOverride("font_size", 11);
            idLbl.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            row.AddChild(idLbl);

            var nameLbl = new Label
            {
                Text = string.IsNullOrWhiteSpace(c.NameChs) ? (c.NameEng ?? "") : c.NameChs,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 12);
            nameLbl.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
            row.AddChild(nameLbl);

            var clanLbl = new Label { Text = c.Clan ?? "Neutral", CustomMinimumSize = new Vector2(96, 0) };
            clanLbl.AddThemeFontSizeOverride("font_size", 11);
            clanLbl.AddThemeColorOverride("font_color", SvWbTheme.ClanColor(c.Clan));
            row.AddChild(clanLbl);

            var costLbl = new Label { Text = c.Cost + " 费", CustomMinimumSize = new Vector2(52, 0) };
            costLbl.AddThemeFontSizeOverride("font_size", 11);
            costLbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
            row.AddChild(costLbl);

            _cardList.AddChild(row);
        }
    }

    /// <summary>
    /// 按反射列出 MatchRules 的全部字段。
    ///
    /// 手写 36 行既容易漏、又会在 MatchRules 加字段时过期 ——
    /// 反射能让这里永远与内核同步。
    /// </summary>
    private void RefreshRules()
    {
        foreach (var c in _ruleList.GetChildren()) c.QueueFree();

        foreach (var kv in _store.PoolConfigs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var head = new Label { Text = "【" + (kv.Value.Name ?? kv.Key) + "】" };
            head.AddThemeFontSizeOverride("font_size", 13);
            head.AddThemeColorOverride("font_color", new Color("FEF08A"));
            _ruleList.AddChild(head);

            var rules = kv.Value.Rules;
            if (rules == null) continue;

            int count = 0;
            foreach (var prop in typeof(MatchRules).GetProperties())
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;

                object val;
                try { val = prop.GetValue(rules); }
                catch { continue; }
                if (val == null) continue;

                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);

                var a = new Label { Text = prop.Name, CustomMinimumSize = new Vector2(220, 0) };
                a.AddThemeFontSizeOverride("font_size", 11);
                a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
                row.AddChild(a);

                string shown = val is bool b ? (b ? "是" : "否") : val.ToString();
                var v = new Label { Text = shown };
                v.AddThemeFontSizeOverride("font_size", 11);
                v.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
                row.AddChild(v);

                _ruleList.AddChild(row);
                count++;
            }

            var foot = new Label { Text = "—— 共 " + count + " 项规则 ——" };
            foot.AddThemeFontSizeOverride("font_size", 10);
            foot.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _ruleList.AddChild(foot);
        }
    }
}