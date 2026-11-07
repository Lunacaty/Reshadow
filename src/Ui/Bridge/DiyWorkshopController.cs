using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    public DiyWorkspace.CnlCardSpec CnlSpec { get; set; }

    public bool FormIsReadOnly { get; private set; }
    public string FormReadOnlyReason { get; private set; } = "";
    public List<string> LastErrors { get; } = new();
    public string LastStatusMessage { get; private set; } = "就绪";

    public event Action OnCardUpdated;

    /// <summary>
    /// 工坊当前绑定的卡池。工坊不是「无环境」的造卡器 ——
    /// 卡池里定义的自定义职业、主战者计数器、卡名对照，
    /// 造卡时必须拿得到，否则造出来的卡在自己的卡池里编译不过。
    /// </summary>
    public string ActivePoolId { get; private set; } = "standard";

    public DiyWorkshopController(GameSessionStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Store.Initialize();

        BindPool(ActivePoolId, loadSample: true);
    }

    /// <summary>
    /// 切换工坊绑定的卡池并把符号表/词法表重建到该卡池的环境上。
    ///
    /// 只用 DiyWorkspace.Create 的话会新造一套 GameSchema ——
    /// 卡池里的自定义职业/计数器 SymbolId 跟本体对不上，卡造出来在自己的池子里跑不起来。
    /// </summary>
    public void BindPool(string poolId, bool loadSample = false)
    {
        ActivePoolId = string.IsNullOrWhiteSpace(poolId) ? "standard" : poolId;

        CompileContext ctx = null;
        Glossary glossary = Store.Catalog.Glossary;
        try
        {
            var rt = Store.GetPoolRuntime(ActivePoolId);
            if (rt != null)
            {
                ctx = CompileContext.For(rt.CreateBattleState(seed: 7));
                if (rt.Glossary != null) glossary = rt.Glossary;
            }
        }
        catch (Exception)
        {
            // 卡池没配好时退回默认环境：工坊不该因为池子坏了就打不开。
            ctx = null;
        }

        Workspace = ctx != null
            ? DiyWorkspace.For(ctx, glossary)
            : DiyWorkspace.Create(7, glossary);

        // 词法表只灌**当前卡池**的卡名。
        //
        // 从全局总表灌是错的：玩家在自己卡池里定义了一个 Knight，
        // 写「Summon a Knight」会解析到官方卡池那个 Knight 的卡号 ——
        // 名字一样、卡不一样，而且不报错，只表现为「召唤出了别的卡」。
        // 每个卡池有自己的符号表，DIY 就该只认自己池子里的东西。
        if (ctx != null)
        {
            var rt = Store.GetPoolRuntime(ActivePoolId);
            if (rt != null)
            {
                foreach (var kv in rt.CardsByName)
                {
                    if (kv.Value != null) Workspace.Lexicon.AddCard(kv.Key, kv.Value.CardId);
                }
            }
        }
        Workspace.Lexicon.Build();

        if (loadSample) LoadSampleCard();
        LastStatusMessage = "当前卡池：" + ActivePoolId;
    }

    /// <summary>
    /// 加载一张带构筑规则与入场曲的示例卡，让工坊一打开就有东西看。
    ///
    /// 语法必须是 PyAST（缩进体 + 冒号头）。旧的花括号脚本语法已经下线，
    /// 还留着它的话进工坊第一句就解析失败，CurrentDef 为 null，整个界面白屏。
    /// </summary>
    public void LoadSampleCard()
    {
        string sample = string.Join("\n",
            "card 950001 \"星界旅者\" (Follower, clan=皇家, cost=3, atk=3, life=4):",
            "    deck_mod: ModifyDeckSize 10 0",
            "    keywords: [ward, rush]",
            "    fanfare:",
            "        draw 1");
        CompileFromScript(sample);
    }


    // ---- 168 专项三：保存时区分「覆盖修改原卡」与「另存为新卡」----

    /// <summary>当前卡牌是否源自已有卡（决定保存时要不要问「覆盖还是另存」）。</summary>
    public int? LoadedOriginCardId { get; private set; }
    public string LoadedOriginPackId { get; private set; }

    public bool IsEditingExistingCard =>
        LoadedOriginCardId.HasValue && CurrentDef != null &&
        Store.Catalog.AllCards.ContainsKey(LoadedOriginCardId.Value);

    /// <summary>分配一个够用的新卡 ID（DIY 段 950000 之后取最大值 +1）。</summary>
    public int AllocateNewCardId()
    {
        int maxId = 950000;
        foreach (var id in Store.Catalog.AllCards.Keys)
            if (id >= 950000 && id < 10000000 && id > maxId) maxId = id;
        return maxId + 1;
    }

    /// <summary>
    /// 保存当前卡牌到指定卡包。
    ///
    /// overwriteExisting = true  覆盖原卡（沿用来源卡号）
    /// overwriteExisting = false 另存为新卡（分配新卡号）
    /// </summary>
    public bool SaveCurrentCardToPackAny(string packId, bool overwriteExisting, out string message)
    {
        message = "";
        if (CurrentDef == null) { message = "当前没有可保存的卡牌"; return false; }

        // 占位文字「（选择卡包）」绝不能被当成目录名写进磁盘。
        if (string.IsNullOrWhiteSpace(packId) || packId.StartsWith("（", StringComparison.Ordinal))
            packId = !string.IsNullOrWhiteSpace(LoadedOriginPackId) ? LoadedOriginPackId : "testset-v2";

        int targetId = overwriteExisting && LoadedOriginCardId.HasValue
            ? LoadedOriginCardId.Value
            : (overwriteExisting ? CurrentDef.CardId : AllocateNewCardId());

        // 覆盖模式但换了卡包：必须先从原卡包删掉旧卡，否则两个包同号，
        // 总表按卡号去重后只剩一张，另一张永远查不到。
        if (overwriteExisting && LoadedOriginCardId.HasValue &&
            !string.IsNullOrWhiteSpace(LoadedOriginPackId) &&
            !string.Equals(LoadedOriginPackId, packId, StringComparison.OrdinalIgnoreCase))
        {
            Store.DeleteCardFromPack(LoadedOriginPackId, targetId, out _);
        }

        var saved = CloneDefWithId(CurrentDef, targetId, packId);
        CurrentDef = saved;
        SyncAllEndpointsFromCurrentDef();
        Store.SaveCardIntoPack(packId, saved, AstCanonicalPrinter.PrintCard(saved));

        LoadedOriginCardId = saved.CardId;
        LoadedOriginPackId = packId;

        string tag = overwriteExisting ? "覆盖修改原卡" : "另存为新卡 (#" + targetId + ")";
        LastStatusMessage = "✔ 已" + tag + "《" + DeckEditorViewModel.DisplayName(saved) + "》至卡包 [" + packId + "]";
        message = LastStatusMessage;
        OnCardUpdated?.Invoke();
        return true;
    }

    /// <summary>复制一份卡定义并改掉卡号与所属卡包（其余字段原样保留）。</summary>
    private static ShadowverseFan.KernelV3.Ast.CardDef CloneDefWithId(
        ShadowverseFan.KernelV3.Ast.CardDef src, int newId, string packId)
    {
        if (src == null) return null;
        var c = src;
        return new ShadowverseFan.KernelV3.Ast.CardDef
        {
            CardId = newId,
            // 另存新卡要继承原卡立绘号，否则新卡去找 950002.png 找不到，
            // 手牌、构筑台、战场全是黑底无图。
            ArtCardId = c.ArtCardId > 0 ? c.ArtCardId : (c.CardId > 0 ? c.CardId : 0),
            PackId = packId,
            Name = c.Name,
            NameChs = c.NameChs,
            Kind = c.Kind,
            Clan = c.Clan,
            Traits = c.Traits,
            BaseCost = c.BaseCost,
            BaseAttack = c.BaseAttack,
            BaseLife = c.BaseLife,
            Rarity = c.Rarity,
            IsToken = c.IsToken,
            InnateKeywords = c.InnateKeywords,
            InnateCounters = c.InnateCounters,
            Abilities = c.Abilities,
            DeckModifiers = c.DeckModifiers,
            HasFuse = c.HasFuse,
            FuseMaterialFilter = c.FuseMaterialFilter,
            CrestDef = c.CrestDef,
            FaithDef = c.FaithDef,
            AccelerateDef = c.AccelerateDef,
            CrystallizeDef = c.CrystallizeDef,
            SourceText = c.SourceText,
        };
    }

    public void LoadExistingCard(CardDef def)
    {
        if (def == null) return;
        // 记下来源：保存时要靠它判断「这是改出来的卡」并弹出
        // 「覆盖原卡 / 另存新卡」二选一。不记的话双选框永远不出现，
        // 玩家想留个原版时会直接把原卡改没。
        LoadedOriginCardId = def.CardId > 0 ? def.CardId : (int?)null;
        LoadedOriginPackId = def.PackId;
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
                // 179 号文档 D5：只读表单也必须完整保留元属性。
                // 漏了 ArtCardId 会让卡面变成黑底（找图按卡号查，查不到那张立绘）；
                // 漏了 Rarity 会让稀有度框掉回普通。
                ArtCardId = CurrentDef.ArtCardId,
                Rarity = CurrentDef.Rarity,
                PackId = CurrentDef.PackId,
                IsToken = CurrentDef.IsToken,
                Name = string.IsNullOrWhiteSpace(form.NameEng) ? CurrentDef.Name : form.NameEng,
                NameChs = !string.IsNullOrWhiteSpace(form.NameChs) ? form.NameChs : CurrentDef.NameChs,
                // 卡种必须听表单的：只读的是「效果能力」，不是「这张卡是什么」。
                // 写死 CurrentDef.Kind 的话，玩家在表单里把随从改成法术，
                // 保存后还是随从，而界面上完全看不出被回退了。
                Kind = ParseKindOrKeep(form.Kind, CurrentDef.Kind),
                Clan = ParseClanOrKeep(form.Clan, CurrentDef.Clan),
                Traits = ParseTraitsOrKeep(form.Traits, CurrentDef.Traits),
                BaseCost = form.Cost,
                BaseAttack = form.Attack,
                BaseLife = form.Life,
                // 常驻关键词允许在只读表单里改（它属于头部属性，不是复杂能力）。
                // 只认表单里非空的情况，空则沿用原值，避免清空玩家没动过的那一栏。
                InnateKeywords = (form.Keywords != null && form.Keywords.Count > 0)
                    ? ParseKeywordsOrKeep(form.Keywords, CurrentDef.InnateKeywords)
                    : CurrentDef.InnateKeywords,
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
        CardDef parsedCrest = null, parsedFaith = null, parsedAccel = null, parsedCryst = null;
        try
        {
            // 必须走带子卡段的版本：手写 crest / faith 段时 ParseAbilities 会抛
            // 未捕获的 SubDefException 直接崩掉（174 号文档 五.3.1）。
            var parsed = parser.ParseAbilitiesWithSubDefs(dsl);
            abilities = parsed.Abilities;
            parsedCrest = parsed.Crest;
            parsedFaith = parsed.Faith;
            parsedAccel = parsed.Accel;
            parsedCryst = parsed.Cryst;
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
            ArtCardId = head.ArtCardId,
            PackId = head.PackId,
            Rarity = head.Rarity,
            IsToken = head.IsToken,
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
            // 允许删除子卡，直接使用解析结果；元属性完整保留（178 号文档 5.1 & 二.1）
            CrestDef = parsedCrest,
            FaithDef = parsedFaith,
            AccelerateDef = parsedAccel,
            CrystallizeDef = parsedCryst,
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

        // ④ 端支持内联子段标记：[Crest] / [Faith] / [Accelerate (N)] / [Crystallize (N)]。
        // 用户在同一个文本框里写完本体再换行写子段即可（174 号文档 五.3.2 / 3.3）。
        var (bodyText, inlineAlts) = SplitInlineAltModes(spec.EngText);
        if (inlineAlts.Count > 0)
        {
            spec = new DiyWorkspace.CnlCardSpec
            {
                CardId = spec.CardId, Name = spec.Name, NameChs = spec.NameChs,
                Kind = spec.Kind, Cost = spec.Cost, Attack = spec.Attack, Life = spec.Life,
                Clan = spec.Clan, Traits = spec.Traits,
                EngText = bodyText, AltModes = inlineAlts,
            };
        }
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

    /// <summary>
    /// 把英文文本里内联的子段标记切出来：
    ///   [Crest] ... / [Faith] ... / [Accelerate (N)] ... / [Crystallize (N)] ...
    /// 返回本体文本与 alt_modes 列表。没有标记时原样返回。
    /// </summary>
    private static (string Body, List<(string typeEng, int cost, string textEng)> Alts)
        SplitInlineAltModes(string text)
    {
        var alts = new List<(string, int, string)>();
        if (string.IsNullOrEmpty(text)) return ("", alts);

        var body = new List<string>();
        var cur = new List<string>();
        string curTag = null;
        int curCost = 0;

        void Flush()
        {
            if (curTag == null) return;
            alts.Add((curTag, curCost, string.Join("\n", cur).Trim()));
            curTag = null; curCost = 0; cur.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string t = line.Trim();
            if (t.Length > 2 && t[0] == '[' && t[^1] == ']')
            {
                string inner = t.Substring(1, t.Length - 2).Trim();
                int cost = 0;
                string tag = inner;
                int lp = inner.IndexOf('(');
                if (lp > 0 && inner.EndsWith(")"))
                {
                    tag = inner.Substring(0, lp).Trim();
                    int.TryParse(inner.Substring(lp + 1, inner.Length - lp - 2).Trim(), out cost);
                }
                if (tag is "Crest" or "Faith" or "Accelerate" or "Crystallize")
                {
                    Flush();
                    curTag = tag; curCost = cost;
                    continue;
                }
            }
            (curTag != null ? cur : body).Add(line);
        }
        Flush();
        return (string.Join("\n", body).Trim(), alts);
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
        // 必须连子卡段一起打印：只打本体的 Abilities 会让纹章 / 信仰在 ③ 端彻底消失（174 号文档 五.3.1）。
        AstJsonText = AstCanonicalPrinter.PrintBodyAndSubDefs(CurrentDef);

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
                Clan = CurrentDef.Clan.IsValid ? CurrentDef.Clan.Name : "中立",
                Traits = (CurrentDef.Traits ?? Array.Empty<SymbolId<TraitTag>>())
                         .Select(t => t.Name).ToList(),
                Cost = CurrentDef.BaseCost,
                Attack = CurrentDef.BaseAttack,
                Life = CurrentDef.BaseLife,
            };
        }

        // ④ 受限英文端
        var np = new NaturalPrinter(Workspace.Glossary, RenderLang.English);
        // SourceText 只在「原文本来就是英文 CNL」时才是英文。
        // 卡片来自 py_ast 时 SourceText 里存的是 PyAST 代码（"card 950001 …"），
        // 直接拿来当英文端内容会让玩家在 ④ 端看到 PyAST 源码，点编译当场报错
        // （179 号文档 C2）。必须识别并改走 NaturalPrinter 重新打印标准英文。
        string engText = (!string.IsNullOrWhiteSpace(CurrentDef.SourceText)
                          && !LooksLikePyAst(CurrentDef.SourceText))
            ? CurrentDef.SourceText
            : np.PrintCard(CurrentDef.Abilities);

        // 子卡段内联进英文文本：只写本体的话，④ 端看不到也写不了纹章 / 信仰（174 号文档 五.3.2）。
        {
            var sb = new StringBuilder(engText);
            void AppendAlt(string tag, CardDef sub, bool withCost)
            {
                if (sub == null) return;
                string subTxt = (!string.IsNullOrWhiteSpace(sub.SourceText)
                                 && !LooksLikePyAst(sub.SourceText))
                    ? sub.SourceText : np.PrintCard(sub.Abilities);
                sb.Append("\n\n[").Append(withCost ? $"{tag} ({sub.BaseCost})" : tag)
                  .Append("]\n").Append(subTxt);
            }
            AppendAlt("Crest", CurrentDef.CrestDef, false);
            AppendAlt("Faith", CurrentDef.FaithDef, false);
            AppendAlt("Accelerate", CurrentDef.AccelerateDef, true);
            AppendAlt("Crystallize", CurrentDef.CrystallizeDef, true);
            engText = sb.ToString();
        }

        CnlSpec = new DiyWorkspace.CnlCardSpec
        {
            CardId = CurrentDef.CardId,
            Name = CurrentDef.Name,
            NameChs = DeckEditorViewModel.DisplayName(CurrentDef),
            Kind = CurrentDef.Kind,
            Cost = CurrentDef.BaseCost,
            Attack = CurrentDef.BaseAttack,
            Life = CurrentDef.BaseLife,
            Clan = CurrentDef.Clan.IsValid ? CurrentDef.Clan.Name : "中立",
            Traits = (CurrentDef.Traits ?? Array.Empty<SymbolId<TraitTag>>())
                     .Select(t => t.Name).ToList(),
            EngText = engText,
        };
    }

    public string GetChineseCardText()
        => CurrentDef == null ? "" : Workspace.PrintChinese(CurrentDef);

    /// <summary>
    /// 左侧「AST 规范形」预览。
    ///
    /// 必须走 PrintBodyAndSubDefs：只打本体 Abilities 的话，
    /// 左下角预览看不到关联纹章 / 信仰 / 激奏 / 结晶，玩家以为子卡丢了
    /// （179 号文档 C2）。
    /// </summary>
    public string GetCanonicalAstText()
        => CurrentDef == null ? "" : AstCanonicalPrinter.PrintBodyAndSubDefs(CurrentDef);

    /// <summary>
    /// 这段文本像不像 PyAST 源码（而不是英文 CNL）。
    /// 卡片经 py_ast 端编辑保存后，SourceText 会被写成 PyAST 文本。
    /// </summary>
    private static bool LooksLikePyAst(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.TrimStart();
        return t.StartsWith("card ", StringComparison.Ordinal)
            || t.StartsWith("fanfare:", StringComparison.Ordinal)
            || t.StartsWith("aura:", StringComparison.Ordinal)
            || t.StartsWith("last_words:", StringComparison.Ordinal)
            || t.Contains("\n    ", StringComparison.Ordinal);
    }

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

    // ---------------------------------------------------------------- 自定义词条
    //
    // 三者都只取**一个名字**：名字本身就是标识符，也是显示名。
    // PyAST 词法器原生支持中文标识符，不需要先起个英文 ID 再配中文名。
    //
    // 注册必须落盘到当前卡池：只写内存的话，下一次 Reload 重建 Catalog 就没了，
    // 玩家看到刚加的职业/兵种/计数器凭空消失。

    public void DefineCustomTrait(string name, int min, int max, bool persist = true)
    {
        string n = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(n)) { LastStatusMessage = "名称不能为空"; return; }
        Workspace.DefineTrait(n, n);
        Store.Catalog.Schema.Trait(n);
        Store.Catalog.Lexicon.AddTrait(n);
        if (persist) PersistPackEntry("traits", n, min, max);
        LastStatusMessage = "已注册新兵种：" + n;
        OnCardUpdated?.Invoke();
    }

    public void DefineCustomCounter(string name, int min, int max, bool persist = true)
    {
        string n = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(n)) { LastStatusMessage = "名称不能为空"; return; }
        Workspace.DefineCounter(n, n, min, max);
        Store.Catalog.Schema.Counter(n);
        Store.Catalog.Schema.CounterLimits[n] = (min, max);
        if (persist) PersistPackEntry("counters", n, min, max);
        LastStatusMessage = "已注册新计数器：" + n + "（范围 " + min + ".." + max + "）";
        OnCardUpdated?.Invoke();
    }

    public void DefineCustomClan(string name, bool persist = true)
    {
        string n = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(n)) { LastStatusMessage = "名称不能为空"; return; }
        Workspace.DefineClan(n, n);
        Store.Catalog.Schema.Clan(n);
        Store.Catalog.Lexicon.AddClan(n);
        if (persist) PersistPoolClan(n);
        LastStatusMessage = "已注册新职业：" + n;
        OnCardUpdated?.Invoke();
    }

    /// <summary>把自定义兵种 / 计数器写进当前卡池的第一个启用卡包并落盘。</summary>
    private void PersistPackEntry(string kind, string name, int min, int max)
    {
        try
        {
            if (!Store.PoolConfigs.TryGetValue(ActivePoolId ?? "", out var cfg))
                cfg = Store.PoolConfigs.Values.FirstOrDefault();
            // 必须避开 official-sv：几乎每个卡池的 EnabledPackIds[0] 都是它，
            // 直接取第一个会把自定义兵种/计数器写进官方核心包。
            string packId = cfg?.EnabledPackIds?
                .FirstOrDefault(x => !string.Equals(x, "official-sv", StringComparison.OrdinalIgnoreCase))
                ?? "testset-v2";
            Store.AddPackSymbolEntry(packId, kind, name, min, max, out _);
        }
        catch (Exception) { }
    }

    /// <summary>把自定义职业写进当前卡池的 pool_clans 并落盘。</summary>
    private void PersistPoolClan(string name)
    {
        try
        {
            // 找当前卡池；ActivePoolId 可能是个还没落盘的默认值（"standard"），
            // 那就退到第一个真实存在的卡池，总比静默丢弃强。
            if (!Store.PoolConfigs.TryGetValue(ActivePoolId ?? "", out var cfg))
            {
                cfg = Store.PoolConfigs.Values.FirstOrDefault();
                if (cfg == null) return;
            }
            var clans = new System.Collections.Generic.List<string>(cfg.PoolClans ?? new System.Collections.Generic.List<string>());
            if (clans.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            clans.Add(name);
            Store.SavePoolConfigFull(cfg.PoolId, cfg.Name, cfg.EnabledPackIds, cfg.Rules,
                                     cfg.NamedDecks, out _, clans, cfg.LeaderCounters);
        }
        catch (Exception) { }
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

    /// <summary>解析卡种；表单没给或给错时保留原值。</summary>
    private static ShadowverseFan.KernelV3.Ast.CardKind ParseKindOrKeep(
        string kind, ShadowverseFan.KernelV3.Ast.CardKind fallback)
    {
        if (string.IsNullOrWhiteSpace(kind)) return fallback;
        return Enum.TryParse<ShadowverseFan.KernelV3.Ast.CardKind>(kind.Trim(), ignoreCase: true, out var k)
            ? k : fallback;
    }

    private SymbolId<ClanTag> ParseClanOrKeep(string clanName, SymbolId<ClanTag> keep)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return keep;
        if (string.Equals(clanName, "中立", StringComparison.OrdinalIgnoreCase))
            return SymbolId<ClanTag>.Invalid;
        return Workspace.Schema.Clan(clanName);
    }

    /// <summary>
    /// 把表单里的关键词名字解析成 SymbolId；空则沿用原值。
    /// 只读表单更新常驻关键词用（179 号文档 D5）。
    /// </summary>
    private IReadOnlyList<SymbolId<KeywordTag>> ParseKeywordsOrKeep(
        List<string> names, IReadOnlyList<SymbolId<KeywordTag>> keep)
    {
        if (names == null || names.Count == 0) return keep;
        var list = new List<SymbolId<KeywordTag>>();
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var k = Store.Catalog.Schema.Keyword(n.Trim());
            if (k.IsValid && !list.Contains(k)) list.Add(k);
        }
        return list.Count > 0 ? list : keep;
    }

    private IReadOnlyList<SymbolId<TraitTag>> ParseTraitsOrKeep(
        List<string> names, IReadOnlyList<SymbolId<TraitTag>> keep)
    {
        if (names == null || names.Count == 0) return keep;
        return names.Select(n => Workspace.Schema.Trait(n)).ToList();
    }

    private CardDef PreserveChineseName(CardDef d, string nameChs)
    {
        if (d == null) return null;
        var prev = CurrentDef;
        return new CardDef
        {
            CardId = d.CardId > 0 ? d.CardId : (prev?.CardId ?? 0),
            ArtCardId = d.ArtCardId > 0 ? d.ArtCardId : (prev?.ArtCardId ?? 0),
            PackId = !string.IsNullOrEmpty(d.PackId) ? d.PackId : (prev?.PackId ?? ""),
            Rarity = d.Rarity > 0 ? d.Rarity : (prev?.Rarity ?? 0),
            IsToken = d.IsToken || (prev?.IsToken ?? false),
            Name = d.Name,
            NameChs = !string.IsNullOrWhiteSpace(nameChs) ? nameChs : (d.NameChs ?? prev?.NameChs),
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

    /// <summary>最近插入的积木。界面据此把选中推进过去，连续点击就能一路往下搭。</summary>
    public Block LastInsertedBlock { get; private set; }

    /// <summary>插入目标分支：null=主 Stacks，"else"=ElseStacks，"branch"=Branches[i]。</summary>
    public string ActiveBranch { get; set; }

    /// <summary>ActiveBranch == "branch" 时的分支下标。</summary>
    public int ActiveBranchIndex { get; set; } = -1;

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
    /// <summary>
    /// 插入一颗新积木。
    ///
    /// 插入位置按**积木形状 + 当前选中**自动决定，玩家不需要先点「设为插入点」：
    ///   · 事件帽      -> 永远追加到能力域根列表（帽子只能挂根上）
    ///   · 构筑修正    -> 永远进 DeckModBlocks
    ///   · 动作 / 控制流 -> 插在当前选中积木的同级正下方（连续点击就能一路往下搭）
    ///   · 没有选中    -> 落到本域最后一顶帽子的 Stacks（没帽子就自动补一顶）
    /// </summary>
    public bool InsertBlock(string opcode, int scope = 0, Block selectedBlock = null)
    {
        if (BlockModel == null) return false;
        var blk = DiyBlockTree.CreateDefaultBlock(opcode);
        if (blk == null) return false;

        var root = RootListOf(scope);
        if (root == null) return false;

        List<Block> targetList = null;
        int insertIdx = -1;

        // 规则 1：事件帽永远挂在能力域根列表。
        if (opcode.StartsWith("hat_"))
        {
            targetList = root;
            insertIdx = root.Count;
        }
        // 规则 2：构筑修正永远进 DeckModBlocks，混进事件帽会让编译报「不是事件帽」。
        else if (opcode == "act_deck_mod")
        {
            BlockModel.DeckModBlocks ??= new List<Block>();
            targetList = BlockModel.DeckModBlocks;
            insertIdx = targetList.Count;
        }
        // 规则 3b：选中的是控制流块 -> 落到它的分支槽。
        else if (selectedBlock != null && (selectedBlock.Opcode ?? "").StartsWith("c_"))
        {
            switch (ActiveBranch)
            {
                case "else":
                    targetList = selectedBlock.ElseStacks ??= new List<Block>();
                    break;
                case "branch" when ActiveBranchIndex >= 0:
                    selectedBlock.Branches ??= new List<List<Block>>();
                    while (selectedBlock.Branches.Count <= ActiveBranchIndex)
                        selectedBlock.Branches.Add(new List<Block>());
                    targetList = selectedBlock.Branches[ActiveBranchIndex];
                    break;
                default:
                    targetList = selectedBlock.Stacks ??= new List<Block>();
                    break;
            }
            insertIdx = targetList.Count;
        }
        // 规则 3：选中了某颗非帽积木 -> 插在它同级正下方。
        else if (selectedBlock != null && !(selectedBlock.Opcode ?? "").StartsWith("hat_"))
        {
            var parent = DiyBlockTree.FindParentContainer(BlockModel, selectedBlock);
            if (parent != null)
            {
                targetList = parent;
                int idx = parent.IndexOf(selectedBlock);
                insertIdx = idx >= 0 ? idx + 1 : parent.Count;
            }
        }

        // 规则 4：有明确插入点（且不是域根）。
        // 必须排除域根：Hats 只收事件帽，动作块塞进去编译会报
        // 「不是事件帽积木」，而玩家看到的是「点了没反应」。
        if (targetList == null && ActiveInsertList != null && !ReferenceEquals(ActiveInsertList, root))
        {
            targetList = ActiveInsertList;
            insertIdx = targetList.Count;
        }

        // 规则 5：兜底落到本域最后一顶帽子的 Stacks（没帽子就补一顶）。
        if (targetList == null)
        {
            if (root.Count == 0 || !(root[root.Count - 1].Opcode ?? "").StartsWith("hat_"))
                root.Add(DiyBlockTree.CreateDefaultBlock("hat_self_event"));
            var lastHat = root[root.Count - 1];
            targetList = lastHat.Stacks ??= new List<Block>();
            insertIdx = targetList.Count;
        }

        // 统一在插入前记容器路径，重编译后从新树精准取回新节点。
        //
        // 这一步必须在**末尾统一做**：早期只在分支槽那条路上写了路径恢复，
        // 其余四条的 LastInsertedBlock 都指向重编译前旧树上的死节点，
        // UI 的选中绑在死节点上，右侧检视器改任何属性都不生效。
        var slotPath = PathOfListIn(BlockModel, targetList);
        targetList.Insert(insertIdx, blk);
        if (!CompileFromBlocks(BlockModel)) return false;

        LastInsertedBlock = ResolveBlockAt(BlockModel, slotPath, insertIdx);
        ActiveInsertList = LastInsertedBlock != null
            ? ((LastInsertedBlock.Opcode ?? "").StartsWith("hat_")
                ? (LastInsertedBlock.Stacks ??= new List<Block>())
                : DiyBlockTree.FindParentContainer(BlockModel, LastInsertedBlock))
            : null;
        return true;
    }


    /// <summary>把 List&lt;Block&gt; 编码成从域根出发的路径：每步是「第几个块 + 哪个子槽」。</summary>
    private static List<(int Index, string Slot)> PathOfListIn(CardBlockWorkspace ws, List<Block> target)
    {
        if (ws == null || target == null) return null;
        List<(int, string)> found = null;

        void WalkBlock(Block b, List<(int, string)> prefix)
        {
            if (found != null || b == null) return;
            void Try(List<Block> lst, string slot)
            {
                if (found != null || lst == null) return;
                if (ReferenceEquals(lst, target)) { found = new List<(int, string)>(prefix) { (-1, slot) }; return; }
                for (int i = 0; i < lst.Count; i++)
                {
                    var child = lst[i];
                    if (child == null) continue;
                    var next = new List<(int, string)>(prefix) { (i, slot) };
                    WalkBlock(child, next);
                }
            }
            Try(b.Stacks, "stacks");
            Try(b.ElseStacks, "else");
            if (b.Branches != null)
                for (int bi = 0; bi < b.Branches.Count; bi++) Try(b.Branches[bi], "branch:" + bi);
        }

        foreach (var (lst, slot) in new[]{
            (ws.Hats, "hats"), (ws.CrestHats, "crests"), (ws.FaithHats, "faiths"),
            (ws.AccelerateHats, "accel"), (ws.CrystallizeHats, "cryst"), (ws.DeckModBlocks, "deckmod") })
        {
            if (found != null) break;
            if (lst == null) continue;
            if (ReferenceEquals(lst, target)) { return new List<(int, string)> { (-1, slot) }; }
            for (int i = 0; i < lst.Count; i++)
            {
                var child = lst[i];
                if (child == null) continue;
                WalkBlock(child, new List<(int, string)> { (i, slot) });
            }
        }
        return found;
    }

    /// <summary>按路径在新树上找回某个列表里的第 index 个块。</summary>
    private static Block ResolveBlockAt(CardBlockWorkspace ws, List<(int Index, string Slot)> path, int index)
    {
        if (ws == null || path == null || path.Count == 0 || index < 0) return null;
        List<Block> cur = path[0].Slot switch
        {
            "hats" => ws.Hats,
            "crests" => ws.CrestHats,
            "faiths" => ws.FaithHats,
            "accel" => ws.AccelerateHats,
            "cryst" => ws.CrystallizeHats,
            "deckmod" => ws.DeckModBlocks,
            _ => null,
        };
        if (cur == null) return null;

        // 单层路径 = 直接插在域根（事件帽 / 构筑修正），
        // 此时 path[0].Index 是 -1（根槽没有「第几个块」），
        // 进下面的循环会在 bi < 0 处直接返回 null，选中焦点随之丢失。
        if (path.Count == 1)
            return index < cur.Count ? cur[index] : null;

        // path[0].Slot 是域根槽；后续元素依次是 (块下标, 该块内的子槽)
        for (int i = 0; i < path.Count; i++)
        {
            if (cur == null) return null;
            int bi = path[i].Index;
            if (i == 0)
            {
                if (bi < 0 || bi >= cur.Count) return null;
                var blk0 = cur[bi];
                cur = SubListOf(blk0, path.Count > 1 ? path[1].Slot : null);
                continue;
            }
            var slotName = path[i].Slot;
            if (i + 1 >= path.Count)
            {
                // 最后一层：bi 是 index 之前的位置，直接取 index
                return index < cur.Count ? cur[index] : null;
            }
            if (bi < 0 || bi >= cur.Count) return null;
            cur = SubListOf(cur[bi], path[i + 1].Slot);
        }
        return index < cur.Count ? cur[index] : null;
    }

    private static List<Block> SubListOf(Block b, string slot)
    {
        if (b == null || slot == null) return null;
        if (slot == "stacks") return b.Stacks ??= new List<Block>();
        if (slot == "else") return b.ElseStacks ??= new List<Block>();
        if (slot.StartsWith("branch:"))
        {
            int bi = int.Parse(slot.Substring(7));
            b.Branches ??= new List<List<Block>>();
            while (b.Branches.Count <= bi) b.Branches.Add(new List<Block>());
            return b.Branches[bi];
        }
        return null;
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
