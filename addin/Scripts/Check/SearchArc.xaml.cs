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
    /// 查找弧线段：检查所选图层要素几何中的曲线段（圆弧/椭圆弧 + 贝塞尔）。
    /// 与本项目其他工具风格一致：选 .gdb → 多选图层 → 结果落库（输出 GDB，可选）。
    ///
    /// 功能特性：
    /// 1. 数据源是 .gdb 路径（与唯一编码/面积重算一致）
    /// 2. 图层可多选批量检查
    /// 3. 同时检出贝塞尔段（SegmentType.Bezier）与圆弧/椭圆弧段（SegmentType.EllipticArc）
    /// 4. 结果要素带 来源OBJECTID/段类型 字段，可追溯到原图斑
    /// 5. 结果落用户指定输出库（留空仅统计）
    ///
    /// 铁律：Geodatabase 访问全部 QueuedTask 异步；事件处理先判空。
    /// </summary>
    public partial class SearchArc : ProWindow
    {
        private const string ToolLabel = "查找弧线段";

        private CancellationTokenSource _cts;
        private SearchArcHelp _help;

        public SearchArc()
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

            if (layers.Count == 0)
                MessageBox.Show("该数据库中未找到要素类。", ToolLabel);
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
            _help = new SearchArcHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 主流程：逐图层扫描曲线段（EllipticArc 圆弧/椭圆弧 + Bezier 贝塞尔）。
        /// 结果写入输出库线要素类 弧线段_{图层名}（已存在先删），字段：来源OBJECTID、段类型。
        /// </summary>
        private async Task RunSearchAsync(string gdbPath, List<string> layers, string outputGdb, CancellationToken ct)
        {
            long totalSegs = 0;

            Log($"数据库：{gdbPath}");
            Log($"图层数：{layers.Count}（{string.Join(", ", layers)}）");
            Log($"检查项：圆弧/椭圆弧段 + 贝塞尔曲线段");
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

                totalSegs += found;
                Log(found > 0
                    ? $"  {fcName}：发现 {found} 个曲线段" + (outputGdb != null ? $"（已写入 弧线段_{fcName}）" : "")
                    : $"  {fcName}：未发现曲线段");
            }

            Log("");
            Log(totalSegs > 0
                ? $"检查完成：共发现 {totalSegs} 个曲线段。"
                : "检查完成：全部图层均未发现曲线段。");
        }

        /// <summary>
        /// 单图层扫描：遍历要素各环各段，命中 EllipticArc/Bezier 段。
        /// 输出库不为空时，每段生成一条 Polyline 写入结果要素类（带来源OID与段类型）。
        /// </summary>
        private static async Task<long> ScanOneLayerAsync(
            string gdbPath, string fcName, string outputGdb, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            long found = 0;
            var hits = new List<(long Oid, string SegType, Polyline Line)>();

            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开数据库。");
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        using (RowCursor cursor = fc.Search(null, false))
                        {
                            while (cursor.MoveNext())
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!(cursor.Current is Feature feature)) continue;
                                Geometry geom = feature.GetShape();
                                if (!(geom is Multipart mp) || geom.IsEmpty) continue;

                                long oid = feature.GetObjectID();
                                foreach (ReadOnlySegmentCollection part in mp.Parts)
                                {
                                    foreach (Segment seg in part)
                                    {
                                        string segType;
                                        switch (seg.SegmentType)
                                        {
                                            case SegmentType.EllipticArc:
                                                segType = "圆弧/椭圆弧";
                                                break;
                                            case SegmentType.Bezier:
                                                segType = "贝塞尔";
                                                break;
                                            default:
                                                continue;   // 直线段不关心
                                        }
                                        found++;
                                        // 命中段转 Polyline 记录（写入输出库用）：
                                        // PolylineBuilderEx 构造器不收 Segment，需 AddSegment
                                        var builder = new PolylineBuilderEx();
                                        builder.AddSegment(seg);
                                        hits.Add((oid, segType, builder.ToGeometry()));
                                    }
                                }
                            }
                        }
                    }
                }
            });

            // 结果落库（GP 工具链：有则先删 → CopyFeatures 创建 + Append 追加）
            if (outputGdb != null && hits.Count > 0)
                await WriteHitsAsync(gdbPath, fcName, outputGdb, hits, ct);

            return found;
        }

        /// <summary>把命中段写入输出库（结果为内存临时 GDB 要素类再导出，字段带来源OID/段类型）</summary>
        private static async Task WriteHitsAsync(
            string gdbPath, string fcName, string outputGdb,
            List<(long Oid, string SegType, Polyline Line)> hits, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string outName = $"弧线段_{fcName}";
            if (outName.Length > 160) outName = outName.Substring(0, 160);
            string outPath = $"{outputGdb.TrimEnd('\\')}\\{outName}";

            // 结果要素类已存在则先删（重复运行覆盖）
            if (await GpHelper.ExistsDatasetAsync(outPath))
                await GpHelper.RunToolAsync("management.Delete",
                    ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(outPath), ct);

            // 结果写入走 SchemaBuilder 建表 + EditOperation 写行（本项目数据库比对同款路线验证可行，
            // 但比对工具的教训是 DDL 在 NuGet 引用下不可用 → 改用 GP：先 CopyFeatures 从源库拷贝
            // 命中 OID 的要素作为"载体"，再建结果线要素类的方案过于绕。
            // 简化方案：用 GP CreateFeatureclass 建空线表 → EditOperation 逐段插入。
            await GpHelper.RunToolAsync("management.CreateFeatureclass",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outputGdb, outName, "POLYLINE"), ct);
            await GpHelper.RunToolAsync("management.AddField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outPath, "来源OBJECTID", "LONG"), ct);
            await GpHelper.RunToolAsync("management.AddField",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(
                    outPath, "段类型", "TEXT", null, null, 20), ct);

            await QueuedTask.Run(() =>
            {
                using (var outGdb = GpHelper.OpenGeodatabase(outputGdb))
                {
                    if (outGdb == null) throw new InvalidOperationException("无法打开输出数据库。");
                    using (FeatureClass outFc = outGdb.OpenDataset<FeatureClass>(outName))
                    {
                        var edit = new EditOperation { Name = $"弧线段结果 {fcName}" };
                        edit.Callback(ctx =>
                        {
                            foreach (var (oid, segType, line) in hits)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                using (RowBuffer rb = outFc.CreateRowBuffer())
                                {
                                    rb["来源OBJECTID"] = oid;
                                    rb["段类型"] = segType;
                                    rb[outFc.GetDefinition().GetShapeField()] = line;
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
