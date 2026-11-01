using System;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Packs;
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

        ShowDeckList();
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
        view.OnCreateDeck += ShowWizard;
        view.OnEditDeck += d => ShowBuilder(d);
        Swap(view);
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
    }

    private void ShowBuilder(ConstructedDeck deck)
    {
        var view = new DeckBuilderView(_store, deck);
        view.OnSaveAndBack += _ => ShowDeckList();
        Swap(view);
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
