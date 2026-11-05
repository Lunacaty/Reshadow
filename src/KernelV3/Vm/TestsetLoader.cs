using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// 把 <c>data/cards/testset_cards.json</c> 里的 30 张卡**原封不动**加载进引擎。
///
/// 为什么必须有这个类：测试若各自手抄卡面文本，抄的人会不自觉地
/// 「删掉跑不通的那一段」—— 最后测的是他自己写的简化版，不是测试集。
/// 加载器把「读文件」这件事收成一个入口，谁都没有机会改写卡面。
///
/// 加载内容一应俱全：card_id / name_eng / type / cost / atk / life /
/// eng_text（含全部 hr / ev / sev 分段）/ alt_modes / tribe_name_eng / class。
/// </summary>
public static class TestsetLoader
{
    /// <summary>一张测试集卡在引擎里的完整定义。</summary>
    public sealed class LoadedCard
    {
        public int CardId { get; init; }
        public string Name { get; init; }
        public CardKind Kind { get; init; }
        public int Cost { get; init; }
        public int Attack { get; init; }
        public int Life { get; init; }
        public string Text { get; init; }
        public CardDef Def { get; init; }
        public IReadOnlyList<string> AltModeTypes { get; init; }
    }

    /// <summary>找 data/cards/testset_cards.json（从当前目录向上找仓库根）。</summary>
    public static string ResolvePath(string startDir = null)
    {
        var dir = startDir ?? AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var p = Path.Combine(dir, "data", "cards", "testset_cards.json");
            if (File.Exists(p)) return p;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException(
            "找不到 data/cards/testset_cards.json。测试集加载器必须读真实文件，不允许手抄卡面。");
    }

    /// <summary>
    /// 把 30 张卡全部编译并注册进 <paramref name="ctx"/> 的 BattleState，
    /// 同时把卡名登记进词法层（供「add a X to your hand」之类引用解析）。
    /// </summary>
    public static List<LoadedCard> LoadInto(CompileContext ctx, bool verbose = false)
    {
        var path = ResolvePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var cards = doc.RootElement.GetProperty("cards");

        // 第一遍：把全部卡名登记进词法层。
        // 必须在编译**之前**做完 —— 「Add a Gear of Ambition to your hand」
        // 引用的是另一张卡的卡名，那张卡此刻可能还没编译。
        var raw = new List<(int id, string name, string text, int type, int cost, int atk, int life,
                            string tribe, string clan, List<(string t, int c, string x)> alt)>();
        foreach (var c in cards.EnumerateArray())
        {
            var alt = new List<(string, int, string)>();
            if (c.TryGetProperty("alt_modes", out var am) && am.ValueKind == JsonValueKind.Array)
                foreach (var m in am.EnumerateArray())
                    alt.Add((m.GetProperty("type_eng").GetString(),
                             m.GetProperty("cost").GetInt32(),
                             m.GetProperty("text_eng").GetString()));

            raw.Add((
                c.GetProperty("card_id").GetInt32(),
                c.GetProperty("name_eng").GetString(),
                c.GetProperty("eng_text").GetString(),
                c.GetProperty("type").GetInt32(),
                c.GetProperty("cost").GetInt32(),
                c.TryGetProperty("atk", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0,
                c.TryGetProperty("life", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0,
                c.TryGetProperty("tribe_name_eng", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString() : null,
                c.TryGetProperty("class", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() : null,
                alt));
        }

        foreach (var r in raw)
        {
            ctx.Lexicon.AddCard(r.name, r.id);
            // 兵种与职业也要登记，词法层才认得出「Artifact」「Pixie」这些词。
            if (!string.IsNullOrEmpty(r.tribe)) ctx.Lexicon.AddTrait(r.tribe);
            if (!string.IsNullOrEmpty(r.clan)) ctx.Lexicon.AddClan(r.clan);
        }

        // 测试集正文会引用一批**不在 30 张里**的衍生卡：
        // 「Add a Fairy to your hand」「Summon 3 copies of Fairy」
        // 「transform ... into Ominous Artifact α」「Add a Remnant of Hollowness」。
        // 它们必须一并登记进词法层，否则那些句子编译期就认不出宾语。
        foreach (var t in BattleState.BuiltinTokenDefs)
            ctx.Lexicon.AddCard(t.name, t.id);

        ctx.Lexicon.Build();

        var comp = new EnglishCompiler(ctx.Lexicon);
        var st = ctx.State;
        // 传 Lexicon 进去：有卡面文本的衍生卡（Remnant of Hollowness 等）
        // 需要当场编译出能力，否则它们是白板。
        st.RegisterBuiltinTokens(ctx.Lexicon);

        var result = new List<LoadedCard>();
        foreach (var r in raw)
        {
            var kind = r.type switch
            {
                1 => CardKind.Follower,
                2 => CardKind.Amulet,
                3 => CardKind.Spell,
                4 => CardKind.Crest,
                _ => CardKind.Follower,
            };

            var res = comp.Compile(r.text, r.alt.ToArray(), r.name, r.id);

            var traits = new List<SymbolId<TraitTag>>();
            if (!string.IsNullOrEmpty(r.tribe)) traits.Add(ctx.Schema.Trait(r.tribe));
            // Clan 是值类型，未设置用 Invalid 而不是 null。
            var clan = string.IsNullOrEmpty(r.clan)
                ? SymbolId<ClanTag>.Invalid
                : ctx.Schema.Clan(r.clan);

            var def = new CardDef
            {
                CardId = r.id,
                Name = r.name,
                Kind = kind,
                BaseCost = r.cost,
                BaseAttack = r.atk,
                BaseLife = r.life,
                Abilities = res.Abilities,
                // 卡面裸关键词是卡牌定义属性，编译期收集在 Result 上。
                // 不传会让测试集的 Ghost / Slaus / Yuna 等卡失去疾驰/潜行/守护。
                InnateKeywords = res.InnateKeywords,
                Traits = traits,
                Clan = clan,
                HasFuse = res.HasFuse,
                FuseMaterialFilter = res.FuseMaterialFilter,
                CrestDef = res.CrestDef,
                FaithDef = res.FaithDef,
                AccelerateDef = res.AccelerateDef,
                CrystallizeDef = res.CrystallizeDef,
                SourceText = r.text,
            };
            st.RegisterCard(def);

            if (res.Errors.Count > 0 && verbose)
                foreach (var e in res.Errors) Console.WriteLine("[TestsetLoader] " + r.name + " : " + e);

            result.Add(new LoadedCard
            {
                CardId = r.id,
                Name = r.name,
                Kind = kind,
                Cost = r.cost,
                Attack = r.atk,
                Life = r.life,
                Text = r.text,
                Def = def,
                AltModeTypes = r.alt.Select(x => x.t).ToList(),
            });
        }
        return result;
    }

    /// <summary>只取一张卡的原始文本（测试要断言「跑的是真卡面」时用）。</summary>
    public static string TextOf(int cardId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ResolvePath(), System.Text.Encoding.UTF8));
        foreach (var c in doc.RootElement.GetProperty("cards").EnumerateArray())
            if (c.GetProperty("card_id").GetInt32() == cardId)
                return c.GetProperty("eng_text").GetString();
        return null;
    }
}
