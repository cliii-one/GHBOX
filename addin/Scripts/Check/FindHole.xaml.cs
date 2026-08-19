using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GHBoxAddIn.Scripts.GDB;

namespace GHBoxAddIn.Scripts.Check
{
    /// <summary>
    /// 检查空洞：通过"融合→线→面→融合→擦除"工具链提取面图层中的空洞图斑。
    /// 国土空间规划入库前质量检查中，空洞通常是拓扑错误或编辑残留，需定位后清理。
    ///
    /// 功能特性：
    /// 1. 数据源是 .gdb 路径（与查找弧线段/尖锐角一致）
    /// 2. 图层可多选批量检查；点/线图层自动跳过
    /// 3. 空洞提取原理（与用户手工流程一致）：
    ///    ① 融合面图层（结果1）——所有面合并为一个大面，内环空洞仍保留（PairwiseDissolve 成对融合加速）
    ///    ② 结果1 要素转线——面边界全部转为线，含空洞边界
    ///    ③ 线 转面——所有封闭区域变独立面，空洞区域变实心面
    ///    ④ 转面结果再融合（结果2）——实心面合并，空洞被填充（PairwiseDissolve 成对融合加速）
    ///    ⑤ 结果2 擦除 结果1——差集即空洞图斑（可能为多部件）
    ///    ⑥ 空洞 多部件转单部件——每个空洞独立成行，数量与面积才准确
    /// 4. 结果落用户指定输出库（留空仅统计），带"空洞面积"字段（椭球面积·平方米）
    ///
    /// 铁律：不改写源数据；临时数据集用唯一标记，运行后自动清理。
    /// </summary>
    public partial class FindHole : ProWindow
    {
        private const string ToolLabel = "检查空洞";

        private CancellationTokenSource _cts;
        private FindHoleHelp _help;

        public FindHole()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件：选库 ----------------

        /// <summary>选择输入数据库并枚举图层</summary>
        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            string gdb = PickGeodatabase("选择输入数据库");
            if (gdb == null) return;
            TextInputGdb.Text = gdb;
            await LoadLayersAsync(gdb);
        }

        /// <summary>选择结果输出数据库（留空则不落库，仅统计）</summary>
        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            string gdb = PickGeodatabase("选择结果输出数据库（.gdb）");
            if (gdb != null) TextOutputGdb.Text = gdb;
        }

        private static string PickGeodatabase(string title)
        {
            var dlg = new OpenItemDialog
            {
                Title = title,
                MultiSelect = false,
                Filter = ItemFilters.Geodatabases
            };
            return dlg.ShowDialog() == true && dlg.Items.Any() ? dlg.Items.First().Path : null;
        }

        /// <summary>枚举库内顶层要素类（跳过 GDB_ 系统表）填充图层列表</summary>
        private async Task LoadLayersAsync(string gdbPath)
        {
            List<string> layers = await QueuedTask.Run(() =>
            {
                var names = new List<string>();
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) return names;
                    foreach (FeatureClassDefinition def in gdb.GetDefinitions<FeatureClassDefinition>())
                        if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                            names.Add(def.GetName());
                }
                return names;
            });

            layers.Sort(StringComparer.OrdinalIgnoreCase);
            ListLayers.Items.Clear();
            foreach (string name in layers)
                ListLayers.Items.Add(name);
            UpdateSelectedCount(); // 清空后选区必然为空，计数归零

            if (layers.Count == 0)
                MessageBox.Show("该数据库中未找到要素类。", ToolLabel);
        }

        /// <summary>选中图层变化时刷新右侧"已选择 x 个图层"计数</summary>
        private void ListLayers_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateSelectedCount();
        }

        /// <summary>更新"已选择 x 个图层"计数显示（初始化期控件未就绪时先判空）</summary>
        private void UpdateSelectedCount()
        {
            if (TextSelCount == null) return;
            TextSelCount.Text = $"已选择 {ListLayers.SelectedItems?.Count ?? 0} 个图层";
        }

        // ---------------- 执行 ----------------

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var layers = ListLayers.SelectedItems?.Cast<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
                if (layers.Count == 0)
                {
                    MessageBox.Show("请先选择至少一个图层。", ToolLabel);
                    return;
                }

                string gdbPath = TextInputGdb.Text?.Trim().TrimEnd('\\');
                if (string.IsNullOrWhiteSpace(gdbPath) || !System.IO.Directory.Exists(gdbPath))
                {
                    MessageBox.Show("请先选择输入数据库。", ToolLabel);
                    return;
                }

                string outputGdb = TextOutputGdb.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(outputGdb) &&
                    (!outputGdb.ToLowerInvariant().EndsWith(".gdb") || !System.IO.Directory.Exists(outputGdb)))
                {
                    MessageBox.Show("输出数据库必须是已存在的 .gdb（留空则仅统计不落库）。", ToolLabel);
                    return;
                }

                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunSearchAsync(gdbPath, layers,
                    string.IsNullOrWhiteSpace(outputGdb) ? null : outputGdb, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消检查。");
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

        /// <summary>取消正在进行的检查</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new FindHoleHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 主流程：逐图层用"融合→线→面→融合→擦除"工具链提取空洞图斑。
        /// 结果写入输出库面要素类 空洞_{图层名}（已存在先删），字段：空洞面积（椭球面积·平方米）。
        /// </summary>
        private async Task RunSearchAsync(string gdbPath, List<string> layers, string outputGdb, CancellationToken ct)
        {
            long totalHoles = 0;

            Log($"数据库：{gdbPath}");
            Log($"图层数：{layers.Count}（{string.Join(", ", layers)}）");
            Log($"检查项：面要素空洞图斑（融合→线→面→融合→擦除→拆单部件）");
            Log("");

            for (int i = 0; i < layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string fcName = layers[i];
                SetProgress(100.0 * i / layers.Count, $"[{i + 1}/{layers.Count}] 检查图层：{fcName}");

                long found;
                try
                {
                    found = await ScanOneLayerAsync(gdbPath, fcName, outputGdb, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogError($"图层 {fcName} 检查失败：{ex.Message}");
                    continue;
                }

                if (found < 0)
                {
                    // -1 表示非面图层，扫描内部已跳过
                    Log($"  {fcName}：非面图层，跳过（本工具仅检查面要素）");
                    continue;
                }

                totalHoles += found;
                Log(found > 0
                    ? $"  {fcName}：发现 {found} 个空洞" + (outputGdb != null ? $"（已写入 空洞_{fcName}）" : "")
                    : $"  {fcName}：未发现空洞图斑");
            }

            Log("");
            Log(totalHoles > 0
                ? $"检查完成：共发现 {totalHoles} 个空洞。"
                : "检查完成：全部图层均未发现空洞图斑。");
            // 全部图层处理完毕，进度条置满（循环内最后一次进度只到 (N-1)/N）
            SetProgress(100, "检查完成");
        }

        /// <summary>
        /// 单图层空洞提取：用 GP 工具链"融合→要素转线→要素转面→融合→擦除→拆单部件"实现。
        /// 原理（与用户手工流程完全一致）：
        ///   ① 融合源面（PairwiseDissolve，多对一）→ 结果1（所有面合并，内环空洞保留）
        ///   ② 结果1 要素转线 → 线（面边界全部转线，含空洞边界）
        ///   ③ 线 要素转面 → 面2（所有封闭区域变实心面，空洞被填充）
        ///   ④ 面2 融合（PairwiseDissolve，多对一）→ 结果2（实心面合并为大面）
        ///   ⑤ 结果2 擦除 结果1 → 空洞（差集即空洞图斑，可能为多部件）
        ///   ⑥ 空洞 多部件转单部件 → 每个空洞独立成行，数量与面积才准确
        /// 融合用"成对融合"PairwiseDissolve（Analysis 工具箱，默认并行处理，比经典 Dissolve 更快）；
        /// 官方说明其输出与 Dissolve 相似可互换，但内部实现不同、输出几何会有细微差异。
        /// 不指定字段时全部要素一次融合成一个大面（内环空洞保留），等效且速度更快。
        /// 所有中间数据写入输出库（或源库）的临时要素类，带 _tmp_ 前缀+标记，运行后清理。
        /// 返回 -1 表示非面图层（跳过）；否则返回空洞个数。
        /// </summary>
        private static async Task<long> ScanOneLayerAsync(
            string gdbPath, string fcName, string outputGdb, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // 判断图层几何类型并获取源 SR（GP 工具需要）
            string fcPath = $"{gdbPath.TrimEnd('\\')}\\{fcName}";
            string srWkid = "";
            bool isPolygonLayer = false;

            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开数据库。");
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        isPolygonLayer = fc.GetDefinition().GetShapeType() == GeometryType.Polygon;
                        if (!isPolygonLayer) return;
                        SpatialReference sourceSr = fc.GetDefinition().GetSpatialReference();
                        srWkid = (sourceSr != null && sourceSr.Wkid > 0) ? sourceSr.Wkid.ToString() : "";
                    }
                }
            });

            if (!isPolygonLayer)
                return -1;

            // 临时库：结果输出库优先，否则放源库
            string tmpGdb = !string.IsNullOrWhiteSpace(outputGdb) ? outputGdb : gdbPath;
            // 用时间戳标记本次临时数据，避免并发冲突
            string tag = DateTime.Now.ToString("HHmmss");
            string s1 = $"_tmp_diss_{tag}";       // ① 融合结果1
            string s2 = $"_tmp_lin_{tag}";        // ② 要素转线
            string s3 = $"_tmp_fac_{tag}";        // ③ 要素转面
            string s4 = $"_tmp_dis2_{tag}";       // ④ 融合结果2
            string s5 = $"_tmp_hole_{tag}";       // ⑤ 擦除结果（可能多部件）
            string sHole = $"空洞_{fcName}";      // ⑥ 拆单部件后的最终空洞结果

            // 临时数据存在先删（防止上次残留）
            foreach (string tmp in new[] { s1, s2, s3, s4, s5, sHole })
            {
                string p = $"{tmpGdb.TrimEnd('\\')}\\{tmp}";
                if (await GpHelper.ExistsDatasetAsync(p))
                    await GpHelper.RunToolAsync("management.Delete",
                        ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(p), ct);
            }

            // ① 融合源面（整面合并，PairwiseDissolve 不指定字段 → 全部要素融合成一个面，内环空洞保留）
            //    源面直接参与融合：多部件图斑的间隙本来就是要素内部空隙，融合后并不会因此漏判，
            //    无需预先拆单部件（拆单部件仅在最后一步对空洞结果执行一次）
            await GpHelper.RunToolAsync("analysis.PairwiseDissolve",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    fcPath, $"{tmpGdb.TrimEnd('\\')}\\{s1}"), ct);

            // ② 结果1 要素转线 → 线（面边界全部转线，含空洞边界）
            await GpHelper.RunToolAsync("management.FeatureToLine",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    $"{tmpGdb.TrimEnd('\\')}\\{s1}", $"{tmpGdb.TrimEnd('\\')}\\{s2}"), ct);

            // ③ 线 要素转面 → 面2（所有封闭区域变实心面，空洞被填充）
            await GpHelper.RunToolAsync("management.FeatureToPolygon",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    $"{tmpGdb.TrimEnd('\\')}\\{s2}", $"{tmpGdb.TrimEnd('\\')}\\{s3}"), ct);

            // ④ 面2 融合（整面合并，PairwiseDissolve）→ 结果2（实心面合并为大面）
            await GpHelper.RunToolAsync("analysis.PairwiseDissolve",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    $"{tmpGdb.TrimEnd('\\')}\\{s3}", $"{tmpGdb.TrimEnd('\\')}\\{s4}"), ct);

            // ⑤ 结果2 擦除 结果1 → 空洞（差集即空洞图斑，可能为多部件）
            await GpHelper.RunToolAsync("analysis.Erase",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    $"{tmpGdb.TrimEnd('\\')}\\{s4}",      // 输入：结果2（实心大面）
                    $"{tmpGdb.TrimEnd('\\')}\\{s1}",      // 擦除要素：结果1（原融合面，空洞保留）
                    $"{tmpGdb.TrimEnd('\\')}\\{s5}"), ct);  // 输出：空洞（中间结果）

            // ⑥ 空洞 多部件转单部件：每个空洞独立成行，数量与面积才准确
            //    （否则一个多部件空洞只算 1 个，面积却是多部件合计）
            await GpHelper.RunToolAsync("management.MultipartToSinglepart",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    $"{tmpGdb.TrimEnd('\\')}\\{s5}", $"{tmpGdb.TrimEnd('\\')}\\{sHole}"), ct);

            // 清理临时数据（①~⑤）
            foreach (string tmp in new[] { s1, s2, s3, s4, s5 })
            {
                string p = $"{tmpGdb.TrimEnd('\\')}\\{tmp}";
                if (await GpHelper.ExistsDatasetAsync(p))
                    await GpHelper.RunToolAsync("management.Delete",
                        ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(p), ct);
            }

            // 统计空洞个数 + 计算椭球面积 + 确保输出 FC 有坐标系和面积字段
            long found = await FinalizeHolesAsync(tmpGdb, sHole, srWkid, ct);

            // 若用户没指定输出库，临时空洞结果也清理掉（已统计完即可）
            if (outputGdb == null)
            {
                string p = $"{tmpGdb.TrimEnd('\\')}\\{sHole}";
                if (await GpHelper.ExistsDatasetAsync(p))
                    await GpHelper.RunToolAsync("management.Delete",
                        ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(p), ct);
            }

            return found;
        }

        /// <summary>
        /// 给空洞结果要素类补坐标系和"空洞面积"字段，并统计行数。
        /// 椭球面积用 GP 的"添加几何信息"（AddGeometryAttributes）的 AREA_GEODESIC：
        /// 该选项在任意坐标系下都按测地线算法计算椭球面积（平方米），输出字段名固定为 AREA_GEO。
        /// </summary>
        private static async Task<long> FinalizeHolesAsync(
            string tmpGdb, string holeFcName, string srWkid, CancellationToken ct)
        {
            string holePath = $"{tmpGdb.TrimEnd('\\')}\\{holeFcName}";

            // 若 SR 已知，用 DefineProjection 确保输出 FC 坐标系正确（Erase 输出可能继承不了 SR）
            if (!string.IsNullOrEmpty(srWkid))
                await GpHelper.RunToolAsync("management.DefineProjection",
                    ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(holePath, srWkid), ct);

            // 加"空洞面积"字段，用 AddGeometryAttributes 的 AREA_GEODESIC 填充
            //   AREA_GEODESIC 在任意坐标系下都按测地线算法算椭球面积（平方米），字段名固定为 AREA_GEO
            await GpHelper.RunToolAsync("management.AddField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    holePath, "空洞面积", "DOUBLE"), ct);
            await GpHelper.RunToolAsync("management.AddGeometryAttributes",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    holePath, "AREA_GEODESIC"), ct);
            // AddGeometryAttributes 生成的字段名固定为 AREA_GEO，把它的值拷贝到"空洞面积"
            //   用 CalculateField 计算：[空洞面积] = [AREA_GEO]，并四舍五入到两位小数
            await GpHelper.RunToolAsync("management.CalculateField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    holePath, "空洞面积", "round(!AREA_GEO!, 2)", "PYTHON3"), ct);

            // 统计行数
            return await GpHelper.GetCountAsync(holePath, ct);
        }

        // ---------------- 界面辅助 ----------------

        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputGdb.IsEnabled = !running;
            TextOutputGdb.IsEnabled = !running;
            ListLayers.IsEnabled = !running;
            BtnCancel.IsEnabled = running;
        }

        private void SetProgress(double percent, string message)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.Value = percent;
                Log(message);
            });
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TextLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TextLog.ScrollToEnd();
            });
        }

        private void LogError(string message) => Log("[错误] " + message);
    }
}