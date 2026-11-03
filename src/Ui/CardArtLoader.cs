using System.Collections.Generic;
using System.IO;
using Godot;

namespace ShadowverseFan.Ui;

/// <summary>
/// 立绘加载器（Godot 侧）。
///
/// 路径拼接在 ContentPaths（纯 C#），真正的解码与纹理创建在这里 ——
/// 因为 Image / ImageTexture 是 Godot 类型，不能污染 Bridge 层。
///
/// **必须缓存**：一张立绘 PNG 动辄 70 万字节，解码 + 建纹理要几十毫秒。
/// _Draw 在动画期间每帧都会调用一次，不缓存的话：
///   · 一帧解码几十 MB，帧率掉到个位数；
///   · 纹理还没建好就画了出去 —— 表现是白块，
///     等动画停下、画面不再重绘，才在下一帧画出正常立绘。
/// </summary>
public static class CardArtLoader
{
    private static readonly Dictionary<int, Texture2D> _cache = new();

    /// <summary>cardId -> 所属卡包（让 ResolveArtFile 优先查该包自己的 cards/）。</summary>
    private static readonly Dictionary<int, string> _cardPackId = new();

    /// <summary>登记某张卡属于哪个卡包（卡池构建时调用，供查找立绘时优先定位）。</summary>
    public static void RegisterCardPack(int cardId, string packId)
    {
        if (cardId > 0 && !string.IsNullOrEmpty(packId)) _cardPackId[cardId] = packId;
    }
    private static readonly HashSet<int> _missing = new();

    /// <summary>
    /// 加载一张卡牌立绘：先读 exe 旁热加载的 png，再退回 pck 内的 res://。
    /// 都没有则返回 null，卡面自动回落到程序化星象魔法阵。
    /// </summary>
    public static Texture2D Load(int cardId)
    {
        // 玩家切换过异画版本时走变体路径（一处切换，手牌/详情/棋子全生效）。
        // 默认 0 号变体仍走原来的 _cache 快路径，行为不变。
        int variant = GetPreferredVariant(cardId);
        if (variant != 0) return LoadVariant(cardId, variant, evolved: false);

        if (_cache.TryGetValue(cardId, out var cached)) return cached;
        if (_missing.Contains(cardId)) return null;

        var tex = LoadUncached(cardId);
        if (tex != null) _cache[cardId] = tex;
        else _missing.Add(cardId);
        return tex;
    }

    private static Texture2D LoadUncached(int cardId)
    {
        try
        {
            var abs = ContentPaths.CardArtPath(cardId);
            if (File.Exists(abs))
            {
                var img = Image.LoadFromFile(abs);
                if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0)
                    return ImageTexture.CreateFromImage(img);
            }
        }
        catch { }

        try
        {
            string resPath = "res://assets/cards/" + cardId + ".png";
            if (ResourceLoader.Exists(resPath)) return GD.Load<Texture2D>(resPath);
        }
        catch { }

        return null;
    }

    private static readonly Dictionary<int, Texture2D> _evolvedCache = new();
    private static readonly HashSet<int> _evolvedMissing = new();

    /// <summary>
    /// 加载一张卡的**进化后**立绘（assets/cards/&lt;id&gt;_e.png）。
    ///
    /// 源素材文件名是 {id}+末位：0=进化前、1=进化后；导入时把末位 1
    /// 落成 &lt;id&gt;_e.png。没有进化后立绘的卡返回 null，调用方继续用
    /// 进化前的图 —— 只换边框颜色也不影响可读性。
    /// </summary>
    public static Texture2D LoadEvolved(int cardId)
    {
        int variant = GetPreferredVariant(cardId);
        if (variant != 0) return LoadVariant(cardId, variant, evolved: true);

        if (_evolvedCache.TryGetValue(cardId, out var cached)) return cached;
        if (_evolvedMissing.Contains(cardId)) return null;

        var tex = LoadEvolvedUncached(cardId);
        if (tex != null) _evolvedCache[cardId] = tex;
        else _evolvedMissing.Add(cardId);
        return tex;
    }

    private static Texture2D LoadEvolvedUncached(int cardId)
    {
        try
        {
            var abs = ContentPaths.CardArtPath(cardId, evolved: true);
            if (File.Exists(abs))
            {
                var img = Image.LoadFromFile(abs);
                if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0)
                    return ImageTexture.CreateFromImage(img);
            }
        }
        catch { }

        try
        {
            string resPath = "res://assets/cards/" + cardId + "_e.png";
            if (ResourceLoader.Exists(resPath)) return GD.Load<Texture2D>(resPath);
        }
        catch { }

        return null;
    }


    // ---------------------------------------------------------------- 多变体异画（126 轮）

    /// <summary>cardId -> 当前选用立绘变体代号（0=原画，3=异画I，5=异画II…）。</summary>
    private static readonly Dictionary<int, int> _preferredVariant = new();

    /// <summary>某卡当前选用的变体代号（默认 0 原画）。</summary>
    private static bool _prefsLoaded;
    private static string PrefsPath => ContentPaths.FilePath("decks", "art_variants.json");

    /// <summary>首次访问时把玩家选过的异画偏好从磁盘读回来。</summary>
    private static void EnsurePrefsLoaded()
    {
        if (_prefsLoaded) return;
        _prefsLoaded = true;
        try
        {
            if (!File.Exists(PrefsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PrefsPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (int.TryParse(prop.Name, out int cid) && prop.Value.TryGetInt32(out int v) && v > 0)
                    _preferredVariant[cid] = v;
        }
        catch { }
    }

    private static void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            var dict = new Dictionary<string, int>();
            foreach (var kv in _preferredVariant) dict[kv.Key.ToString()] = kv.Value;
            File.WriteAllText(PrefsPath, System.Text.Json.JsonSerializer.Serialize(dict));
        }
        catch { }
    }

    public static int GetPreferredVariant(int cardId)
    {
        EnsurePrefsLoaded();
        return _preferredVariant.TryGetValue(cardId, out int v) ? v : 0;
    }

    /// <summary>
    /// 切换某卡的立绘版本（0=原画，3=异画I…）。一处切换，全场景生效：
    /// 手牌、详情、构筑台、战场棋子全走 Load/LoadEvolved，下次取图即新版。
    /// </summary>
    public static void SetPreferredVariant(int cardId, int variantCode)
    {
        EnsurePrefsLoaded();
        if (variantCode <= 0) _preferredVariant.Remove(cardId);
        else _preferredVariant[cardId] = variantCode;
        ShadowverseFan.Ui.Battle.BoardUnitWidget.InvalidateArtCache(cardId);
        // 落盘：不存的话重启游戏后图鉴里选好的异画会全部丢失。
        SavePrefs();
    }

    /// <summary>探测某卡磁盘上可用的立绘档位（图鉴渲染切换按钮用）。</summary>
    public static List<(int Code, string Label)> GetAvailableVariants(int cardId)
    {
        var list = new List<(int, string)> { (0, "原画") };
        int[] altCodes = { 3, 5, 7, 9 };
        string[] altNames = { "异画 I", "异画 II", "异画 III", "异画 IV" };
        for (int i = 0; i < altCodes.Length; i++)
            if (ResolveArtFile(cardId, altCodes[i], evolved: false) != null)
                list.Add((altCodes[i], altNames[i]));
        return list;
    }

    public static Texture2D LoadVariant(int cardId, int variant, bool evolved)
    {
        var key = (cardId, variant, evolved);
        if (_variantCache.TryGetValue(key, out var hit)) return hit;

        string path = ResolveArtFile(cardId, variant, evolved);
        // 异画往往只有进化前一张（只有末位3没有末位4）：进化后回落异画进化前。
        if (path == null && evolved && variant > 0)
            path = ResolveArtFile(cardId, variant, evolved: false);
        // 异画文件不存在整体回落原画。
        if (path == null && variant > 0)
            path = ResolveArtFile(cardId, 0, evolved);

        Texture2D tex = null;
        if (path != null)
        {
            try
            {
                var img = Image.LoadFromFile(path);
                if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0)
                    tex = ImageTexture.CreateFromImage(img);
            }
            catch { }
        }
        _variantCache[key] = tex;
        return tex;
    }

    private static readonly Dictionary<(int, int, bool), Texture2D> _variantCache = new();

    /// <summary>
    /// 按优先级找立绘文件：卡包自包含目录 data/packs/&lt;packId&gt;/cards/ 优先，
    /// 全局 assets/cards/ 兜底；同时兼容 &lt;id&gt;_3.png 与源素材 9 位 &lt;id&gt;3.png。
    /// </summary>
    private static string ResolveArtFile(int cardId, int variant, bool evolved)
    {
        // 查找优先级（126 专题三「卡包自包含目录」）：
        //   1. 本卡所属卡包自己的 cards/ —— 创作者发一个文件夹就能带走素材；
        //   2. 其余卡包的 cards/（跨包引用/依赖链）；
        //   3. 全局 assets/cards/ 兜底（官方基础图库）。
        var dirs = new List<string>();

        string packId = _cardPackId.TryGetValue(cardId, out var pid) ? pid : null;
        if (!string.IsNullOrEmpty(packId))
            dirs.Add(ContentPaths.FilePath("data", "packs", packId, "cards"));

        var packsRoot = ContentPaths.FilePath("data", "packs");
        try
        {
            if (System.IO.Directory.Exists(packsRoot))
                foreach (var d in System.IO.Directory.GetDirectories(packsRoot))
                    dirs.Add(System.IO.Path.Combine(d, "cards"));
        }
        catch { }
        dirs.Add(ContentPaths.FilePath("assets", "cards"));

        // 末位映射：原画 0/1；异画 n 的进化前是 n、进化后是 n+1（3->3/4，5->5/6）。
        string[] names = variant switch
        {
            0 => evolved ? new[] { $"{cardId}_e.png", $"{cardId}1.png" }
                         : new[] { $"{cardId}.png", $"{cardId}0.png" },
            _ => evolved ? new[] { $"{cardId}_{variant}_e.png", $"{cardId}_{variant + 1}.png", $"{cardId}{variant + 1}.png" }
                         : new[] { $"{cardId}_{variant}.png", $"{cardId}{variant}.png" },
        };

        foreach (var dir in dirs)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (var fn in names)
            {
                string full = System.IO.Path.Combine(dir, fn);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>清空缓存（换素材、热重载时调用）。</summary>
    public static void ClearCache()
    {
        _cache.Clear();
        _missing.Clear();
        // 早先只清了这两张表：在 DIY 里上传进化后立绘或异画后预览不刷新，
        // 因为进化/异画各有自己的缓存与缺失表。
        _evolvedCache.Clear();
        _evolvedMissing.Clear();
        _variantCache.Clear();
    }
}