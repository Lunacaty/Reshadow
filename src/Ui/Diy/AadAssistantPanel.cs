using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

// ============================================================================
// AAD（AI 辅助 DIY）面板 —— 185 号文档 专题二。
//
// 三块结构：
//   ① 顶部配置栏：API 设置 + 提示词资产 + 清空上下文
//   ② 中部上下文时间线：每条消息可启停 / 换角色 / 上下移 / 删除 / 直接改文本
//   ③ 底部生成与回填：发送请求 → 预览 PyAST → 两个回填按钮
//
// 为什么时间线要可编辑：模型偶尔会给出半对的结果，玩家直接改那条
// assistant 消息比「重新描述一遍需求」快得多。
// ============================================================================

public sealed partial class AadAssistantPanel : VBoxContainer
{
    private AadSessionController _sess;
    private DiyWorkshopController _ctrl;
    private string _writableRoot;

    private VBoxContainer _timeline;
    private TextEdit _preview;
    private Label _status;
    /// <summary>请求进行中的旋转指示（文档要求「异步等待期间显示 Spinner」）。</summary>
    private TextureRect _spinner;
    private double _spinT;
    private Button _sendBtn;
    private OptionButton _assetPicker;
    private bool _busy;

    public void Setup(DiyWorkshopController ctrl, AadSessionController sess, string writableRoot)
    {
        _ctrl = ctrl;
        _sess = sess;
        _writableRoot = writableRoot;

        AddThemeConstantOverride("separation", 8);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        BuildTopBar();

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(scroll);
        AddChild(scroll);

        _timeline = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _timeline.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_timeline);

        BuildBottomBar();
        RebuildTimeline();
    }

    // ---------------------------------------------------------------- 顶部

    private void BuildTopBar()
    {
        var bar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddThemeConstantOverride("h_separation", 8);
        bar.AddThemeConstantOverride("v_separation", 6);
        AddChild(bar);

        var cfgBtn = new Button { Text = "⚙ API 设置", CustomMinimumSize = new Vector2(110, 30) };
        SvWbTheme.StyleButton(cfgBtn);
        cfgBtn.AddThemeFontSizeOverride("font_size", 11);
        cfgBtn.Pressed += OpenConfigDialog;
        bar.AddChild(cfgBtn);

        _assetPicker = new OptionButton { CustomMinimumSize = new Vector2(170, 30) };
        SvWbTheme.StyleButton(_assetPicker);
        SvWbTheme.StyleOptionPopup(_assetPicker);
        _assetPicker.AddThemeFontSizeOverride("font_size", 11);
        bar.AddChild(_assetPicker);

        var injectBtn = new Button { Text = "➕ 插入到上下文", CustomMinimumSize = new Vector2(130, 30) };
        SvWbTheme.StyleButton(injectBtn, customAccent: SvWbTheme.AccentCyan);
        injectBtn.AddThemeFontSizeOverride("font_size", 11);
        injectBtn.Pressed += () =>
        {
            int i = _assetPicker.Selected;
            if (i < 0 || i >= _sess.PromptAssets.Count) { SetStatus("先选一个提示词资产"); return; }
            _sess.InjectPromptAsset(_sess.PromptAssets[i]);
            RebuildTimeline();
            SetStatus("已插入提示词：" + _sess.PromptAssets[i].Name);
        };
        bar.AddChild(injectBtn);

        // 186 号文档 缺陷 5：资产与会话此前只能靠手改磁盘文件。
        var newAssetBtn = new Button { Text = "📝 新建资产", CustomMinimumSize = new Vector2(100, 30) };
        SvWbTheme.StyleButton(newAssetBtn);
        newAssetBtn.AddThemeFontSizeOverride("font_size", 11);
        newAssetBtn.Pressed += OpenNewAssetDialog;
        bar.AddChild(newAssetBtn);

        var openDirBtn = new Button { Text = "📂 资产目录", CustomMinimumSize = new Vector2(100, 30) };
        SvWbTheme.StyleButton(openDirBtn);
        openDirBtn.AddThemeFontSizeOverride("font_size", 11);
        openDirBtn.Pressed += () =>
        {
            string dir = System.IO.Path.Combine(_sess.AadRoot(_writableRoot), "prompts");
            System.IO.Directory.CreateDirectory(dir);
            OS.ShellOpen(dir);
        };
        bar.AddChild(openDirBtn);

        var saveSessBtn = new Button { Text = "💾 保存会话", CustomMinimumSize = new Vector2(100, 30) };
        SvWbTheme.StyleButton(saveSessBtn);
        saveSessBtn.AddThemeFontSizeOverride("font_size", 11);
        saveSessBtn.Pressed += OpenSaveSessionDialog;
        bar.AddChild(saveSessBtn);

        var loadSessBtn = new Button { Text = "📂 加载会话", CustomMinimumSize = new Vector2(100, 30) };
        SvWbTheme.StyleButton(loadSessBtn);
        loadSessBtn.AddThemeFontSizeOverride("font_size", 11);
        loadSessBtn.Pressed += OpenLoadSessionDialog;
        bar.AddChild(loadSessBtn);

        var clearBtn = new Button { Text = "🗑 清空上下文", CustomMinimumSize = new Vector2(110, 30) };
        SvWbTheme.StyleButton(clearBtn, customAccent: SvWbTheme.StatusDanger);
        clearBtn.AddThemeFontSizeOverride("font_size", 11);
        clearBtn.Pressed += () => { _sess.ClearMessages(); RebuildTimeline(); SetStatus("上下文已清空"); };
        bar.AddChild(clearBtn);

        var addSys = new Button { Text = "+ System", CustomMinimumSize = new Vector2(90, 30) };
        SvWbTheme.StyleButton(addSys);
        addSys.AddThemeFontSizeOverride("font_size", 11);
        addSys.Pressed += () => { _sess.AddMessage("system"); RebuildTimeline(); };
        bar.AddChild(addSys);

        var addUser = new Button { Text = "+ User", CustomMinimumSize = new Vector2(80, 30) };
        SvWbTheme.StyleButton(addUser);
        addUser.AddThemeFontSizeOverride("font_size", 11);
        addUser.Pressed += () => { _sess.AddMessage("user"); RebuildTimeline(); };
        bar.AddChild(addUser);

        var addAsst = new Button { Text = "+ Assistant", CustomMinimumSize = new Vector2(100, 30) };
        SvWbTheme.StyleButton(addAsst);
        addAsst.AddThemeFontSizeOverride("font_size", 11);
        addAsst.Pressed += () => { _sess.AddMessage("assistant"); RebuildTimeline(); };
        bar.AddChild(addAsst);

        RefreshAssetPicker();
    }

    private void RefreshAssetPicker()
    {
        if (_assetPicker == null || !IsInstanceValid(_assetPicker)) return;
        _assetPicker.Clear();
        _sess.LoadPromptAssets(_writableRoot, out _);
        if (_sess.PromptAssets.Count == 0)
        {
            _assetPicker.AddItem("（无提示词资产）");
            _assetPicker.Disabled = true;
            return;
        }
        _assetPicker.Disabled = false;
        foreach (var a in _sess.PromptAssets) _assetPicker.AddItem(a.Name);
        _assetPicker.Selected = 0;
    }

    // ---------------------------------------------------------------- 底部

    private void BuildBottomBar()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        _sendBtn = new Button { Text = "🚀 发送请求生成 PyAST", CustomMinimumSize = new Vector2(200, 34) };
        SvWbTheme.StyleButton(_sendBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        _sendBtn.AddThemeFontSizeOverride("font_size", 12);
        _sendBtn.Pressed += OnSendPressed;
        row.AddChild(_sendBtn);

        var applyFull = new Button { Text = "⚡ 编译整卡（含 card 头）", CustomMinimumSize = new Vector2(190, 34) };
        SvWbTheme.StyleButton(applyFull);
        applyFull.AddThemeFontSizeOverride("font_size", 11);
        applyFull.Pressed += () =>
        {
            string code = _preview?.Text ?? "";
            if (string.IsNullOrWhiteSpace(code)) { SetStatus("预览为空"); return; }
            bool ok = _ctrl.CompileFromScript(code);
            SetStatus(ok ? "✔ 整卡编译成功" : "✘ 编译失败：" + _ctrl.LastStatusMessage);
        };
        row.AddChild(applyFull);

        var applyBody = new Button { Text = "⚡ 仅应用效果块", CustomMinimumSize = new Vector2(160, 34) };
        SvWbTheme.StyleButton(applyBody);
        applyBody.AddThemeFontSizeOverride("font_size", 11);
        applyBody.Pressed += () =>
        {
            string code = _preview?.Text ?? "";
            if (string.IsNullOrWhiteSpace(code)) { SetStatus("预览为空"); return; }
            bool ok = _ctrl.CompileFromCanonicalAst(code);
            SetStatus(ok ? "✔ 效果块已应用" : "✘ 编译失败：" + _ctrl.LastStatusMessage);
        };
        row.AddChild(applyBody);

        _spinner = new TextureRect
        {
            CustomMinimumSize = new Vector2(18, 18),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false,
            Texture = MakeSpinnerTexture(),
        };
        row.AddChild(_spinner);

        _status = new Label { Text = "就绪", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        row.AddChild(_status);

        _preview = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 120),
            PlaceholderText = "提取出的 PyAST 会显示在这里，可直接编辑后回填",
            WrapMode = TextEdit.LineWrappingMode.None,
        };
        _preview.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_preview);
    }

    // ---------------------------------------------------------------- 时间线

    private void RebuildTimeline()
    {
        if (_timeline == null || !IsInstanceValid(_timeline)) return;
        foreach (var c in _timeline.GetChildren()) { _timeline.RemoveChild(c); c.QueueFree(); }

        if (_sess.ContextMessages.Count == 0)
        {
            var hint = new Label
            {
                Text = "上下文为空。点上方 [+ System] 加规则，再点 [+ User] 写需求；"
                       + "或点 [⚙ API 设置] 配好模型后直接用内置提示词。",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            hint.AddThemeFontSizeOverride("font_size", 12);
            hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _timeline.AddChild(hint);
            return;
        }

        for (int i = 0; i < _sess.ContextMessages.Count; i++)
            _timeline.AddChild(BuildMessageRow(_sess.ContextMessages[i], i));
    }

    private Control BuildMessageRow(AadChatMessage msg, int index)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 6, 1, 6));

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        box.AddChild(v);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        v.AddChild(head);

        var chk = new CheckBox { ButtonPressed = msg.Enabled, TooltipText = "启用 / 停用这条上下文" };
        chk.Toggled += on => { msg.Enabled = on; };
        head.AddChild(chk);

        var role = new OptionButton { CustomMinimumSize = new Vector2(110, 26) };
        SvWbTheme.StyleButton(role);
        SvWbTheme.StyleOptionPopup(role);
        role.AddItem("system");
        role.AddItem("user");
        role.AddItem("assistant");
        role.Selected = msg.Role == "system" ? 0 : msg.Role == "assistant" ? 2 : 1;
        role.ItemSelected += idx =>
        {
            msg.Role = (int)idx == 0 ? "system" : (int)idx == 2 ? "assistant" : "user";
        };
        role.AddThemeFontSizeOverride("font_size", 11);
        head.AddChild(role);

        var idxLabel = new Label { Text = "#" + index, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        idxLabel.AddThemeFontSizeOverride("font_size", 10);
        idxLabel.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        head.AddChild(idxLabel);

        var up = new Button { Text = "▲", CustomMinimumSize = new Vector2(30, 26) };
        SvWbTheme.StyleButton(up);
        up.Pressed += () => { _sess.MoveMessage(index, -1); RebuildTimeline(); };
        head.AddChild(up);

        var down = new Button { Text = "▼", CustomMinimumSize = new Vector2(30, 26) };
        SvWbTheme.StyleButton(down);
        down.Pressed += () => { _sess.MoveMessage(index, 1); RebuildTimeline(); };
        head.AddChild(down);

        var del = new Button { Text = "🗑", CustomMinimumSize = new Vector2(34, 26) };
        SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
        del.Pressed += () => { _sess.RemoveMessage(index); RebuildTimeline(); };
        head.AddChild(del);

        var editor = new TextEdit
        {
            Text = msg.Content ?? "",
            CustomMinimumSize = new Vector2(0, 56),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        editor.AddThemeFontSizeOverride("font_size", 12);
        editor.TextChanged += () => { msg.Content = editor.Text; };
        v.AddChild(editor);

        return box;
    }

    // ---------------------------------------------------------------- 发送

    private async void OnSendPressed()
    {
        if (_busy) return;
        _busy = true;
        _sendBtn.Disabled = true;
        SetStatus("请求中…");
        if (_spinner != null && IsInstanceValid(_spinner)) _spinner.Visible = true;

        var (ok, raw, py, err) = await _sess.SendAsync(CancellationToken.None);

        _busy = false;
        if (IsInstanceValid(_sendBtn)) _sendBtn.Disabled = false;

        if (_spinner != null && IsInstanceValid(_spinner)) _spinner.Visible = false;
        if (!ok) { SetStatus("✘ " + err); return; }

        _sess.AddMessage("assistant", raw);
        RebuildTimeline();
        if (_preview != null && IsInstanceValid(_preview)) _preview.Text = py;
        SetStatus(string.IsNullOrWhiteSpace(py) ? "⚠ 未从回复里提取到代码" : "✔ 已生成，可编辑后回填");
    }

    /// <summary>画一个 18x18 的圆环贴图，配 _Process 旋转即成 Spinner。</summary>
    private static Texture2D MakeSpinnerTexture()
    {
        const int n = 18;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        float cx = (n - 1) / 2f, cy = (n - 1) / 2f, r = n / 2f - 1.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > r - 1.4f && d < r + 0.4f)
                {
                    // 沿圆周做透明度渐隐，旋转时才有「转」的观感。
                    float ang = (Mathf.Atan2(dy, dx) + Mathf.Pi) / (2f * Mathf.Pi);
                    float a = 0.15f + 0.85f * ang;
                    img.SetPixel(x, y, new Color(0.22f, 0.74f, 0.97f, a));
                }
            }
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        if (_spinner == null || !IsInstanceValid(_spinner) || !_spinner.Visible) return;
        _spinT += delta * 6.0;
        _spinner.PivotOffset = _spinner.Size / 2f;
        _spinner.Rotation = (float)_spinT;
    }

    private void SetStatus(string s)
    {
        if (_status != null && IsInstanceValid(_status)) _status.Text = s;
    }

    // ---------------------------------------------------------------- 配置弹窗

    /// <summary>新建 / 覆盖提示词资产（写入 aad/prompts/&lt;名称&gt;.md）。</summary>
    private void OpenNewAssetDialog()
    {
        var dlg = new AcceptDialog { Title = "新建 / 覆盖提示词资产", Size = new Vector2I(600, 460) };
        dlg.MaxSize = new Vector2I(640, 640);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);

        int sel = _assetPicker != null ? _assetPicker.Selected : -1;
        var curAsset = (sel >= 0 && sel < _sess.PromptAssets.Count) ? _sess.PromptAssets[sel] : null;

        var nameEdit = new LineEdit
        {
            Text = curAsset?.Name ?? "",
            PlaceholderText = "资产名称（如 01_我的PyAST语法文档）",
        };
        box.AddChild(new Label { Text = "文件名（保存在 aad/prompts/<名称>.md）：" });
        box.AddChild(nameEdit);

        var contentEdit = new TextEdit
        {
            Text = curAsset?.Content ?? "",
            CustomMinimumSize = new Vector2(560, 300),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "在此粘贴 PyAST 语法文档、规则说明或 Few-Shot 范例…",
        };
        contentEdit.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(new Label { Text = "提示词内容：" });
        box.AddChild(contentEdit);

        dlg.AddChild(box);
        dlg.Confirmed += () =>
        {
            string rawName = (nameEdit.Text ?? "").Trim();
            if (string.IsNullOrEmpty(rawName)) { SetStatus("✘ 资产名称不能为空"); return; }
            string safe = string.Join("_", rawName.Split(System.IO.Path.GetInvalidFileNameChars()));
            string dir = System.IO.Path.Combine(_sess.AadRoot(_writableRoot), "prompts");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, safe + ".md"),
                    contentEdit.Text ?? "", new System.Text.UTF8Encoding(false));
                RefreshAssetPicker();
                SetStatus("✔ 已保存提示词资产：" + safe);
            }
            catch (Exception ex) { SetStatus("✘ 保存失败：" + ex.Message); }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>把当前上下文时间线存成命名会话。</summary>
    private void OpenSaveSessionDialog()
    {
        var dlg = new AcceptDialog { Title = "保存当前上下文会话", Size = new Vector2I(420, 170) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(new Label { Text = "会话名称（保存在 aad/sessions/<名称>.json）：" });
        var edit = new LineEdit { Text = "default", PlaceholderText = "输入会话名称…" };
        box.AddChild(edit);
        dlg.AddChild(box);
        dlg.Confirmed += () =>
        {
            _sess.SaveSession(_writableRoot, edit.Text?.Trim(), out string msg);
            SetStatus(msg);
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>从 aad/sessions/*.json 里挑一个会话载入时间线。</summary>
    private void OpenLoadSessionDialog()
    {
        string dir = System.IO.Path.Combine(_sess.AadRoot(_writableRoot), "sessions");
        System.IO.Directory.CreateDirectory(dir);
        var files = System.IO.Directory.GetFiles(dir, "*.json")
                    .Select(System.IO.Path.GetFileNameWithoutExtension)
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (files.Count == 0) { SetStatus("暂无已保存的会话"); return; }

        var dlg = new AcceptDialog { Title = "加载上下文会话", Size = new Vector2I(420, 190) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(new Label { Text = "选择要载入的会话（会覆盖当前时间线）：" });
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleButton(picker);
        SvWbTheme.StyleOptionPopup(picker);
        foreach (var f in files) picker.AddItem(f);
        picker.Selected = 0;
        box.AddChild(picker);
        dlg.AddChild(box);
        dlg.Confirmed += () =>
        {
            string chosen = files[Math.Clamp(picker.Selected, 0, files.Count - 1)];
            if (_sess.LoadSession(_writableRoot, chosen, out string msg)) RebuildTimeline();
            SetStatus(msg);
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OpenConfigDialog()
    {
        var dlg = new AcceptDialog { Title = "AAD · API 设置", Size = new Vector2I(520, 300) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);

        LineEdit Field(string label, string initial, string placeholder = "")
        {
            var row = new HBoxContainer();
            var lb = new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) };
            lb.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(lb);
            var ed = new LineEdit
            {
                Text = initial ?? "",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                PlaceholderText = placeholder,
            };
            row.AddChild(ed);
            box.AddChild(row);
            return ed;
        }

        var urlEdit = Field("BaseUrl", _sess.Config.BaseUrl, "https://api.openai.com/v1");
        var keyEdit = Field("ApiKey", _sess.Config.ApiKey, "sk-…（本地模型可留空）");
        keyEdit.Secret = true;
        var modelEdit = Field("Model", _sess.Config.Model, "gpt-4o-mini / deepseek-chat …");

        var tempRow = new HBoxContainer();
        var tempLb = new Label { Text = "Temperature", CustomMinimumSize = new Vector2(90, 0) };
        tempLb.AddThemeFontSizeOverride("font_size", 12);
        tempRow.AddChild(tempLb);
        var tempSpin = new SpinBox { MinValue = 0, MaxValue = 2, Step = 0.1, Value = _sess.Config.Temperature };
        tempSpin.CustomMinimumSize = new Vector2(100, 30);
        tempRow.AddChild(tempSpin);
        box.AddChild(tempRow);

        var useDefault = new Button { Text = "插入内置语法提示词为 System", CustomMinimumSize = new Vector2(0, 30) };
        SvWbTheme.StyleButton(useDefault, customAccent: SvWbTheme.AccentCyan);
        useDefault.AddThemeFontSizeOverride("font_size", 11);
        useDefault.Pressed += () =>
        {
            _sess.InsertAt(0, "system", AadSessionController.DefaultSystemPrompt());
            RebuildTimeline();
            dlg.QueueFree();
            SetStatus("已插入内置语法提示词");
        };
        box.AddChild(useDefault);

        var hint = new Label
        {
            Text = "兼容 OpenAI 格式：DeepSeek / 各类转发 / 本地 Ollama 都可用。"
                   + "配置写入 aad/llm_config.json。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        box.AddChild(hint);

        dlg.AddChild(box);
        dlg.Confirmed += () =>
        {
            _sess.Config.BaseUrl = urlEdit.Text?.Trim() ?? "";
            _sess.Config.ApiKey = keyEdit.Text?.Trim() ?? "";
            _sess.Config.Model = modelEdit.Text?.Trim() ?? "";
            _sess.Config.Temperature = (float)tempSpin.Value;
            _sess.SaveConfig(_writableRoot, out string msg);
            SetStatus(msg);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }
}