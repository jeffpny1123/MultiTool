using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  設計工具（Rabi Design）：修圖（點陣圖、圖層），之後加向量繪圖
//  • 介面係 lib\design\index.html（WebView2），原始碼喺 web\design（自己寫嘅 Canvas 圖層引擎 + JSZip）
//  • 成個 lib\design 資料夾壓做 design.zip 嵌入 exe，第一次用解壓去 data\lib\design
//  • 開檔：PNG、JPG、WebP、BMP、GIF 同設計工具專案（.rdesign = ZIP：doc.json + 每個圖層一張 PNG）
//  • 儲存：頁面先問「寫去邊」（save-ask），主程式決定位置同格式（save-target），頁面再按格式編碼交返嚟
//  • 檔案全部喺本機處理：頁面只可以讀自己嘅檔案（其他網絡請求一律擋）
// =====================================================================
internal static class DesignLib
{
    public const string Host = "design.rabi";
    public static string Dir { get; private set; } = "";

    /// <summary>開得嘅副檔名（細楷，連點）。</summary>
    public static readonly string[] Exts = { ".rdesign", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    /// <summary>儲存得嘅格式（冇點）：修圖 / 向量繪圖各有一套，次序同 SaveFilter 一樣。</summary>
    public static string[] SaveExts(bool vector) => vector ? new[] { "rdesign", "svg", "pdf", "png" } : new[] { "rdesign", "png", "jpg", "webp" };
    public static bool Supported(string path) => Array.IndexOf(Exts, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>解壓嵌入嘅編輯器檔案（zip 大細變咗 = 新版本，重新解壓）。</summary>
    public static bool Prepare()
    {
        try
        {
            using var s = typeof(DesignLib).Assembly.GetManifestResourceStream("design.zip") ?? throw new FileNotFoundException("design.zip");
            string dir = Path.Combine(AppPaths.Data, "lib", "design");
            string mark = Path.Combine(dir, ".size");
            string want = s.Length.ToString();
            if (!File.Exists(mark) || File.ReadAllText(mark) != want || !File.Exists(Path.Combine(dir, "index.html")))
            {
                if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
                Directory.CreateDirectory(dir);
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read)) zip.ExtractToDirectory(dir, true);
                File.WriteAllText(mark, want);
            }
            Dir = dir;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("設計工具準備", ex);
            return false;
        }
    }

    /// <summary>頁面用嘅介面文字同設定（strings.js）。</summary>
    public static string StringsJs()
    {
        var l = new Dictionary<string, string>
        {
            // ---- 首頁 / 外殼 ----
            ["title"] = Lang.T("設計工具"),
            ["sub"] = Lang.T("修圖同向量繪圖：圖層、選取、筆刷、鋼筆、形狀同文字，開同存 PNG、JPG、WebP、SVG，匯出 PDF。檔案只會喺你部電腦處理，唔會上載。"),
            ["openfile"] = Lang.T("開啟檔案"), ["drop"] = Lang.T("或者將檔案拖入呢個視窗"), ["newwin"] = Lang.T("開新視窗"),
            ["new"] = Lang.T("新增"), ["open"] = Lang.T("開啟"), ["soon"] = Lang.T("即將推出"),
            ["k_photo"] = Lang.T("修圖"), ["k_photo_d"] = Lang.T("相片同點陣圖：圖層、混合模式、選取、筆刷、擦膠、填色、裁切同旋轉。"),
            ["k_vector"] = Lang.T("向量繪圖"), ["k_vector_d"] = Lang.T("用路徑同形狀畫標誌、圖示同插圖，放幾大都清晰。"),
            ["recent"] = Lang.T("最近開過"), ["recentclear"] = Lang.T("清除紀錄"), ["recentdel"] = Lang.T("由紀錄移除"),
            ["back"] = Lang.T("← 返回"), ["save"] = Lang.T("儲存"), ["saveas"] = Lang.T("另存新檔"), ["rename_tip"] = Lang.T("撳一下改名"),
            ["m_file"] = Lang.T("檔案"), ["m_edit"] = Lang.T("編輯"), ["m_view"] = Lang.T("檢視"),
            ["undo"] = Lang.T("復原"), ["redo"] = Lang.T("重做"), ["selectall"] = Lang.T("全選"),
            ["cut"] = Lang.T("剪下"), ["copy"] = Lang.T("複製"), ["paste"] = Lang.T("貼上"), ["paste_tip"] = Lang.T("請用 Ctrl+V 貼上。"),
            ["cancel"] = Lang.T("取消"), ["ok"] = Lang.T("確定"), ["close"] = Lang.T("關閉"),
            ["saving"] = Lang.T("儲存緊…"), ["saved"] = Lang.T("已儲存：{0}"), ["savefail"] = Lang.T("儲存失敗：{0}"),
            ["loading"] = Lang.T("載入緊…"), ["openfail"] = Lang.T("開唔到呢個檔案"), ["leave"] = Lang.T("有未儲存嘅改動，確定要離開？"),
            // ---- 修圖 ----
            ["ph_new"] = Lang.T("新增圖片…"), ["ph_newimg"] = Lang.T("新增圖片"), ["ph_untitled"] = Lang.T("未命名圖片"),
            ["ph_preset"] = Lang.T("預設尺寸"), ["ph_width"] = Lang.T("闊度（px）"), ["ph_height"] = Lang.T("高度（px）"), ["ph_bgfill"] = Lang.T("背景"),
            ["ps_hd"] = Lang.T("高清畫面"), ["ps_square"] = Lang.T("正方形帖文"), ["ps_story"] = Lang.T("直向短片 / 限時動態"), ["ps_a4"] = Lang.T("A4（300 dpi）"), ["ps_thumb"] = Lang.T("影片縮圖"),
            ["bg_white"] = Lang.T("白色"), ["bg_clear"] = Lang.T("透明"), ["bg_black"] = Lang.T("黑色"),
            ["ph_background"] = Lang.T("背景"), ["ph_layer_n"] = Lang.T("圖層 {0}"), ["ph_copyof"] = Lang.T("{0} 副本"), ["ph_pasted"] = Lang.T("貼上嘅圖層"),
            ["ph_badimg"] = Lang.T("讀唔到呢張圖片（檔案壞咗或者唔係圖片）。"), ["ph_toobig"] = Lang.T("圖片太大（最多大約 2.6 億像素）。"),
            ["ph_export"] = Lang.T("匯出為"), ["ph_close"] = Lang.T("關閉圖片"),
            ["ph_copymerged"] = Lang.T("複製（合併所有圖層）"), ["ph_clear"] = Lang.T("刪除選取範圍入面嘅內容"),
            ["ph_fillfg"] = Lang.T("用前景色填滿"), ["ph_fillbg"] = Lang.T("用背景色填滿"),
            ["ph_m_image"] = Lang.T("圖片"), ["ph_m_layer"] = Lang.T("圖層"), ["ph_m_select"] = Lang.T("選取"),
            ["ph_imgsize"] = Lang.T("圖片尺寸…"), ["ph_canvassize"] = Lang.T("畫布尺寸…"), ["ph_crop"] = Lang.T("裁切到選取範圍"),
            ["ph_rot_cw"] = Lang.T("順時針轉 90°"), ["ph_rot_ccw"] = Lang.T("逆時針轉 90°"), ["ph_rot_180"] = Lang.T("轉 180°"),
            ["ph_flip_h"] = Lang.T("左右反轉成張圖"), ["ph_flip_v"] = Lang.T("上下反轉成張圖"), ["ph_flatten"] = Lang.T("合併所有圖層"),
            ["ph_keepratio"] = Lang.T("保持比例"), ["ph_anchor"] = Lang.T("原圖位置"),
            ["ph_newlayer"] = Lang.T("新增圖層"), ["ph_duplayer"] = Lang.T("複製圖層"), ["ph_dellayer"] = Lang.T("刪除圖層"), ["ph_renamelayer"] = Lang.T("重新命名圖層"),
            ["ph_layername"] = Lang.T("圖層名稱"), ["ph_layerup"] = Lang.T("圖層上移"), ["ph_layerdown"] = Lang.T("圖層下移"), ["ph_mergedown"] = Lang.T("向下合併"),
            ["ph_lflip_h"] = Lang.T("左右反轉圖層"), ["ph_lflip_v"] = Lang.T("上下反轉圖層"), ["ph_reorder"] = Lang.T("調動圖層次序"),
            ["ph_lastlayer"] = Lang.T("最少要有一個圖層。"), ["ph_visible"] = Lang.T("顯示 / 隱藏圖層"), ["ph_hidden"] = Lang.T("呢個圖層隱藏咗，先撳眼仔顯示返。"),
            ["ph_layers"] = Lang.T("圖層"), ["ph_blend"] = Lang.T("混合模式"), ["ph_opacity"] = Lang.T("不透明度"),
            ["ph_deselect"] = Lang.T("取消選取"), ["ph_invert"] = Lang.T("反轉選取範圍"), ["ph_sellayer"] = Lang.T("選取圖層內容"), ["ph_nosel"] = Lang.T("未有選取範圍。"),
            ["ph_zoomin"] = Lang.T("放大"), ["ph_zoomout"] = Lang.T("縮細"), ["ph_fit"] = Lang.T("符合視窗大細"), ["ph_actual"] = Lang.T("實際大細（100%）"),
            ["ph_size"] = Lang.T("大細"), ["ph_hard"] = Lang.T("硬度"), ["ph_tol"] = Lang.T("容差"), ["ph_contig"] = Lang.T("只揀相連嘅位置"),
            ["ph_fg"] = Lang.T("前景色"), ["ph_bg"] = Lang.T("背景色"), ["ph_swap"] = Lang.T("對調前景色同背景色"), ["ph_resetcolors"] = Lang.T("還原黑白"),
            ["ph_flat_t"] = Lang.T("呢個格式冇圖層"),
            ["ph_flat"] = Lang.T("{0} 檔案只可以存一層。想保留圖層，可以另存做設計工具專案（.rdesign）；或者合併晒所有圖層再儲存。"),
            ["ph_flat_merge"] = Lang.T("合併後儲存"), ["ph_flat_project"] = Lang.T("另存做專案"),
            // 面板 / 視窗選單
            ["m_window"] = Lang.T("視窗"), ["ph_p_tools"] = Lang.T("工具"), ["ph_p_color"] = Lang.T("顏色"), ["ph_p_swatches"] = Lang.T("色板"),
            ["ph_p_nav"] = Lang.T("導覽"), ["ph_p_history"] = Lang.T("步驟紀錄"), ["ph_hist_open"] = Lang.T("開啟時"),
            ["ph_togglepanels"] = Lang.T("隱藏 / 顯示所有面板"), ["ph_resetlayout"] = Lang.T("還原預設版面"),
            ["ph_dockcollapse"] = Lang.T("摺埋做圖示"), ["ph_dockexpand"] = Lang.T("展開面板"), ["ph_collapse"] = Lang.T("摺埋 / 展開"), ["ph_closepanel"] = Lang.T("關閉面板"),
            ["ph_dragtip"] = Lang.T("拖曳可以搬去其他位置；放喺畫布上面就變成浮動視窗。"),
            ["ph_recentcolors"] = Lang.T("最近用過"), ["ph_basiccolors"] = Lang.T("基本顏色"), ["ph_swatchtip"] = Lang.T("撳一下做前景色；Alt + 撳 = 背景色"),
            // 工具
            ["ph_t_zoom"] = Lang.T("縮放"), ["ph_h_zoom"] = Lang.T("撳一下放大；Alt + 撳 = 縮細。"),
            ["ph_t_move"] = Lang.T("移動"), ["ph_t_rect"] = Lang.T("矩形選取"), ["ph_t_ellipse"] = Lang.T("橢圓選取"), ["ph_t_lasso"] = Lang.T("套索"),
            ["ph_t_wand"] = Lang.T("魔術棒"), ["ph_t_brush"] = Lang.T("筆刷"), ["ph_t_eraser"] = Lang.T("擦膠"), ["ph_t_fill"] = Lang.T("填色"),
            ["ph_t_picker"] = Lang.T("吸管"), ["ph_t_hand"] = Lang.T("手形"),
            ["ph_h_move"] = Lang.T("拖曳移動圖層；有選取範圍就只移動揀咗嘅部分。方向鍵每次移 1 px（Shift = 10 px）。"),
            ["ph_h_rect"] = Lang.T("拖曳揀一個長方形範圍。Shift = 加入、Alt = 減去；拉嘅時候撳住 Shift = 正方形。"),
            ["ph_h_ellipse"] = Lang.T("拖曳揀一個橢圓範圍。Shift = 加入、Alt = 減去。"),
            ["ph_h_lasso"] = Lang.T("撳住拖曳，隨手圈出範圍。Shift = 加入、Alt = 減去。"),
            ["ph_h_wand"] = Lang.T("撳一下揀顏色相近嘅位置。Shift = 加入、Alt = 減去。"),
            ["ph_h_brush"] = Lang.T("用前景色畫。[ ] 改大細；Shift + 撳 = 由上一筆畫直線。支援手寫筆壓力。"),
            ["ph_h_eraser"] = Lang.T("擦走而家圖層嘅內容（變透明）。[ ] 改大細。"),
            ["ph_h_fill"] = Lang.T("撳一下用前景色填滿顏色相近嘅位置。"),
            ["ph_h_picker"] = Lang.T("撳一下攞畫面上嘅顏色做前景色；Alt + 撳 = 背景色。"),
            ["ph_h_hand"] = Lang.T("拖曳移動畫面。用其他工具時撳住空白鍵都得；Ctrl + 滾輪縮放。"),
            ["ph_sm_new"] = Lang.T("新範圍"), ["ph_sm_add"] = Lang.T("加入"), ["ph_sm_sub"] = Lang.T("減去"), ["ph_sm_and"] = Lang.T("交集"),
            // 混合模式
            ["ph_bl_source-over"] = Lang.T("正常"), ["ph_bl_multiply"] = Lang.T("相乘"), ["ph_bl_screen"] = Lang.T("濾光"), ["ph_bl_overlay"] = Lang.T("疊加"),
            ["ph_bl_darken"] = Lang.T("變暗"), ["ph_bl_lighten"] = Lang.T("變亮"), ["ph_bl_color-dodge"] = Lang.T("顏色減淡"), ["ph_bl_color-burn"] = Lang.T("顏色加深"),
            ["ph_bl_hard-light"] = Lang.T("強光"), ["ph_bl_soft-light"] = Lang.T("柔光"), ["ph_bl_difference"] = Lang.T("差值"), ["ph_bl_exclusion"] = Lang.T("排除"),
            ["ph_bl_hue"] = Lang.T("色相"), ["ph_bl_saturation"] = Lang.T("飽和度"), ["ph_bl_color"] = Lang.T("顏色"), ["ph_bl_luminosity"] = Lang.T("亮度"),
            // ---- 修圖第二步：調整、濾鏡、變形、文字 ----
            ["ph_t_polylasso"] = Lang.T("多邊形套索"),
            ["ph_t_crop"] = Lang.T("裁切"),
            ["ph_t_pencil"] = Lang.T("鉛筆"),
            ["ph_t_clone"] = Lang.T("仿製印章"),
            ["ph_t_gradient"] = Lang.T("漸層"),
            ["ph_t_text"] = Lang.T("文字"),
            ["ph_t_shrect"] = Lang.T("矩形"),
            ["ph_t_shellipse"] = Lang.T("橢圓形"),
            ["ph_t_shline"] = Lang.T("直線"),
            ["ph_h_polylasso"] = Lang.T("逐點撳出多邊形；撳返起點、雙擊或者 Enter 完成，Backspace 刪最後一點，Esc 取消。Shift = 45°。"),
            ["ph_h_crop"] = Lang.T("拖曳四邊同四角調整範圍，拖入面移動；Enter 或雙擊裁切，Esc 還原。Shift = 保持比例。"),
            ["ph_h_pencil"] = Lang.T("冇柔邊嘅實心筆，啱畫像素圖。[ ] 改大細；Alt + 撳 = 吸色。"),
            ["ph_h_clone"] = Lang.T("Alt + 撳設定取樣點，再喺其他位置畫，就會複製取樣點嘅內容。"),
            ["ph_h_gradient"] = Lang.T("拖曳拉出漸層（前景色到背景色）。Shift = 45°；有選取範圍就只填入面。"),
            ["ph_h_text"] = Lang.T("撳一下開始打字；撳現有文字可以再改。Ctrl + Enter 完成，Esc 取消。"),
            ["ph_h_shrect"] = Lang.T("拖曳畫矩形（自動開新圖層）。Shift = 正方形，Alt = 由中心畫。"),
            ["ph_h_shellipse"] = Lang.T("拖曳畫橢圓形（自動開新圖層）。Shift = 正圓，Alt = 由中心畫。"),
            ["ph_h_shline"] = Lang.T("拖曳畫直線（自動開新圖層）。Shift = 45°。"),
            ["ph_h_xf"] = Lang.T("拖曳把手縮放（角位保持比例，Shift = 自由），框外拖曳旋轉，Alt = 由中心。Enter 完成，Esc 取消。"),
            ["ph_aligned"] = Lang.T("對齊取樣"),
            ["ph_g_linear"] = Lang.T("線性"),
            ["ph_g_radial"] = Lang.T("放射"),
            ["ph_g_fgbg"] = Lang.T("前景 → 背景"),
            ["ph_g_fgclear"] = Lang.T("前景 → 透明"),
            ["ph_g_reverse"] = Lang.T("反轉"),
            ["ph_s_fill"] = Lang.T("填色"),
            ["ph_s_stroke"] = Lang.T("外框"),
            ["ph_s_width"] = Lang.T("粗幼"),
            ["ph_apply"] = Lang.T("套用"),
            ["ph_font"] = Lang.T("字型"),
            ["ph_fontsize"] = Lang.T("大細"),
            ["ph_bold"] = Lang.T("粗體"),
            ["ph_italic"] = Lang.T("斜體"),
            ["ph_textcolor"] = Lang.T("文字顏色"),
            ["ph_textlayer"] = Lang.T("文字圖層（可以再改字）"),
            ["ph_textstyle"] = Lang.T("改文字樣式"),
            ["ph_edittext"] = Lang.T("改文字"),
            ["ph_rasterize"] = Lang.T("點陣化文字"),
            ["ph_rasterized"] = Lang.T("文字圖層已經變成普通圖層（唔可以再改字）"),
            ["ph_clonesrc"] = Lang.T("先撳住 Alt 撳一下設定取樣點"),
            ["ph_clonesrc_set"] = Lang.T("已設定取樣點"),
            ["ph_xf_empty"] = Lang.T("呢個圖層冇內容可以變形"),
            ["ph_thumbtip"] = Lang.T("Ctrl + 撳：揀返呢個圖層有內容嘅位置"),
            ["ph_movesel"] = Lang.T("移動選取範圍"),
            ["ph_togglelast"] = Lang.T("切換最後一步"),
            ["ph_pasteinplace"] = Lang.T("原位貼上"),
            ["ph_filldlg"] = Lang.T("填滿…"),
            ["ph_strokedlg"] = Lang.T("筆畫…"),
            ["ph_freetransform"] = Lang.T("自由變形"),
            ["ph_transform"] = Lang.T("變形"),
            ["ph_lrot_180"] = Lang.T("旋轉 180°"),
            ["ph_lrot_cw"] = Lang.T("順時針轉 90°"),
            ["ph_lrot_ccw"] = Lang.T("逆時針轉 90°"),
            ["ph_adjust"] = Lang.T("調整"),
            ["ph_a_bc"] = Lang.T("亮度 / 對比…"),
            ["ph_a_levels"] = Lang.T("色階…"),
            ["ph_a_curves"] = Lang.T("曲線…"),
            ["ph_a_exposure"] = Lang.T("曝光度…"),
            ["ph_a_vibrance"] = Lang.T("自然飽和度…"),
            ["ph_a_huesat"] = Lang.T("色相 / 飽和度…"),
            ["ph_a_balance"] = Lang.T("色彩平衡…"),
            ["ph_a_bw"] = Lang.T("黑白…"),
            ["ph_a_photofilter"] = Lang.T("相片濾鏡…"),
            ["ph_a_invert"] = Lang.T("負片"),
            ["ph_a_posterize"] = Lang.T("色調分離…"),
            ["ph_a_threshold"] = Lang.T("臨界值…"),
            ["ph_a_desat"] = Lang.T("去色"),
            ["ph_a_autotone"] = Lang.T("自動色調"),
            ["ph_a_autocontrast"] = Lang.T("自動對比"),
            ["ph_a_autocolor"] = Lang.T("自動顏色"),
            ["ph_imgrotate"] = Lang.T("影像旋轉"),
            ["ph_trim"] = Lang.T("修剪透明位"),
            ["ph_trimempty"] = Lang.T("成張圖都係透明"),
            ["ph_trimnone"] = Lang.T("四邊冇透明位可以剪"),
            ["ph_layercut"] = Lang.T("剪下嘅圖層"),
            ["ph_arrange"] = Lang.T("排列"),
            ["ph_tofront"] = Lang.T("移到最頂"),
            ["ph_toback"] = Lang.T("移到最底"),
            ["ph_selabove"] = Lang.T("揀上面嘅圖層"),
            ["ph_selbelow"] = Lang.T("揀下面嘅圖層"),
            ["ph_mergevisible"] = Lang.T("合併看得見嘅圖層"),
            ["ph_stamp"] = Lang.T("蓋印看得見嘅圖層"),
            ["ph_reselect"] = Lang.T("重新選取"),
            ["ph_modify"] = Lang.T("修改"),
            ["ph_border"] = Lang.T("邊框…"),
            ["ph_smooth"] = Lang.T("平滑…"),
            ["ph_expand"] = Lang.T("擴大…"),
            ["ph_contract"] = Lang.T("收細…"),
            ["ph_feather"] = Lang.T("羽化…"),
            ["ph_featherr"] = Lang.T("羽化半徑（px）"),
            ["ph_bypx"] = Lang.T("幾多像素（px）"),
            ["ph_m_filter"] = Lang.T("濾鏡"),
            ["ph_lastfilter"] = Lang.T("再做一次：{0}"),
            ["ph_lastfilter0"] = Lang.T("再做一次上個濾鏡"),
            ["ph_f_blur"] = Lang.T("模糊"),
            ["ph_f_gauss"] = Lang.T("高斯模糊…"),
            ["ph_f_motion"] = Lang.T("動態模糊…"),
            ["ph_f_sharpen"] = Lang.T("銳化"),
            ["ph_f_unsharp"] = Lang.T("遮色片銳化…"),
            ["ph_f_sharp"] = Lang.T("銳化"),
            ["ph_f_noise"] = Lang.T("雜訊"),
            ["ph_f_addnoise"] = Lang.T("增加雜訊…"),
            ["ph_f_median"] = Lang.T("中間值（減少雜訊）…"),
            ["ph_f_pixelate"] = Lang.T("像素化"),
            ["ph_f_mosaic"] = Lang.T("馬賽克…"),
            ["ph_f_stylize"] = Lang.T("風格化"),
            ["ph_f_emboss"] = Lang.T("浮雕…"),
            ["ph_f_edges"] = Lang.T("找出邊緣"),
            ["ph_f_other"] = Lang.T("其他"),
            ["ph_f_highpass"] = Lang.T("高反差保留…"),
            ["ph_extras"] = Lang.T("顯示選取框"),
            ["ph_grid"] = Lang.T("格線"),
            ["ph_brightness"] = Lang.T("亮度"),
            ["ph_contrast"] = Lang.T("對比"),
            ["ph_exposure"] = Lang.T("曝光度"),
            ["ph_offset"] = Lang.T("位移"),
            ["ph_gamma"] = Lang.T("Gamma 校正"),
            ["ph_vibrance"] = Lang.T("自然飽和度"),
            ["ph_saturation"] = Lang.T("飽和度"),
            ["ph_hue"] = Lang.T("色相"),
            ["ph_lightness"] = Lang.T("明度"),
            ["ph_colorize"] = Lang.T("上色"),
            ["ph_c_red"] = Lang.T("紅"),
            ["ph_c_yellow"] = Lang.T("黃"),
            ["ph_c_green"] = Lang.T("綠"),
            ["ph_c_cyan"] = Lang.T("青"),
            ["ph_c_blue"] = Lang.T("藍"),
            ["ph_c_magenta"] = Lang.T("洋紅"),
            ["ph_shadows"] = Lang.T("陰影"),
            ["ph_midtones"] = Lang.T("中間調"),
            ["ph_highlights"] = Lang.T("亮部"),
            ["ph_keeplum"] = Lang.T("保持光暗"),
            ["ph_tint"] = Lang.T("色調"),
            ["ph_color"] = Lang.T("顏色"),
            ["ph_density"] = Lang.T("濃度"),
            ["ph_levelsn"] = Lang.T("色階數"),
            ["ph_threshold"] = Lang.T("臨界值"),
            ["ph_radius"] = Lang.T("半徑（px）"),
            ["ph_angle"] = Lang.T("角度"),
            ["ph_distance"] = Lang.T("距離（px）"),
            ["ph_amount"] = Lang.T("數量（%）"),
            ["ph_mono"] = Lang.T("單色"),
            ["ph_cell"] = Lang.T("格仔大細（px）"),
            ["ph_height2"] = Lang.T("高度"),
            ["ph_channel"] = Lang.T("色版"),
            ["ph_inblack"] = Lang.T("輸入：黑"),
            ["ph_midtone"] = Lang.T("輸入：中間調"),
            ["ph_inwhite"] = Lang.T("輸入：白"),
            ["ph_outblack"] = Lang.T("輸出：黑"),
            ["ph_outwhite"] = Lang.T("輸出：白"),
            ["ph_auto"] = Lang.T("自動"),
            ["ph_input"] = Lang.T("輸入"),
            ["ph_output"] = Lang.T("輸出"),
            ["ph_reset"] = Lang.T("重設"),
            ["ph_contents"] = Lang.T("內容"),
            ["ph_black"] = Lang.T("黑色"),
            ["ph_gray50"] = Lang.T("50% 灰"),
            ["ph_white"] = Lang.T("白色"),
            ["ph_keepalpha"] = Lang.T("保留透明區域"),
            ["ph_location"] = Lang.T("位置"),
            ["ph_inside"] = Lang.T("內側"),
            ["ph_center"] = Lang.T("居中"),
            ["ph_outside"] = Lang.T("外側"),
            // ---- 向量繪圖 ----
            ["k_vector_d2"] = Lang.T("路徑、形狀、文字同鋼筆工具：畫標誌、圖示同插圖，放幾大都清晰。開同存 SVG，匯出 PDF 同 PNG。"),
            ["vc_newdoc"] = Lang.T("新增向量文件"),
            ["vc_untitled"] = Lang.T("未命名繪圖"),
            ["ps_a4v"] = Lang.T("A4（直向）"),
            ["ps_letter"] = Lang.T("Letter（直向）"),
            ["ps_icon"] = Lang.T("圖示"),
            ["ps_logo"] = Lang.T("標誌"),
            ["vc_fill"] = Lang.T("填色"),
            ["vc_stroke"] = Lang.T("筆畫"),
            ["vc_none"] = Lang.T("無"),
            ["vc_swap"] = Lang.T("對調填色同筆畫"),
            ["vc_default"] = Lang.T("預設填色同筆畫"),
            ["vc_color"] = Lang.T("改顏色"),
            ["vc_preview"] = Lang.T("預覽"),
            ["vc_scale"] = Lang.T("縮放物件"),
            ["vc_rotate"] = Lang.T("旋轉物件"),
            ["vc_dupmove"] = Lang.T("複製並移動"),
            ["vc_delete"] = Lang.T("刪除"),
            ["vc_group"] = Lang.T("組成群組"),
            ["vc_ungroup"] = Lang.T("解散群組"),
            ["vc_pdfraster"] = Lang.T("有中文等字型：PDF 用咗高解像度圖片顯示，免得變亂碼。"),
            ["vt_select"] = Lang.T("選取"),
            ["vt_direct"] = Lang.T("直接選取"),
            ["vt_move"] = Lang.T("移動"),
            ["vt_pen"] = Lang.T("鋼筆"),
            ["vt_text"] = Lang.T("文字"),
            ["vt_line"] = Lang.T("直線"),
            ["vt_rect"] = Lang.T("矩形"),
            ["vt_ellipse"] = Lang.T("橢圓"),
            ["vt_polygon"] = Lang.T("多邊形"),
            ["vt_star"] = Lang.T("星形"),
            ["vt_pencil"] = Lang.T("鉛筆"),
            ["vt_picker"] = Lang.T("吸管"),
            ["vt_hand"] = Lang.T("手形"),
            ["vt_zoom"] = Lang.T("縮放"),
            ["vh_select"] = Lang.T("撳一下揀物件，Shift = 加減；拖曳移動，Alt + 拖曳 = 複製。拉角落把手縮放（Shift 保持比例），喺角落外面拖曳就旋轉。"),
            ["vh_direct"] = Lang.T("揀錨點同把手嚟改路徑形狀；拉框揀幾個錨點。Alt + 拉把手 = 只改一邊。"),
            ["vh_pen"] = Lang.T("撳一下加角點，撳住拖曳加平滑點；撳返起點就封閉路徑，Enter 或 Esc 完成。Shift = 45° 角。"),
            ["vh_text"] = Lang.T("撳一下開始打字；撳現有文字就修改。Esc 完成。"),
            ["vh_line"] = Lang.T("拖曳畫直線。Shift = 45° 角，Alt = 由中間開始。"),
            ["vh_rect"] = Lang.T("拖曳畫矩形；撳一下輸入大細。Shift = 正方形，Alt = 由中間開始。"),
            ["vh_ellipse"] = Lang.T("拖曳畫橢圓；撳一下輸入大細。Shift = 正圓，Alt = 由中間開始。"),
            ["vh_polygon"] = Lang.T("拖曳畫多邊形，邊數喺上面改。Shift = 唔拉伸。"),
            ["vh_star"] = Lang.T("拖曳畫星形，角數同內徑喺上面改。Shift = 唔拉伸。"),
            ["vh_pencil"] = Lang.T("隨手畫線，放手會自動變順滑嘅曲線。"),
            ["vh_picker"] = Lang.T("撳一件物件攞佢嘅填色同筆畫（有揀嘢就套用落去）。"),
            ["vh_hand"] = Lang.T("拖曳移動畫面。用其他工具時撳住空白鍵都得；Ctrl + 滾輪縮放。"),
            ["vh_zoom"] = Lang.T("撳一下放大；Alt + 撳 = 縮細。"),
            ["vo_strokew"] = Lang.T("筆畫粗幼"),
            ["vo_radius"] = Lang.T("圓角"),
            ["vo_sides"] = Lang.T("邊數"),
            ["vo_points"] = Lang.T("角數"),
            ["vo_inner"] = Lang.T("內徑"),
            ["vo_font"] = Lang.T("字型"),
            ["vo_fontsize"] = Lang.T("字型大細"),
            ["vo_path"] = Lang.T("路徑"),
            ["vo_compound"] = Lang.T("複合路徑"),
            ["vo_group"] = Lang.T("群組"),
            ["vo_image"] = Lang.T("圖片"),
            ["vm_new"] = Lang.T("新增…"),
            ["vm_object"] = Lang.T("物件"),
            ["vm_transform"] = Lang.T("變形"),
            ["vm_fh"] = Lang.T("左右反轉"),
            ["vm_fv"] = Lang.T("上下反轉"),
            ["vm_r90"] = Lang.T("順時針轉 90°"),
            ["vm_r-90"] = Lang.T("逆時針轉 90°"),
            ["vm_arrange"] = Lang.T("排列"),
            ["vm_front"] = Lang.T("移到最上面"),
            ["vm_forward"] = Lang.T("移上一層"),
            ["vm_backward"] = Lang.T("移落一層"),
            ["vm_back"] = Lang.T("移到最底"),
            ["vm_artboard"] = Lang.T("畫板尺寸"),
            ["vm_fitart"] = Lang.T("畫板跟晒所有物件大細"),
            ["vm_fitboard"] = Lang.T("畫板放入視窗"),
            ["vm_pastefront"] = Lang.T("原位貼喺上面"),
            ["vm_pasteback"] = Lang.T("原位貼喺下面"),
            ["vp_props"] = Lang.T("內容"),
            ["vp_document"] = Lang.T("文件"),
            ["vp_nosel"] = Lang.T("揀一件物件就可以改佢嘅位置、大細、顏色同筆畫。"),
            ["vp_transform"] = Lang.T("變形"),
            ["vp_rotateby"] = Lang.T("輸入角度再撳 Enter 旋轉"),
            ["vp_appearance"] = Lang.T("外觀"),
            ["vp_cap"] = Lang.T("端點"),
            ["vp_cap_butt"] = Lang.T("平頭"),
            ["vp_cap_round"] = Lang.T("圓頭"),
            ["vp_cap_square"] = Lang.T("方頭"),
            ["vp_join"] = Lang.T("轉角"),
            ["vp_join_miter"] = Lang.T("尖角"),
            ["vp_join_round"] = Lang.T("圓轉角"),
            ["vp_join_bevel"] = Lang.T("斜角"),
            ["vp_dash"] = Lang.T("虛線"),
            ["vp_align"] = Lang.T("對齊"),
            ["vp_alignobj"] = Lang.T("對齊物件"),
            ["vp_al_l"] = Lang.T("靠左"),
            ["vp_al_ch"] = Lang.T("水平置中"),
            ["vp_al_r"] = Lang.T("靠右"),
            ["vp_al_t"] = Lang.T("靠頂"),
            ["vp_al_cv"] = Lang.T("垂直置中"),
            ["vp_al_b"] = Lang.T("靠底"),
            ["vp_distribute"] = Lang.T("平均分佈"),
            ["vp_dist_h"] = Lang.T("左右平均分佈"),
            ["vp_dist_v"] = Lang.T("上下平均分佈"),
            ["vp_toboard"] = Lang.T("揀咗一件：對齊畫板。"),
            ["vp_tosel"] = Lang.T("揀咗幾件：對齊佢哋嘅範圍。"),
            ["vp_pick"] = Lang.T("先揀物件。"),
            ["vp_ops"] = Lang.T("形狀運算"),
            ["vp_ops_modes"] = Lang.T("合併形狀"),
            ["vp_op_unite"] = Lang.T("聯集"),
            ["vp_op_minus"] = Lang.T("減去上面嘅形狀"),
            ["vp_op_intersect"] = Lang.T("只留重疊位"),
            ["vp_op_exclude"] = Lang.T("去走重疊位"),
            ["vp_ops_need"] = Lang.T("要揀最少兩個路徑（群組要先解散）。"),
            ["vp_ops_tip"] = Lang.T("揀兩個或者以上嘅形狀，再撳一種合併方法。"),
            ["vp_objects"] = Lang.T("物件清單"),
            ["vp_empty"] = Lang.T("仲未有物件。"),
            ["vp_lock"] = Lang.T("鎖定 / 解鎖"),
            ["vp_rename"] = Lang.T("改名"),
            ["vp_name"] = Lang.T("名稱"),
        };
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var cfg = new
        {
            light = Ui.Light,
            lang = Lang.Code(Lang.Current),
            recent = DesignRecent.ForJs(),
            colors = new Dictionary<string, string>
            {
                ["bg"] = Hex(Ui.Bg), ["card"] = Hex(Ui.Card), ["hi"] = Hex(Ui.CardHi), ["text"] = Hex(Ui.Text), ["dim"] = Hex(Ui.TextDim),
                ["border"] = Hex(Ui.Border), ["accent"] = Hex(Ui.Accent), ["input"] = Hex(Ui.Input), ["danger"] = Hex(Ui.Danger), ["ok"] = Hex(Ui.Success)
            }
        };
        var opt = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return "window.L=" + JsonSerializer.Serialize(l, opt) + ";window.CFG=" + JsonSerializer.Serialize(cfg, opt) + ";";
    }

    /// <summary>開檔對話框嘅檔案類型。</summary>
    public static string OpenFilter =>
        Lang.T("支援嘅檔案") + " (*.rdesign;*.svg;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.rdesign;*.svg;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|" +
        Lang.T("設計工具專案") + " (*.rdesign)|*.rdesign|SVG (*.svg)|*.svg|" + Lang.T("圖片") + " (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif";

    /// <summary>儲存對話框：每種格式一行（次序同 SaveExts 一樣），FilterIndex 由 1 開始。</summary>
    public static string SaveFilter(bool vector) => vector
        ? Lang.T("設計工具專案") + " (*.rdesign)|*.rdesign|SVG (*.svg)|*.svg|PDF (*.pdf)|*.pdf|PNG (*.png)|*.png"
        : Lang.T("設計工具專案（保留圖層）") + " (*.rdesign)|*.rdesign|PNG (*.png)|*.png|JPG (*.jpg)|*.jpg|WebP (*.webp)|*.webp";
}

/// <summary>設計工具嘅 WebView2（WebView2 類型只可以喺呢個 class 出現）。</summary>
internal sealed class DesignWeb
{
    private CoreWebView2Controller _ctl = null!;
    private Control _host = null!;
    private MemoryStream? _save;
    private string? _saveTarget;                                // 頁面交返嚟嘅資料寫去邊（save-ask 揀咗）
    private bool _saveExport;
    private string? _pending;                                   // 頁面準備好就開呢個檔
    private string? _curPath;                                   // 而家開緊嘅檔案（null = 未儲存過嘅新圖）
    private readonly HashSet<string> _backed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(); // 虛擬網址 token → 真實路徑（只可以讀主程式自己交出去嘅檔案）
    private int _fileSeq;
    private static string _lastDir = "";
    /// <summary>開咗檔案（俾獨立視窗改標題）。</summary>
    public Action<string>? Opened;

    public static async Task<DesignWeb> CreateAsync(Control host, string? openPath = null)
    {
        string data = Path.Combine(AppPaths.Data, "webview", "design");
        Directory.CreateDirectory(data);
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        var w = new DesignWeb { _host = host, _pending = openPath, _ctl = await env.CreateCoreWebView2ControllerAsync(host.Handle) };
        w._ctl.DefaultBackgroundColor = Ui.Bg;
        try { w._ctl.AllowExternalDrop = true; } catch { }
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        host.VisibleChanged += (_, _) =>
        {
            try
            {
                w._ctl.IsVisible = host.Visible;
                if (!host.Visible) return;
                w._ctl.Bounds = host.ClientRectangle;
                w._ctl.NotifyParentWindowPositionChanged();
            }
            catch { }
        };
        var wv = w._ctl.CoreWebView2;
        try { wv.Profile.PreferredColorScheme = Ui.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark; } catch { }
        var st = wv.Settings;
        st.AreDevToolsEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreDefaultContextMenusEnabled = false;
        st.AreHostObjectsAllowed = false;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        string origin = "https://" + DesignLib.Host + "/";
        wv.NavigationStarting += (_, e) => { if (!(e.Uri ?? "").StartsWith(origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        wv.NewWindowRequested += (_, e) => e.Handled = true;
        // 複製圖片去系統剪貼簿要權限：只准呢頁自己用
        wv.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead && (e.Uri ?? "").StartsWith(origin, StringComparison.OrdinalIgnoreCase)) e.State = CoreWebView2PermissionState.Allow;
            else e.State = CoreWebView2PermissionState.Deny;
        };
        // 只准讀編輯器自己嘅檔案；其他網絡請求一律擋（圖片唔會離開部電腦）
        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, e) =>
        {
            string u = e.Request.Uri ?? "";
            if (u.StartsWith("data:") || u.StartsWith("blob:") || u.StartsWith("about:")) return;
            try
            {
                if (u.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Uri.UnescapeDataString(new Uri(u).AbsolutePath.TrimStart('/'));
                    if (rel == "strings.js")
                    {
                        var js = Encoding.UTF8.GetBytes(DesignLib.StringsJs());
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(js), 200, "OK", "Content-Type: text/javascript; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    if (rel.StartsWith("__file/"))
                    {
                        if (w._files.TryGetValue(rel[7..], out var fp) && File.Exists(fp))
                            e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(fp)), 200, "OK", "Content-Type: application/octet-stream\r\nCache-Control: no-store");
                        else e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                        return;
                    }
                    string root = Path.GetFullPath(DesignLib.Dir) + Path.DirectorySeparatorChar;
                    string p = Path.GetFullPath(Path.Combine(root, rel));
                    if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    {
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(p)), 200, "OK", $"Content-Type: {PdfEditLib.Mime(p)}\r\nCache-Control: no-store");
                        return;
                    }
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
            }
            catch { }
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        wv.WebMessageReceived += (_, e) =>
        {
            try
            {
                // 拖入嚟嘅檔案：頁面用 postMessageWithAdditionalObjects 交個 File 過嚟，喺呢度先攞到真實路徑
                string? dropped = null;
                try
                {
                    foreach (var o in e.AdditionalObjects)
                        if (o is CoreWebView2File f) { dropped = f.Path; break; }
                }
                catch { }
                if (dropped != null) { w.SendFile(dropped); return; }
                w.OnMessage(e.WebMessageAsJson);
            }
            catch (Exception ex) { AppPaths.LogError("設計工具訊息", ex); }
        };
        wv.ProcessFailed += (_, _) => { try { host.BeginInvoke(() => wv.Navigate(origin + "index.html")); } catch { } };
        wv.Navigate(origin + "index.html");
        return w;
    }

    private void Post(JsonObject o) { try { _ctl.CoreWebView2.PostWebMessageAsJson(o.ToJsonString()); } catch { } }

    private void OnMessage(string json)
    {
        var m = JsonNode.Parse(json) as JsonObject;
        if (m == null) return;
        switch ((string?)m["t"])
        {
            case "ready":
                _curPath = null;
                if (_pending is { } pp) { _pending = null; SendFile(pp); }
                break;
            case "opened":
                // 有 token = 主程式交出去嘅檔，冇 = 新圖 / 貼上嘅圖
                _curPath = (string?)m["token"] is { } tk && _files.TryGetValue(tk, out var op) ? op : null;
                if (_curPath != null) { DesignRecent.Add(_curPath); PostRecent(); }
                Opened?.Invoke((string?)m["name"] ?? "");
                break;
            case "closed":
                _curPath = null;
                Opened?.Invoke("");
                PostRecent();
                break;
            case "pick":
            {
                using var od = new OpenFileDialog { Filter = DesignLib.OpenFilter, InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : "" };
                if (od.ShowDialog(_host.FindForm()) == DialogResult.OK) SendFile(od.FileName);
                break;
            }
            case "recent-open":
                if ((string?)m["path"] is { } rp && DesignRecent.Contains(rp)) SendFile(rp);
                break;
            case "recent-remove":
                if ((string?)m["path"] is { } rr) { DesignRecent.Remove(rr); PostRecent(); }
                break;
            case "recent-clear":
                DesignRecent.Clear(); PostRecent();
                break;
            case "rename":
                Rename((string?)m["name"] ?? "", (string?)m["old"] ?? "");
                break;
            case "new-window":
                DesignWindow.Open(null);
                break;
            case "save-ask":
                SaveAsk((bool?)m["as"] ?? false, (bool?)m["export"] ?? false, (string?)m["prefer"] ?? "png", (string?)m["name"] ?? "image.png", (string?)m["kind"] == "vector");
                break;
            case "save-begin":
                _save = new MemoryStream();
                break;
            case "save-chunk":
                if (_save != null && (string?)m["d"] is { } d) { var b = Convert.FromBase64String(d); _save.Write(b, 0, b.Length); }
                break;
            case "save-end":
                SaveToFile();
                break;
        }
    }

    /// <summary>
    /// 決定寫去邊：儲存（唔係另存）而又有路徑、格式又啱 = 原位覆寫；否則彈對話框（揀位置同格式）。
    /// 回覆 save-target{ext, name}，頁面跟住按呢個格式編碼交返嚟；取消就回覆冇 ext。
    /// </summary>
    private void SaveAsk(bool saveAs, bool export, string prefer, string name, bool vector)
    {
        var exts = DesignLib.SaveExts(vector);
        prefer = prefer.ToLowerInvariant() == "jpeg" ? "jpg" : prefer.ToLowerInvariant();
        if (Array.IndexOf(exts, prefer) < 0) prefer = exts[1];
        string? target = null;
        if (!saveAs && !export && _curPath != null)
        {
            string cur = Path.GetExtension(_curPath).TrimStart('.').ToLowerInvariant();
            if (cur == "jpeg") cur = "jpg";
            if (cur == prefer) target = _curPath;
        }
        if (target == null)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            using var d = new SaveFileDialog
            {
                Filter = DesignLib.SaveFilter(vector), FilterIndex = Array.IndexOf(exts, prefer) + 1,
                FileName = Path.ChangeExtension(name, prefer), DefaultExt = prefer, AddExtension = true, OverwritePrompt = true,
                InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (d.ShowDialog(_host.FindForm()) != DialogResult.OK) { Post(new JsonObject { ["t"] = "save-target" }); Post(new JsonObject { ["t"] = "saved", ["ok"] = false }); return; }
            target = d.FileName;
            // 檔名嘅副檔名同揀嘅格式唔夾：跟對話框揀嘅格式
            string ext = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
            if (ext == "jpeg") ext = "jpg";
            if (Array.IndexOf(exts, ext) < 0)
            {
                ext = exts[Math.Clamp(d.FilterIndex - 1, 0, exts.Length - 1)];
                target += "." + ext;
            }
        }
        _saveTarget = target;
        _saveExport = export;
        string e2 = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
        Post(new JsonObject { ["t"] = "save-target", ["ext"] = e2 == "jpeg" ? "jpg" : e2, ["name"] = Path.GetFileName(target) });
    }

    private void SaveToFile()
    {
        var data = _save;
        string? target = _saveTarget;
        _save = null; _saveTarget = null;
        if (data == null || target == null) return;
        try
        {
            Backup(target);
            // 先寫去暫存檔再換：寫到一半出事都唔會整壞原檔
            string tmp = target + ".rabi-tmp";
            File.WriteAllBytes(tmp, data.ToArray());
            File.Move(tmp, target, true);
            _lastDir = Path.GetDirectoryName(target) ?? "";
            if (!_saveExport)
            {
                _curPath = target;
                DesignRecent.Add(target); PostRecent();
                Opened?.Invoke(Path.GetFileName(target));
            }
            Post(new JsonObject { ["t"] = "saved", ["ok"] = true, ["export"] = _saveExport, ["name"] = Path.GetFileName(target) });
        }
        catch (Exception ex)
        {
            AppPaths.LogError("儲存設計", ex);
            Post(new JsonObject { ["t"] = "saved", ["ok"] = false, ["err"] = ex.Message });
        }
    }

    /// <summary>第一次覆寫一個已有嘅檔案之前，先抄一份去 data\design\backup（只留最新 20 個）。</summary>
    private void Backup(string target)
    {
        try
        {
            if (!File.Exists(target) || !_backed.Add(target)) return;
            string dir = Path.Combine(AppPaths.Data, "design", "backup");
            Directory.CreateDirectory(dir);
            File.Copy(target, Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + Path.GetFileName(target)), true);
            var old = new DirectoryInfo(dir).GetFiles();
            Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 20; i < old.Length; i++) try { old[i].Delete(); } catch { }
        }
        catch (Exception ex) { AppPaths.LogError("設計備份", ex); }
    }

    /// <summary>撳檔名改名：已經儲存過嘅檔案連磁碟上面個檔一齊改名，未儲存過就只係記住個名。</summary>
    private void Rename(string name, string old)
    {
        void Fail(string err) => Post(new JsonObject { ["t"] = "renamed", ["ok"] = false, ["name"] = _curPath != null ? Path.GetFileName(_curPath) : old, ["err"] = err });
        try
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0 || Path.GetExtension(name).Length < 2) { Fail(""); return; }
            if (_curPath != null)
            {
                if (!Path.GetExtension(name).Equals(Path.GetExtension(_curPath), StringComparison.OrdinalIgnoreCase)) { Fail(""); return; }
                string target = Path.Combine(Path.GetDirectoryName(_curPath) ?? "", name);
                if (!target.Equals(_curPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target)) { Fail(Lang.F("已經有一個叫「{0}」嘅檔案，改唔到名。", name)); return; }
                    if (!File.Exists(_curPath)) { Fail(Lang.F("搵唔到檔案：{0}", _curPath)); return; }
                    File.Move(_curPath, target);
                    DesignRecent.Remove(_curPath); DesignRecent.Add(target);
                    _curPath = target;
                }
            }
            Post(new JsonObject { ["t"] = "renamed", ["ok"] = true, ["name"] = name });
            Opened?.Invoke(name);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("改檔名", ex);
            Fail(Lang.T("改名失敗：") + ex.Message);
        }
    }

    private void PostRecent()
    {
        var o = new JsonObject { ["t"] = "recent" };
        o["list"] = JsonNode.Parse(JsonSerializer.Serialize(DesignRecent.ForJs()));
        Post(o);
    }

    /// <summary>將一個檔案交俾頁面開（頁面真係開咗先記入歷史紀錄）。</summary>
    public void SendFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                DesignRecent.Remove(path); PostRecent();
                Post(new JsonObject { ["t"] = "toast", ["text"] = Lang.F("搵唔到檔案：{0}", path) });
                return;
            }
            if (!DesignLib.Supported(path))
            {
                Post(new JsonObject { ["t"] = "toast", ["text"] = Lang.T("呢種檔案暫時未支援。") });
                return;
            }
            string token = (++_fileSeq).ToString();
            _files[token] = path;
            _lastDir = Path.GetDirectoryName(path) ?? "";
            Post(new JsonObject { ["t"] = "file", ["token"] = token, ["url"] = "https://" + DesignLib.Host + "/__file/" + token, ["name"] = Path.GetFileName(path) });
        }
        catch (Exception ex) { AppPaths.LogError("開設計檔", ex); }
    }

    public void Close() { try { _ctl.Close(); } catch { } }
}

/// <summary>「設計工具」頁。</summary>
internal sealed class DesignPage : Panel
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Label _msg = new() { Dock = DockStyle.Fill, ForeColor = Ui.TextDim, Font = Ui.F(10.5f), TextAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Bg };
    private object? _web;   // DesignWeb
    private bool _started;
    private string? _open;
    /// <summary>開咗檔案（檔名）。</summary>
    public event Action<string>? Opened;

    /// <summary>主視窗嗰頁：開住圖片時側欄自動摺起（俾多啲位編輯），返首頁就還原。</summary>
    public static Control Build()
    {
        var p = new DesignPage(null);
        p.Opened += n => { try { MainForm.Instance?.SetDocEditing(n.Length > 0); } catch { } };
        return p;
    }

    public DesignPage(string? openPath)
    {
        _open = openPath;
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        Controls.Add(_host);
        _host.Controls.Add(_msg);
        // 第一次顯示先開瀏覽器（唔好拖慢開程式）
        VisibleChanged += (_, _) => { if (Visible && !_started) { _started = true; _ = StartAsync(); } };
        Disposed += (_, _) => { if (_web != null) CloseWeb(); };
    }

    private async Task StartAsync()
    {
        try
        {
            if (!Mail.WebView2Lib.Prepare()) { _msg.Text = Lang.T("要用 Microsoft Edge WebView2 Runtime 先用到（Windows 10 / 11 通常已經有）。"); return; }
            if (!DesignLib.Prepare()) { _msg.Text = Lang.T("載入失敗：") + "design"; return; }
            _web = await CreateWeb();
            _msg.Visible = false;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("開設計工具", ex);
            _msg.Text = Lang.T("載入失敗：") + ex.Message;
            _started = false;
        }
    }

    // WebView2 類型只喺呢幾個方法出現（唔好 inline，等 DLL 準備好先載入）
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<object> CreateWeb()
    {
        var w = await DesignWeb.CreateAsync(_host, _open);
        _open = null;
        w.Opened = n => Opened?.Invoke(n);
        return w;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private void CloseWeb() => ((DesignWeb)_web!).Close();
}

/// <summary>設計工具歷史紀錄（data\design\recent.json，最多 15 個；只記路徑）。</summary>
internal static class DesignRecent
{
    private static string FilePath => Path.Combine(AppPaths.Data, "design", "recent.json");
    private static List<string>? _list;
    private static List<string> List
    {
        get
        {
            if (_list != null) return _list;
            try { _list = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) : null; } catch { }
            return _list ??= new();
        }
    }
    private static void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(List)); }
        catch (Exception ex) { AppPaths.LogError("設計工具歷史紀錄", ex); }
    }
    public static bool Contains(string path) => List.Exists(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
    public static void Add(string path)
    {
        List.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        List.Insert(0, path);
        if (List.Count > 15) List.RemoveRange(15, List.Count - 15);
        Save();
    }
    public static void Remove(string path) { if (List.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0) Save(); }
    public static void Clear() { List.Clear(); Save(); }
    public static List<Dictionary<string, string>> ForJs()
    {
        var r = new List<Dictionary<string, string>>();
        foreach (var p in List) r.Add(new() { ["path"] = p, ["name"] = Path.GetFileName(p), ["dir"] = Path.GetDirectoryName(p) ?? "" });
        return r;
    }
}

/// <summary>設計工具獨立視窗（撳「開新視窗」）。可以同時開幾個。</summary>
internal sealed class DesignWindow : Form
{
    public static void Open(string? path)
    {
        var f = new DesignWindow(path);
        DocShell.WindowOpened(f);
        f.Show();
        f.Activate();
    }

    private DesignWindow(string? path)
    {
        Text = Lang.T("設計工具") + " - " + Program.AppName;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? Icon; } catch { }
        BackColor = Ui.Bg;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(Ui.S(1320), wa.Width - Ui.S(60)), Math.Min(Ui.S(920), wa.Height - Ui.S(60)));
        MinimumSize = new Size(Ui.S(900), Ui.S(560));
        var page = new DesignPage(path);
        page.Opened += n => { try { Text = n.Length > 0 ? n + " - " + Lang.T("設計工具") : Lang.T("設計工具") + " - " + Program.AppName; } catch { } };
        Controls.Add(page);
        HandleCreated += (_, _) => DarkTheme.DarkTitleBar(this);
        FormClosed += (_, _) => page.Dispose();
    }
}
