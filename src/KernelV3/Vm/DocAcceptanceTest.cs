using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// 按 76-a / 76-b / 76-c / 76-d 四份文档逐条验收的对抗测试。
/// 每个测试都标注文档条号，可以直接对回原文。
/// </summary>
public static partial class DocAcceptanceTest
{
    private static int _pass, _fail;
    private static readonly List<string> _log = new();

    public static string Run()
    {
        _pass = _fail = 0;
        _log.Clear();

        // ---- 76-a ----
        RunDocA();

        // ---- 76-b ----
        RunDocB();

        // ---- 76-c ----
        RunDocC();

        // ---- 设计3 ----
        RunDesign3();

        // ---- 76-d ----
        RunDocD();

        _log.Add("");
        _log.Add(_fail == 0
            ? $"文档验收 {_pass}/{_pass + _fail} 全部通过"
            : $"文档验收 {_pass}/{_pass + _fail}，{_fail} 项失败");
        return string.Join("\n", _log);
    }

    private static void Ok(string doc, string item, bool pass, string detail = "")
        => Check($"[{doc}] {item}", pass, detail);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            _log.Add($"  [OK]   {name}");
        }
        else
        {
            _fail++;
            _log.Add($"  [FAIL] {name}" + (detail.Length > 0 ? $"  —— {detail}" : ""));
        }
    }

    /// <summary>
    /// 把能力树完整展开成文本，包括 ConditionalNode 的分支正文。
    /// PrintAbility 只打印顶层节点，看不到 Then / Else 里面的东西。
    /// </summary>
    private static string DumpDeep(IEnumerable<AbilityDef> abilities)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in abilities)
        {
            sb.Append('[').Append(a.Trigger?.Event).Append(']');
            foreach (var n in a.Body ?? Array.Empty<EffectNode>()) DumpNode(n, sb);
        }
        return sb.ToString();
    }

    private static void DumpNode(EffectNode n, System.Text.StringBuilder sb)
    {
        if (n is ConditionalNode c)
        {
            sb.Append(" IF ").Append(AstEnglishPrinter.PrintBool(c.Condition));
            foreach (var t in c.Then) DumpNode(t, sb);
            if (c.Else != null) foreach (var e in c.Else) DumpNode(e, sb);
            return;
        }
        sb.Append(' ').Append(AstEnglishPrinter.PrintNode(n));
    }

    private static Lexicon BuildFullLexicon()
    {
        var lex = new Lexicon();
        // 测试运行目录是 .et/，不一定能找到卡池文件，所以逐级往上找。
        string path = null;
        var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "_corpus_cards.json");
            if (System.IO.File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        if (path != null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var c in doc.RootElement.EnumerateArray())
                lex.AddCard(c.GetProperty("name").GetString() ?? "", c.GetProperty("id").GetInt32());
        }
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        foreach (var tr in new[] { "officer", "commander", "artifact", "puppetry", "shikigami", "golem",
                                   "pixie", "merman", "departed", "clue", "token", "marine", "loot", "encroacher" })
            lex.AddTrait(tr);
        foreach (var cl in new[] { "forestcraft", "swordcraft", "runecraft", "dragoncraft", "shadowcraft",
                                   "bloodcraft", "havencraft", "portalcraft", "neutral", "abysscraft" })
            lex.AddClan(cl);
        lex.Build();
        return lex;
    }
}
