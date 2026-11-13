using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

// =====================================================================
//  腳本分享：匯出 / 匯入 .mtscript（zip：script.txt + 用到嘅圖片）
//  • 唔再用 P2P 直接連其他人部機；將來上 Steam 後改用 Steam 工作坊
//  • 工作坊上載嘅會係同一樣內容（腳本資料夾），所以 .mtscript 同工作坊互通
// =====================================================================
internal static class ScriptFiles
{
    public const string Ext = ".mtscript";
    private static string Filter => Lang.T("拉比工具箱腳本") + " (*" + Ext + ")|*" + Ext;

    /// <summary>匯出一個腳本做 .mtscript（唔會帶走快捷鍵、視窗等私人設定）。</summary>
    public static void Export(IWin32Window? owner, Script sc)
    {
        using var d = new SaveFileDialog { Filter = Filter, FileName = SafeName(sc.Name) + Ext, AddExtension = true, DefaultExt = Ext };
        if (d.ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(d.FileName, ScriptPackage.Pack(sc));
            Tools.Toast.Show(Lang.F("已匯出：{0}", Path.GetFileName(d.FileName)), Ui.Success, 3000);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("匯出腳本", ex);
            MessageBox.Show(owner, Lang.T("匯出失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>揀 .mtscript 檔匯入（可以一次揀幾個）。回傳加入咗幾多個。</summary>
    public static int ImportPick(IWin32Window? owner)
    {
        using var d = new OpenFileDialog { Filter = Filter, Multiselect = true };
        if (d.ShowDialog(owner) != DialogResult.OK) return 0;
        return d.FileNames.Count(f => Import(owner, f));
    }

    /// <summary>匯入一個 .mtscript：會先列出危險動作俾用家確認，唔會自動開始。</summary>
    public static bool Import(IWin32Window? owner, string path)
    {
        try
        {
            var pkg = ScriptPackage.Read(File.ReadAllBytes(path));
            if (pkg == null)
            {
                MessageBox.Show(owner, Lang.F("「{0}」唔係有效嘅腳本檔。", Path.GetFileName(path)), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var (sc, images) = pkg.Value;
            var risks = ScriptPackage.Risks(sc);
            string msg = Lang.F("加入「{0}」到我的腳本？", sc.Name);
            if (sc.Desc.Length > 0) msg += "\n\n" + sc.Desc;
            if (risks.Count > 0)
                msg += "\n\n" + Lang.T("⚠ 呢個腳本會做以下動作，請確認你信得過作者：") + "\n" + string.Join("\n", risks.Take(15).Select(r => "• " + r));
            msg += "\n\n" + Lang.T("加入之後唔會自動開始。");
            if (MessageBox.Show(owner, msg, Program.AppName, MessageBoxButtons.YesNo, risks.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return false;
            ScriptPackage.Install(sc, images);
            ScriptsPage.Instance?.AddNewFromDisk();
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("匯入腳本", ex);
            MessageBox.Show(owner, Lang.T("匯入失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim();
        return s.Length == 0 ? "script" : s.Length > 60 ? s[..60] : s;
    }

    /// <summary>拖入嚟嘅檔案入面有冇 .mtscript。</summary>
    public static string[] Dropped(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] fs ? fs.Where(f => f.EndsWith(Ext, StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<string>();
}

/// <summary>腳本庫：而家用 .mtscript 檔分享；上架 Steam 後會接 Steam 工作坊。</summary>
public sealed class LibraryPage : Panel
{
    public static Control Build() => new LibraryPage();

    private LibraryPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        var stack = new CardStack { Dock = DockStyle.Fill };

        var share = new CardPanel
        {
            Title = Lang.T("分享腳本"),
            Hint = Lang.T("喺「我的腳本」右鍵一個腳本 →「匯出」，就會存做 .mtscript 檔，可以傳俾朋友或者放上網。收到嘅 .mtscript 撳下面「匯入」，或者直接拖入呢個視窗。")
        };
        var import = new FancyButton { Text = Lang.T("📥 匯入 .mtscript…"), Kind = BtnKind.Primary, Font = Ui.F(10f) };
        import.Size = new Size(Math.Max(Ui.S(160), import.GetPreferredSize(Size.Empty).Width), Ui.S(38));
        import.Click += (_, _) => ScriptFiles.ImportPick(FindForm());
        share.SetContent(new WrapText(Lang.T("匯入嘅腳本會加入「我的腳本」，唔會自動開始；有系統動作或者打字嘅腳本會先提你確認。"), import));
        stack.AddRow(share);

        if (SteamHub.Ready) stack.AddRow(WorkshopCards.Browse(SteamHub.TagScript));
        else if (!Program.IsSteam)
        {
            var ws = new CardPanel
            {
                Title = Lang.T("Steam 工作坊（即將推出）"),
                Hint = Lang.T("上架 Steam 之後，可以喺呢度直接搜尋、訂閱同上載腳本，唔使再自己傳檔案。")
            };
            ws.SetContent(new WrapText(Lang.T("工作坊用嘅都係同一種腳本內容，你而家匯出嘅 .mtscript 到時都可以照用。")));
            stack.AddRow(ws);
        }
        Controls.Add(stack);

        // 拖 .mtscript 入嚟就匯入
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = ScriptFiles.Dropped(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { foreach (var f in ScriptFiles.Dropped(e)) ScriptFiles.Import(FindForm(), f); };
    }
}
