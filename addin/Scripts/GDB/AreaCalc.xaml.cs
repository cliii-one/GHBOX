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

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 面积重算：按椭球面积（测地线）给所选图层要素计算面积并写入面积字段。
    /// 业务逻辑与《面积重算.pyt》的 AreaCalcTool 保持一致：
    /// 1. 选库 → 多选图层 → 面积字段（只列所选图层公共的双精度/浮点字段）
    /// 2. 面积单位：平方米 / 公顷 / 平方公里 / 亩 / 万亩
    /// 3. 计算：GeometryEngine.GeodesicArea（椭球面积，返回平方米）按单位换算
    /// 4. 写入：EditOperation.Callback 游标直写，单图层一事务，失败整图层回滚
    ///
    /// 重要（铁律，同唯一编码）：
    /// - 所有 Geodatabase/Geometry 访问必须 QueuedTask 异步，严禁 UI 线程 .Result/.Wait()（死锁）
    /// - XAML 默认值会在 InitializeComponent 期间触发事件，事件处理必须先判空（NRE 闪退）
    /// </summary>
    public partial class AreaCalc : ProWindow
    {
        private const string ToolLabel = "面积重算";

        /// <summary>面积单位（每 m² 换算系数）</summary>
        private static readonly (string Name, double Factor)[] Units =
        {
            ("平方米", 1.0),
            ("公顷", 1.0 / 10000.0),
            ("平方公里", 1.0 / 1000000.0),
            ("亩", 3.0 / 2000.0),
            ("万亩", 3.0 / 20000000.0),
        };

        /// <summary>小数位数选项：显示文本 → 位数（-1 = 不保留，写原始值）</summary>
        private static readonly (string Name, int Digits)[] RoundingOptions =
        {
            ("不保留（原始值）", -1),
            ("保留 2 位小数", 2),
            ("保留 4 位小数", 4),
        };

        private CancellationTokenSource _cts;
        private AreaCalcHelp _help;
        private bool _updatingField;
        private int _fieldRequestSeq;

        public AreaCalc()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载后设置单位与小数位默认值——不在 XAML 里设，规避初始化期事件触发</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (ComboUnit.Items.Count == 0)
            {
                foreach (var (name, _) in Units)
                    ComboUnit.Items.Add(name);
                ComboUnit.SelectedIndex = 0;
            }
            if (ComboDigits.Items.Count == 0)
            {
                foreach (var (name, _) in RoundingOptions)
                    ComboDigits.Items.Add(name);
                ComboDigits.SelectedIndex = 0;   // 默认：不保留（原始值）
            }
        }

        // ---------------- 界面事件：选库 ----------------

        /// <summary>选择输入数据库并枚举图层（异步）</summary>
        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择输入数据库",
                MultiSelect = false,
                Filter = ItemFilters.Geodatabases
            };
            if (dlg.ShowDialog() != true || !dlg.Items.Any()) return;

            string gdb = dlg.Items.First().Path;
            if (!gdb.ToLowerInvariant().EndsWith(".gdb"))
            {
                MessageBox.Show("仅支持 .gdb 文件地理数据库。", ToolLabel);
                return;
            }
            TextInputGdb.Text = gdb;
            await LoadLayersAsync(gdb);
        }

        /// <summary>枚举库内顶层要素类填充图层列表（跳过 GDB_ 系统表）</summary>
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
            _updatingField = true;
            ListLayers.Items.Clear();
            foreach (string name in layers)
                ListLayers.Items.Add(name);
            ComboField.Items.Clear();
            ComboField.SelectedItem = null;
            _updatingField = false;

            if (layers.Count == 0)
                MessageBox.Show("该数据库中未找到要素类。", ToolLabel);
        }

        // ---------------- 界面事件：字段联动 ----------------

        /// <summary>
        /// 图层选择变化 → 面积字段下拉填充所选图层公共的双精度/浮点字段（交集）。
        /// 面积字段选择逻辑：只列 Double/Single 型；默认按 MJ、MJA、Shape_Area 顺序找第一个存在的。
        /// </summary>
        private async void ListLayers_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateSelectedCount();   // 先刷新计数（清列表时 _updatingField 为真也会走到这里，计数归零）
            if (_updatingField) return;

            var selected = ListLayers.SelectedItems?.Cast<string>().ToList() ?? new List<string>();
            if (selected.Count == 0)
            {
                _updatingField = true;
                ComboField.Items.Clear();
                ComboField.SelectedItem = null;
                _updatingField = false;
                return;
            }

            string gdbPath = TextInputGdb.Text?.Trim();
            if (string.IsNullOrEmpty(gdbPath)) return;

            int requestSeq = ++_fieldRequestSeq;

            List<FieldInfo> common = await QueuedTask.Run(() =>
            {
                List<FieldInfo> inter = null;
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) return new List<FieldInfo>();
                    foreach (string fcName in selected)
                    {
                        using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                        {
                            var fields = fc.GetDefinition().GetFields()
                                .Where(f => IsAreaField(f))
                                .Select(f => new FieldInfo { Name = f.Name, Type = f.FieldType })
                                .ToList();
                            inter = inter == null ? fields
                                : inter.Where(a => fields.Any(b => b.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                        }
                    }
                }
                return inter ?? new List<FieldInfo>();
            });

            if (requestSeq != _fieldRequestSeq) return;   // 过期结果，丢弃

            _updatingField = true;
            ComboField.Items.Clear();
            // 默认选中常见面积字段名（国土惯例），否则第一个
            FieldInfo prefer = null;
            foreach (string candidate in new[] { "MJ", "MJA", "SHAPE_AREA" })
            {
                prefer = common.FirstOrDefault(f => f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (prefer != null) break;
            }
            if (prefer == null) prefer = common.FirstOrDefault();
            foreach (FieldInfo f in common.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                ComboField.Items.Add(f);
            if (prefer != null) ComboField.SelectedItem = prefer;
            _updatingField = false;
        }

        /// <summary>可写入面积的字段：双精度或浮点（文本存数值会失真、整型精度不足）</summary>
        private static bool IsAreaField(Field f)
        {
            FieldType t = f.FieldType;
            return t == FieldType.Double || t == FieldType.Single;
        }

        /// <summary>更新"已选择 x 个图层"计数显示（初始化期控件未就绪时先判空）</summary>
        private void UpdateSelectedCount()
        {
            if (TextSelCount == null) return;
            TextSelCount.Text = $"已选择 {ListLayers.SelectedItems?.Count ?? 0} 个图层";
        }

        /// <summary>字段信息（界面下拉项）</summary>
        private sealed class FieldInfo
        {
            public string Name { get; set; }
            public FieldType Type { get; set; }

            public override string ToString() => Type == FieldType.Double ? $"{Name}（双精度）" : $"{Name}（单精度）";
        }

        private void ComboField_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
        }

        private void ComboUnit_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
        }

        // ---------------- 执行 ----------------

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var layers = ListLayers.SelectedItems.Cast<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (layers.Count == 0)
                {
                    MessageBox.Show("请先选择至少一个图层。", ToolLabel);
                    return;
                }

                if (!(ComboField.SelectedItem is FieldInfo field))
                {
                    MessageBox.Show("请选择面积字段（双精度/浮点型）。", ToolLabel);
                    return;
                }

                if (ComboUnit.SelectedIndex < 0)
                {
                    MessageBox.Show("请选择面积单位。", ToolLabel);
                    return;
                }
                if (ComboDigits.SelectedIndex < 0)
                {
                    MessageBox.Show("请选择小数位数。", ToolLabel);
                    return;
                }

                string gdbPath = TextInputGdb.Text?.Trim().TrimEnd('\\');
                var (unitName, factor) = Units[ComboUnit.SelectedIndex];
                int digits = RoundingOptions[ComboDigits.SelectedIndex].Digits;

                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunCalcAsync(gdbPath, layers, field.Name, unitName, factor, digits, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消计算。");
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

        /// <summary>取消正在进行的计算</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开面积重算工具的使用说明窗口（单例）</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new AreaCalcHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 计算主流程（对应 .pyt execute）：逐图层按椭球面积计算并写入。
        /// 每图层一个 EditOperation（单图层失败只回滚该图层）。
        /// </summary>
        private async Task RunCalcAsync(
            string gdbPath, List<string> layers, string fieldName,
            string unitName, double factor, int digits, CancellationToken ct)
        {
            long totalFeatures = 0;
            long totalSkipped = 0;

            Log($"数据库：{gdbPath}");
            Log($"图层数：{layers.Count}（{string.Join(", ", layers)}）");
            string digitsNote = digits < 0 ? "不保留（原始值）" : $"保留 {digits} 位小数";
            Log($"面积字段：{fieldName}；面积单位：{unitName}；小数位：{digitsNote}；口径：椭球面积（测地线）");
            Log("");

            for (int i = 0; i < layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string fcName = layers[i];
                SetProgress(100.0 * i / layers.Count, $"[{i + 1}/{layers.Count}] 计算图层：{fcName}");

                (long written, long skipped) result;
                try
                {
                    result = await CalcOneLayerAsync(gdbPath, fcName, fieldName, factor, digits, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogError($"图层 {fcName} 计算失败（已回滚该图层）：{ex.Message}");
                    continue;
                }

                totalFeatures += result.written;
                totalSkipped += result.skipped;
                string skipNote = result.skipped > 0 ? $"，跳过空几何 {result.skipped} 条" : "";
                Log($"  {fcName}：完成 {result.written} 条{skipNote}");
            }

            Log("");
            Log($"面积重算完成：{layers.Count} 个图层共写入 {totalFeatures} 条" +
                (totalSkipped > 0 ? $"（跳过空几何 {totalSkipped} 条）" : "") + "。");
            // 全部图层处理完毕，进度条置满（循环内最后一次进度只到 (N-1)/N）
            SetProgress(100, "面积重算完成");
        }

        /// <summary>
        /// 单图层计算：GeometryEngine.GeodesicArea 得平方米 → 按单位系数换算 →（可选）四舍五入保留小数位 → 写入字段。
        /// 空几何跳过计数；EditOperation.Callback 游标直写，异常整图层回滚。
        /// </summary>
        private static async Task<(long written, long skipped)> CalcOneLayerAsync(
            string gdbPath, string fcName, string fieldName, double factor, int digits, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            long written = 0;
            long skipped = 0;

            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开数据库。");
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        var edit = new EditOperation { Name = $"面积重算 {fcName}" };
                        edit.Callback(context =>
                        {
                            using (RowCursor cursor = fc.Search(null, false))
                            {
                                while (cursor.MoveNext())
                                {
                                    ct.ThrowIfCancellationRequested();
                                    Row row = cursor.Current;
                                    var geom = row[row.FindField(fc.GetDefinition().GetShapeField())] as Geometry;
                                    if (geom == null || geom.IsEmpty)
                                    {
                                        skipped++;
                                        continue;
                                    }
                                    double areaM2 = GeometryEngine.Instance.GeodesicArea(geom); // 椭球面积（平方米）
                                    double value = areaM2 * factor;
                                    // 小数位保留：digits < 0 不处理（原始值），否则四舍五入到指定位数
                                    if (digits >= 0)
                                        value = Math.Round(value, digits, MidpointRounding.AwayFromZero);
                                    row[fieldName] = value;
                                    row.Store();
                                    written++;
                                }
                            }
                        }, fc);

                        if (!edit.Execute())
                            throw new InvalidOperationException(edit.ErrorMessage ?? "写入失败。");
                    }
                }
            });

            return (written, skipped);
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputGdb.IsEnabled = !running;
            ListLayers.IsEnabled = !running;
            ComboField.IsEnabled = !running;
            ComboUnit.IsEnabled = !running;
            ComboDigits.IsEnabled = !running;
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

        /// <summary>线程安全写错误日志（前缀区分）</summary>
        private void LogError(string message) => Log("[错误] " + message);
    }
}
