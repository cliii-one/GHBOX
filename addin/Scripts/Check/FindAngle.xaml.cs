using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Editing;
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
    /// 查找尖锐角：检查所选图层要素几何中夹角小于阈值的顶点（尖锐角/钉子角）。
    /// 与本项目风格一致：选 .gdb → 多选图层 → 结果落输出库。
    ///
    /// 核心算法：
    /// 顶点内角 = |180° − 转向角|，转向角 = atan2(出边) − atan2(入边)，归一化到 [0,180]；
    /// 内角 &lt; 阈值即命中（默认 10°）。
    ///
    /// 功能特性：
    /// 1. 数据源是 .gdb 路径
    /// 2. 图层可多选批量
    /// 3. 线要素同样检查（沿线顶点夹角）
    /// 4. 阈值单位度、默认 10，窗口即时预览说明
    /// 5. 结果点要素带 来源OBJECTID/夹角度数/X/Y 坐标
    /// 6. 落用户指定输出库（留空仅统计）
    ///
    /// 铁律：Geodatabase 访问全部 QueuedTask 异步；事件处理先判空。
    /// </summary>
    public partial class FindAngle : ProWindow
    {
        private const string ToolLabel = "查找尖锐角";

        private CancellationTokenSource _cts;
        private FindAngleHelp _help;

        public FindAngle()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件 ----------------

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            string gdb = PickGeodatabase("选择输入数据库");
            if (gdb == null) return;
            TextInputGdb.Text = gdb;
            await LoadLayersAsync(gdb);
        }

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

        private void TextAngle_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (TextHint == null || TextAngle == null) return;   // 初始化期判空（XAML 默认值会提前触发事件）
            UpdateHint();
        }

        /// <summary>底部提示条：说明当前阈值语义</summary>
        private void UpdateHint()
        {
            if (double.TryParse(TextAngle.Text?.Trim(), out double angle) && angle > 0 && angle < 180)
                TextHint.Text = $"当前阈值 {angle}°：顶点内角小于 {angle}° 的顶点将被标记（内角=|180°−转向角|，直线为 180° 不命中）";
            else
                TextHint.Text = "示例：阈值 10 → 夹角小于 10° 的顶点（如折返的钉子角）被标记";
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

                if (!double.TryParse(TextAngle.Text?.Trim(), out double threshold) || threshold <= 0 || threshold >= 180)
                {
                    MessageBox.Show("角度阈值须为 0~180 之间的数（不含端点）。", ToolLabel);
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
                await RunFindAsync(gdbPath, layers, threshold,
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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new FindAngleHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        private async Task RunFindAsync(string gdbPath, List<string> layers, double threshold,
            string outputGdb, CancellationToken ct)
        {
            long totalHits = 0;

            Log($"数据库：{gdbPath}");
            Log($"图层数：{layers.Count}（{string.Join(", ", layers)}）");
            Log($"角度阈值：{threshold}°（顶点内角 < 阈值即命中）");
            Log("");

            for (int i = 0; i < layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string fcName = layers[i];
                SetProgress(100.0 * i / layers.Count, $"[{i + 1}/{layers.Count}] 检查图层：{fcName}");

                long found;
                try
                {
                    found = await ScanOneLayerAsync(gdbPath, fcName, threshold, outputGdb, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogError($"图层 {fcName} 检查失败：{ex.Message}");
                    continue;
                }

                totalHits += found;
                Log(found > 0
                    ? $"  {fcName}：发现 {found} 个尖锐角顶点" + (outputGdb != null ? $"（已写入 尖锐角_{fcName}）" : "")
                    : $"  {fcName}：未发现尖锐角");
            }

            Log("");
            Log(totalHits > 0
                ? $"检查完成：共发现 {totalHits} 个尖锐角顶点。"
                : "检查完成：全部图层均未发现尖锐角。");
            // 全部图层处理完毕，进度条置满（循环内最后一次进度只到 (N-1)/N）
            SetProgress(100, "检查完成");
        }

        /// <summary>命中记录：尖锐角顶点及其角度、所属要素</summary>
        private sealed class AngleHit
        {
            public long Oid;
            public MapPoint Point;
            public double InteriorAngle;
        }

        /// <summary>
        /// 单图层扫描：遍历要素各环顶点序列，逐点算内角，小于阈值命中。
        /// 面与线均处理；环闭合（首尾点重复参与角度计算）。
        /// </summary>
        private static async Task<long> ScanOneLayerAsync(
            string gdbPath, string fcName, double threshold, string outputGdb, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var hits = new List<AngleHit>();
            string srWkid = "";   // 源要素类空间参考 WKID，传给输出 FC 避免无坐标系导致几何退化/不套合

            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开数据库。");
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        // 获取源要素类空间参考，供输出 FC 使用（避免输出 FC 无坐标系导致几何退化/不套合）
                        SpatialReference sourceSr = fc.GetDefinition().GetSpatialReference();
                        srWkid = (sourceSr != null && sourceSr.Wkid > 0) ? sourceSr.Wkid.ToString() : "";

                        using (RowCursor cursor = fc.Search(null, false))
                        {
                            while (cursor.MoveNext())
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!(cursor.Current is Feature feature)) continue;
                                Geometry geom = feature.GetShape();
                                if (geom == null || geom.IsEmpty) continue;

                                long oid = feature.GetObjectID();
                                bool isPolygon = geom is Polygon;

                                foreach (ReadOnlySegmentCollection part in ((Multipart)geom).Parts)
                                {
                                    // 段端点连成顶点序列（StartCoordinate/EndCoordinate 是 Coordinate2D，
                                    // 需转 MapPoint 参与角度计算与落库）
                                    var pts = new List<MapPoint>();
                                    foreach (Segment seg in part)
                                    {
                                        if (pts.Count == 0) pts.Add(MapPointBuilderEx.CreateMapPoint(seg.StartCoordinate));
                                        pts.Add(MapPointBuilderEx.CreateMapPoint(seg.EndCoordinate));
                                    }
                                    if (pts.Count < 3) continue;

                                    // 面环闭合：尾接首，使首尾顶点也有前后邻居
                                    if (isPolygon)
                                    {
                                        pts.Add(pts[0]);
                                        pts.Insert(0, pts[pts.Count - 2]);
                                    }

                                    for (int k = 1; k < pts.Count - 1; k++)
                                    {
                                        double interior = InteriorAngle(pts[k - 1], pts[k], pts[k + 1]);
                                        if (interior < threshold)
                                            hits.Add(new AngleHit { Oid = oid, Point = pts[k], InteriorAngle = interior });
                                    }
                                }
                            }
                        }
                    }
                }
            });

            if (outputGdb != null && hits.Count > 0)
                await WriteHitsAsync(fcName, outputGdb, hits, srWkid, ct);

            return hits.Count;
        }

        /// <summary>
        /// 顶点内角：入边/出边方向角差（转向角）→ 内角 = |180° − 转向角|。
        /// 直线段转向角 0 → 内角 180；折返钉子角转向角近 180 → 内角近 0。
        /// </summary>
        private static double InteriorAngle(MapPoint prev, MapPoint cur, MapPoint next)
        {
            // 重合点（零长段）无方向，跳过返回 180（不命中）
            if (prev.IsEqual(cur) || cur.IsEqual(next)) return 180.0;

            double a1 = Math.Atan2(cur.Y - prev.Y, cur.X - prev.X);
            double a2 = Math.Atan2(next.Y - cur.Y, next.X - cur.X);
            double turn = Math.Abs((a2 - a1) * 180.0 / Math.PI);
            if (turn > 180.0) turn = 360.0 - turn;
            return Math.Abs(180.0 - turn);
        }

        /// <summary>命中顶点写入输出库点要素类 尖锐角_{图层名}（先删后建）</summary>
        private static async Task WriteHitsAsync(
            string fcName, string outputGdb, List<AngleHit> hits, string srWkid, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string outName = $"尖锐角_{fcName}";
            if (outName.Length > 160) outName = outName.Substring(0, 160);
            string outPath = $"{outputGdb.TrimEnd('\\')}\\{outName}";

            if (await GpHelper.ExistsDatasetAsync(outPath))
                await GpHelper.RunToolAsync("management.Delete",
                    ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(outPath), ct);

            await GpHelper.RunToolAsync("management.CreateFeatureclass",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outputGdb, outName, "POINT"), ct);
            // 关键：用 DefineProjection 给输出 FC 设置源数据坐标系，
            //   否则输出 FC 无坐标系，写入的几何会退化为空，且加载到地图后无法与源图层套合
            if (!string.IsNullOrEmpty(srWkid))
            {
                await GpHelper.RunToolAsync("management.DefineProjection",
                    ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(outPath, srWkid), ct);
            }
            await GpHelper.RunToolAsync("management.AddField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outPath, "来源OBJECTID", "LONG"), ct);
            await GpHelper.RunToolAsync("management.AddField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outPath, "夹角度数", "DOUBLE"), ct);

            await QueuedTask.Run(() =>
            {
                using (var outGdb = GpHelper.OpenGeodatabase(outputGdb))
                {
                    if (outGdb == null) throw new InvalidOperationException("无法打开输出数据库。");
                    using (FeatureClass outFc = outGdb.OpenDataset<FeatureClass>(outName))
                    {
                        var edit = new EditOperation { Name = $"尖锐角结果 {fcName}" };
                        edit.Callback(ctx =>
                        {
                            foreach (AngleHit hit in hits)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                using (RowBuffer rb = outFc.CreateRowBuffer())
                                {
                                    rb["来源OBJECTID"] = hit.Oid;
                                    rb["夹角度数"] = Math.Round(hit.InteriorAngle, 2);
                                    rb[outFc.GetDefinition().GetShapeField()] = hit.Point;
                                    outFc.CreateRow(rb)?.Dispose();
                                }
                            }
                        }, outFc);
                        if (!edit.Execute())
                            throw new InvalidOperationException(edit.ErrorMessage ?? "结果写入失败。");
                    }
                }
            });
        }

        // ---------------- 界面辅助 ----------------

        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputGdb.IsEnabled = !running;
            TextOutputGdb.IsEnabled = !running;
            ListLayers.IsEnabled = !running;
            TextAngle.IsEnabled = !running;
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
