using System;
using System.IO;

namespace ShadowverseFan.Ui;

/// <summary>
/// 热加载内容定位器。
///
/// 官方卡库与立绘是**热加载资源**：以松散文件放在 exe 旁边的 assets/ 与 data/，
/// 不打进 pck。改一张图、加一个卡包，重启即生效，不用重新导出。
///
/// **本类绝不能引用 Godot** —— Bridge 层要能在无头测试工程里直接跑。
/// 早先用 OS.GetExecutablePath() 定位，导致 RegressDeckUi 在 .et 里
/// 因缺少 Godot 程序集直接抛 TypeInitializationException。
/// 现在只用 System.IO / Environment。
/// </summary>
public static class ContentPaths
{
    private static string _root;

    /// <summary>内容根（含 assets/ 与 data/ 的那个目录）。</summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;

            // 1) 进程所在目录（导出后 = exe 目录）
            var procDir = ProcDir();
            if (!string.IsNullOrEmpty(procDir) && HasContent(procDir))
            {
                _root = procDir;
                return _root;
            }

            // 2) 程序集目录向上找（开发期从 .godot 或 .et 启动）
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (HasContent(dir)) { _root = dir; return _root; }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }

            // 3) 当前工作目录向上找
            dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (HasContent(dir)) { _root = dir; return _root; }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }

            _root = Directory.GetCurrentDirectory();
            return _root;
        }
    }

    private static bool HasContent(string dir)
    {
        try
        {
            return Directory.Exists(Path.Combine(dir, "assets"))
                || Directory.Exists(Path.Combine(dir, "data", "packs"));
        }
        catch { return false; }
    }

    /// <summary>
    /// 进程所在目录。
    ///
    /// 不用 Godot 的 OS.GetExecutablePath：那会在无头工程里触发
    /// Godot 程序集的类型初始化。Environment.ProcessPath 是纯 .NET 的。
    /// </summary>
    private static string ProcDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
        }
        catch { return null; }
    }

    /// <summary>拼出文件系统上的绝对路径（不保证存在）。</summary>
    public static string FilePath(params string[] parts)
        => Path.Combine(Root, Path.Combine(parts));

    /// <summary>卡牌立绘的绝对路径（不保证存在）。</summary>
    public static string CardArtPath(int cardId)
        => Path.Combine(Root, "assets", "cards", cardId + ".png");

    /// <summary>进化后立绘：源素材文件名 {id}+1，导入时落成 &lt;id&gt;_e.png。</summary>
    public static string CardArtPath(int cardId, bool evolved)
        => Path.Combine(Root, "assets", "cards", cardId + (evolved ? "_e" : "") + ".png");

    /// <summary>
    /// 启动自检：把定位结果打成文本。
    ///
    /// 打包后「内容到底有没有读到」不能靠猜 —— 上一轮我以为 exe 旁边
    /// 放 data/ 就够了，但从没验证过。现在开局就打印，一眼可查。
    /// </summary>
    public static string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ContentPaths] root = " + Root);
        sb.AppendLine("  proc = " + (ProcDir() ?? "?"));
        sb.AppendLine("  base = " + AppContext.BaseDirectory);
        sb.AppendLine("  cwd  = " + Directory.GetCurrentDirectory());
        sb.AppendLine("  assets/      " + (Directory.Exists(Path.Combine(Root, "assets")) ? "OK" : "MISSING"));
        sb.AppendLine("  data/packs/  " + (Directory.Exists(Path.Combine(Root, "data", "packs")) ? "OK" : "MISSING"));
        sb.AppendLine("  data/pools/  " + (Directory.Exists(Path.Combine(Root, "data", "pools")) ? "OK" : "MISSING"));

        int n = 0;
        try
        {
            var cardsDir = Path.Combine(Root, "assets", "cards");
            if (Directory.Exists(cardsDir)) n = Directory.GetFiles(cardsDir, "*.png").Length;
        }
        catch { }
        sb.AppendLine("  assets/cards/*.png  " + n);
        return sb.ToString();
    }
}