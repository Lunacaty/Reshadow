using System;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Decks;

/// <summary>
/// 卡组管理根控制器：管理三大界面的切换。
///
/// 这是 .tscn 里唯一挂载的脚本 —— 其余节点全部在 C# 中程序化生成。
/// 手拖几十个节点会让锚点与引用在改分辨率时全线错乱，
/// 而程序化布局只需要调常量。
/// </summary>
public partial class DeckManagementRoot : Control
{
    private GameSessionStore _store;
    private Control _current;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 星象圣殿背景（替代单调色块）：渐变 + 星轨同心圆 + 斜向圣光带。
        var bg = new AstralBackgroundCanvas();
        AddChild(bg);

        _store = new GameSessionStore();
        try
        {
            _store.Initialize();
        }
        catch (Exception ex)
        {
            ShowFatal(ex.Message);
            return;
        }

        ShowMainMenu();
    }

    /// <summary>
    /// 主界面 · Astral Sanctum。
    ///
    /// 它是所有流程的中庭：牌组一览、DIY 工坊、卡包管理、开战都从这里进，
    /// 各自的「← 返回」也回到这里 —— 而不是像早先那样一进来就
    /// 直接把牌组一览铺在启动画面上。
    /// </summary>
    private void ShowMainMenu()
    {
        var menu = new MainMenuView();
        Swap(menu);
        menu.Boot(_store);

        menu.OnStartBattle    += ShowBattleSelect;
        menu.OnLanBattle      += ShowLanLobby;
        menu.OnDeckManagement += ShowDeckList;
        menu.OnDiyWorkshop    += ShowDiyWorkshop;
        menu.OnPackManager    += ShowPackManager;
        menu.OnQuit           += () => GetTree().Quit();
    }

    /// <summary>局域网 / Radmin 联机大厅：建房或加入房间。</summary>
    private void ShowLanLobby()
    {
        var lobby = new Net.LanLobbyModal();
        AddChild(lobby);
        lobby.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        lobby.Boot(_store);

        lobby.OnCancelled += () => lobby.QueueFree();

        // 加入房间：连上权威端后由 RemoteBattleClient 持续接收裁剪快照。
        // 客机本地不持有真实 BattleState，改内存没有意义。
        lobby.OnJoinRoom += info =>
        {
            lobby.QueueFree();
            var client = new Net.RemoteBattleClient();
            _remoteClient = client;
            _ = client.ConnectAsync(info.Ip, info.Port, 1);
        };

        lobby.OnHostRoom += (poolId, deck) =>
        {
            lobby.QueueFree();
        };
    }

    private Net.RemoteBattleClient _remoteClient;

    /// <summary>对战选择窗：先卡池、再双方牌组，然后直接开局。</summary>
    private void ShowBattleSelect()
    {
        var dlg = new BattleSelectDialog();
        AddChild(dlg);
        dlg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dlg.Boot(_store);

        dlg.OnCancelled += () => dlg.QueueFree();
        dlg.OnStart += (my, foe) =>
        {
            dlg.QueueFree();
            StartBattle(my, foe);
        };
    }

    private void StartBattle(ConstructedDeck my, ConstructedDeck foe)
    {
        var view = new Battle.BattleScreenRoot();
        Swap(view);
        view.Boot(_store, my, foe);
        view.OnExitToDecks += ShowMainMenu;
    }

    private void Swap(Control next)
    {
        if (_current != null)
        {
            _current.QueueFree();
            _current = null;
        }
        _current = next;
        AddChild(next);

        // 必须显式铺满父容器。
        // Control 节点 AddChild 后默认是 0x0 —— 只有 _Ready 里自己调
        // SetAnchorsPreset 才有效，而那时父容器尺寸可能还没定。
        // 这里由父级统一设置，子视图就不必各自处理。
        // FullRect 只设锚点，offset 仍是 0 —— 宽高会保持 0。
        // 必须用 AndOffsets 版本才会真正铺满。
        next.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    private void ShowDeckList()
    {
        var view = new DeckListView(_store);
        view.OnExitToMainMenu += ShowMainMenu;
        view.OnCreateDeck += ShowWizard;
        view.OnEditDeck += d => ShowBuilder(d);
        view.OnBattleDeck += d => ShowBattle(d);
        view.OnOpenDiyWorkshop += ShowDiyWorkshop;
        view.OnOpenPackManager += ShowPackManager;
        Swap(view);
    }

    private void ShowBattle(ConstructedDeck deck)
    {
        if (_store == null) return;
        var view = new Battle.BattleScreenRoot();
        view.OnExitToDecks += ShowDeckList;
        Swap(view);

        // 对手用同一副牌组（本地演示没有联机对手）
        view.Boot(_store, deck, deck);
    }

    private void ShowDiyWorkshop()
    {
        if (_store == null) return;
        var view = new ShadowverseFan.Ui.Diy.DiyWorkshopView();
        view.OnBackToDecks += ShowDeckList;
        Swap(view);
        view.Boot(_store);
    }

    private void ShowPackManager()
    {
        if (_store == null) return;
        var view = new ShadowverseFan.Ui.Diy.PackAndPoolManagerView();
        view.OnBackToDecks += ShowDeckList;
        Swap(view);
        view.Boot(_store);
    }

    private void ShowWizard()
    {
        var modal = new CreateDeckWizardModal(_store);
        modal.OnCreated += deck =>
        {
            _store.SaveDeck(deck);
            ShowBuilder(deck);
        };
        modal.OnCancelled += () => { };
        AddChild(modal);

        // 模态层必须自己铺满：Control 的子节点 AddChild 后默认 0x0，
        // 而模态内部靠 SetAnchorsPreset(FullRect) 定位遮罩 ——
        // 父级是 0x0 时遮罩与居中面板全部不可见。
        modal.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    private void ShowBuilder(ConstructedDeck deck)
    {
        var view = new DeckBuilderView(_store, deck);
        view.OnSaveAndBack += _ => ShowDeckList();
        Swap(view);
    }

    /// <summary>开发期入口：稀有度对照页（截图用）。</summary>
    public void OpenRaritySheetForShot() => ShowRaritySheet();

    /// <summary>
    /// 稀有度对照：铜/银/金/虹 各抽两张并排，用来肉眼校验卡框配色。
    /// </summary>
    private void ShowRaritySheet()
    {
        var view = new Control();
        Swap(view);

        var bg = new Widgets.AstralBackgroundCanvas();
        view.AddChild(bg);

        var title = new Label { Text = "稀有度卡框对照 · 铜 / 银 / 金 / 虹" };
        view.AddChild(title);
        title.SetAnchorsPreset(LayoutPreset.TopLeft);
        title.Position = new Vector2(40, 24);
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));

        var pool = _store.GetPoolView("official");
        string[] names = { "青铜", "白银", "黄金", "虹彩" };
        Color[] hues = { new("B07A4A"), new("C8D0DC"), new("E6B23C"), new("C77DFF") };

        const float CW = 122f, CH = 152f, GAP = 18f;

        for (int r = 1; r <= 4; r++)
        {
            float y = 62 + (r - 1) * (CH + 16f);

            var lbl = new Label { Text = names[r - 1] };
            view.AddChild(lbl);
            lbl.SetAnchorsPreset(LayoutPreset.TopLeft);
            lbl.Position = new Vector2(36, y + CH * 0.5f - 14);
            lbl.Size = new Vector2(56, 0);
            lbl.AddThemeFontSizeOverride("font_size", 17);
            lbl.AddThemeColorOverride("font_color", hues[r - 1]);

            var picks = pool.EnumerateCollectibleCards()
                .Where(c => c.Rarity == r).OrderBy(c => c.CardId).Take(4).ToList();

            for (int i = 0; i < picks.Count; i++)
            {
                var def = picks[i];
                var w = new Widgets.CardVisualWidget();
                view.AddChild(w);
                w.SetCardSize(CW, CH);
                w.Position = new Vector2(110 + i * (CW + GAP), y);
                w.Setup(def, _store.CardPrinter, 1, 3);
            }
        }
    }

    /// <summary>开发期入口：牌组一览（截图用）。</summary>
    public void OpenDeckListForShot() => ShowDeckList();

    /// <summary>开发期入口：主界面（截图用）。</summary>
    public void OpenMainMenuForShot() => ShowMainMenu();

    /// <summary>开发期入口：对战选择窗（截图用）。</summary>
    public void OpenBattleSelectForShot()
    {
        ShowMainMenu();
        ShowBattleSelect();
    }

    /// <summary>开发期入口：直接打开第一副牌组的构筑台（截图用）。</summary>
    public void OpenFirstDeckForShot()
    {
        if (_store == null) return;
        // 挑一副有卡的，没有就现造一副
        var deck = _store.SavedDecks.OrderByDescending(d => d.Cards.Count).FirstOrDefault()
                   ?? _store.CreateDeck("standard", "Swordcraft", "截图牌组");
        ShowBuilder(deck);
    }

    /// <summary>开发期入口：直接弹创建向导（截图用）。</summary>
    public void OpenWizardForShot()
    {
        if (_store == null) return;
        ShowDeckList();
        ShowWizard();
    }

    /// <summary>开发期入口：进构筑台并让浮窗显示第一张卡的效果（截图用）。</summary>
    public void OpenHoverForShot()
    {
        if (_store == null) return;
        var deck = _store.SavedDecks.OrderByDescending(d => d.Cards.Count).FirstOrDefault()
                   ?? _store.CreateDeck("standard", "Swordcraft", "截图牌组");
        var view = new DeckBuilderView(_store, deck);
        view.OnSaveAndBack += _ => ShowDeckList();
        Swap(view);
        CallDeferred(nameof(ForceHover));
    }

    private void ForceHover()
    {
        if (_current is DeckBuilderView bv) bv.ShowFirstTooltipForShot();
    }

    /// <summary>开发期入口：直接进战斗桌面（截图用）。</summary>
    public void OpenBattleForShot(bool expanded, bool badges = false, bool mulligan = false, bool board = false)
    {
        if (_store == null) return;
        var deck = _store.SavedDecks.OrderByDescending(d => d.Cards.Count).FirstOrDefault()
                   ?? _store.CreateDeck("all-clans", "Forestcraft", "截图牌组");

        var view = new Battle.BattleScreenRoot();
        Swap(view);
        view.Boot(_store, deck, deck);

        // 截图分流：--mulligan 停在换牌画面，
        // 其余模式（--battle / --battle-expanded / --battle-badges）跳过换牌直接进交战画面。
        // 否则 Boot 默认停在 Mulligan，后几种截到的永远是换牌界面。
        // board 模式也必须跳过换牌：否则截图里只有换牌遮罩，
        // 战场上的随从全被压在后面看不见。
        if (!mulligan)
        {
            if (view.Controller != null && view.Controller.IsInMulligan)
            {
                view.Controller.ConfirmPlayerMulligan(Array.Empty<RuntimeEntity>());
                view.Rebuild();
            }
        }

        if (expanded) view.SetHandExpanded(true);
        if (badges) view.SpawnBadgeDemo();

        // board 模式：确认换牌之后再放随从。
        // 反过来的话，SpawnBoardDemo 里的 Rebuild() 会先跑一遍，
        // 而那时仍是 Mulligan 阶段，换牌遮罩被重新建出来盖住战场。
        if (board)
        {
            GD.Print("[board] before confirm: mulligan=" + (view.Controller?.IsInMulligan));
            if (view.Controller != null && view.Controller.IsInMulligan)
                view.Controller.ConfirmPlayerMulligan(Array.Empty<RuntimeEntity>());
            GD.Print("[board] after confirm: mulligan=" + (view.Controller?.IsInMulligan)
                     + " phase=" + view.Controller?.Runner.Phase);
            view.SpawnBoardDemo();
        }
    }

    /// <summary>开发期入口：在战斗界面制造一次伤害（截图用）。</summary>
    public void PlayDamageDemoForShot()
    {
        if (_current is Battle.BattleScreenRoot b) b.PlayDamageDemo();
    }

    /// <summary>开发期入口：直接打开 DIY 造卡工坊（截图用）。</summary>
    public void OpenDiyForShot() => ShowDiyWorkshop();

    /// <summary>开发期入口：直接打开卡包/卡池管理（截图用）。</summary>
    public void OpenPackMgrForShot() => ShowPackManager();

    private void ShowFatal(string msg)
    {
        var lbl = new Label
        {
            Text = "加载失败：" + msg,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        lbl.AddThemeColorOverride("font_color", SvWbTheme.StatusDanger);
        lbl.SetAnchorsPreset(LayoutPreset.Center);
        AddChild(lbl);
    }
}
