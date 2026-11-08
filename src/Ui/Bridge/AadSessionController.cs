using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowverseFan.Ui.Bridge;

// ============================================================================
// AAD（AI 辅助 DIY）会话控制器 —— 纯 C#，零 Godot 依赖。
//
// 职责：
//   · 持有 LLM 连接配置（OpenAI 兼容，兼容 DeepSeek / Ollama / 各类转发）
//   · 持有可编辑的上下文时间线（system / user / assistant 任意增删改排序）
//   · 管理提示词资产库（语法圣经、few-shot 范例）
//   · 发起异步请求并从回复里提取 PyAST 代码块
//
// 落盘布局（可写目录，与卡池数据分开）：
//   aad/llm_config.json      连接配置
//   aad/prompts/*.md         提示词资产
//   aad/sessions/*.json      命名上下文
// ============================================================================

public sealed class LlmConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public float Temperature { get; set; } = 0.2f;
}

public sealed class AadChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    /// <summary>"system" | "user" | "assistant"</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    /// <summary>允许临时勾选 / 屏蔽某条上下文，不必删除。</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class PromptAsset
{
    public string Name { get; set; } = "默认语法规范";
    public string Content { get; set; } = "";
}

public sealed class AadSessionController
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public LlmConfig Config { get; set; } = new();
    public List<PromptAsset> PromptAssets { get; } = new();
    public List<AadChatMessage> ContextMessages { get; } = new();

    /// <summary>最近一次请求的原始回复（供 UI 显示排查）。</summary>
    public string LastRawReply { get; private set; } = "";

    /// <summary>最近一次错误（空 = 无错）。</summary>
    public string LastError { get; private set; } = "";

    public string AadRoot(string writableRoot)
        => Path.Combine(writableRoot ?? ".", "aad");

    // ---------------------------------------------------------------- 上下文编辑

    public AadChatMessage AddMessage(string role, string content = "")
    {
        var m = new AadChatMessage { Role = role, Content = content };
        ContextMessages.Add(m);
        return m;
    }

    public void RemoveMessage(int index)
    {
        if (index < 0 || index >= ContextMessages.Count) return;
        ContextMessages.RemoveAt(index);
    }

    public void MoveMessage(int index, int dir)
    {
        int dst = index + dir;
        if (index < 0 || index >= ContextMessages.Count) return;
        if (dst < 0 || dst >= ContextMessages.Count) return;
        (ContextMessages[index], ContextMessages[dst]) = (ContextMessages[dst], ContextMessages[index]);
    }

    public void ClearMessages() => ContextMessages.Clear();

    /// <summary>把提示词资产插到最前面作为 system 消息。</summary>
    public AadChatMessage InjectPromptAsset(PromptAsset asset)
        => InsertAt(0, "system", asset?.Content ?? "");

    public AadChatMessage InsertAt(int index, string role, string content)
    {
        var m = new AadChatMessage { Role = role, Content = content };
        index = Math.Clamp(index, 0, ContextMessages.Count);
        ContextMessages.Insert(index, m);
        return m;
    }

    // ---------------------------------------------------------------- 请求

    /// <summary>发起一次对话补全。返回 (是否成功, 原始回复, 提取出的 PyAST, 错误)。</summary>
    public async Task<(bool Ok, string RawReply, string ExtractedPyAst, string Error)>
        SendAsync(CancellationToken ct = default)
    {
        LastError = "";
        try
        {
            var enabled = ContextMessages.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Content)).ToList();
            if (enabled.Count == 0)
            {
                LastError = "上下文为空：至少需要一条启用的消息";
                return (false, "", "", LastError);
            }

            string url = (Config.BaseUrl ?? "").TrimEnd('/') + "/chat/completions";
            var payload = new
            {
                model = Config.Model,
                temperature = Config.Temperature,
                messages = enabled.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(Config.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload),
                                            Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            LastRawReply = json;

            if (!resp.IsSuccessStatusCode)
            {
                LastError = "HTTP " + (int)resp.StatusCode + ": " + Clip(json, 300);
                return (false, json, "", LastError);
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0)
            {
                LastError = "回复里没有 choices：" + Clip(json, 300);
                return (false, json, "", LastError);
            }

            string reply = "";
            var first = choices[0];
            if (first.TryGetProperty("message", out var msgEl)
                && msgEl.TryGetProperty("content", out var contentEl))
                reply = contentEl.GetString() ?? "";

            return (true, reply, ExtractPyAstCode(reply), "");
        }
        catch (OperationCanceledException)
        {
            LastError = "请求超时或被取消";
            return (false, "", "", LastError);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (false, "", "", LastError);
        }
    }

    /// <summary>
    /// 从回复里提取代码块。优先认 pyast / python 标记，其次任意代码块，
    /// 都没有才退回全文 —— 模型偶尔会直接吐裸代码。
    /// </summary>
    public static string ExtractPyAstCode(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "";
        var tagged = Regex.Match(reply, "```(?:pyast|python|ast|text)?[ \\t]*\\r?\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase);
        if (tagged.Success) return tagged.Groups[1].Value.Trim();
        var any = Regex.Match(reply, "```([\\s\\S]*?)```");
        if (any.Success) return any.Groups[1].Value.Trim();
        return reply.Trim();
    }

    private static string Clip(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    // ---------------------------------------------------------------- 落盘

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool SaveConfig(string writableRoot, out string msg)
        => SaveJson(Path.Combine(AadRoot(writableRoot), "llm_config.json"), Config, out msg);

    public bool LoadConfig(string writableRoot, out string msg)
    {
        string p = Path.Combine(AadRoot(writableRoot), "llm_config.json");
        if (!File.Exists(p)) { msg = "尚无配置文件，使用默认值"; return false; }
        try
        {
            var c = JsonSerializer.Deserialize<LlmConfig>(File.ReadAllText(p, Encoding.UTF8));
            if (c != null) Config = c;
            msg = "已加载配置";
            return true;
        }
        catch (Exception ex) { msg = "配置读取失败：" + ex.Message; return false; }
    }

    /// <summary>加载 aad/prompts/*.md 作为提示词资产。</summary>
    public int LoadPromptAssets(string writableRoot, out string msg)
    {
        PromptAssets.Clear();
        string dir = Path.Combine(AadRoot(writableRoot), "prompts");
        if (!Directory.Exists(dir)) { msg = "尚无提示词资产目录"; return 0; }
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.*")
                                       .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                                || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(f => f, StringComparer.Ordinal))
            {
                PromptAssets.Add(new PromptAsset
                {
                    Name = Path.GetFileNameWithoutExtension(f),
                    Content = File.ReadAllText(f, Encoding.UTF8),
                });
            }
            msg = "已加载 " + PromptAssets.Count + " 个提示词资产";
            return PromptAssets.Count;
        }
        catch (Exception ex) { msg = "提示词读取失败：" + ex.Message; return 0; }
    }

    public bool SaveSession(string writableRoot, string name, out string msg)
    {
        string safe = string.Join("_", (name ?? "session").Split(Path.GetInvalidFileNameChars()));
        string p = Path.Combine(AadRoot(writableRoot), "sessions", safe + ".json");
        return SaveJson(p, ContextMessages, out msg);
    }

    public bool LoadSession(string writableRoot, string name, out string msg)
    {
        string safe = string.Join("_", (name ?? "session").Split(Path.GetInvalidFileNameChars()));
        string p = Path.Combine(AadRoot(writableRoot), "sessions", safe + ".json");
        if (!File.Exists(p)) { msg = "会话不存在：" + safe; return false; }
        try
        {
            var list = JsonSerializer.Deserialize<List<AadChatMessage>>(File.ReadAllText(p, Encoding.UTF8));
            ContextMessages.Clear();
            if (list != null) ContextMessages.AddRange(list);
            msg = "已加载会话（" + ContextMessages.Count + " 条）";
            return true;
        }
        catch (Exception ex) { msg = "会话读取失败：" + ex.Message; return false; }
    }

    private static bool SaveJson<T>(string path, T value, out string msg)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts), new UTF8Encoding(false));
            msg = "已保存：" + path;
            return true;
        }
        catch (Exception ex) { msg = "保存失败：" + ex.Message; return false; }
    }

    /// <summary>
    /// 内置默认提示词：把 PyAST 语法要点压成一段可直接发送的 system 内容。
    /// 玩家第一次打开 AAD 时点一下就能用，不必自己写。
    /// </summary>
    public static string DefaultSystemPrompt() => string.Join("\n", new[]
    {
        "你是《影之诗：超凡世界》同人引擎 Reshadow 的造卡助手。",
        "只输出 PyAST 效果块代码，用 pyast 代码块包裹，不要解释。",
        "",
        "PyAST 规则要点：",
        "1. 用 4 空格缩进表达层级，不要写大括号。",
        "2. 触发头：fanfare: / last_words: / evolve: / super_evolve: / strike: / clash:",
        "   turn_start: / turn_end: / aura: / on_enter: / on_leave:",
        "3. 常用动作：draw N / deal N damage to <目标> / restore N life to <目标> /",
        "   summon N #卡号 / add_to_hand N #卡号 / destroy <目标> / buff <目标> +X/+Y /",
        "   grant_keyword <关键词> to <目标>",
        "4. 目标写法：opponent:leader / self:leader / opponent:board<Follower> All /",
        "   self:board<Follower> All where has trait <兵种> / $self / $selected / $created",
        "5. 条件：if <表达式>: 下面缩进写动作。计数器写作 ctr:<名字>。",
        "6. 引用卡牌一律用 #卡号，不要写卡名。",
    });
}