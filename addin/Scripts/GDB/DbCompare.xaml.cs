using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 数据库比对窗口：选择 A/B 两版本数据库，比对图层名称、图层范围、图斑几何与属性差异。
    /// 逻辑在 DbCompareCore（纯逻辑类），本类只负责界面与调度。
    /// </summary>
    public partial class DbCompare : ProWindow
    {
        private CancellationTokenSource _cts;
        private DbCompareHelp _help;

        public DbCompare()
        {
            InitializeComponent();
        }

        // ---------------- 路径选择 ----------------

        /// <summary>选择文件地理数据库（在目录浏览中选 .gdb 文件夹）</summary>
        private string PickGeodatabase(string title)
        {
            var dlg = new OpenItemDialog
            {
                Title = title,
                MultiSelect = false,
                Filter = ItemFilters.Geodatabases
            };
            return dlg.ShowDialog() == true && dlg.Items.Any() ? dlg.Items.First().Path : null;
        }

        private void BrowseGdbA_Click(object sender, RoutedEventArgs e)
        {
            string p = PickGeodatabase("选择 A 版本数据库（.gdb）");
            if (p != null) TextGdbA.Text = p;
        }

        private void BrowseGdbB_Click(object sender, RoutedEventArgs e)
        {
            string p = PickGeodatabase("选择 B 版本数据库（.gdb）");
            if (p != null) TextGdbB.Text = p;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            string p = PickGeodatabase("选择差异图斑输出数据库（.gdb）");
            if (p != null) TextOutputGdb.Text = p;
        }

        // ---------------- 执行 ----------------

        /// <summary>开始比对（校验 → 后台调度核心类 → 异常兜底）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            string gdbA = TextGdbA.Text?.Trim();
            string gdbB = TextGdbB.Text?.Trim();
            string outputGdb = TextOutputGdb.Text?.Trim();
            string idField = string.IsNullOrWhiteSpace(TextIdField.Text) ? "BSM" : TextIdField.Text.Trim();

            // 校验：两库必须存在且为 .gdb
            if (!IsValidGdb(gdbA, "A 版本数据库", out string errA)) { MessageBox.Show(errA); return; }
            if (!IsValidGdb(gdbB, "B 版本数据库", out string errB)) { MessageBox.Show(errB); return; }
            if (string.Equals(gdbA, gdbB, StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show("A、B 不能是同一个数据库。"); return; }
            if (!string.IsNullOrWhiteSpace(outputGdb) && !IsValidGdb(outputGdb, "输出数据库", out string errOut))
            { MessageBox.Show(errOut); return; }

            List<string> layerFilter = ParseLayerNames(TextLayers.Text);

            _cts = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                await QueuedTask.Run(async () =>
                {
                    var core = new DbCompareCore
                    {
                        Progress = (p, m) => SetProgress(p, m),
                        Log = m => Log(m),
                        LogWarning = m => Log("[警告] " + m)
                    };
                    await core.Run(gdbA, gdbB, idField, layerFilter,
                        string.IsNullOrWhiteSpace(outputGdb) ? null : outputGdb,
                        _cts.Token);
                });
                Log("数据库比对执行完成。");
            }
            catch (OperationCanceledException)
            {
                Log("已取消比对。");
            }
            catch (Exception ex)
            {
                Log($"比对失败：{ex.Message}");
                MessageBox.Show(ex.Message, "数据库比对");
            }
            finally
            {
                SetRunning(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>取消正在进行的比对</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        /// <summary>校验路径为已存在的 .gdb 目录</summary>
        private static bool IsValidGdb(string path, string label, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            { error = $"{label}不存在，请检查路径。"; return false; }
            if (!path.ToLowerInvariant().EndsWith(".gdb"))
            { error = $"{label}仅支持 .gdb（差异落库需要 Geodatabase API 写入能力）。"; return false; }
            return true;
        }

        /// <summary>解析图层名：中英文逗号、去空、去重；空文本返回 null（表示全库比对）</summary>
        private static List<string> ParseLayerNames(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Replace("，", ",")
                       .Split(',')
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开使用说明窗口（单例）</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new DbCompareHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            BtnCancel.IsEnabled = running;
            TextGdbA.IsEnabled = !running;
            TextGdbB.IsEnabled = !running;
            TextLayers.IsEnabled = !running;
            TextIdField.IsEnabled = !running;
            TextOutputGdb.IsEnabled = !running;
        }

        /// <summary>更新进度并写普通日志</summary>
        private void SetProgress(double percent, string message)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.Value = percent;
                Log(message);
            });
        }

        /// <summary>线程安全写日志</summary>
        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TextLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TextLog.ScrollToEnd();
            });
        }
    }
}
