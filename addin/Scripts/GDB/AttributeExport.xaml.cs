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
    /// 按属性拆库：从输入文件夹下多个 GDB/MDB 数据库中查找指定图层，
    /// 按属性筛选后分别导出为 SHP 或 GDB。
    /// 业务逻辑与 `toolbox/按属性拆库.pyt` 基准完全一致：
    /// 1. 枚举输入文件夹第一层的 .gdb/.mdb，按名称排序逐库处理
    /// 2. 在库中查找指定图层（大小写不敏感，含要素数据集内；找不到 → 警告跳过）
    /// 3. 导出为 GDB：按库名生成同名 GDB（同名则复用，否则 {基名}.gdb），图层导出到该 GDB 内
    /// 4. 导出为 SHP：按「库基底名.shp」导出到输出文件夹
    /// 5. 有筛选条件用 Select，为空用 CopyFeatures；输出已存在 → 先删除
    /// 6. 导出后核对条数，0 条仅警告；单库异常 → 写异常日志，继续下一个库
    /// </summary>
    public partial class AttributeExport : ProWindow
    {
        private const string ToolLabel = "按属性拆库";

        private CancellationTokenSource _cts;
        private string _exceptionLogPath;
        private readonly StringBuilder _exceptionLog = new StringBuilder();
        private AttributeExportHelp _help;

        public AttributeExport()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件 ----------------

        /// <summary>选择输入文件夹</summary>
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = BrowseFolder("选择输入文件夹");
            if (folder != null) TextFolderPath.Text = folder;
        }

        /// <summary>选择输出文件夹</summary>
        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = BrowseFolder("选择输出文件夹");
            if (folder != null) TextOutputFolder.Text = folder;
        }

        /// <summary>开始导出（异步，不阻塞界面）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 收集参数（对应 .pyt updateMessages 的校验）
                string inputFolder = TextFolderPath.Text?.Trim();
                string layerName = TextLayerName.Text?.Trim();
                string whereClause = TextWhereClause.Text?.Trim() ?? "";
                string outputFolder = TextOutputFolder.Text?.Trim();
                bool exportToGdb = CheckExportToGdb.IsChecked == true;

                if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                {
                    MessageBox.Show("输入文件夹不存在，请检查路径是否正确。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(layerName))
                {
                    MessageBox.Show("请输入图层名称。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(outputFolder))
                {
                    MessageBox.Show("请输入输出文件夹。");
                    return;
                }

                // 2. 枚举输入数据库（对应 _list_input_workspaces）
                List<string> workspaces = ListAttributeWorkspaces(inputFolder);
                if (workspaces.Count == 0)
                {
                    MessageBox.Show("在输入文件夹下未找到任何 .gdb 或 .mdb 数据库。");
                    return;
                }

                _cts = new CancellationTokenSource();
                SetRunning(true);

                // 3. 主流程（对应 execute）
                await RunExportAsync(workspaces, layerName, whereClause, outputFolder, exportToGdb, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消导出。");
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

        /// <summary>取消正在进行的导出</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开按属性拆库工具的使用说明窗口</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new AttributeExportHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 导出主流程：逐数据库处理，单库失败不中断整体（对应 .pyt execute）。
        /// </summary>
        private async Task RunExportAsync(
            List<string> workspaces, string layerName, string whereClause,
            string outputFolder, bool exportToGdb, CancellationToken ct)
        {
            // 输出文件夹不存在时自动创建（对应 _ensure_folder）
            Directory.CreateDirectory(outputFolder);

            // 异常日志放在输出文件夹下（对应 _build_exception_log_path）
            _exceptionLogPath = BuildExceptionLogPath(outputFolder, ToolLabel);

            string exportModeText = exportToGdb ? "GDB" : "SHP";
            string filterModeText = string.IsNullOrEmpty(whereClause) ? "整层导出" : "按条件筛选导出";

            Log("####### 按属性拆库开始 #######");
            Log($"输入文件夹：{Path.GetDirectoryName(workspaces[0])}");
            Log($"筛选方式：{filterModeText}");
            if (string.IsNullOrEmpty(whereClause))
                Log("筛选条件为空，当前按整层导出处理。");
            else
                Log($"筛选条件：{whereClause}");
            Log($"共发现 {workspaces.Count} 个输入数据库。");
            Log($"导出模式：{exportModeText}");

            int successCount = 0;
            int foundLayerCount = 0;
            var failedWorkspaces = new List<string>();

            foreach (string ws in workspaces)
            {
                ct.ThrowIfCancellationRequested();
                string wsName = Path.GetFileName(ws.TrimEnd('\\'));
                Log($"####### 开始处理数据库：{wsName} #######");
                SetProgress(100.0 * (successCount + failedWorkspaces.Count) / workspaces.Count,
                    $"开始处理数据库：{wsName}");
                try
                {
                    bool layerFound = await ExportSingleWorkspaceAsync(
                        ws, layerName, whereClause, outputFolder, exportToGdb, ct);
                    if (layerFound) { foundLayerCount++; successCount++; }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedWorkspaces.Add(wsName);
                    RecordWorkspaceException(ws, ex);
                }
            }

            if (foundLayerCount == 0)
                LogWarning($"所有输入数据库中都未找到图层：{layerName}");

            // 运行汇总（对应 _log_run_summary）
            Log("####### 运行汇总 #######");
            Log($"成功数据库：{successCount}");
            Log($"失败数据库：{failedWorkspaces.Count}");
            if (failedWorkspaces.Count > 0)
            {
                FlushExceptionLog();
                Log($"失败数据库：{string.Join("，", failedWorkspaces)}");
                Log($"异常日志已保存至：{_exceptionLogPath}");
            }
            Log("按属性拆库执行完成。");
            // 全部数据库处理完毕，进度条置满（循环内最后一次进度只到 (N-1)/N）
            SetProgress(100, "按属性拆库执行完成");
        }

        /// <summary>
        /// 导出单个数据库（对应 _export_single_workspace_by_attribute）：
        /// 查找图层 → 统计源条数 → 按导出模式输出 → 导出后条数核对与 0 条警告。
        /// </summary>
        private async Task<bool> ExportSingleWorkspaceAsync(
            string workspace, string layerName, string whereClause,
            string outputFolder, bool exportToGdb, CancellationToken ct)
        {
            string fcPath = await FindFeatureClassAsync(workspace, layerName);
            string wsName = Path.GetFileName(workspace.TrimEnd('\\'));

            if (fcPath == null)
            {
                LogWarning($"数据库 {wsName} 中未找到图层：{layerName}");
                return false;
            }

            string realName = Path.GetFileName(fcPath);
            long sourceCount = await GpHelper.GetCountAsync(fcPath, ct);
            Log($"数据库 {wsName} 中图层 {realName} 原始条数：{sourceCount}");

            string modeText = exportToGdb ? "GDB" : "SHP";
            if (string.IsNullOrEmpty(whereClause))
                Log($"数据库 {wsName} 筛选条件为空，当前按整层导出，导出模式：{modeText}");
            else
                Log($"数据库 {wsName} 开始按条件导出，当前模式：{modeText}");

            if (exportToGdb)
            {
                // 导出为 GDB
                string outputGdbPath = BuildWorkspaceOutputGdbPath(outputFolder, workspace);
                await EnsureOutputFileGdb(outputGdbPath, ct);
                string outputFcPath = Path.Combine(outputGdbPath, realName);

                if (await GpHelper.ExistsDatasetAsync(outputFcPath))
                {
                    LogWarning($"输出 GDB 中已存在同名图层，已先删除：{outputFcPath}");
                    await GpHelper.RunToolAsync("management.Delete",
                        Geoprocessing.MakeValueArray(outputFcPath), ct);
                }

                Log($"数据库 {wsName} 输出 GDB：{outputGdbPath}");
                await ExportFeaturesAsync(fcPath, outputFcPath, whereClause, ct);

                long exportCount = await GpHelper.GetCountAsync(outputFcPath, ct);
                Log($"数据库 {wsName} 导出条数：{exportCount}");
                Log($"数据库 {wsName} 导出图层：{outputFcPath}");
                if (exportCount == 0)
                    LogWarning(string.IsNullOrEmpty(whereClause)
                        ? $"数据库 {wsName} 当前图层为空，已导出空要素类。"
                        : $"数据库 {wsName} 筛选结果为空，已导出空要素类。");
            }
            else
            {
                // 导出为 SHP
                string outputShpPath = BuildWorkspaceOutputShpPath(outputFolder, workspace);

                if (await GpHelper.ExistsDatasetAsync(outputShpPath))
                {
                    LogWarning($"输出文件夹中已存在同名 shp，已先删除：{outputShpPath}");
                    await GpHelper.RunToolAsync("management.Delete",
                        Geoprocessing.MakeValueArray(outputShpPath), ct);
                }

                await ExportFeaturesAsync(fcPath, outputShpPath, whereClause, ct);

                long exportCount = await GpHelper.GetCountAsync(outputShpPath, ct);
                Log($"数据库 {wsName} 导出条数：{exportCount}");
                Log($"数据库 {wsName} 导出结果：{outputShpPath}");
                if (exportCount == 0)
                    LogWarning(string.IsNullOrEmpty(whereClause)
                        ? $"数据库 {wsName} 当前图层为空，已导出空 SHP。"
                        : $"数据库 {wsName} 筛选结果为空，已导出空 SHP。");
            }

            return true;
        }

        /// <summary>
        /// 按筛选条件执行导出：有 where_clause 用 analysis.Select，为空用 management.CopyFeatures。
        /// </summary>
        private async Task ExportFeaturesAsync(
            string sourcePath, string outputPath, string whereClause, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(whereClause))
            {
                await GpHelper.RunToolAsync("management.CopyFeatures",
                    Geoprocessing.MakeValueArray(sourcePath, outputPath), ct);
            }
            else
            {
                await GpHelper.RunToolAsync("analysis.Select",
                    Geoprocessing.MakeValueArray(sourcePath, outputPath, whereClause), ct);
            }
        }

        // ---------------- 工具方法（对应 .pyt 各私有方法） ----------------

        /// <summary>输入数据库仅支持 .gdb/.mdb（对应 _is_supported_workspace）</summary>
        private static bool IsSupportedWorkspace(string path)
        {
            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".gdb") || lower.EndsWith(".mdb");
        }

        /// <summary>枚举文件夹第一层所有 .gdb/.mdb 子目录（对应 _list_input_workspaces）</summary>
        private static List<string> ListAttributeWorkspaces(string inputFolder)
        {
            return Directory.EnumerateDirectories(inputFolder)
                .Where(IsSupportedWorkspace)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>SHP 输出路径：库基底名.shp，非法字符替换为 _（对应 _build_workspace_output_shp_path）</summary>
        private static string BuildWorkspaceOutputShpPath(string outputFolder, string workspace)
        {
            string baseName = Path.GetFileNameWithoutExtension(workspace);
            return Path.Combine(outputFolder, SanitizeFeatureClass(baseName) + ".shp");
        }

        /// <summary>GDB 输出路径：原名 .gdb 则同名，否则 {基名}.gdb（对应 _build_workspace_output_gdb_path）</summary>
        private static string BuildWorkspaceOutputGdbPath(string outputFolder, string workspace)
        {
            string wsName = Path.GetFileName(workspace.TrimEnd('\\'));
            if (wsName.ToLowerInvariant().EndsWith(".gdb"))
                return Path.Combine(outputFolder, wsName);

            string baseName = Path.GetFileNameWithoutExtension(wsName);
            return Path.Combine(outputFolder, baseName + ".gdb");
        }

        /// <summary>输出 GDB 不存在时自动创建（对应 _ensure_output_file_gdb）</summary>
        private static async Task EnsureOutputFileGdb(string outputGdbPath, CancellationToken ct)
        {
            if (await GpHelper.ExistsDatasetAsync(outputGdbPath))
                return;

            string parentFolder = Path.GetDirectoryName(outputGdbPath);
            string gdbName = Path.GetFileNameWithoutExtension(outputGdbPath);
            await GpHelper.RunToolAsync("management.CreateFileGDB",
                Geoprocessing.MakeValueArray(parentFolder, gdbName), ct);
        }

        /// <summary>
        /// 在库中按名称查找要素类（大小写不敏感，含要素数据集内；对应 _find_feature_class_by_name_in_workspace）。
        /// .mdb 通过 Geodatabase API 打不开时返回 null，由上层按「未找到图层」处理。
        /// </summary>
        private static async Task<string> FindFeatureClassAsync(string workspace, string targetName)
        {
            return await QueuedTask.Run(() =>
            {
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

        /// <summary>清洗要素类/文件名中的非法字符，替换为 _（对应 _sanitize_feature_class_name）</summary>
        private static string SanitizeFeatureClass(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
            return string.IsNullOrWhiteSpace(cleaned) ? "导出结果" : cleaned;
        }

        // ---------------- 异常日志（对应 _record_workspace_exception / _append_exception_log） ----------------

        /// <summary>记录单数据库异常：界面警告 + 暂存日志文本</summary>
        private void RecordWorkspaceException(string workspace, Exception ex)
        {
            string wsName = Path.GetFileName(workspace.TrimEnd('\\'));
            LogWarning($"####### 数据库异常：{wsName} #######");
            LogWarning($"当前数据库处理失败，已跳过并继续后续数据库：{wsName}");
            LogWarning($"错误摘要：{ex.Message}");
            _exceptionLog.AppendLine("####### 数据库异常 #######");
            _exceptionLog.AppendLine($"数据库：{workspace}");
            _exceptionLog.AppendLine($"错误摘要：{ex.Message}");
            _exceptionLog.AppendLine("详细堆栈：");
            _exceptionLog.AppendLine(ex.ToString());
            _exceptionLog.AppendLine();
        }

        /// <summary>异常日志路径：输出文件夹/按属性拆库_异常日志_时间戳.txt</summary>
        private static string BuildExceptionLogPath(string folder, string toolLabel)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = string.Join("_", toolLabel.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(folder ?? ".", $"{safeName}_异常日志_{stamp}.txt");
        }

        /// <summary>有失败数据库时才落盘异常日志</summary>
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
            TextLayerName.IsEnabled = !running;
            TextWhereClause.IsEnabled = !running;
            TextOutputFolder.IsEnabled = !running;
            CheckExportToGdb.IsEnabled = !running;
            BtnCancel.IsEnabled = running;
        }

        /// <summary>更新进度</summary>
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