using ArcGIS.Core.Data;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 数据库合并：将多个 GDB/MDB 中的同名图层合并到一个输出数据库。
    /// 业务逻辑与 `toolbox/数据库合并.pyt` 基准完全一致：
    /// 1. 枚举输入文件夹下的 .gdb/.mdb（不含输出库自身语义，按原工具未做排除，保持一致）
    /// 2. 图层名支持逗号分隔或 ALL（收集所有库中出现过的全部要素类名）
    /// 3. 逐图层合并：某库缺该图层 → 警告跳过；所有库都没有 → 跳过该图层
    /// 4. 输出库已有同名图层 → 先删除再合并
    /// 5. 合并后核对条数：不一致仅警告，不中断
    /// 6. 单图层异常 → 写异常日志文件，继续处理下一个图层
    /// </summary>
    public partial class LayerMerge : ProWindow
    {
        private const string ToolLabel = "数据库合并";

        private CancellationTokenSource _cts;
        private string _exceptionLogPath;
        private readonly StringBuilder _exceptionLog = new StringBuilder();
        private MergeHelp _help;

        public LayerMerge()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件 ----------------

        /// <summary>选择输入文件夹</summary>
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = BrowseFolder("选择需要合并的文件夹");
            if (folder != null) TextFolderPath.Text = folder;
        }

        /// <summary>选择输出数据库（GDB/MDB）</summary>
        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择输出数据库",
                MultiSelect = false,
                Filter = ItemFilters.Geodatabases     // GDB/MDB 等数据库
            };
            if (dlg.ShowDialog() == true && dlg.Items.Any())
            {
                TextOutputWorkspace.Text = dlg.Items.First().Path;
            }
        }

        /// <summary>开始合并（异步，不阻塞界面）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 收集参数（对应 .pyt updateMessages 的校验）
                string inputFolder = TextFolderPath.Text?.Trim();
                string outputWorkspace = TextOutputWorkspace.Text?.Trim();
                string layerNamesText = TextLayerNames.Text?.Trim();

                if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                {
                    MessageBox.Show("输入的需要合并的文件夹不存在，请检查路径是否正确。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(outputWorkspace) || !Directory.Exists(outputWorkspace))
                {
                    MessageBox.Show("输出数据库不存在，请先创建好 GDB 或 MDB。");
                    return;
                }
                if (!IsSupportedWorkspace(outputWorkspace))
                {
                    MessageBox.Show("输出数据库仅支持 .gdb 或 .mdb。");
                    return;
                }
                List<string> layerNames = ParseLayerNames(layerNamesText);
                if (layerNames.Count == 0)
                {
                    MessageBox.Show("请输入要合并的图层名称，多个图层用英文逗号分隔，或输入 ALL。");
                    return;
                }

                // 2. 枚举输入数据库（对应 _list_merge_workspaces）
                List<string> workspaces = ListMergeWorkspaces(inputFolder);
                if (workspaces.Count == 0)
                {
                    MessageBox.Show("在输入文件夹下未找到任何 .gdb 或 .mdb 数据库。");
                    return;
                }

                // 3. ALL → 收集全部要素类名（对应 _collect_all_feature_class_names）
                if (layerNames.Count == 1 && layerNames[0] == "ALL")
                {
                    layerNames = await CollectAllFeatureClassNamesAsync(workspaces);
                    if (layerNames.Count == 0)
                    {
                        MessageBox.Show("输入文件夹下未找到任何可合并的图层。");
                        return;
                    }
                }

                _cts = new CancellationTokenSource();
                SetRunning(true);

                // 4. 主流程（对应 execute）
                await RunMergeAsync(workspaces, outputWorkspace, layerNames, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消合并。");
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

        /// <summary>取消正在进行的合并</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开数据库合并工具的使用说明窗口</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new MergeHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 合并主流程：逐图层处理，单图层失败不中断整体（对应 .pyt execute）。
        /// </summary>
        private async Task RunMergeAsync(
            List<string> workspaces, string outputWorkspace, List<string> layerNames, CancellationToken ct)
        {
            // 异常日志放在输出数据库同级目录（对应 _build_exception_log_path）
            _exceptionLogPath = BuildExceptionLogPath(
                Path.GetDirectoryName(outputWorkspace), ToolLabel);

            Log("####### 数据库合并开始 #######");
            Log($"输入数据库文件夹：{workspaces[0]} 的同级目录已枚举 {workspaces.Count} 个库" );
            Log($"输出数据库：{outputWorkspace}");
            Log($"共发现 {workspaces.Count} 个输入数据库。");
            Log($"本次需要合并的图层：{string.Join("，", layerNames)}");

            int successCount = 0;
            var failedLayers = new List<string>();

            foreach (string layerName in layerNames)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    SetProgress(100.0 * (successCount + failedLayers.Count) / layerNames.Count,
                        $"开始合并图层：{layerName}");
                    await MergeSingleLayerAsync(workspaces, outputWorkspace, layerName, ct);
                    successCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedLayers.Add(layerName);
                    RecordLayerException(layerName, ex);
                }
            }

            // 运行汇总（对应 _log_run_summary）
            Log("####### 运行汇总 #######");
            Log($"成功图层：{successCount}");
            Log($"失败图层：{failedLayers.Count}");
            if (failedLayers.Count > 0)
            {
                FlushExceptionLog();
                Log($"失败图层：{string.Join("，", failedLayers)}");
                Log($"异常日志已保存至：{_exceptionLogPath}");
            }
            Log("数据库合并执行完成。");
        }

        /// <summary>
        /// 合并单个图层（对应 _merge_single_layer）：
        /// 逐库查找同名图层并统计条数 → 全部缺失则跳过 → 删除输出旧图层 → Merge → 条数核对。
        /// </summary>
        private async Task MergeSingleLayerAsync(
            List<string> workspaces, string outputWorkspace, string layerName, CancellationToken ct)
        {
            Log($"####### 开始合并图层：{layerName} #######");

            var inputs = new List<string>();
            var inputCounts = new List<(string WorkspaceName, string FcName, long Count)>();

            foreach (string ws in workspaces)
            {
                ct.ThrowIfCancellationRequested();
                string fcPath = await FindFeatureClassAsync(ws, layerName);
                string wsName = Path.GetFileName(ws.TrimEnd('\\'));

                if (fcPath == null)
                {
                    LogWarning($"数据库 {wsName} 中未找到图层：{layerName}");
                    continue;
                }
                long count = await GpHelper.GetCountAsync(fcPath, ct);
                inputs.Add(fcPath);
                inputCounts.Add((wsName, Path.GetFileName(fcPath), count));
                Log($"数据库 {wsName} 中图层 {layerName} 条数：{count}");
            }

            if (inputs.Count == 0)
            {
                LogWarning($"所有输入数据库中都未找到图层：{layerName}");
                return;
            }

            // 输出库已有同名图层 → 先删除（对应 _build_output_feature_class_path + Delete）
            string outputFc = Path.Combine(outputWorkspace, layerName);
            if (Directory.Exists(outputWorkspace) && await GpHelper.ExistsDatasetAsync(outputFc))
            {
                LogWarning($"输出数据库中已存在同名图层，已先删除：{outputFc}");
                await GpHelper.RunToolAsync("management.Delete", Geoprocessing.MakeValueArray(outputFc), ct);
            }

            // 合并（对应 arcpy.management.Merge）
            await GpHelper.RunToolAsync("management.Merge",
                Geoprocessing.MakeValueArray(string.Join(";", inputs), outputFc), ct);

            // 条数核对（预期为各库条数之和；不一致仅警告，不中断 —— 与 .pyt 一致）
            long mergedCount = await GpHelper.GetCountAsync(outputFc, ct);
            long expectedCount = inputCounts.Sum(c => c.Count);
            string countExpression = string.Join(" + ", inputCounts.Select(c => c.Count.ToString()));
            Log($"图层 {layerName} 合并后输出：{outputFc}");
            Log($"图层 {layerName} 条数核对：{countExpression} = {mergedCount}");

            if (expectedCount != mergedCount)
                LogWarning($"图层 {layerName} 条数核对不一致：预期 {expectedCount}，实际 {mergedCount}。");
            else
                Log($"图层 {layerName} 条数核对通过。");
        }

        // ---------------- 工具方法（对应 .pyt 各私有方法） ----------------

        /// <summary>输出数据库仅支持 .gdb/.mdb（对应 _is_supported_workspace）</summary>
        private static bool IsSupportedWorkspace(string path)
        {
            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".gdb") || lower.EndsWith(".mdb");
        }

        /// <summary>枚举文件夹下所有 .gdb/.mdb 子目录（对应 _list_merge_workspaces）</summary>
        private static List<string> ListMergeWorkspaces(string inputFolder)
        {
            return Directory.EnumerateDirectories(inputFolder)
                .Where(IsSupportedWorkspace)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 解析图层名：中英文逗号分隔、去重（不区分大小写）、ALL 返回 ["ALL"]（对应 _parse_layer_names）。
        /// </summary>
        private static List<string> ParseLayerNames(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return names;

            foreach (string item in text.Replace("，", ",").Split(','))
            {
                string clean = item.Trim();
                if (clean.Length == 0) continue;
                if (clean.ToUpperInvariant() == "ALL") return new List<string> { "ALL" };
                if (!names.Any(n => n.ToUpperInvariant() == clean.ToUpperInvariant()))
                    names.Add(clean);
            }
            return names;
        }

        /// <summary>
        /// 收集所有库中全部要素类名（对应 _collect_all_feature_class_names，arcpy.da.Walk 等价实现）。
        /// </summary>
        private static async Task<List<string>> CollectAllFeatureClassNamesAsync(List<string> workspaces)
        {
            var collected = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string ws in workspaces)
            {
                var names = await QueuedTask.Run(() =>
                {
                    var list = new List<string>();
                    using (var gdb = GpHelper.OpenGeodatabase(ws))
                    {
                        if (gdb == null) return list;
                        // 顶层 + 要素数据集内的要素类
                        foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                            list.Add(def.GetName());
                    }
                    return list;
                });

                foreach (string name in names)
                {
                    if (seen.Add(name)) collected.Add(name);
                }
            }
            return collected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 在库中按名称查找要素类（大小写不敏感，含要素数据集内；对应 _find_feature_class_by_name_in_workspace）。
        /// </summary>
        private static async Task<string> FindFeatureClassAsync(string workspace, string targetName)
        {
            return await QueuedTask.Run(() =>
            {
                // .mdb 通过 Geodatabase API 打不开时，退化为 GP 工具路径探测
                using (var gdb = GpHelper.OpenGeodatabase(workspace))
                {
                    if (gdb == null) return null;

                    foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                    {
                        if (string.Equals(def.GetName(), targetName, StringComparison.OrdinalIgnoreCase))
                            return Path.Combine(workspace, def.GetName());
                    }
                    // 要素数据集内的要素类
                    foreach (FeatureDatasetDefinition dsDef in gdb.GetDefinitions<FeatureDatasetDefinition>())
                    {
                        using (FeatureDataset ds = gdb.OpenDataset<FeatureDataset>(dsDef.GetName()))
                        {
                            foreach (FeatureClassDefinition fcDef in ds.GetDefinitions<FeatureClassDefinition>())
                            {
                                if (string.Equals(fcDef.GetName(), targetName, StringComparison.OrdinalIgnoreCase))
                                    return Path.Combine(workspace, dsDef.GetName(), fcDef.GetName());
                            }
                        }
                    }
                }
                return null;
            });
        }

        // OpenGeodatabase / ExistsDatasetAsync / GetCountAsync / RunToolAsync
        // 已抽到公共 GpHelper，供数据库合并与删除图层两个工具共用

        // ---------------- 异常日志（对应 _record_layer_exception / _append_exception_log） ----------------

        /// <summary>记录单图层异常：界面警告 + 暂存日志文本</summary>
        private void RecordLayerException(string layerName, Exception ex)
        {
            LogWarning($"####### 图层异常：{layerName} #######");
            LogWarning($"当前图层处理失败，已跳过并继续后续图层：{layerName}");
            LogWarning($"错误摘要：{ex.Message}");
            _exceptionLog.AppendLine("####### 图层异常 #######");
            _exceptionLog.AppendLine($"图层名称：{layerName}");
            _exceptionLog.AppendLine($"错误摘要：{ex.Message}");
            _exceptionLog.AppendLine("详细堆栈：");
            _exceptionLog.AppendLine(ex.ToString());
            _exceptionLog.AppendLine();
        }

        /// <summary>异常日志路径：输出数据库同级目录/数据库合并_异常日志_时间戳.txt</summary>
        private static string BuildExceptionLogPath(string folder, string toolLabel)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = string.Join("_", toolLabel.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(folder ?? ".", $"{safeName}_异常日志_{stamp}.txt");
        }

        /// <summary>有失败图层时才落盘异常日志</summary>
        private void FlushExceptionLog()
        {
            try
            {
                if (_exceptionLog.Length > 0)
                    File.AppendAllText(_exceptionLogPath, _exceptionLog.ToString(), Encoding.UTF8);
            }
            catch { /* 日志写失败不影响业务 */ }
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>打开文件夹选择对话框，取消返回 null</summary>
        private static string BrowseFolder(string title)
        {
            var dlg = new OpenItemDialog
            {
                Title = title,
                MultiSelect = false,
                Filter = ItemFilters.Folders
            };
            return dlg.ShowDialog() == true && dlg.Items.Any() ? dlg.Items.First().Path : null;
        }

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextFolderPath.IsEnabled = !running;
            TextOutputWorkspace.IsEnabled = !running;
            TextLayerNames.IsEnabled = !running;
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
    }
}
