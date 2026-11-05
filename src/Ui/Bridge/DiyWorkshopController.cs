using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.Ui.Bridge;

public enum DiyEndpointKind { Form = 0, Block = 1, Script = 2, EnglishCnl = 3 }

/// <summary>
/// DIY 四端造卡工坊的纯 C# 控制器（零 Godot 依赖，可在 .et 门禁里直接单测）。
///
/// 核心：四端共享同一个真相源 CurrentDef（CardDef）。
/// 任一端编译成功后立刻反向投影更新另外三端，
/// 玩家切页签时看到的永远是同一张卡的四种表达。
///
/// 表单端遇到超纲效果时标记只读并给出原因，**绝不静默丢弃** ——
/// 静默丢弃会让玩家以为自己造出了一张卡，实际存下来的是残缺版。
/// </summary>
public sealed class DiyWorkshopController
{
    public GameSessionStore Store { get; }
    public DiyWorkspace Workspace { get; private set; }

    public CardDef CurrentDef { get; private set; }
    public CardFormModel FormModel { get; private set; }
    public CardBlockWorkspace BlockModel { get; private set; }
    public string ScriptText { get; set; } = "";

    /// <summary>当前卡片的 AST JSON 文本（第三端改为直接编辑语法树）。</summary>
    public string AstJsonText { get; set; } = "";

    /// <summary>规范 AST 文本背后的原始 JSON（供 AST JSON 端点/序列化使用）。</summary>
    public string AstJsonRaw { get; set; } = "";
    public DiyWorkspace.CnlCardSpec CnlSpec { get; set; }

    public bool FormIsReadOnly { get; private set; }
    public string FormReadOnlyReason { get; private set; } = "";
    public List<string> LastErrors { get; } = new();
    public string LastStatusMessage { get; private set; } = "就绪";

    public event Action OnCardUpdated;

    public DiyWorkshopController(GameSessionStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Store.Initialize();

        Workspace = DiyWorkspace.Create(7, Store.Catalog.Glossary);

        // 把总表已注册的卡名灌进词法表，
        // 否则编辑现有卡时它连自己的卡名都认不出。
        foreach (var kv in Store.Catalog.CardsByName)
        {
            if (kv.Value != null) Workspace.Lexicon.AddCard(kv.Key, kv.Value.CardId);
        }
        Workspace.Lexicon.Build();

        LoadSampleCard();
    }

    /// <summary>加载一张带构筑规则与入场曲的示例卡，让工坊一打开就有东西看。</summary>
    public void LoadSampleCard()
    {
        string sample = string.Join("\n",
            "card 950001 \"星界旅者\" (Follower, cost: 3, atk: 3, life: 4, clan: Swordcraft) {",
            "    deck_mod size(+10)",
            "    keywords: [ward, rush]",
            "    on play {",
            "        draw(1)",
            "    }",
            "}");
        CompileFromScript(sample);
    }

    public void LoadExistingCard(CardDef def)
    {
        if (def == null) return;
        CurrentDef = def;
        LastErrors.Clear();
        SyncAllEndpointsFromCurrentDef();
        LastStatusMessage = "已加载《" + DeckEditorViewModel.DisplayName(def) + "》(ID: " + def.CardId + ")";
        OnCardUpdated?.Invoke();
    }

    // ---------------------------------------------------------------- 四端编译入口

    public bool CompileFromForm(CardFormModel form)
    {
        LastErrors.Clear();
        if (form == null) { LastStatusMessage = "表单为空"; return false; }

        var fc = new DiyFormCompiler(Workspace.Schema, Workspace.Lexicon);
        var def = fc.FormToCardDef(form);
        if (def == null || fc.Errors.Count > 0)
        {
            LastErrors.AddRange(fc.Errors);
            LastStatusMessage = "表单编译失败：" + string.Join("；", LastErrors);
            return false;
        }

        // 只读模式守卫：本卡的效果超出表单表达能力（有纹章/信仰/激奏/结晶/融合…），
        // 表单里的 Abilities 是空的。若直接拿它编译，会把《艾莉雅》的纹章、
        // 进化能力全部洗空 —— 玩家只是想改个费用，结果卡废了。
        //
        // 只允许用表单更新**头部属性**，复杂能力原样保留。
        if (FormIsReadOnly && CurrentDef != null)
        {
            var merged = new CardDef
            {
                CardId = CurrentDef.CardId,
                PackId = CurrentDef.PackId,
                IsToken = CurrentDef.IsToken,
                Name = string.IsNullOrWhiteSpace(form.NameEng) ? CurrentDef.Name : form.NameEng,
                NameChs = !string.IsNullOrWhiteSpace(form.NameChs) ? form.NameChs : CurrentDef.NameChs,
                Kind = CurrentDef.Kind,
                Clan = ParseClanOrKeep(form.Clan, CurrentDef.Clan),
                Traits = ParseTraitsOrKeep(form.Traits, CurrentDef.Traits),
                BaseCost = form.Cost,
                BaseAttack = form.Attack,
                BaseLife = form.Life,
                InnateKeywords = CurrentDef.InnateKeywords,
                InnateCounters = CurrentDef.InnateCounters,
                // 以下全部原样保留
                Abilities = CurrentDef.Abilities,
                DeckModifiers = CurrentDef.DeckModifiers,
                HasFuse = CurrentDef.HasFuse,
                FuseMaterialFilter = CurrentDef.FuseMaterialFilter,
                CrestDef = CurrentDef.CrestDef,
                FaithDef = CurrentDef.FaithDef,
                AccelerateDef = CurrentDef.AccelerateDef,
                CrystallizeDef = CurrentDef.CrystallizeDef,
                SourceText = CurrentDef.SourceText,
            };

            CurrentDef = merged;
            SyncAllEndpointsFromCurrentDef();
            LastStatusMessage = "✔ 已更新基础属性（本卡效果超出表单表达，复杂能力已保留）";
            OnCardUpdated?.Invoke();
            return true;
        }

        CurrentDef = PreserveChineseName(def, form.NameChs);
        SyncAllEndpointsFromCurrentDef();
        LastStatusMessage = "✔ 从【表单向导端】编译成功，已同步至其余三端";
        OnCardUpdated?.Invoke();
        return true;
    }

    public bool CompileFromBlocks(CardBlockWorkspace blocks)
    {
        LastErrors.Clear();
        if (blocks == null) { LastStatusMessage = "积木为空"; return false; }

        var bc = new DiyBlockCompiler(Workspace.Schema, Workspace.Lexicon);
        var res = bc.Compile(blocks);
        if (!res.Ok)
        {
            LastErrors.AddRange(res.Errors);
            LastStatusMessage = "积木编译失败：" + string.Join("；", LastErrors);
            return false;
        }

        CurrentDef = PreserveChineseName(res.Def, blocks.Name);
        SyncAllEndpointsFromCurrentDef();
        LastStatusMessage = "✔ 从【Scratch 积木端】编译成功，已同步至其余三端";
        OnCardUpdated?.Invoke();
        return true;
    }

    /// <summary>
    /// ③ AST 端：编辑 PyAST DSL 的**效果块**（无卡头）。
    ///
    /// 204/206 号文档落地后，这里从旧的 ReshadowScript 效果块换成了规范 AST 语法：
    ///   · 打印：AstCanonicalPrinter（同一份语法也用于卡包 source_format: "py_ast"）
    ///   · 解析：AstCanonicalParser.ParseAbilities（效果块 = 纯能力列表，无卡头）
    /// 卡名 / 费用 / 身材沿用当前卡，效果块只改能力。
    /// </summary>
    public bool CompileFromCanonicalAst(string dsl)
    {
        LastErrors.Clear();
        if (string.IsNullOrWhiteSpace(dsl))
        {
            LastStatusMessage = "AST 效果块为空";
            return false;
        }

        var parser = new AstCanonicalParser(Workspace.Schema, Workspace.Lexicon);
        List<AbilityDef> abilities = null;
        try
        {
            abilities = parser.ParseAbilities(dsl);
        }
        catch (Exception ex)
        {
            LastErrors.Add("AST 解析失败：" + ex.Message);
            LastStatusMessage = "AST 解析失败";
            return false;
        }

        if (parser.Errors.Count > 0)
        {
            LastErrors.AddRange(parser.Errors);
            LastStatusMessage = "AST 解析失败：" + parser.Errors[0];
            return false;
        }

        var head = CurrentDef ?? new CardDef { Kind = CardKind.Follower, Name = "DIY" };
        CurrentDef = new CardDef
        {
            CardId = head.CardId,
            Name = head.Name,
            NameChs = head.NameChs,
            Kind = head.Kind,
            BaseCost = head.BaseCost,
            BaseAttack = head.BaseAttack,
            BaseLife = head.BaseLife,
            Clan = head.Clan,
            Traits = head.Traits,
            InnateKeywords = head.InnateKeywords,
            DeckModifiers = head.DeckModifiers,
            HasFuse = head.HasFuse,
            FuseMaterialFilter = head.FuseMaterialFilter,
            CrestDef = head.CrestDef,
            FaithDef = head.FaithDef,
            AccelerateDef = head.AccelerateDef,
            CrystallizeDef = head.CrystallizeDef,
            IsToken = head.IsToken,
            Abilities = abilities,
            SourceText = head.SourceText,
        };

        // 同步其余三端；同步会重算 AstJsonText，所以最后覆盖回用户手写的文本，
        // 避免「刚敲的效果块被格式化结果顶掉」。
        string userText = dsl;
        SyncAllEndpointsFromCurrentDef();
        AstJsonText = userText;
        LastStatusMessage = "✔ 从【AST 端】编译成功（PyAST 效果块，卡头沿用原卡）";
        OnCardUpdated?.Invoke();
        return true;
    }

    /// <summary>
    /// ③ 端整卡编译：吃 PyAST 规范语法（含 card 头）。
    ///
    /// 旧 ReshadowScript 已下线 —— PyAST 是唯一的 AST 文本格式。
    /// 存量卡包里的 reshadow_script 数据仍由 GlobalCatalog / CardPool 的
    /// 兼容分支读取，但编辑端不再产出它。
    /// </summary>
    public bool CompileFromScript(string script)
    {
        LastErrors.Clear();
        ScriptText = script ?? "";

        var parser = new AstCanonicalParser(Workspace.Schema, Workspace.Lexicon);
        CardDef def = null;
        try { def = parser.ParseCard(ScriptText); }
        catch (Exception ex) { LastErrors.Add("PyAST 解析失败：" + ex.Message); }

        if (def == null || parser.Errors.Count > 0)
        {
            LastErrors.AddRange(parser.Errors);
            if (LastErrors.Count == 0) LastErrors.Add("PyAST 中没有 card 声明");
            LastStatusMessage = "编译失败：" + string.Join("；", LastErrors);
            return false;
        }

        CurrentDef = PreserveChineseName(def, def.Name);
        SyncAllEndpointsFromCurrentDef();
        LastStatusMessage = "✔ 从【AST 规范语法】编译成功，已同步至其余三端";
        OnCardUpdated?.Invoke();
        return true;
    }

    public bool CompileFromEnglishCnl(DiyWorkspace.CnlCardSpec spec)
    {
        LastErrors.Clear();
        if (spec == null) { LastStatusMessage = "英文规格为空"; return false; }
        CnlSpec = spec;

        var def = Workspace.CompileFromEnglishCnl(spec, out var errs);
        if (def == null || (errs != null && errs.Count > 0))
        {
            if (errs != null) LastErrors.AddRange(errs);
            LastStatusMessage = "英文编译失败：" + string.Join("；", LastErrors);
            return false;
        }

        CurrentDef = PreserveChineseName(def, spec.NameChs);
        SyncAllEndpointsFromCurrentDef();
        LastStatusMessage = "✔ 从【受限英文端】编译成功，已同步至其余三端";
        OnCardUpdated?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- 反向投影

    /// <summary>用 CurrentDef 反向生成四端模型，保证四端时刻同构。</summary>
    private void SyncAllEndpointsFromCurrentDef()
    {
        if (CurrentDef == null) return;

        // ① 脚本端：204/206 号文档后统一用 PyAST 规范语法。
        // 旧 ReshadowScriptCompiler.Print 已不再参与生产，只在读旧卡包时保留兼容。
        ScriptText = AstCanonicalPrinter.PrintCard(CurrentDef);

        // ② 积木端
        BlockModel = new DiyBlockDecompiler(Workspace.Schema).Decompile(CurrentDef);

        // ③ AST 端：直接把当前 CardDef 展开成可读的 JSON 语法树。
        // ③ AST 端展示的是 ReshadowScript 的**效果块**（无卡头），不是 JSON：
        // 复用脚本编译器自带的符号绑定，天然过四端同构门禁，
        // 不再维护第四套 AST 表示。
        AstJsonText = AstCanonicalPrinter.PrintAbilities(CurrentDef.Abilities ?? new List<AbilityDef>(), 0);
        AstJsonRaw = AstJsonCodec.Encode(CurrentDef);

        // ③ 表单端：超纲时标只读并保留原因，绝不静默丢效果
        var fc = new DiyFormCompiler(Workspace.Schema, Workspace.Lexicon);
        if (fc.TryCardDefToForm(CurrentDef, out var fm, out var reason))
        {
            FormModel = fm;
            FormIsReadOnly = false;
            FormReadOnlyReason = "";
        }
        else
        {
            FormIsReadOnly = true;
            FormReadOnlyReason = string.IsNullOrEmpty(reason) ? "该效果超出表单表达能力" : reason;
            // 兜底也要把卡号/英文名/卡种/职业/兵种填上 ——
            // 缺这些的话，玩家切到表单页签会看到「卡号 0、职业 中立」，
            // 完全认不出这是自己刚导入的那张卡。
            FormModel = new CardFormModel
            {
                CardId = CurrentDef.CardId,
                NameChs = DeckEditorViewModel.DisplayName(CurrentDef),
                NameEng = CurrentDef.Name ?? "",
                Kind = CurrentDef.Kind.ToString(),
                Clan = CurrentDef.Clan.IsValid ? CurrentDef.Clan.Name : "Neutral",
                Traits = (CurrentDef.Traits ?? Array.Empty<SymbolId<TraitTag>>())
                         .Select(t => t.Name).ToList(),
                Cost = CurrentDef.BaseCost,
                Attack = CurrentDef.BaseAttack,
                Life = CurrentDef.BaseLife,
            };
        }

        // ④ 受限英文端
        var np = new NaturalPrinter(Workspace.Glossary, RenderLang.English);
        string engText = !string.IsNullOrWhiteSpace(CurrentDef.SourceText)
            ? CurrentDef.SourceText
            : np.PrintCard(CurrentDef.Abilities);

        CnlSpec = new DiyWorkspace.CnlCardSpec
        {
            CardId = CurrentDef.CardId,
            Name = CurrentDef.Name,
            NameChs = DeckEditorViewModel.DisplayName(CurrentDef),
            Kind = CurrentDef.Kind,
            Cost = CurrentDef.BaseCost,
            Attack = CurrentDef.BaseAttack,
            Life = CurrentDef.BaseLife,
            Clan = CurrentDef.Clan.IsValid ? CurrentDef.Clan.Name : "Neutral",
            Traits = (CurrentDef.Traits ?? Array.Empty<SymbolId<TraitTag>>())
                     .Select(t => t.Name).ToList(),
            EngText = engText,
        };
    }

    public string GetChineseCardText()
        => CurrentDef == null ? "" : Workspace.PrintChinese(CurrentDef);

    public string GetCanonicalAstText()
        => CurrentDef == null ? "" : AstCanonicalPrinter.PrintAbilities(CurrentDef.Abilities);

    /// <summary>四端往返同构自检：Script 打印 → 重编译 → 结构化判等。</summary>
    public bool VerifyRoundTripIsomorphism(out string diff)
    {
        diff = "";
        if (CurrentDef == null) { diff = "当前无卡牌定义"; return false; }

        // PyAST 往返：打印 → 解析 → 结构判等。
        // 这是唯一的 AST 文本格式，旧 ReshadowScript / ast_json 已全部下线。
        string printed = AstCanonicalPrinter.PrintCard(CurrentDef);
        var parser = new AstCanonicalParser(Workspace.Schema, Workspace.Lexicon);
        CardDef recompiled = parser.ParseCard(printed);
        if (recompiled == null || parser.Errors.Count > 0)
        {
            diff = "PyAST 重解析失败：" + (parser.Errors.FirstOrDefault() ?? "null");
            return false;
        }
        if (!DiyWorkspace.StructurallyEqual(CurrentDef, recompiled, out diff))
        {
            diff = "PyAST 端：" + diff;
            return false;
        }
        // 二次打印必须字节级一致（证明是自给自足的双向序列化）。
        string again = AstCanonicalPrinter.PrintCard(recompiled);
        if (again != printed)
        {
            diff = "PyAST 二次打印不一致";
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 自定义词条

    public void DefineCustomTrait(string nameEng, string nameChs)
    {
        Workspace.DefineTrait(nameEng, nameChs);
        Store.Catalog.Glossary.Add(nameEng, nameChs);
        LastStatusMessage = "已注册新兵种：" + nameEng + "（" + nameChs + "）";
        OnCardUpdated?.Invoke();
    }

    public void DefineCustomCounter(string nameEng, string nameChs, int min, int max)
    {
        Workspace.DefineCounter(nameEng, nameChs, min, max);
        Store.Catalog.Glossary.Add(nameEng, nameChs);
        LastStatusMessage = "已注册新计数器：" + nameEng + "（" + nameChs + "，范围 " + min + ".." + max + "）";
        OnCardUpdated?.Invoke();
    }

    public void DefineCustomClan(string nameEng, string nameChs)
    {
        Workspace.DefineClan(nameEng, nameChs);
        Store.Catalog.Glossary.Add(nameEng, nameChs);
        LastStatusMessage = "已注册新职业：" + nameEng + "（" + nameChs + "）";
        OnCardUpdated?.Invoke();
    }

    // ---------------------------------------------------------------- 保存

    public bool SaveCurrentCardToPack(string packId, out string message)
    {
        message = "";
        if (CurrentDef == null) { message = "当前没有可保存的卡牌"; return false; }
        if (string.IsNullOrWhiteSpace(packId)) packId = "testset-v2";

        Store.SaveCardIntoPack(packId, CurrentDef, ScriptText);
        LastStatusMessage = "✔ 已将《" + DeckEditorViewModel.DisplayName(CurrentDef)
                          + "》保存至卡包 [" + packId + "] 并热重载卡池";
        message = LastStatusMessage;
        OnCardUpdated?.Invoke();
        return true;
    }

    /// <summary>
    /// 保留中文名。
    ///
    /// 各端的编译器只从自己的输入里取英文名，中文名会丢 ——
    /// 而中文名是卡面上玩家实际看到的那行字，丢了卡面就变英文了。
    /// </summary>
    private SymbolId<ClanTag> ParseClanOrKeep(string clanName, SymbolId<ClanTag> keep)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return keep;
        if (string.Equals(clanName, "Neutral", StringComparison.OrdinalIgnoreCase))
            return SymbolId<ClanTag>.Invalid;
        return Workspace.Schema.Clan(clanName);
    }

    private IReadOnlyList<SymbolId<TraitTag>> ParseTraitsOrKeep(
        List<string> names, IReadOnlyList<SymbolId<TraitTag>> keep)
    {
        if (names == null || names.Count == 0) return keep;
        return names.Select(n => Workspace.Schema.Trait(n)).ToList();
    }

    private static CardDef PreserveChineseName(CardDef d, string nameChs)
    {
        if (d == null) return null;
        return new CardDef
        {
            CardId = d.CardId,
            PackId = d.PackId,
            IsToken = d.IsToken,
            Name = d.Name,
            NameChs = !string.IsNullOrWhiteSpace(nameChs) ? nameChs : d.NameChs,
            Kind = d.Kind,
            Clan = d.Clan,
            Traits = d.Traits,
            BaseCost = d.BaseCost,
            BaseAttack = d.BaseAttack,
            BaseLife = d.BaseLife,
            InnateKeywords = d.InnateKeywords,
            InnateCounters = d.InnateCounters,
            Abilities = d.Abilities,
            DeckModifiers = d.DeckModifiers,
            HasFuse = d.HasFuse,
            FuseMaterialFilter = d.FuseMaterialFilter,
            CrestDef = d.CrestDef,
            FaithDef = d.FaithDef,
            AccelerateDef = d.AccelerateDef,
            CrystallizeDef = d.CrystallizeDef,
            SourceText = d.SourceText,
        };
    }

    // ---------------------------------------------------------------- 积木编辑

    /// <summary>
    /// 往某个事件帽下追加一个动作块。
    ///
    /// 下沉到控制器而不是留在视图里：视图是 Control 子类，
    /// 无头工程里根本 new 不出来 —— 逻辑留在这里才能被门禁覆盖。
    /// </summary>
    public bool AddActionBlock(int hatIndex, string opcode = "act_draw", int amount = 1)
    {
        var hats = BlockModel?.Hats;
        if (hats == null || hatIndex < 0 || hatIndex >= hats.Count) return false;

        var hat = hats[hatIndex];
        hat.Stacks ??= new List<Block>();

        // act_draw / act_damage 这类动作块的数值挂在 **Number** 上，
        // 而 Number 本身又是一颗积木（数值块），不是 IntA、也不是 NumExpr。
        // 早先按 IntA 塞值，反编译回来时数量整个丢失（全变成 0）。
        hat.Stacks.Add(new Block
        {
            Opcode = opcode,
            Number = new Block { Opcode = "num_const", IntA = amount },
        });

        return CompileFromBlocks(BlockModel);
    }

    /// <summary>删掉某个动作块（在任一事件帽的 Stacks / ElseStacks 里查找）。</summary>
    public bool RemoveActionBlock(Block blk)
    {
        var hats = BlockModel?.Hats;
        if (hats == null || blk == null) return false;

        foreach (var h in hats)
        {
            if (h.Stacks != null && h.Stacks.Remove(blk)) return CompileFromBlocks(BlockModel);
            if (h.ElseStacks != null && h.ElseStacks.Remove(blk)) return CompileFromBlocks(BlockModel);
        }
        return false;
    }

    /// <summary>
    /// 在同一事件帽内上移/下移一个动作块（dir = -1 上移，+1 下移）。
    ///
    /// 用**索引**而不是 Block 对象定位。原因：
    /// 每次 CompileFromBlocks 都重建整个 BlockModel，
    /// UI 手里那个 Block 是上一代的实例，IndexOf 必然找不到 ——
    /// 早先按对象身份找，导致「上移/下移」在真实交互里永远静默失败。
    /// </summary>
    public bool MoveActionBlock(int hatIndex, int slotIndex, int dir)
    {
        var hats = BlockModel?.Hats;
        if (hats == null || hatIndex < 0 || hatIndex >= hats.Count) return false;

        var list = hats[hatIndex].Stacks;
        if (list == null || slotIndex < 0 || slotIndex >= list.Count) return false;

        int ni = slotIndex + dir;
        if (ni < 0 || ni >= list.Count) return false;

        (list[slotIndex], list[ni]) = (list[ni], list[slotIndex]);
        return CompileFromBlocks(BlockModel);
    }

    /// <summary>
    /// 按对象删除（会在各帽子的 Stacks 里找同一实例）。
    /// 因为 CompileFromBlocks 会重建模型，调用方应尽量用 RemoveActionAt(hat, idx)。
    /// </summary>
    public bool RemoveActionAt(int hatIndex, int slotIndex)
    {
        var hats = BlockModel?.Hats;
        if (hats == null || hatIndex < 0 || hatIndex >= hats.Count) return false;

        var list = hats[hatIndex].Stacks;
        if (list == null || slotIndex < 0 || slotIndex >= list.Count) return false;

        list.RemoveAt(slotIndex);
        return CompileFromBlocks(BlockModel);
    }

    // ---------------------------------------------------------------- 全树操作（支持任意嵌套）

    /// <summary>当前选中的插入焦点列表；null 表示未指定。</summary>
    public List<Block> ActiveInsertList { get; private set; }

    /// <summary>把某个列表设为当前的插入焦点（画布上的「设为插入点」按钮）。</summary>
    public void SetActiveInsertList(List<Block> list) => ActiveInsertList = list;

    /// <summary>取某个能力域（0 本体 / 1 纹章 / 2 信仰 / 3 激奏 / 4 结晶 / 5 构筑）的根列表。</summary>
    public List<Block> RootListOf(int scope)
    {
        var ws = BlockModel;
        if (ws == null) return null;
        return scope switch
        {
            0 => ws.Hats,
            1 => ws.CrestHats,
            2 => ws.FaithHats,
            3 => ws.AccelerateHats,
            4 => ws.CrystallizeHats,
            5 => ws.DeckModBlocks,
            _ => ws.Hats,
        };
    }

    /// <summary>
    /// 按 opcode 追加一颗积木（调色盘点击）。
    ///
    /// 目标列表的选法是有讲究的：
    ///   · 根列表只收事件帽（hat_*）—— 往 Hats 里塞 act_damage 会被编译器
    ///     判成「不是事件帽积木」而整体失败；
    ///   · 动作块与控制流必须落到某个事件帽的 Stacks 里。
    /// 所以：有显式插入焦点就听它；否则按 opcode 自己挑合适的落点。
    /// </summary>
    public bool InsertBlock(string opcode, int scope = 0)
    {
        if (BlockModel == null) return false;
        var blk = DiyBlockTree.CreateDefaultBlock(opcode);
        if (blk == null) return false;

        List<Block> target = ActiveInsertList;
        if (target == null)
        {
            var root = RootListOf(scope);
            if (root == null) return false;
            if (opcode.StartsWith("hat_"))
            {
                target = root;
            }
            else
            {
                // 动作块 / 控制流：落到这个能力域最后一顶事件帽的 Stacks。
                // 没有帽子就自动补一顶，否则玩家点了调色盘什么都看不到。
                if (root.Count == 0 || !(root[root.Count - 1].Opcode ?? "").StartsWith("hat_"))
                    root.Add(DiyBlockTree.CreateDefaultBlock("hat_self_event"));
                var hat = root[root.Count - 1];
                hat.Stacks ??= new List<Block>();
                target = hat.Stacks;
            }
        }

        target.Add(blk);
        return CompileFromBlocks(BlockModel);
    }

    /// <summary>在整棵树里递归上下移动（支持嵌套在 if / repeat / mode 里的积木）。</summary>
    public bool MoveBlockInTree(Block blk, int dir)
    {
        if (BlockModel == null || blk == null) return false;
        if (!DiyBlockTree.Move(BlockModel, blk, dir)) return false;
        return CompileFromBlocks(BlockModel);
    }

    /// <summary>在整棵树里递归删除。</summary>
    public bool RemoveBlockFromTree(Block blk)
    {
        if (BlockModel == null || blk == null) return false;
        if (!DiyBlockTree.Remove(BlockModel, blk)) return false;
        return CompileFromBlocks(BlockModel);
    }

    /// <summary>在同级位置复制一份（深拷贝）。</summary>
    public bool DuplicateBlockInTree(Block blk)
    {
        if (BlockModel == null || blk == null) return false;
        if (!DiyBlockTree.Duplicate(BlockModel, blk)) return false;
        return CompileFromBlocks(BlockModel);
    }

    /// <summary>整棵树定位某个积木的父列表（UI 判断能否上下移/删除时用）。</summary>
    public List<Block> ParentContainerOf(Block blk)
        => BlockModel == null ? null : DiyBlockTree.FindParentContainer(BlockModel, blk);

    /// <summary>某个积木在兄弟列表里的位置（-1 表示不在树里）。</summary>
    public int IndexOfBlock(Block blk)
    {
        var list = ParentContainerOf(blk);
        return list == null ? -1 : list.IndexOf(blk);
    }

    /// <summary>当前第一顶事件帽下的动作块条数（供 UI 与门禁查询）。</summary>
    public int ActionCountOfHat(int hatIndex)
    {
        var hats = BlockModel?.Hats;
        if (hats == null || hatIndex < 0 || hatIndex >= hats.Count) return 0;
        return hats[hatIndex].Stacks?.Count ?? 0;
    }
}
