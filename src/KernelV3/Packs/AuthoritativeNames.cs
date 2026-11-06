using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 权威卡名表（英文 / 简中各一份，按卡号索引）
//
// 数据源：assets/cards.json（svwbmeta 官方导出，982 条，含五种语言）。
//
// 为什么需要它：
//   卡包是编译产物，卡包里只留本地化后的显示名（中文）。
//   但**英文名不能丢** —— 英文编译器靠词法表把
//   "Add a Fairy to your hand" 里的 Fairy 认成一张卡，
//   表里没有英文名，这半句会直接被判「无法解析宾语」而整条效果消失。
//   产物时代没地方存英文名了，就从这份权威表按卡号补回来。
//
// 懒加载：只在真正用到时读一次，读不到就返回空表（不影响卡包加载）。
// ============================================================================

public static class AuthoritativeNames
{
    private static Dictionary<int, string> _en;
    private static Dictionary<int, string> _zh;
    private static readonly object _lock = new();

    /// <summary>卡号 -> 英文名。</summary>
    public static IReadOnlyDictionary<int, string> En { get { Ensure(); return _en; } }

    /// <summary>卡号 -> 简中名。</summary>
    public static IReadOnlyDictionary<int, string> Zh { get { Ensure(); return _zh; } }

    private static readonly Dictionary<string, int> _enToId = new(StringComparer.OrdinalIgnoreCase);

    private static void Ensure()
    {
        if (_en != null) return;
        lock (_lock)
        {
            if (_en != null) return;
            var en = new Dictionary<int, string>();
            var zh = new Dictionary<int, string>();
            try
            {
                string path = Resolve();
                if (path != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (!int.TryParse(p.Name, out int cid)) continue;
                        if (!p.Value.TryGetProperty("names", out var ns)) continue;
                        if (ns.TryGetProperty("en", out var e) && e.ValueKind == JsonValueKind.String)
                        {
                            var v = e.GetString();
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                en[cid] = v;
                                _enToId[v.Trim()] = cid;
                            }
                        }
                        string z = null;
                        if (ns.TryGetProperty("zh-Hans", out var h1) && h1.ValueKind == JsonValueKind.String) z = h1.GetString();
                        if (string.IsNullOrEmpty(z) && ns.TryGetProperty("zh-Hant", out var h2) && h2.ValueKind == JsonValueKind.String) z = h2.GetString();
                        if (!string.IsNullOrWhiteSpace(z)) zh[cid] = z;
                    }
                }
            }
            catch (Exception)
            {
                // 文件缺失或损坏不该让卡包加载失败：退化成「没有英文名」。
            }
            _en = en;
            _zh = zh;
        }
    }

    /// <summary>按卡号取英文名；没有返回 null。</summary>
    /// <summary>英文卡名 -> 卡号；查不到返回 0。</summary>
    public static int IdOfEnglish(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return 0;
        Ensure();
        return _enToId.TryGetValue(en.Trim(), out int id) ? id : 0;
    }

    /// <summary>卡号 -> 中文名；查不到返回空串。</summary>
    public static string ChineseOf(int cardId)
    {
        if (cardId <= 0) return "";
        try { return Zh.TryGetValue(cardId, out var zh) ? (zh ?? "") : ""; }
        catch (Exception) { return ""; }
    }

    public static string EnglishOf(int cardId)
        => En.TryGetValue(cardId, out var v) ? v : null;

    private static string Resolve(string startDir = null)
    {
        var dir = startDir ?? AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var rel in new[] { Path.Combine("assets", "cards.json"), Path.Combine("game", "assets", "cards.json") })
            {
                var p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
