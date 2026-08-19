using ArcGIS.Core.Data;
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
    /// 数据库拆分：按 XDM（县代码）+ XMC（县名称）将省级数据库拆分为县级 GDB。
    /// 业务逻辑与《数据库拆分.pyt》的 SplitByCounty 保持一致：
    /// 1. 枚举库内要素类（跳过 GDB_ 系统表），要求含 XDM/XMC 字段
    /// 2. 收集两库唯一 (XDM, XMC) 组合，每个县创建 XDM+XMC.gdb
    /// 3. 逐县逐图层 Select(XDM = '代码') 导出，条数为 0 时删除空图层
    /// 4. 命名格式①原图层名 / ②县名+移除前缀后的图层名
    /// 5. 单图层导出失败 → 记错误继续；不中断整体
    /// </summary>
    public partial class DbSplit : ProWindow
    {
        private const string ToolLabel = "数据库拆分";

        private CancellationTokenSource _cts;
        private DbSplitHelp _help;

        public DbSplit()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件 ----------------

        /// <summary>选择输入省级数据库</summary>
        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择输入省级数据库",
                MultiSelect = false,
                Filter = ItemFilters.Geodatabases
            };
            if (dlg.ShowDialog() == true && dlg.Items.Any())
                TextInputGdb.Text = dlg.Items.First().Path;
        }

        /// <summary>选择输出根文件夹</summary>
        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择输出根文件夹",
                MultiSelect = false,
                Filter = ItemFilters.Folders
            };
            if (dlg.ShowDialog() == true && dlg.Items.Any())
                TextOutputFolder.Text = dlg.Items.First().Path;
        }

        /// <summary>开始拆分（异步，不阻塞界面）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 参数校验（对应 execute 开头校验）
                string inputGdb = TextInputGdb.Text?.Trim().TrimEnd('\\');
                if (string.IsNullOrWhiteSpace(inputGdb) ||
                    !inputGdb.ToLowerInvariant().EndsWith(".gdb") || !Directory.Exists(inputGdb))
                {
                    MessageBox.Show("错误：输入数据库必须是已存在的 .gdb。");
                    return;
                }

                string outputFolder = TextOutputFolder.Text?.Trim();
                if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
                {
                    MessageBox.Show("错误：输出根文件夹不存在。");
                    return;
                }

                string prefix = TextPrefix.Text?.Trim() ?? string.Empty;
                bool keepOriginal = RadioKeepOriginal.IsChecked == true;

                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunSplitAsync(inputGdb, outputFolder, prefix, keepOriginal, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消拆分。");
            }
            catch (Exception ex)
            {
                Log($"执行失败：{ex.Message}");
                MessageBox.Show(ex.Message, ToolLabel);
            }
            finally
            {
                SetRunning(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>取消正在进行的拆分</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开数据库拆分工具的使用说明窗口（单例）</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new DbSplitHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 拆分主流程（对应 execute）：收集县 → 逐县建库导出。
        /// </summary>
        private async Task RunSplitAsync(
            string inputGdb, string outputFolder, string prefix, bool keepOriginal, CancellationToken ct)
        {
            // 1. 枚举要素类（对应 arcpy.ListFeatureClasses + 字段过滤）
            List<string> fcs = await QueuedTask.Run(() => ListFeatureClasses(inputGdb));
            if (fcs.Count == 0)
            {
                Log("输入数据库中未找到任何要素类！");
                return;
            }
            Log($"找到 {fcs.Count} 个要素类：{string.Join(", ", fcs)}");
            Log("拆分依据：字段 XDM（县代码）和 XMC（县名称）");

            // 2. 找出含 XDM/XMC 字段的要素类并收集唯一县组合（对应 counties 收集）
            var usable = new List<string>();
            var counties = new SortedSet<CountyInfo>(Comparer<CountyInfo>.Create((a, b) => string.CompareOrdinal(a.Code, b.Code)));
            foreach (string fc in fcs)
            {
                bool hasFields = await QueuedTask.Run(() => HasXdmXmcFields(inputGdb, fc));
                if (!hasFields)
                {
                    LogWarning($"要素类 {fc} 缺少 XDM 或 XMC 字段，已跳过");
                    continue;
                }
                usable.Add(fc);

                foreach (CountyInfo c in await QueuedTask.Run(() => CollectCounties(inputGdb, fc)))
                    counties.Add(c);
            }

            if (counties.Count == 0)
            {
                Log("未找到任何有效的县代码/名称组合！");
                return;
            }
            Log($"共发现 {counties.Count} 个县：{string.Join(", ", counties.Select(c => $"{c.Code}({c.Name})"))}");

            // 3. 预处理基名：格式②移除前缀（对应 base_names 计算）
            var baseNames = new Dictionary<string, string>();
            foreach (string fc in usable)
            {
                string baseName = (!keepOriginal && prefix.Length > 0 && fc.StartsWith(prefix, StringComparison.Ordinal))
                    ? fc.Substring(prefix.Length)
                    : fc;
                baseNames[fc] = baseName;
                Log($"原始图层 {fc} -> 基名: '{baseName}'");
            }

            // 4. 逐县建库导出（对应 counties 主循环）
            int total = counties.Count;
            int idx = 0;
            foreach (CountyInfo county in counties)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                string gdbName = $"{county.Code}{county.Name}.gdb";
                string targetGdb = Path.Combine(outputFolder, gdbName);
                SetProgress(100.0 * (idx - 1) / total, $"[{idx}/{total}] 处理 {county.Name} ({county.Code})");

                if (!Directory.Exists(targetGdb))
                {
                    await GpHelper.RunToolAsync("management.CreateFileGDB",
                        Geoprocessing.MakeValueArray(outputFolder, gdbName), ct);
                    Log($"  创建数据库: {targetGdb}");
                }

                foreach (string fc in usable)
                {
                    ct.ThrowIfCancellationRequested();
                    string targetName = keepOriginal ? fc : $"{county.Name}{baseNames[fc]}";
                    string targetFc = Path.Combine(targetGdb, targetName);
                    string srcFc = Path.Combine(inputGdb, fc);

                    try
                    {
                        await GpHelper.RunToolAsync("analysis.Select",
                            Geoprocessing.MakeValueArray(srcFc, targetFc, $"XDM = '{county.Code}'"), ct);
                        long count = await GpHelper.GetCountAsync(targetFc, ct);
                        if (count > 0)
                        {
                            Log($"  导出 {targetName} ({count} 个要素)");
                        }
                        else
                        {
                            await GpHelper.RunToolAsync("management.Delete",
                                Geoprocessing.MakeValueArray(targetFc), ct);
                            LogWarning($"  {county.Name} 在 {fc} 中无要素，未创建空图层");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogError($"  导出失败 {fc} -> {targetName}: {ex.Message}");
                    }
                }
            }

            Log("");
            Log("县级拆分完成！");
            // 全部县处理完毕，进度条置满（循环内最后一次进度只到 (total-1)/total）
            SetProgress(100, "县级拆分完成");
        }

        // ---------------- 数据访问 ----------------

        /// <summary>县信息（XDM 代码 + XMC 名称）</summary>
        private sealed class CountyInfo
        {
            public string Code { get; set; }
            public string Name { get; set; }
        }

        /// <summary>
        /// 枚举库内顶层要素类（对应 arcpy.ListFeatureClasses，跳过 GDB_ 系统表）。
        /// </summary>
        private static List<string> ListFeatureClasses(string gdbPath)
        {
            var result = new List<string>();
            using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
            {
                if (gdb == null) return result;

                foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                    if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                        result.Add(def.GetName());
            }
            return result;
        }

        /// <summary>判断要素类是否同时含 XDM/XMC 字段</summary>
        private static bool HasXdmXmcFields(string gdbPath, string fcName)
        {
            using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
            {
                if (gdb == null) return false;
                using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                {
                    var names = new HashSet<string>(
                        fc.GetDefinition().GetFields().Select(f => f.Name),
                        StringComparer.OrdinalIgnoreCase);
                    return names.Contains("XDM") && names.Contains("XMC");
                }
            }
        }

        /// <summary>收集要素类中的唯一县组合（对应 counties.add((xdm, xmc))）</summary>
        private static List<CountyInfo> CollectCounties(string gdbPath, string fcName)
        {
            var list = new List<CountyInfo>();
            using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
            {
                if (gdb == null) return list;
                using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    using (RowCursor cursor = fc.Search())
                    {
                        while (cursor.MoveNext())
                        {
                            string xdm = cursor.Current["XDM"]?.ToString();
                            string xmc = cursor.Current["XMC"]?.ToString();
                            if (string.IsNullOrEmpty(xdm) || string.IsNullOrEmpty(xmc)) continue;
                            if (!seen.Add(xdm)) continue;   // 同一代码只留首个县名（对应 set 去重）
                            list.Add(new CountyInfo { Code = xdm, Name = xmc });
                        }
                    }
                }
            }
            return list;
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputGdb.IsEnabled = !running;
            TextOutputFolder.IsEnabled = !running;
            TextPrefix.IsEnabled = !running;
            RadioKeepOriginal.IsEnabled = !running;
            RadioCountyPrefix.IsEnabled = !running;
            BtnCancel.IsEnabled = running;
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

        /// <summary>线程安全写普通日志</summary>
        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TextLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TextLog.ScrollToEnd();
            });
        }

        /// <summary>线程安全写警告日志（前缀区分）</summary>
        private void LogWarning(string message) => Log("[警告] " + message);

        /// <summary>线程安全写错误日志（前缀区分）</summary>
        private void LogError(string message) => Log("[错误] " + message);
    }
}
