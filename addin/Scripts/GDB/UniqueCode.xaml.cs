using ArcGIS.Core.Data;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 唯一编码：按顺序给所选图层的要素写入唯一编码（标识码）。
    /// 编码 = 编码开头 + 序号（左补零至 编码长度-开头长度 位）。
    /// 业务逻辑与《唯一编码.pyt》的 UniqueCodeTool 保持一致：
    /// 1. 选库 → 多选图层 → 编码字段（所选图层公共可写字段，下拉选择非手输）
    /// 2. 每图层独立编号 / 跨图层连续编号两种方式
    /// 3. 校验：开头纯数字、长度匹配、序号容量、字段类型（文本或整型）
    /// 4. 写入：EditOperation.EditCallback 单图层一事务，异常整图层回滚
    ///
    /// 重要：所有 Geodatabase 访问必须在 QueuedTask 上异步执行，
    /// 严禁 UI 线程同步等待（.Result / .Wait()）——会导致 Pro 死锁假死。
    /// </summary>
    public partial class UniqueCode : ProWindow
    {
        private const string ToolLabel = "唯一编码";

        private CancellationTokenSource _cts;
        private UniqueCodeHelp _help;
        private bool _updatingField;

        public UniqueCode()
        {
            InitializeComponent();
        }

        // ---------------- 界面事件：选库 ----------------

        /// <summary>选择输入数据库并枚举图层（异步，不阻塞 UI）</summary>
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

        /// <summary>枚举库内顶层要素类填充图层列表（跳过 GDB_ 系统表），QueuedTask 异步执行</summary>
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
            _updatingField = true;   // 清列表会触发 SelectionChanged，避免空刷新
            ListLayers.Items.Clear();
            foreach (string name in layers)
                ListLayers.Items.Add(name);
            ComboField.Items.Clear();
            ComboField.SelectedItem = null;
            _updatingField = false;
            UpdatePreview();

            if (layers.Count == 0)
                MessageBox.Show("该数据库中未找到要素类。", ToolLabel);
        }

        // ---------------- 界面事件：字段联动 ----------------

        /// <summary>
        /// 图层选择变化 → 编码字段下拉填充所选图层的公共可写字段（文本/整型）。
        /// 编码字段选择逻辑：多图层时只列交集，避免某图层无该字段导致写入失败。
        /// 异步执行（Geodatabase 访问必须在 QueuedTask 上）。
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
                UpdatePreview();
                return;
            }

            string gdbPath = TextInputGdb.Text?.Trim();
            if (string.IsNullOrEmpty(gdbPath)) return;

            // 记录本次请求序号：异步回来时若用户已改选其他图层，丢弃过期结果
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
                                .Where(f => IsWritableCodeField(f))
                                .Select(f => new FieldInfo { Name = f.Name, Type = f.FieldType, Length = f.Length })
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
            FieldInfo prefer = common.FirstOrDefault(f => f.Name.Equals("BSM", StringComparison.OrdinalIgnoreCase))
                               ?? common.FirstOrDefault();
            foreach (FieldInfo f in common.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                ComboField.Items.Add(f);
            if (prefer != null) ComboField.SelectedItem = prefer;
            _updatingField = false;

            UpdatePreview();
        }

        /// <summary>字段联动请求序号（防异步过期结果覆盖新选择）</summary>
        private int _fieldRequestSeq;

        /// <summary>更新"已选择 x 个图层"计数显示（初始化期控件未就绪时先判空）</summary>
        private void UpdateSelectedCount()
        {
            if (TextSelCount == null) return;
            TextSelCount.Text = $"已选择 {ListLayers.SelectedItems?.Count ?? 0} 个图层";
        }

        /// <summary>可写入编码的字段：文本型或整型（双精度可能失真、OID/GlobalID 系统字段不可写）</summary>
        private static bool IsWritableCodeField(Field f)
        {
            FieldType t = f.FieldType;
            return t == FieldType.String ||
                   t == FieldType.Integer ||
                   t == FieldType.BigInteger;
        }

        /// <summary>字段信息（界面下拉项）</summary>
        private sealed class FieldInfo
        {
            public string Name { get; set; }
            public FieldType Type { get; set; }
            public int Length { get; set; }

            public override string ToString() => Type == FieldType.String ? $"{Name}（文本 {Length}）" : $"{Name}（整型）";
        }

        private void ComboField_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_updatingField) return;
            UpdatePreview();
        }

        // ---------------- 界面事件：预览 ----------------

        private void Param_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

        private void RadioMode_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

        /// <summary>
        /// 实时预览首码（对应 .pyt 的示例说明）。
        /// 注意：XAML 中 Text="18"/IsChecked="True" 会在 InitializeComponent 解析期间
        /// 触发 TextChanged/Checked 事件，此时后续控件尚未创建（null），
        /// 必须先判空，否则 NRE 直接导致 Pro 闪退（严重应用程序错误）。
        /// </summary>
        private void UpdatePreview()
        {
            // 初始化未完成时跳过（控件可能尚未创建）
            if (TextPreview == null || TextCodeLength == null || TextCodePrefix == null ||
                TextStartValue == null || RadioPerLayer == null || ListLayers == null)
                return;

            string prefix = TextCodePrefix.Text?.Trim() ?? "";
            bool okLen = int.TryParse(TextCodeLength.Text?.Trim(), out int len);
            bool okStart = long.TryParse(TextStartValue.Text?.Trim(), out long start);
            int selCount = ListLayers.SelectedItems?.Count ?? 0;

            if (!okLen || !okStart || prefix.Length == 0 || prefix.Length >= len || !IsAllDigits(prefix))
            {
                TextPreview.Text = "示例：长度 18、开头 4201232026、起始值 100 → 首码 420123202600000100";
                return;
            }

            string first = prefix + start.ToString(CultureInfo.InvariantCulture).PadLeft(len - prefix.Length, '0');
            string mode = RadioPerLayer.IsChecked == true ? "每图层独立编号" : "跨图层连续编号";
            TextPreview.Text = $"首码 {first}（共 {len} 位）；{mode}；已选 {selCount} 个图层";
        }

        private static bool IsAllDigits(string s)
        {
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return s.Length > 0;
        }

        // ---------------- 执行 ----------------

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 参数校验
                var layers = ListLayers.SelectedItems.Cast<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (layers.Count == 0)
                {
                    MessageBox.Show("请先选择至少一个图层。", ToolLabel);
                    return;
                }

                if (!(ComboField.SelectedItem is FieldInfo field))
                {
                    MessageBox.Show("请选择编码字段（所选图层的公共字段）。", ToolLabel);
                    return;
                }

                string gdbPath = TextInputGdb.Text?.Trim().TrimEnd('\\');
                string prefix = TextCodePrefix.Text?.Trim();
                if (!int.TryParse(TextCodeLength.Text?.Trim(), out int codeLength) || codeLength < 2 || codeLength > 40)
                {
                    MessageBox.Show("编码长度须为 2~40 的整数。", ToolLabel);
                    return;
                }
                if (string.IsNullOrEmpty(prefix) || !IsAllDigits(prefix))
                {
                    MessageBox.Show("编码开头必须为数字。", ToolLabel);
                    return;
                }
                if (prefix.Length >= codeLength)
                {
                    MessageBox.Show($"编码开头（{prefix.Length} 位）必须短于编码长度（{codeLength} 位），至少留 1 位序号。",
                        ToolLabel);
                    return;
                }
                if (!long.TryParse(TextStartValue.Text?.Trim(), out long startValue) || startValue < 0)
                {
                    MessageBox.Show("编码起始值须为非负整数。", ToolLabel);
                    return;
                }
                if (field.Type == FieldType.String && field.Length > 0 && field.Length < codeLength)
                {
                    MessageBox.Show($"字段 {field.Name} 长度 {field.Length} 不足以存放 {codeLength} 位编码。", ToolLabel);
                    return;
                }
                if (field.Type == FieldType.Integer && codeLength > 9)
                {
                    MessageBox.Show($"整型字段 {field.Name} 最多约 9 位数字，{codeLength} 位编码请改用文本或长整型字段。", ToolLabel);
                    return;
                }

                bool perLayer = RadioPerLayer.IsChecked == true;
                int seqDigits = codeLength - prefix.Length;

                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunCodeAsync(gdbPath, layers, field.Name, prefix, codeLength, startValue, perLayer, seqDigits, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消编码。");
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

        /// <summary>取消正在进行的编码</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        // ---------------- 使用说明 ----------------

        /// <summary>打开唯一编码工具的使用说明窗口（单例）</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new UniqueCodeHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ---------------- 主流程 ----------------

        /// <summary>
        /// 编码主流程（对应 .pyt execute）：逐图层按 OID 升序写入编码。
        /// 每图层一个 EditOperation（单图层失败只回滚该图层，不影响其他图层）。
        /// </summary>
        private async Task RunCodeAsync(
            string gdbPath, List<string> layers, string fieldName,
            string prefix, int codeLength, long startValue, bool perLayer, int seqDigits,
            CancellationToken ct)
        {
            long seq = startValue;
            long totalFeatures = 0;

            Log($"数据库：{gdbPath}");
            Log($"图层数：{layers.Count}（{string.Join(", ", layers)}）");
            Log($"编码字段：{fieldName}；长度 {codeLength}；开头 {prefix}；起始 {startValue}；" +
                $"{(perLayer ? "每图层独立编号" : "跨图层连续编号")}");
            Log("");

            for (int i = 0; i < layers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string fcName = layers[i];
                long layerStart = perLayer ? startValue : seq;
                SetProgress(100.0 * i / layers.Count, $"[{i + 1}/{layers.Count}] 编码图层：{fcName}");

                long written;
                try
                {
                    written = await CodeOneLayerAsync(gdbPath, fcName, fieldName, prefix, layerStart, seqDigits, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogError($"图层 {fcName} 编码失败（已回滚该图层）：{ex.Message}");
                    continue;
                }

                totalFeatures += written;
                if (!perLayer)
                    seq += written;
                if (written > 0)
                {
                    Log($"  {fcName}：完成 {written} 条，编码 {prefix}{layerStart.ToString().PadLeft(seqDigits, '0')} ~ " +
                        $"{prefix}{(layerStart + written - 1).ToString().PadLeft(seqDigits, '0')}");
                }
            }

            Log("");
            Log($"唯一编码完成：{layers.Count} 个图层共写入 {totalFeatures} 条。");
            // 全部图层处理完毕，进度条置满（循环内最后一次进度只到 (N-1)/N）
            SetProgress(100, "唯一编码完成");
        }

        /// <summary>
        /// 单图层编码：OID 升序 = 编码顺序（稳定可复现）。
        /// 容量校验：起始值+条数-1 不得超过序号容量，否则整图层跳过。
        /// 写入：EditOperation.Callback 在 MCT 上游标直写 + row.Store()，
        /// 异常时整图层回滚（不留半编码），比 Inspector 逐行 Load 快得多。
        /// </summary>
        private static async Task<long> CodeOneLayerAsync(
            string gdbPath, string fcName, string fieldName,
            string prefix, long start, int seqDigits, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            long written = 0;

            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开数据库。");
                    using (FeatureClass fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        // 容量校验（先数条数）
                        long count = 0;
                        using (RowCursor c = fc.Search(null, false))
                            while (c.MoveNext()) count++;
                        if (count == 0) return;

                        long maxSeq = start + count - 1;
                        // 序号位数 ≥19 时 10^n 超出 long 范围（double 强转会溢出为负），
                        // 此时容量按 long.MaxValue 处理（19 位序号实际不可能写满）
                        long capacity = seqDigits >= 19
                            ? long.MaxValue
                            : (long)Math.Pow(10, seqDigits) - 1;
                        // 连续编号模式下，上一图层递增后的起始值可能已超容量（count==0 的图层
                        // 会绕过本校验），在写入前兜底拦截
                        if (start > capacity)
                            throw new InvalidOperationException(
                                $"起始值 {start} 已超出 {seqDigits} 位序号容量（最大 {capacity}）。");
                        if (maxSeq > capacity)
                        {
                            throw new InvalidOperationException(
                                $"序号将达 {maxSeq}，超出 {seqDigits} 位容量（最大 {capacity}）。请增大编码长度或减小起始值。");
                        }

                        var edit = new EditOperation { Name = $"唯一编码 {fcName}" };
                        // Callback(action, datasets)：自定义编辑体，Execute 失败时整体回滚
                        edit.Callback(context =>
                        {
                            // OID 升序游标直写（RecyclingCursor=false 才能修改行）
                            long n = start;
                            using (RowCursor cursor = fc.Search(null, false))
                            {
                                while (cursor.MoveNext())
                                {
                                    ct.ThrowIfCancellationRequested();
                                    Row row = cursor.Current;
                                    row[fieldName] = prefix + n.ToString(CultureInfo.InvariantCulture).PadLeft(seqDigits, '0');
                                    row.Store();
                                    n++;
                                    written++;
                                }
                            }
                        }, fc);

                        if (!edit.Execute())
                            throw new InvalidOperationException(edit.ErrorMessage ?? "写入失败。");
                    }
                }
            });

            return written;
        }

        // ---------------- 界面辅助 ----------------

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextInputGdb.IsEnabled = !running;
            ListLayers.IsEnabled = !running;
            ComboField.IsEnabled = !running;
            TextCodeLength.IsEnabled = !running;
            TextCodePrefix.IsEnabled = !running;
            TextStartValue.IsEnabled = !running;
            RadioPerLayer.IsEnabled = !running;
            RadioContinuous.IsEnabled = !running;
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
