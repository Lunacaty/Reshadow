using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 术语表（Glossary）
//
// 数据源：assets/kernel/glossary.json —— **唯一的术语真相**。
//   前 68 条来自 svwbmeta 官方导出（keywords-5fc4835107b3.json），
//   其余是引擎内部关键词内码（cannot_be_played / ignores ward 这类），
//   官方数据里不存在，由本仓补充。
//
// 代码里不再硬编码任何中英对照：改词条只改 json，不重新编译。
// 查不到时回落到原文而不是抛错 —— 渲染是给人看的，不是校验。
// ============================================================================

public sealed class Glossary
{
    private readonly Dictionary<string, string> _enToZh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _enToEn = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _enToZh.Count;

    /// <summary>登记一条术语（英文 -> 中文）。</summary>
    public void Add(string en, string zh)
    {
        if (string.IsNullOrWhiteSpace(en)) return;
        _enToZh[en.Trim()] = (zh ?? "").Trim();
        _enToEn[en.Trim()] = en.Trim();
    }

    /// <summary>英文术语 -> 中文；查不到时原样返回。</summary>
    public string ToChinese(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";
        return _enToZh.TryGetValue(en.Trim(), out var zh) && zh.Length > 0 ? zh : en.Trim();
    }

    /// <summary>规范英文写法（大小写归一）。</summary>
    public string ToEnglish(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";
        return _enToEn.TryGetValue(en.Trim(), out var v) ? v : en.Trim();
    }

    public bool Has(string en) => !string.IsNullOrWhiteSpace(en) && _enToZh.ContainsKey(en.Trim());

    /// <summary>
    /// 载入内置术语表。
    ///
    /// 找不到文件时返回空表（渲染回落到原文），不抛错 ——
    /// 渲染是给人看的，缺词条不该让整个界面打不开。
    /// </summary>
    public static Glossary CreateDefault()
    {
        var g = new Glossary();
        foreach (var kv in LoadTerms()) g.Add(kv.Key, kv.Value);
        return g;
    }

    private static Dictionary<string, string> _cache;
    private static readonly object _lock = new();

    private static Dictionary<string, string> LoadTerms()
    {
        if (_cache != null) return _cache;
        lock (_lock)
        {
            if (_cache != null) return _cache;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Resolve();
                if (path != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("terms", out var t) && t.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in t.EnumerateObject())
                        {
                            if (string.IsNullOrWhiteSpace(p.Name)) continue;
                            if (p.Value.ValueKind != JsonValueKind.String) continue;
                            map[p.Name.Trim()] = (p.Value.GetString() ?? "").Trim();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 文件缺失或损坏不该让界面打不开：退化成「没有术语表」。
            }
            _cache = map;
            return map;
        }
    }

    private static string Resolve(string startDir = null)
    {
        var dir = startDir ?? AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var rel in new[] { Path.Combine("assets", "kernel", "glossary.json"),
                                        Path.Combine("game", "assets", "kernel", "glossary.json") })
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
