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

    /// <summary>
    /// 当前卡池的卡名+卡号名单提供器。
    ///
    /// 卡池是**动态**的：用户可以在 DIY 工坊里切到自己的自制卡池，
    /// 里面既有官方卡也有他自己造的卡。所以名单不能固化进静态提示词，
    /// 必须在每次发请求时现取 —— 否则切池后 LLM 会一直用旧名单。
    /// 返回 (卡号, 卡名) 序列；为 null 表示不注入名单。
    /// </summary>
    public Func<IEnumerable<(int Id, string Name)>> CardRosterSource { get; set; }

    /// <summary>系统提示词的附加段（卡池名单等），每次发送时重新生成。</summary>
    private string BuildRosterSection()
    {
        var src = CardRosterSource;
        if (src == null) return "";

        List<(int Id, string Name)> list;
        try { list = src()?.Where(x => x.Id > 0).ToList() ?? new List<(int, string)>(); }
        catch { return ""; }
        if (list.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("===== 当前卡池卡表（引用卡牌时只能从下表里选） =====").Append('\n');
        sb.Append("格式：#卡号 《卡名》").Append('\n');
        foreach (var (id, name) in list)
            sb.Append('#').Append(id).Append(" 《").Append(name).Append("》").Append('\n');
        sb.Append("===== 卡表结束（共 ").Append(list.Count).Append(" 张） =====");
        return sb.ToString();
    }

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

            // 卡池名单在**每次发送时**现取并拼到 system 消息末尾。
            // 卡池是动态的（用户可以切到自制卡池），固化进静态提示词会导致
            // 切池后 LLM 仍按旧名单造卡 —— 召唤出来的卡号当前池里根本不存在。
            string roster = BuildRosterSection();
            var payload = new List<object>(enabled.Count);
            foreach (var m in enabled)
            {
                bool isSystem = string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase);
                string content = (!string.IsNullOrEmpty(roster) && isSystem)
                    ? m.Content + "\n\n" + roster
                    : m.Content;
                payload.Add(new { role = m.Role, content });
            }

            string url = (Config.BaseUrl ?? "").TrimEnd('/') + "/chat/completions";
            var bodyJson = new
            {
                model = Config.Model,
                temperature = Config.Temperature,
                messages = payload.ToArray(),
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(Config.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(bodyJson),
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

    /// <summary>
    /// 加载 aad/prompts/*.md 作为提示词资产。
    ///
    /// 186 号文档 缺陷 5：目录不存在或为空时 UI 永远显示「无提示词资产」、
    /// 下拉框还被禁用 —— 玩家得自己先去资源管理器建目录。
    /// 现在目录不存在就创建，为空就落盘一份内置语法规范，开箱可用。
    /// </summary>
    public int LoadPromptAssets(string writableRoot, out string msg)
    {
        PromptAssets.Clear();
        string dir = Path.Combine(AadRoot(writableRoot), "prompts");
        try
        {
            Directory.CreateDirectory(dir);
            var files = Directory.GetFiles(dir, "*.*")
                                  .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                           || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(f => f, StringComparer.Ordinal).ToList();

            // 空目录：写一份内置规范，避免下拉框永远是空。
            if (files.Count == 0)
            {
                string sample = Path.Combine(dir, "00_内置PyAST语法规范.md");
                File.WriteAllText(sample, DefaultSystemPrompt(), new UTF8Encoding(false));
                files.Add(sample);
            }

            foreach (var f in files)
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
    /// <summary>
    /// 内置默认提示词：直接采用 docs/语法PyAST.md 全文，附输出约定。
    ///
    /// 输出约定（防 LLM 添油加醋）：
    ///   · 代码必须包在 <ast></ast> 标签里，标签外的一切文字都会被正则丢弃；
    ///   · 引用卡牌只写 #卡号；
    ///   · 不写 markdown 代码围栏（``` 会被解析器当噪音）。
    /// </summary>
    public static string DefaultSystemPrompt()
    {
        string body;
        try
        {
            // 优先用打包后的可写目录副本（ContentPaths.Root/docs），开发期回落仓库根。
            string p1 = Path.Combine(ContentPaths.Root, "docs", "语法PyAST.md");
            string p2 = Path.Combine(ContentPaths.Root, "..", "docs", "语法PyAST.md");
            string p3 = ContentPaths.Root;
            // 开发期 Root=game/，仓库根的 docs 在上一级；打包后可能与 exe 同级。
            string[] candidates =
            {
                p1,
                p2,
                // 逐级向上找（最多 3 层）。
                Path.Combine(Directory.GetParent(p3)?.FullName ?? p3, "docs", "语法PyAST.md"),
                Path.Combine(Directory.GetParent(Directory.GetParent(p3)?.FullName ?? p3)?.FullName ?? p3,
                             "docs", "语法PyAST.md"),
            };
            body = null;
            foreach (var c in candidates)
                if (File.Exists(c)) { body = File.ReadAllText(c, Encoding.UTF8); break; }
        }
        catch { body = null; }

        if (string.IsNullOrWhiteSpace(body))
            body = "（语法手册缺失：请把 docs/语法PyAST.md 放到程序目录旁）";

        return string.Join("\n", new[]
        {
            "你是同人卡牌引擎 Reshadow 的造卡助手。下面是 PyAST 规范语法手册全文，必须严格遵守其中的语法，",
            "禁止发明手册中不存在的触发词、动作指令或目标写法。",
            "",
            "输出约定：",
            "1. 只把 PyAST 代码放在 <ast></ast> 标签内输出，标签外不要写任何解释文字。",
            "2. 引用卡牌一律写 #卡号（如 #90011110），不要写卡名。",
            "3. 不要使用 markdown 代码围栏（```）。",
            "4. DIY 编辑器场景省略最外层 card 头，直接以 0 缩进写能力块。",
            "",
            "===== 语法手册 =====",
            body,
            "===== 手册结束 =====",
        });
    }

    /// <summary>
    /// 从回复中提取 PyAST 代码：优先 <ast></ast> 标签；
    /// 兼容旧的三反引号代码块；都没有才退回全文。
    /// </summary>
    public static string ExtractPyAstCode(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "";

        // ① <ast> 标签（约定的主格式）
        var tag = Regex.Match(reply, @"<ast>([\s\S]*?)</ast>", RegexOptions.IgnoreCase);
        if (tag.Success)
        {
            var inner = tag.Groups[1].Value.Trim();
            // 有些模型会把代码再裹一层 ```，剥掉。
            var fence = Regex.Match(inner, @"```[a-zA-Z]*\r?\n([\s\S]*?)```");
            return fence.Success ? fence.Groups[1].Value.Trim() : inner;
        }

        // ② 兼容：普通代码围栏
        var tagged = Regex.Match(reply, @"```(?:pyast|python|ast|text)?\s*\r?\n([\s\S]*?)```");
        if (tagged.Success) return tagged.Groups[1].Value.Trim();

        // ③ 都没有：退回全文（模型偶尔直接吐裸代码）
        return reply.Trim();
    }

    /// <summary>
    /// 拉取模型列表（OpenAI 兼容 GET /v1/models）。
    /// 返回 (是否成功, 模型 id 列表, 错误)。
    /// </summary>
    public async Task<(bool Ok, List<string> Models, string Error)> FetchModelsAsync(
        CancellationToken ct = default)
    {
        try
        {
            string url = (Config.BaseUrl ?? "").TrimEnd('/') + "/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(Config.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

            using var resp = await Http.SendAsync(req, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<string>(), "HTTP " + (int)resp.StatusCode + ": " + Clip(json, 200));

            using var doc = JsonDocument.Parse(json);
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                    }
            list.Sort(StringComparer.Ordinal);
            return (true, list, "");
        }
        catch (Exception ex)
        {
            return (false, new List<string>(), ex.Message);
        }
    }
}
