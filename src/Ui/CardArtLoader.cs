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
    private static readonly HashSet<int> _missing = new();

    /// <summary>
    /// 加载一张卡牌立绘：先读 exe 旁热加载的 png，再退回 pck 内的 res://。
    /// 都没有则返回 null，卡面自动回落到程序化星象魔法阵。
    /// </summary>
    public static Texture2D Load(int cardId)
    {
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

    /// <summary>清空缓存（换素材、热重载时调用）。</summary>
    public static void ClearCache()
    {
        _cache.Clear();
        _missing.Clear();
    }
}