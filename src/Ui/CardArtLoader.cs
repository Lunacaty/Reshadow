using System.IO;
using Godot;

namespace ShadowverseFan.Ui;

/// <summary>
/// 立绘加载器（Godot 侧）。
///
/// 路径拼接在 ContentPaths（纯 C#），真正的解码与纹理创建在这里 ——
/// 因为 Image / ImageTexture 是 Godot 类型，不能污染 Bridge 层。
/// </summary>
public static class CardArtLoader
{
    /// <summary>
    /// 加载一张卡牌立绘：先读 exe 旁热加载的 png，再退回 pck 内的 res://。
    /// 都没有则返回 null，卡面自动回落到程序化星象魔法阵。
    /// </summary>
    public static Texture2D Load(int cardId)
    {
        try
        {
            var abs = ContentPaths.CardArtPath(cardId);
            if (File.Exists(abs))
            {
                var img = Image.LoadFromFile(abs);
                if (img != null) return ImageTexture.CreateFromImage(img);
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
}