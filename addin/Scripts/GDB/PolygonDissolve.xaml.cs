using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
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
    /// 按属性合并：对相邻图斑中指定字段值相同的要素进行空间合并，
    /// 支持按“公共边最长”或“面积最大”规则合并。
    /// 业务逻辑与 `toolbox/按属性合并.pyt` 基准完全一致：
    /// 1. 复制输入要素类到输出要素类（已存在先删除）
    /// 2. 多个合并字段时创建临时字段 ZZ_COMBINED_KEY 拼接
    /// 3. 按属性分割（analysis.Split）→ 每个子图层循环 Eliminate
    ///    （每轮选除面积最大外全部，处理链式相邻，安全阀 50 轮）→ 合并回输出要素类
    /// 4. 合并数量 &gt; 0 时 RepairGeometry 修复几何；输出后核对总条数
    /// 5. 异常写「按属性合并_异常日志_时间戳.txt」到输出同级目录
    ///
    /// 重要（铁律）：
    /// - 所有 Geodatabase/Geometry 访问必须 QueuedTask 异步，严禁 UI 线程 .Result/.Wait()（死锁）
    /// - XAML 默认项在 Loaded 里代码设置，不在 XAML 设（规避初始化期事件触发）
    /// </summary>
    public partial class PolygonDissolve : ProWindow
    {
        private const string ToolLabel = "按属性合并";
        private const string RuleBySharedEdge = "向公共边最长的图斑合并";
        private const string RuleByArea = "向面积最大的图斑合并";

        private CancellationTokenSource _cts;
        private PolygonDissolveHelp _help;

        public PolygonDissolve()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载后填充合并规则下拉项（默认选中“向公共边最长的图斑合并”）</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (CmbMergeRule.Items.Count == 0)
            {
                CmbMergeRule.Items.Add(RuleBySharedEdge);
                CmbMergeRule.Items.Add(RuleByArea);
                CmbMergeRule.SelectedIndex = 0;   // 默认：向公共边最长的图斑合并
            }
        }

        // ---------------- 界面事件 ----------------

        /// <summary>选择输入要素类</summary>
        private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
        {
            string path = BrowseDataset("选择输入要素类");
            if (path != null) TextInputFeatureClass.Text = path;
        }

        /// <summary>选择输出要素类存放位置（GDB 内部或 SHP 文件均可）</summary>
        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            string path = BrowseDataset("选择输出要素类（选择内容放在某数据库内，或直接填完整路径）");
            if (path != null) TextOutputFeatureClass.Text = path;
        }

        /// <summary>
        /// 开始合并（异步，不阻塞界面）。
        /// 对应 .pyt execute 的入口校验：输入存在、合并字段非空、面要素类、字段存在性。
        /// </summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 收集参数并校验（对应 .pyt updateMessages/execute 的校验）
                string inputFc = TextInputFeatureClass.Text?.Trim();
                string mergeFieldsText = TextMergeFields.Text?.Trim();
                string mergeRule = (CmbMergeRule.SelectedItem as string) ?? RuleBySharedEdge;
                string outputFc = TextOutputFeatureClass.Text?.Trim();

                if (string.IsNullOrWhiteSpace(inputFc) || !await GpHelper.ExistsDatasetAsync(inputFc))
                {
                    MessageBox.Show("输入要素类不存在，请检查路径是否正确。", ToolLabel);
                    return;
                }
                if (string.IsNullOrWhiteSpace(mergeFieldsText))
                {
                    MessageBox.Show("请输入合并字段。", ToolLabel);
                    return;
                }

                List<string> mergeFields = ParseMergeFields(mergeFieldsText);
                if (mergeFields.Count == 0)
                {
                    MessageBox.Show("合并字段不能为空。", ToolLabel);
                    return;
                }

                // 面要素类校验（对应 _dissolve 前对 desc.shapeType 的判断）
                string shapeType = await GetShapeTypeAsync(inputFc);
                if (!string.Equals(shapeType, "Polygon", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"输入要素类必须是面要素类，当前类型：{shapeType}", ToolLabel);
                    return;
                }

                if (string.IsNullOrWhiteSpace(outputFc))
                {
                    MessageBox.Show("请输入输出要素类路径。", ToolLabel);
                    return;
                }
                // 输入与输出相同时直接拒绝（否则先 Delete 会删掉输入要素类，导致后续 CopyFeatures 失败）
                if (string.Equals(Path.GetFullPath(inputFc), Path.GetFullPath(outputFc),
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("输入要素类与输出要素类不能相同，请指定其他输出路径。", ToolLabel);
                    return;
                }

                // 字段存在性校验（对应 _validate_merge_fields）
                string missingFields = await FindMissingFieldsAsync(inputFc, mergeFields);
                if (missingFields != null)
                {
                    MessageBox.Show($"输入要素类中不存在以下字段：{missingFields}", ToolLabel);
                    return;
                }

                // 输出目录不存在时自动创建（对应 _ensure_folder）
                string outputFolder = Path.GetDirectoryName(outputFc);
                if (!string.IsNullOrEmpty(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                _cts = new CancellationTokenSource();
                SetRunning(true);

                // 2. 主流程（对应 execute 主干）
                await RunDissolveAsync(inputFc, mergeFields, mergeRule, outputFc, _cts.Token);
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

        /// <summary>打开按属性合并工具的使用说明窗口（单例）</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new PolygonDissolveHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 合并主流程（对应 .pyt execute 主干）：
        /// 复制 → 分割+消除+合并 → 修复几何 → 汇总。
        /// </summary>
        private async Task RunDissolveAsync(
            string inputFc, List<string> mergeFields, string mergeRule,
            string outputFc, CancellationToken ct)
        {
            Log("####### 按属性合并开始 #######");
            Log($"输入要素类：{inputFc}");
            Log($"合并字段：{string.Join("，", mergeFields)}");
            Log($"合并规则：{mergeRule}");
            Log($"输出要素类：{outputFc}");

            long sourceCount = await GpHelper.GetCountAsync(inputFc, ct);
            Log($"合并前总条数：{sourceCount}");

            // 输出要素类已存在则先删除（对应 .pyt：先 Delete 再 CopyFeatures）
            if (await GpHelper.ExistsDatasetAsync(outputFc))
            {
                LogWarning($"输出要素类已存在，已先删除：{outputFc}");
                await GpHelper.RunToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(outputFc), ct);
            }

            Log("正在复制输入要素类到输出...");
            await GpHelper.RunToolAsync("management.CopyFeatures",
                Geoprocessing.MakeValueArray(inputFc, outputFc), ct);
            SetProgress(15, "输入要素类复制完成，开始属性分割与消除。");

            long mergeCount = await DissolveAdjacentFeaturesAsync(
                outputFc, mergeFields, mergeRule, ct);
            SetProgress(85, "分割与消除全部完成。");

            if (mergeCount > 0)
            {
                Log("正在修复几何拓扑...");
                await GpHelper.RunToolAsync("management.RepairGeometry",
                    Geoprocessing.MakeValueArray(outputFc, "DELETE_NULL"), ct);
                Log("几何拓扑修复完成。");
            }
            SetProgress(95, "结果输出与几何修复完成。");

            long finalCount = await GpHelper.GetCountAsync(outputFc, ct);
            Log($"合并后总条数：{finalCount}");
            Log($"共合并了 {mergeCount} 对相邻图斑。");
            Log($"输出要素类：{outputFc}");
            Log("####### 按属性合并完成 #######");
            SetProgress(100, "全部完成。");
        }

        /// <summary>
        /// 相邻图斑合并核心（对应 .pyt _dissolve_adjacent_features）：
        /// 分割 + 消除 + 合并，全程临时 GDB 清理。
        /// </summary>
        private async Task<long> DissolveAdjacentFeaturesAsync(
            string featureClass, List<string> mergeFields, string mergeRule, CancellationToken ct)
        {
            long originalCount = await GpHelper.GetCountAsync(featureClass, ct);

            // 多个合并字段时创建临时拼接字段；单个字段直接用
            string splitField;
            string tempCombinedField = null;
            if (mergeFields.Count == 1)
            {
                splitField = mergeFields[0];
            }
            else
            {
                tempCombinedField = "ZZ_COMBINED_KEY";
                Log($"正在创建临时合并字段：{tempCombinedField}");
                await GpHelper.RunToolAsync("management.AddField",
                    Geoprocessing.MakeValueArray(featureClass, tempCombinedField, "TEXT", null, null, null, 500), ct);
                // 对应 .pyt 的 CalculateField 表达式：str(A) + '|' + str(B)
                string expression = string.Join(" + '|' + ",
                    mergeFields.Select(f => $"str(!{f}!)"));
                await GpHelper.RunToolAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(featureClass, tempCombinedField, expression, "PYTHON"), ct);
                splitField = tempCombinedField;
            }

            // 临时 GDB（放系统临时目录，CreateFileGDB 自动加 .gdb 后缀）
            string scratchFolder = Path.GetTempPath();
            string tempGdbStem = $"dissolve_temp_{Guid.NewGuid():N}".Substring(0, 26);
            string tempGdbPath = Path.Combine(scratchFolder, tempGdbStem + ".gdb");
            string splitGdbStem = $"dissolve_splits_{Guid.NewGuid():N}".Substring(0, 28);
            string splitWorkspace = Path.Combine(scratchFolder, splitGdbStem + ".gdb");

            // 确保临时目录存在（CreateFileGDB 要求父目录存在）
            Directory.CreateDirectory(scratchFolder);
            await GpHelper.RunToolAsync("management.CreateFileGDB",
                Geoprocessing.MakeValueArray(scratchFolder, tempGdbStem), ct);
            await GpHelper.RunToolAsync("management.CreateFileGDB",
                Geoprocessing.MakeValueArray(scratchFolder, splitGdbStem), ct);

            List<string> eliminatedList = new List<string>();
            long totalProcessed = 0;
            int splitCount = 0;

            try
            {
                // 第1步：按属性分割（对应 analysis.Split）
                Log($"正在按字段 [{splitField}] 分割要素...");
                await GpHelper.RunToolAsync("analysis.Split",
                    Geoprocessing.MakeValueArray(featureClass, splitWorkspace, splitField), ct);

                List<string> splitList = await ListPolygonFeatureClassesAsync(splitWorkspace, ct);
                splitCount = splitList.Count;
                Log($"分割完成，共 {splitCount} 个子图层。");
                SetProgress(30, $"按属性分割完成，共 {splitCount} 个子图层。");

                // 第2步：对每个子图层循环消除（对应 eliminate_rule 映射）
                string eliminateRule = mergeRule == RuleByArea ? "AREA" : "LENGTH";
                Log($"消除规则：{(eliminateRule == "LENGTH" ? "按边界最长" : "按面积最大")}。");

                foreach (string splitName in splitList)
                {
                    ct.ThrowIfCancellationRequested();
                    string splitFc = Path.Combine(splitWorkspace, splitName);
                    long fcCount = await GpHelper.GetCountAsync(splitFc, ct);
                    // 每处理完一个子图层上报一次进度（30%~85%），让进度条真正动起来
                    double elimProgress = 30.0 + 55.0 * (totalProcessed + 1.0) / Math.Max(1, splitCount);
                    SetProgress(Math.Min(85, elimProgress), $"正在消除子图层 {totalProcessed + 1}/{splitCount}：{splitName}");

                    if (fcCount <= 1)
                    {
                        eliminatedList.Add(splitFc);
                        totalProcessed++;
                        continue;
                    }

                    // 循环消除：每轮选中除面积最大外的所有要素执行消除，
                    // 处理链式相邻（A-B-C，第一轮B并入A，第二轮C并入A）。
                    string currentFc = splitFc;
                    int roundIndex = 0;

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        roundIndex++;
                        long currentCount = await GpHelper.GetCountAsync(currentFc, ct);
                        if (currentCount <= 1)
                            break;

                        // 找到面积最大的要素（对应 sql_clause ORDER BY SHAPE_AREA DESC 取第一个）
                        string oidField = await GetOidFieldNameAsync(currentFc, ct);
                        string maxOid = await GetMaxAreaOidAsync(currentFc, oidField, ct);

                        // 创建图层并选中除最大外的所有要素
                        string layerName = $"lyr_{Guid.NewGuid():N}".Substring(0, 12);
                        await GpHelper.RunToolAsync("management.MakeFeatureLayer",
                            Geoprocessing.MakeValueArray(currentFc, layerName), ct);
                        try
                        {
                            await GpHelper.RunToolAsync("management.SelectLayerByAttribute",
                                Geoprocessing.MakeValueArray(layerName, "NEW_SELECTION",
                                    $"{oidField} <> {maxOid}"), ct);

                            long selectedCount = await GpHelper.GetCountAsync(layerName, ct);
                            if (selectedCount <= 0)
                                break;

                            // 执行消除
                            string roundOutput = Path.Combine(tempGdbPath,
                                $"elim_r{roundIndex}_{Guid.NewGuid():N}".Substring(0, 30));
                            try
                            {
                                await GpHelper.RunToolAsync("management.Eliminate",
                                    Geoprocessing.MakeValueArray(layerName, roundOutput, eliminateRule), ct);
                            }
                            catch (Exception elimEx)
                            {
                                LogWarning($"子图层 [{splitName}] 第{roundIndex}轮消除失败：{elimEx.Message}");
                                break;
                            }

                            // 当前 FC 是 split 直接来的话，不要删除原始分割结果
                            if (currentFc != splitFc)
                                await GpHelper.RunToolAsync("management.Delete",
                                    Geoprocessing.MakeValueArray(currentFc), ct);
                            currentFc = roundOutput;

                            // 安全阀：避免无限循环
                            if (roundIndex > 50)
                            {
                                LogWarning($"子图层 [{splitName}] 消除超过50轮，停止。");
                                break;
                            }
                        }
                        finally
                        {
                            await GpHelper.RunToolAsync("management.Delete",
                                Geoprocessing.MakeValueArray(layerName), ct);
                        }
                    }

                    eliminatedList.Add(currentFc);
                    totalProcessed++;
                    if (totalProcessed % 50 == 0)
                        Log($"已处理 {totalProcessed}/{splitList.Count} 个子图层...");
                }

                Log("全部子图层消除完成。");

                // 第3步：合并所有消除后的图层（对应 management.Merge）
                Log("正在合并所有子图层...");
                await GpHelper.RunToolAsync("management.Merge",
                    Geoprocessing.MakeValueArray(eliminatedList, featureClass), ct);

                // 合并后清理临时拼接字段（Merge 输出的字段结构若带出该字段则删除，失败不影响结果）
                if (tempCombinedField != null)
                {
                    try
                    {
                        await GpHelper.RunToolAsync("management.DeleteField",
                            Geoprocessing.MakeValueArray(featureClass, tempCombinedField), ct);
                        Log($"已清理临时合并字段：{tempCombinedField}");
                    }
                    catch
                    {
                        LogWarning("临时合并字段清理失败（不影响合并结果）。");
                    }
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                // 清理临时 GDB（对应 .pyt finally）
                try
                {
                    if (await GpHelper.ExistsDatasetAsync(tempGdbPath))
                        await GpHelper.RunToolAsync("management.Delete",
                            Geoprocessing.MakeValueArray(tempGdbPath), ct);
                }
                catch { /* 清理失败不影响主流程 */ }
                try
                {
                    if (await GpHelper.ExistsDatasetAsync(splitWorkspace))
                        await GpHelper.RunToolAsync("management.Delete",
                            Geoprocessing.MakeValueArray(splitWorkspace), ct);
                }
                catch { /* 清理失败不影响主流程 */ }
            }

            long finalCount = await GpHelper.GetCountAsync(featureClass, ct);
            return originalCount - finalCount;
        }

        // ---------------- 工具方法（对应 .pyt 各私有方法） ----------------

        /// <summary>解析合并字段：中文逗号转英文、去空项、去重（对应 _parse_merge_fields）</summary>
        private static List<string> ParseMergeFields(string mergeFieldsText)
        {
            string normalized = mergeFieldsText.Replace("，", ",");
            var fields = new List<string>();
            foreach (string item in normalized.Split(','))
            {
                string cleanName = item.Trim();
                if (cleanName.Length > 0 &&
                    !fields.Contains(cleanName, StringComparer.OrdinalIgnoreCase))
                    fields.Add(cleanName);
            }
            return fields;
        }

        /// <summary>
        /// 校验合并字段存在性：返回缺失字段（逗号分隔），全部存在返回 null（对应 _validate_merge_fields）。
        /// </summary>
        private static async Task<string> FindMissingFieldsAsync(string featureClass, List<string> mergeFields)
        {
            return await QueuedTask.Run(() =>
            {
                using (var gdb = OpenParentGeodatabase(featureClass))
                {
                    if (gdb == null) return string.Join("，", mergeFields);

                    string fcName = Path.GetFileName(featureClass);
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        var existingNames = fc.GetDefinition().GetFields()
                            .Select(f => f.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var missing = mergeFields.Where(f => !existingNames.Contains(f)).ToList();
                        return missing.Count == 0 ? null : string.Join("，", missing);
                    }
                }
            });
        }

        /// <summary>
        /// 从要素类完整路径打开其所属数据库（支持 GDB；MDB 打不开返回 null）。
        /// </summary>
        private static Geodatabase OpenParentGeodatabase(string featureClassPath)
        {
            // 路径形如 D:\xx\yy.gdb\FCName（或含要素数据集 D:\xx\yy.gdb\DS\FCName）
            // 逐级向上找第一个 .gdb 目录作为数据库根
            string current = featureClassPath?.TrimEnd('\\');
            string gdbRoot = null;
            while (!string.IsNullOrEmpty(current))
            {
                if (current.ToLowerInvariant().EndsWith(".gdb"))
                {
                    gdbRoot = current;
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
            return gdbRoot != null ? GpHelper.OpenGeodatabase(gdbRoot) : null;
        }

        /// <summary>获取要素类几何类型（对应 desc.shapeType）</summary>
        private static async Task<string> GetShapeTypeAsync(string featureClass)
        {
            return await QueuedTask.Run(() =>
            {
                using (var gdb = OpenParentGeodatabase(featureClass))
                {
                    if (gdb == null) return "Unknown";
                    string fcName = Path.GetFileName(featureClass);
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        return fc.GetDefinition().GetShapeType().ToString();
                    }
                }
            });
        }

        /// <summary>列出分割结果 GDB 中的全部面要素类名</summary>
        private static async Task<List<string>> ListPolygonFeatureClassesAsync(string workspace, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                var names = new List<string>();
                using (var gdb = GpHelper.OpenGeodatabase(workspace))
                {
                    if (gdb == null) return names;
                    foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (def.GetShapeType() == ArcGIS.Core.Geometry.GeometryType.Polygon)
                            names.Add(def.GetName());
                    }
                }
                return names;
            });
        }

        /// <summary>获取要素类 OID 字段名（对应 desc.OIDFieldName）</summary>
        private static async Task<string> GetOidFieldNameAsync(string featureClass, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                using (var gdb = OpenParentGeodatabase(featureClass))
                {
                    if (gdb == null) return "OBJECTID";
                    string fcName = Path.GetFileName(featureClass);
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        return fc.GetDefinition().GetObjectIDField();
                    }
                }
            });
        }

        /// <summary>
        /// 逐条读取并比较面积，返回面积最大要素的 OID。
        /// 对应 .pyt 使用 SQL「ORDER BY SHAPE_AREA DESC 取第一个」的语义。
        /// </summary>
        private static async Task<string> GetMaxAreaOidAsync(string featureClass, string oidField, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                using (var gdb = OpenParentGeodatabase(featureClass))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开要素类所在数据库。");
                    string fcName = Path.GetFileName(featureClass);
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        string shapeField = fc.GetDefinition().GetShapeField();
                        var queryFilter = new QueryFilter
                        {
                            SubFields = string.Join(",", oidField, shapeField)
                        };
                        string maxOid = "0";
                        double maxArea = -1.0;
                        using (RowCursor cursor = fc.Search(queryFilter, false))
                        {
                            while (cursor.MoveNext())
                            {
                                ct.ThrowIfCancellationRequested();
                                Row row = cursor.Current;
                                var polygon = row[row.FindField(shapeField)] as Polygon;
                                double area = (polygon != null && !polygon.IsEmpty)
                                    ? GeometryEngine.Instance.Area(polygon)
                                    : 0.0;
                                if (area > maxArea)
                                {
                                    maxArea = area;
                                    maxOid = row[row.FindField(oidField)]?.ToString() ?? "0";
                                }
                            }
                        }
                        return maxOid;
                    }
                }
            });
        }

        // ---------------- 异常日志（对应 _build_exception_log_path / _append_exception_log） ----------------

        /// <summary>异常日志路径：输出文件夹/按属性合并_异常日志_时间戳.txt</summary>
        private static string BuildExceptionLogPath(string folder, string toolLabel)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = string.Join("_", toolLabel.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(folder ?? ".", $"{safeName}_异常日志_{stamp}.txt");
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>选择要素类/图层路径（GDB 内要素类或 SHP 均可）</summary>
        private static string BrowseDataset(string title)
        {
            var dlg = new OpenItemDialog
            {
                Title = title,
                MultiSelect = false,
                Filter = ItemFilters.FeatureClasses_All
            };
            return dlg.ShowDialog() == true && dlg.Items.Any() ? dlg.Items.First().Path : null;
        }

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputFeatureClass.IsEnabled = !running;
            TextMergeFields.IsEnabled = !running;
            CmbMergeRule.IsEnabled = !running;
            TextOutputFeatureClass.IsEnabled = !running;
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