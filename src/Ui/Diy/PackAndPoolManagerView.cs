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
    private readonly Dictionary<string, Control> _ruleEdits = new();
    private Button _saveRulesBtn;
    private Label _ruleStatus;
    private string _selectedPoolId;

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

        var ruleBar = new HBoxContainer();
        ruleBar.AddThemeConstantOverride("separation", 10);
        right.AddChild(ruleBar);

        var ruleTitle = new Label { Text = "赛制卡池规则（MatchRules 全字段）" };
        ruleTitle.AddThemeFontSizeOverride("font_size", 14);
        ruleTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        ruleBar.AddChild(ruleTitle);

        ruleBar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _saveRulesBtn = new Button { Text = "💾 保存卡池规则", CustomMinimumSize = new Vector2(150, 32) };
        SvWbTheme.StyleButton(_saveRulesBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        _saveRulesBtn.Pressed += OnSaveRules;
        ruleBar.AddChild(_saveRulesBtn);

        _ruleStatus = new Label { Text = "" };
        _ruleStatus.AddThemeFontSizeOverride("font_size", 11);
        _ruleStatus.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
        ruleBar.AddChild(_ruleStatus);

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
    /// 列出 MatchRules 全部字段并**允许编辑**。
    ///
    /// 反射驱动：手写 36 行必然漏，且 MatchRules 加字段时会过期。
    /// 改完点【保存卡池规则】写回 data/pools/*.pool.json 并热重载。
    /// </summary>
    private void RefreshRules()
    {
        foreach (var c in _ruleList.GetChildren()) c.QueueFree();
        _ruleEdits.Clear();
        _selectedPoolId = null;

        foreach (var kv in _store.PoolConfigs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            // 只给第一个池做可编辑控件，避免多个池的同名字段互相覆盖。
            bool editable = _selectedPoolId == null;
            if (editable) _selectedPoolId = kv.Key;

            var head = new Label { Text = "【" + (kv.Value.Name ?? kv.Key) + "】" };
            head.AddThemeFontSizeOverride("font_size", 13);
            head.AddThemeColorOverride("font_color", new Color("FEF08A"));
            _ruleList.AddChild(head);

            if (!editable) continue;

            var headHint = new Label { Text = "（可编辑，其余卡池只读）" };
            headHint.AddThemeFontSizeOverride("font_size", 10);
            headHint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _ruleList.AddChild(headHint);

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

                var a = new Label { Text = prop.Name, CustomMinimumSize = new Vector2(230, 0) };
                a.AddThemeFontSizeOverride("font_size", 11);
                a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
                row.AddChild(a);

                if (val is bool bv)
                {
                    var chk = new CheckBox { ButtonPressed = bv };
                    _ruleEdits[prop.Name] = chk;
                    row.AddChild(chk);
                }
                else if (val is int iv)
                {
                    var sp = new SpinBox { MinValue = 0, MaxValue = 999, Value = iv,
                                          CustomMinimumSize = new Vector2(110, 26) };
                    _ruleEdits[prop.Name] = sp;
                    row.AddChild(sp);
                }
                else
                {
                    var lbl = new Label { Text = val.ToString() };
                    lbl.AddThemeFontSizeOverride("font_size", 11);
                    lbl.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
                    row.AddChild(lbl);
                }

                _ruleList.AddChild(row);
                count++;
            }

            var foot = new Label { Text = "—— 共 " + count + " 项规则（可编辑） ——" };
            foot.AddThemeFontSizeOverride("font_size", 10);
            foot.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _ruleList.AddChild(foot);
        }
    }

    /// <summary>
    /// 把规则面板上的编辑结果写回 data/pools/<id>.pool.json 并热重载。
    ///
    /// 用反射把控件值转回 MatchRules —— 与读取侧共用同一份字段清单，
    /// 不会出现「读得到但存不回」的半套实现。
    /// </summary>
    private void OnSaveRules()
    {
        if (string.IsNullOrEmpty(_selectedPoolId)) return;
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId, out var cfg)) return;

        var src = cfg.Rules ?? MatchRules.CreateStandard();

        // MatchRules 是 init-only 的 record-like class，只能重建一份
        var clone = new MatchRules();
        int applied = 0;

        foreach (var prop in typeof(MatchRules).GetProperties())
        {
            if (!prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length > 0) continue;

            object cur;
            try { cur = prop.GetValue(src); }
            catch { continue; }

            object newVal = cur;
            if (_ruleEdits.TryGetValue(prop.Name, out var ctl))
            {
                if (ctl is CheckBox cb) newVal = cb.ButtonPressed;
                else if (ctl is SpinBox sp) newVal = (int)sp.Value;
            }

            try
            {
                if (newVal != null && prop.PropertyType.IsInstanceOfType(newVal))
                {
                    prop.SetValue(clone, newVal);
                    applied++;
                }
            }
            catch { }
        }

        bool ok = _store.SavePoolRules(_selectedPoolId, clone, out string msg);
        if (_ruleStatus != null)
        {
            _ruleStatus.Text = (ok ? "✔ " : "✘ ") + msg;
            _ruleStatus.AddThemeColorOverride("font_color",
                ok ? SvWbTheme.StatusValid : SvWbTheme.StatusDanger);
        }

        RefreshRules();
    }
}
