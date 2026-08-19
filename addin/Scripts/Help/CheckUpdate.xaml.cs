using ArcGIS.Desktop.Framework.Controls;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace GHBoxAddIn.Scripts.Help
{
    /// <summary>
    /// 检查更新：比对 GitHub 仓库最新版本与本机当前安装版本。
    /// 版本唯一来源为仓库 version.txt，由 CI 构建时注入 DLL 版本并打 GitHub Release tag（v&lt;版本&gt;）。
    ///
    /// 设计说明：
    /// 1. 当前版本取程序集 AssemblyVersion（build_all.ps1 用 -p:Version 注入，SDK 自动同步到 AssemblyVersion）
    /// 2. 最新版本调 GitHub API https://api.github.com/repos/cliii-one/GHBOX/releases/latest 的 tag_name
    /// 3. 有新版 → 提示并提供「前往 GitHub 下载」，用默认浏览器打开 Releases 下载页
    /// 4. 网络/解析失败 → 回显日志，不误导为"已是最新"
    /// 5. 网络请求用 HttpClient 异步，await 后自动回主线程更新 UI（不涉 ArcGIS Core 库，无需 QueuedTask）
    /// </summary>
    public partial class CheckUpdate : ProWindow
    {
        private const string ToolLabel = "检查更新";
        // GitHub 仓库下载页：GitHub 会把 /releases/latest 302 到最新版，无需随版本改代码
        private const string DownloadUrl = "https://github.com/cliii-one/GHBOX/releases/latest";
        // GitHub Releases API：返回最新 release JSON，含 tag_name（形如 v1.0.4）
        private const string ReleasesApiUrl = "https://api.github.com/repos/cliii-one/GHBOX/releases/latest";

        public CheckUpdate()
        {
            InitializeComponent();
        }

        // ---------------- 初始化 ----------------

        /// <summary>窗口加载即自动检查一次（控件未就绪时 TextCurrentVersion 判空）</summary>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 先显示当前版本（不依赖网络）
            TextCurrentVersion.Text = GetCurrentVersion();
            await CheckForUpdateAsync();
        }

        // ---------------- 界面事件 ----------------

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync();
        }

        /// <summary>有新版时跳转 GitHub 下载页；.NET Core 下必须 UseShellExecute=true 才能用默认浏览器</summary>
        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"无法打开浏览器：{ex.Message}");
            }
        }

        // ---------------- 核心逻辑 ----------------

        /// <summary>获取本机当前安装版本（程序集 AssemblyVersion，由构建注入）</summary>
        private static string GetCurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "未知" : v.ToString(3); // 仅主.次.修订，忽略内部号
        }

        /// <summary>拉取 GitHub 最新版本并比对，更新 UI</summary>
        private async Task CheckForUpdateAsync()
        {
            SetBusy(true);
            AppendLog($"开始检查更新（当前版本 {GetCurrentVersion()}）…");

            try
            {
                string latestTag = await FetchLatestTagAsync();
                // 解析最新版本号（去前导 v）
                string latestRaw = latestTag?.TrimStart('v', 'V') ?? "";
                bool parsed = Version.TryParse(latestRaw, out Version latest);

                TextLatestVersion.Text = parsed ? latest.ToString(3) : (latestTag ?? "无法获取");

                if (!parsed)
                {
                    // API 能通但版本号异常：保守处理，不宣称已是最新
                    TextStatus.Text = "未能识别最新版本号";
                    BoxHint.Visibility = Visibility.Collapsed;
                    BtnDownload.IsEnabled = false;
                    AppendLog($"GitHub 返回的版本号无法解析：{latestTag}");
                    return;
                }

                Version current = Assembly.GetExecutingAssembly().GetName().Version;
                if (current != null && latest > current)
                {
                    TextStatus.Text = "发现新版本";
                    BoxHint.Visibility = Visibility.Visible;
                    TextHint.Text = $"GitHub 上有新版本 v{latest}，当前安装为 v{current}。\n点击右侧「前往 GitHub 下载」打开下载页，选择与你的 ArcGIS Pro 版本匹配的安装包。";
                    BtnDownload.IsEnabled = true;
                    AppendLog($"发现新版本：v{latest}（当前 v{current}），可前往 GitHub 下载更新。");
                }
                else
                {
                    TextStatus.Text = "已是最新版本";
                    BoxHint.Visibility = Visibility.Collapsed;
                    BtnDownload.IsEnabled = false;
                    AppendLog("当前已是最新版本。");
                }
            }
            catch (Exception ex)
            {
                // 网络/超时/权限异常统一在此回显，不误报"已是最新"
                TextStatus.Text = "检查失败";
                BoxHint.Visibility = Visibility.Collapsed;
                BtnDownload.IsEnabled = false;
                AppendLog($"检查更新失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>请求 GitHub Releases API 取最新 release 的 tag_name（异常上抛，由调用方处理）</summary>
        private static async Task<string> FetchLatestTagAsync()
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            // GitHub API 要求 UA，缺省会返回 403
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GHBox-AddIn");

            string json = await client.GetStringAsync(ReleasesApiUrl);
            // 取 "tag_name":"v1.0.4" —— 用最简解析，避免引入 JSON 库
            const string key = "\"tag_name\"";
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        // ---------------- UI 辅助 ----------------

        private void SetBusy(bool busy)
        {
            BtnCheck.IsEnabled = !busy;
        }

        private void AppendLog(string line)
        {
            TextLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
    }
}