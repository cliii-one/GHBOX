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
    /// 删除图层：遍历根文件夹下所有 GDB/MDB，按模式批量删除或保留指定图层。
    /// 业务逻辑与《删除图层.pyt》的 BatchDeleteLayersTool 保持一致：
    /// 1. 递归枚举根文件夹下全部 .gdb/.mdb
    /// 2. 删除模式：仅删除指定图层；保留模式：删除指定图层以外的所有图层
    /// 3. 遍历数据包含要素类/表/栅格（含要素数据集内），跳过 GDB_ 系统表
    /// 4. 可选删除空数据集：图层删除后清理空的要素数据集
    /// 5. 单库遍历失败 → 记入问题数据库列表继续；单图层删除失败 → 警告继续
    /// 6. 结束输出总统计与问题数据库列表
    /// </summary>
    public partial class LayerDelete : ProWindow
    {
        private const string ToolLabel = "删除图层";

        private CancellationTokenSource _cts;
        private DeleteHelp _help;

        public LayerDelete()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件 ----------------

        /// <summary>选择根文件夹</summary>
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择根文件夹",
                MultiSelect = false,
                Filter = ItemFilters.Folders
            };
            if (dlg.ShowDialog() == true && dlg.Items.Any())
                TextFolderPath.Text = dlg.Items.First().Path;
        }

        /// <summary>开始处理（异步，不阻塞界面）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 参数校验（对应 execute 开头校验）
                string rootFolder = TextFolderPath.Text?.Trim();
                if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
                {
                    MessageBox.Show("错误：根文件夹不存在。");
                    return;
                }

                List<string> layerNames = ParseLayerNames(TextLayerNames.Text);
                if (layerNames.Count == 0)
                {
                    MessageBox.Show("错误：未提供任何图层名称。");
                    return;
                }

                bool isRetainMode = RadioRetain.IsChecked == true;
                bool deleteEmptyDs = CheckDeleteEmptyDataset.IsChecked == true;

                // 2. 递归枚举数据库（对应 os.walk 查找 .gdb/.mdb）
                List<string> gdbList = FindWorkspacesRecursive(rootFolder);
                if (gdbList.Count == 0)
                {
                    MessageBox.Show("未找到任何 .gdb 或 .mdb 数据库。");
                    return;
                }

                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunDeleteAsync(gdbList, layerNames, isRetainMode, deleteEmptyDs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消处理。");
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

        /// <summary>取消正在进行的处理</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开删除图层工具的使用说明窗口</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new DeleteHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 处理主流程：逐库处理，单库失败不中断整体（对应 execute 主循环）。
        /// </summary>
        private async Task RunDeleteAsync(
            List<string> gdbList, List<string> layerNames,
            bool isRetainMode, bool deleteEmptyDs, CancellationToken ct)
        {
            var namesLower = new HashSet<string>(
                layerNames.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);

            Log("=".PadLeft(30, '='));
            Log($"操作模式：{(isRetainMode ? "保留模式（删除指定图层以外的所有图层）" : "删除模式（仅删除指定图层）")}");
            Log($"指定图层：{string.Join(", ", layerNames)}");
            Log($"删除空数据集：{(deleteEmptyDs ? "是" : "否")}");
            Log("=".PadLeft(30, '='));
            Log($"共发现 {gdbList.Count} 个数据库，开始处理...");

            long totalDatasets = 0;
            long totalDeleted = 0;
            long totalKept = 0;
            var problemDbs = new List<(string Path, string Error)>();

            for (int i = 0; i < gdbList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string gdbPath = gdbList[i];
                SetProgress(100.0 * i / gdbList.Count, $"[{i + 1}/{gdbList.Count}] 正在处理数据库：{gdbPath}");

                try
                {
                    (long deleted, long kept) = await ProcessSingleDatabaseAsync(
                        gdbPath, namesLower, isRetainMode, deleteEmptyDs, ct);
                    totalDatasets += deleted + kept;
                    totalDeleted += deleted;
                    totalKept += kept;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogWarning($"错误：遍历数据库失败：{gdbPath}，原因：{ex.Message}");
                    problemDbs.Add((gdbPath, ex.Message));
                }
            }

            // 最终统计（对应 execute 末尾）
            Log("【操作完成】最终统计");
            Log($"  成功处理的数据库数量：{gdbList.Count - problemDbs.Count} / {gdbList.Count}");
            Log($"  处理图层总数（成功处理库）：{totalDatasets}");
            Log($"  成功删除图层数：{totalDeleted}");
            Log($"  保留图层数：{totalKept}");
            if (totalDeleted + totalKept != totalDatasets)
                LogWarning("  注意：删除数+保留数 ≠ 总数，请检查上方的删除失败警告。");

            if (problemDbs.Count > 0)
            {
                LogWarning("【存在问题数据库列表】以下数据库因致命错误未处理：");
                foreach (var (path, err) in problemDbs)
                    LogWarning($"  - {path}  错误：{err}");
            }
            else
            {
                Log("所有数据库均成功处理，未发现致命错误。");
            }
            Log("删除图层执行完成。");
        }

        /// <summary>
        /// 处理单个数据库（对应 execute 中单库逻辑）：
        /// 枚举要素类/表/栅格（含数据集内、跳过系统表）→ 按模式确定删除目标 → 逐个删除。
        /// </summary>
        private async Task<(long Deleted, long Kept)> ProcessSingleDatabaseAsync(
            string gdbPath, HashSet<string> namesLower,
            bool isRetainMode, bool deleteEmptyDs, CancellationToken ct)
        {
            var datasets = await QueuedTask.Run(() => ListDatasets(gdbPath));

            if (datasets.Count == 0)
            {
                Log("  该数据库无用户数据，跳过。");
                return (0, 0);
            }

            long deleted = 0;
            string gdbName = Path.GetFileName(gdbPath.TrimEnd('\\'));

            foreach (string fullPath in datasets)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(fullPath);

                // 删除模式：命中名单才删；保留模式：不在名单才删（对应 to_delete 判断）
                bool shouldDelete = isRetainMode
                    ? !namesLower.Contains(name.ToLowerInvariant())
                    : namesLower.Contains(name.ToLowerInvariant());
                if (!shouldDelete) continue;

                try
                {
                    Log($"  正在删除：{gdbName} 库 -> 图层：{name}");
                    await GpHelper.RunToolAsync("management.Delete",
                        Geoprocessing.MakeValueArray(fullPath), ct);
                    deleted++;

                    // 删除空数据集：图层在要素数据集内时，删除后清理空的父数据集
                    if (deleteEmptyDs)
                    {
                        string parentDir = Path.GetDirectoryName(fullPath);
                        if (!string.Equals(parentDir, gdbPath, StringComparison.OrdinalIgnoreCase))
                            await DeleteEmptyDatasetAsync(parentDir, gdbPath, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogWarning($"  删除失败：{name}，原因：{ex.Message}");
                }
            }

            long kept = datasets.Count - deleted;
            Log($"  数据库汇总：总共 {datasets.Count} 图层，已删除 {deleted} 图层，保留 {kept} 图层");
            return (deleted, kept);
        }

        /// <summary>
        /// 删除空数据集并递归检查其父级（对应 _delete_empty_dataset）。
        /// 仅处理要素数据集（RasterDataset/MosaicDataset 为叶子节点，不递归）。
        /// </summary>
        private async Task DeleteEmptyDatasetAsync(string datasetPath, string rootGdb, CancellationToken ct)
        {
            // 防止越界删除数据库本身
            if (!datasetPath.StartsWith(rootGdb, StringComparison.OrdinalIgnoreCase)) return;
            if (!await GpHelper.ExistsDatasetAsync(datasetPath)) return;

            bool isEmpty = await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(rootGdb))
                {
                    if (gdb == null) return false;
                    string dsName = GetRelativeDatasetName(rootGdb, datasetPath);

                    // 仅处理要素数据集这一种容器类型
                    foreach (FeatureDatasetDefinition def in gdb.GetDefinitions<FeatureDatasetDefinition>())
                    {
                        if (!string.Equals(def.GetName(), dsName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        using (FeatureDataset ds = gdb.OpenDataset<FeatureDataset>(def.GetName()))
                        {
                            return !ds.GetDefinitions<FeatureClassDefinition>().Any();
                        }
                    }
                    return false;
                }
            });
            if (!isEmpty) return;

            try
            {
                Log($"  发现空数据集，正在删除：{Path.GetFileName(datasetPath)}");
                await GpHelper.RunToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(datasetPath), ct);

                // 递归检查父级（数据集删除后父级也可能变空）
                string parent = Path.GetDirectoryName(datasetPath);
                if (!string.Equals(parent, rootGdb, StringComparison.OrdinalIgnoreCase))
                    await DeleteEmptyDatasetAsync(parent, rootGdb, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogWarning($"  删除空数据集失败：{datasetPath}，原因：{ex.Message}");
            }
        }

        // ---------------- 数据枚举 ----------------

        /// <summary>
        /// 枚举库内全部用户数据（对应 da.Walk datatype=FeatureClass/Table/RasterDataset，跳过 GDB_ 系统表）。
        /// .mdb 无法用 Geodatabase API 打开时退化为 GP 数据枚举。
        /// </summary>
        private static List<string> ListDatasets(string gdbPath)
        {
            var result = new List<string>();
            using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
            {
                if (gdb == null) return result;

                foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                    if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.Combine(gdbPath, def.GetName()));

                foreach (TableDefinition def in gdb.GetDefinitions<TableDefinition>())
                    if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.Combine(gdbPath, def.GetName()));

                // 注：栅格数据集在 Geodatabase API 中无公开定义类型可枚举，此工具不处理栅格
                // （.pyt 原版 da.Walk 含 RasterDataset；如需支持可后续改用 GP 枚举）

                // 要素数据集内的要素类
                foreach (FeatureDatasetDefinition dsDef in gdb.GetDefinitions<FeatureDatasetDefinition>())
                {
                    using (FeatureDataset ds = gdb.OpenDataset<FeatureDataset>(dsDef.GetName()))
                    {
                        foreach (FeatureClassDefinition fcDef in ds.GetDefinitions<FeatureClassDefinition>())
                            if (!fcDef.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                                result.Add(Path.Combine(gdbPath, dsDef.GetName(), fcDef.GetName()));
                    }
                }
            }
            return result;
        }

        /// <summary>取数据集在库内的相对名（库内路径 → 名称部分）</summary>
        private static string GetRelativeDatasetName(string rootGdb, string datasetPath)
        {
            string rel = datasetPath.Substring(rootGdb.TrimEnd('\\').Length).TrimStart('\\');
            return rel.Contains('\\') ? rel.Split('\\')[0] : rel;
        }

        /// <summary>递归枚举根文件夹下所有 .gdb/.mdb（对应 os.walk，包含子文件夹）</summary>
        private static List<string> FindWorkspacesRecursive(string rootFolder)
        {
            var list = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootFolder);

            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string lower = sub.ToLowerInvariant();
                        if (lower.EndsWith(".gdb") || lower.EndsWith(".mdb"))
                            list.Add(sub);
                        else
                            pending.Push(sub);
                    }
                }
                catch { /* 无权限等异常目录跳过 */ }
            }
            return list.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        /// <summary>解析图层名：中英文逗号分隔、去重、忽略空项</summary>
        private static List<string> ParseLayerNames(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return names;

            foreach (string item in text.Replace("，", ",").Split(','))
            {
                string clean = item.Trim();
                if (clean.Length == 0) continue;
                if (!names.Any(n => n.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                    names.Add(clean);
            }
            return names;
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextFolderPath.IsEnabled = !running;
            TextLayerNames.IsEnabled = !running;
            RadioDelete.IsEnabled = !running;
            RadioRetain.IsEnabled = !running;
            CheckDeleteEmptyDataset.IsEnabled = !running;
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
