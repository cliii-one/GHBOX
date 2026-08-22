using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Editing;
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
    /// 动态维护：根据备案数据库A和维护后全量数据库B，生成符合汇交要求的动态维护数据库C。
    /// 业务逻辑与 `toolbox/动态维护.pyt` 的 DynamicMaintenanceTool 完全一致。
    ///
    /// 处理类型：
    /// - Type 2（属性变更）：规划分区 / 用地用海规划分区 / 中心城区规划分区 / 中心城区规划用地用海
    ///   ① 成对相交 A∩B → 找出属性变化的A图斑
    ///   ② WHQ（维护前）= 属性变化的A图斑
    ///   ③ WHH（维护后）= B∩WHQ → 拆单部件 → 重编BSM + 算面积
    ///   ④ WHC（维护层）= WHQ∩WHH → 筛选属性变化记录 → 写入维护元数据
    ///
    /// - Type 13（调入/调出）：城市蓝线/绿线/紫线/黄线/洪涝风险控制线/历史文化保护线
    ///   ① Erase A-B（调出 WHLX=3）+ Erase B-A（调入 WHLX=1）→ Merge → WHC（维护层）
    ///   ② WHQ（维护前）= A中与A-B有交集的图斑
    ///   ③ WHH（维护后）= B∩WHQ → 拆单部件 → 重编BSM + 算面积
    ///
    /// 输出数据库C命名：{行政区代码}{行政区名称}{维护年度}年度县级国土空间总体规划动态维护.gdb
    ///
    /// 重要（铁律）：
    /// - 所有 Geodatabase 访问必须 QueuedTask 异步
    /// - 临时数据用唯一标记，运行后自动清理
    /// - 面积一律椭球面积（测地线）
    /// </summary>
    public partial class DynamicMaintenance : ProWindow
    {
        private const string ToolLabel = "动态维护";

        // ==================== 图层配置 ====================

        /// <summary>图层配置项（对应 .pyt 的 layer_config 字典）</summary>
        private sealed class LayerConfig
        {
            public string Alias;           // 中文别名（用户界面选择）
            public string LayerName;       // 要素类名（如 GHFQ）
            public int Type;               // 处理类型：2=属性变更，13=调入/调出
            public string CompareField;    // 属性变更比较字段（type=13 时为 null）
            public string[] AreaFields;    // 面积字段列表
            public string Ysdm;            // 固定要素代码
            public bool Skip;              // 是否跳过（乡级主体功能定位暂跳过）
        }

        /// <summary>11 个可维护图层的配置（与 .pyt layer_config 一一对应）</summary>
        private static readonly LayerConfig[] LayerConfigs = new[]
        {
            new LayerConfig { Alias = "规划分区",         LayerName = "GHFQ",     Type = 2,  CompareField = "GHFQDM",   AreaFields = new[] { "MJ" },           Ysdm = "2090020610", Skip = false },
            new LayerConfig { Alias = "用地用海规划分区",   LayerName = "YDYHGHFQ", Type = 2,  CompareField = "GHFQDM",   AreaFields = new[] { "MJ" },           Ysdm = "2090020610", Skip = false },
            new LayerConfig { Alias = "中心城区规划分区",   LayerName = "ZXCQGHFQ", Type = 2,  CompareField = "GHFQDM",   AreaFields = new[] { "MJ" },           Ysdm = "2090020610", Skip = false },
            new LayerConfig { Alias = "中心城区规划用地用海", LayerName = "ZXCQGHYDYH", Type = 2, CompareField = "YDYHFLDM", AreaFields = new[] { "TBMJ", "TBDLMJ" }, Ysdm = "2090020620", Skip = false },
            new LayerConfig { Alias = "乡级主体功能定位",   LayerName = "XZZTGNDW", Type = 2,  CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020130", Skip = true  },
            new LayerConfig { Alias = "中心城区城市蓝线",   LayerName = "ZXCQCSLX", Type = 13, CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020233", Skip = false },
            new LayerConfig { Alias = "中心城区城市绿线",   LayerName = "ZXCQCSLVX", Type = 13, CompareField = null,      AreaFields = new[] { "MJ" },           Ysdm = "2090020232", Skip = false },
            new LayerConfig { Alias = "中心城区城市紫线",   LayerName = "ZXCQCSZX", Type = 13, CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020233", Skip = false },
            new LayerConfig { Alias = "中心城区城市黄线",   LayerName = "ZXCQCSHX", Type = 13, CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020234", Skip = false },
            new LayerConfig { Alias = "洪涝风险控制线",     LayerName = "HLFXKZX",  Type = 13, CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020229", Skip = false },
            new LayerConfig { Alias = "历史文化保护线",     LayerName = "LSWHBHX",  Type = 13, CompareField = null,       AreaFields = new[] { "MJ" },           Ysdm = "2090020227", Skip = false },
        };

        /// <summary>维护层标准字段定义（WHC 图层的字段结构）</summary>
        private static readonly (string Name, string Type, int Length, string Alias)[] WhcFieldDefs = new[]
        {
            ("BSM",       "TEXT",  18,  "标识码"),
            ("YSDM",      "TEXT",  10,  "要素代码"),
            ("XZQDM",     "TEXT",  12,  "行政区代码"),
            ("XZQMC",     "TEXT",  100, "行政区名称"),
            ("WHLX",      "TEXT",  2,   "维护类型"),
            ("WHLY",      "TEXT",  3,   "维护理由"),
            ("JDZBLY",    "TEXT",  10,  "机动指标来源"),
            ("WHBH",      "TEXT",  18,  "维护编号"),
            ("KZBSHSLX",  "TEXT",  3,   "扩展倍数核算类型"),
            ("BZ",        "TEXT",  255, "备注"),
        };

        /// <summary>维护层写入字段顺序（对应 .pyt target_fields）</summary>
        private static readonly string[] WhcTargetFields = new[]
        {
            "SHAPE@", "YSDM", "XZQDM", "XZQMC", "WHLX", "WHBH", "BSM", "WHLY", "JDZBLY", "KZBSHSLX", "BZ"
        };

        // ==================== 目标坐标系：CGCS2000 ====================

        /// <summary>
        /// 目标空间参考：CGCS2000（WKID=4490）+ 垂直坐标系（WKID=5737）
        /// 对应 .pyt 的 arcpy.SpatialReference(4490, 5737)
        /// </summary>
        private static SpatialReference TargetSR => SpatialReferenceBuilder.CreateSpatialReference(4490, 5737);

        /// <summary>
        /// 便捷 GP 执行方法，自动传入 TargetSR 设置 outputCoordinateSystem。
        /// 对应 .pyt 的 arcpy.env.outputCoordinateSystem = target_sr
        /// </summary>
        private static Task<IGPResult> RunGpAsync(string tool, IEnumerable<string> args, CancellationToken ct)
            => GpHelper.RunToolAsync(tool, args, ct, TargetSR, _cPath);

        /// <summary>
        /// GP 执行（不传 scratchWorkspace），仅设 outputCoordinateSystem。
        /// 用于 Frequency / ListFeatureClasses / ListTables 等不应受 workspace 干扰的工具。
        /// </summary>
        private static Task<IGPResult> RunGpNoWkAsync(string tool, IEnumerable<string> args, CancellationToken ct)
            => GpHelper.RunToolAsync(tool, args, ct, TargetSR);

        // ==================== 状态 ====================

        private CancellationTokenSource _cts;
        private DynamicMaintenanceHelp _help;
        private static string _cPath; // 输出数据库C路径，作为 GP scratchWorkspace 避免中间数据写入只读输入库

        public DynamicMaintenance()
        {
            InitializeComponent();
        }

        /// <summary>窗口加载后填充图层列表</summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (ListLayers.Items.Count == 0)
            {
                foreach (var cfg in LayerConfigs)
                    ListLayers.Items.Add(cfg.Alias);
            }
            UpdateSelectedCount();
        }

        // ==================== 界面事件 ====================

        /// <summary>选择备案数据库A</summary>
        private void BrowseDbA_Click(object sender, RoutedEventArgs e)
        {
            string path = PickGeodatabase("选择备案数据库A（部备案版本）");
            if (path != null) TextDbA.Text = path;
        }

        /// <summary>选择维护后全量数据库B</summary>
        private void BrowseDbB_Click(object sender, RoutedEventArgs e)
        {
            string path = PickGeodatabase("选择维护后全量数据库B");
            if (path != null) TextDbB.Text = path;
        }

        /// <summary>选择输出目录</summary>
        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenItemDialog
            {
                Title = "选择动态维护数据库C存放目录",
                MultiSelect = false,
                Filter = ItemFilters.Folders
            };
            if (dlg.ShowDialog() == true && dlg.Items.Any())
                TextOutputFolder.Text = dlg.Items.First().Path;
        }

        /// <summary>图层选择变化时刷新计数</summary>
        private void ListLayers_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateSelectedCount();
        }

        /// <summary>更新"已选择 x 个图层"计数显示</summary>
        private void UpdateSelectedCount()
        {
            if (TextSelCount == null) return;
            TextSelCount.Text = $"已选择 {ListLayers.SelectedItems?.Count ?? 0} 个图层";
        }

        /// <summary>开始维护（异步，不阻塞界面）</summary>
        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 参数校验
                string dbA = TextDbA.Text?.Trim().TrimEnd('\\');
                string dbB = TextDbB.Text?.Trim().TrimEnd('\\');
                string outFolder = TextOutputFolder.Text?.Trim();
                string xzqdm = TextXzqdm.Text?.Trim();
                string xzqmc = TextXzqmc.Text?.Trim();
                string year = TextYear.Text?.Trim();

                if (string.IsNullOrWhiteSpace(dbA) || !Directory.Exists(dbA))
                { MessageBox.Show("备案数据库A不存在，请检查路径。", ToolLabel); return; }
                if (!dbA.ToLowerInvariant().EndsWith(".gdb"))
                { MessageBox.Show("备案数据库A仅支持 .gdb。", ToolLabel); return; }

                if (string.IsNullOrWhiteSpace(dbB) || !Directory.Exists(dbB))
                { MessageBox.Show("维护后全量数据库B不存在，请检查路径。", ToolLabel); return; }
                if (!dbB.ToLowerInvariant().EndsWith(".gdb"))
                { MessageBox.Show("维护后全量数据库B仅支持 .gdb。", ToolLabel); return; }

                if (string.IsNullOrWhiteSpace(outFolder) || !Directory.Exists(outFolder))
                { MessageBox.Show("输出目录不存在，请检查路径。", ToolLabel); return; }

                var selected = ListLayers.SelectedItems?.Cast<string>().ToList();
                if (selected == null || selected.Count == 0)
                { MessageBox.Show("请至少选择一个需维护的图层。", ToolLabel); return; }

                if (string.IsNullOrWhiteSpace(xzqdm))
                { MessageBox.Show("请输入行政区代码。", ToolLabel); return; }
                if (string.IsNullOrWhiteSpace(xzqmc))
                { MessageBox.Show("请输入行政区名称。", ToolLabel); return; }
                if (string.IsNullOrWhiteSpace(year))
                { MessageBox.Show("请输入维护年度。", ToolLabel); return; }

                // 2. 主流程
                _cts = new CancellationTokenSource();
                SetRunning(true);
                await RunMaintenanceAsync(dbA, dbB, outFolder, selected, xzqdm, xzqmc, year, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已取消维护。");
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

        /// <summary>取消正在进行的维护</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消...");
        }

        /// <summary>打开使用说明窗口</summary>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null) { _help.Activate(); return; }
            _help = new DynamicMaintenanceHelp { Owner = this };
            _help.Closed += (s, args) => _help = null;
            _help.Show();
        }

        // ==================== 主流程 ====================

        /// <summary>
        /// 动态维护主流程（对应 .pyt execute）：
        /// 创建C库 → 逐图层按类型处理（type2/类型13）→ 汇总。
        /// </summary>
        private async Task RunMaintenanceAsync(
            string dbA, string dbB, string outFolder,
            List<string> selectedAliases, string xzqdm, string xzqmc, string year,
            CancellationToken ct)
        {
            // ---- 1. 创建输出数据库C ----
            string cName = $"{xzqdm}{xzqmc}{year}年度县级国土空间总体规划动态维护.gdb";
            string cPath = Path.Combine(outFolder, cName);
            _cPath = cPath; // 设置 GP scratchWorkspace，避免中间数据写入只读输入库

            if (Directory.Exists(cPath))
            {
                Log($"输出数据库C已存在，先删除：{cName}");
                await RunGpAsync("management.Delete",
                    Geoprocessing.MakeValueArray(cPath), ct);
            }
            await RunGpAsync("management.CreateFileGDB",
                Geoprocessing.MakeValueArray(outFolder, cName), ct);
            Log($"创建动态维护数据库C：{cPath}");

            // ---- 2. 预检：所有选中图层在A/B中必须存在 ----
            var selectedConfigs = new List<LayerConfig>();
            foreach (string alias in selectedAliases)
            {
                var cfg = LayerConfigs.FirstOrDefault(c => c.Alias == alias);
                if (cfg == null) { LogWarning($"未知图层：{alias}，已跳过"); continue; }
                if (cfg.Skip) { Log($"图层 {alias}（{cfg.LayerName}）暂不处理，已跳过"); continue; }

                string fcA = await FindFeatureClassPathAsync(dbA, cfg.LayerName, ct);
                string fcB = await FindFeatureClassPathAsync(dbB, cfg.LayerName, ct);
                if (fcA == null)
                    throw new InvalidOperationException($"数据库A中缺少图层：{cfg.LayerName}（{alias}）");
                if (fcB == null)
                    throw new InvalidOperationException($"数据库B中缺少图层：{cfg.LayerName}（{alias}）");

                selectedConfigs.Add(cfg);
            }

            if (selectedConfigs.Count == 0)
            {
                LogWarning("没有可处理的图层。");
                return;
            }

            Log($"共 {selectedConfigs.Count} 个图层待维护：{string.Join("、", selectedConfigs.Select(c => c.Alias))}");

            // ---- 3. 逐图层处理 ----
            string datePrefix = DateTime.Now.ToString("yyyyMMdd");

            for (int i = 0; i < selectedConfigs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cfg = selectedConfigs[i];

                string fcA = await FindFeatureClassPathAsync(dbA, cfg.LayerName, ct);
                string fcB = await FindFeatureClassPathAsync(dbB, cfg.LayerName, ct);
                if (fcA == null || fcB == null)
                {
                    LogWarning($"  无法定位图层 {cfg.LayerName}，跳过");
                    continue;
                }

                long cntA = await GpHelper.GetCountAsync(fcA, ct);
                long cntB = await GpHelper.GetCountAsync(fcB, ct);
                Log($"[{i + 1}/{selectedConfigs.Count}] 开始维护：{cfg.Alias}（{cfg.LayerName}）");
                Log($"  备案数据库A图斑：{cntA}，维护后全量数据库B图斑：{cntB}");

                if (cntA == 0 || cntB == 0)
                {
                    LogWarning($"  图层为空，跳过处理");
                    continue;
                }

                if (cfg.Type == 2)
                {
                    await ProcessType2Async(
                        cfg, fcA, fcB, cPath, xzqdm, xzqmc, datePrefix, ct);
                }
                else if (cfg.Type == 13)
                {
                    await ProcessType13Async(
                        cfg, fcA, fcB, cPath, xzqdm, xzqmc, datePrefix, ct);
                }

                // 每维护完一个图层，立即按已知路径删除所有临时图层
                await CleanupTmpLayersAsync(cPath, ct);

                // 等待当前图层的所有 GP 操作完全释放，再处理下一个图层
                await QueuedTask.Run(() => { });
                await Task.Delay(500, ct);
            }

            // 最终兜底清理
            await CleanupTmpLayersAsync(cPath, ct);

            Log("");
            Log("所有图层处理完成！");
            SetProgress(100, "动态维护完成");
        }

        // ==================== Type 2（属性变更）====================

        /// <summary>
        /// 处理类型2（属性变更）：成对相交→维护前→维护后→维护层。
        /// 对应 .pyt process_type2。
        /// </summary>
        private async Task ProcessType2Async(
            LayerConfig cfg, string fcA, string fcB, string cPath,
            string xzqdm, string xzqmc, string datePrefix,
            CancellationToken ct)
        {
            string compareField = cfg.CompareField;
            string prefix = cfg.LayerName;
            string qName = $"WHQ{prefix}";    // 维护前
            string hName = $"WHH{prefix}";    // 维护后
            string cName = $"WHC{prefix}";    // 维护层

            // ---- 1. 成对相交 A∩B，找出属性变化的A图斑 ----
            string intersectAB = Path.Combine(cPath, "_tmp_int_ab");
            await RunGpAsync("analysis.PairwiseIntersect",
                Geoprocessing.MakeValueArray(
                    string.Join(";", fcA, fcB), intersectAB, "ALL", "", "INPUT"), ct);

            // 字段映射：区分A/B的BSM和比较字段
            var fieldMap = await GetFieldMapAsync(intersectAB, compareField, ct);

            // 找出属性变化的A图斑BSM集合
            var aBsmSet = new HashSet<string>(StringComparer.Ordinal);
            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(cPath))
                {
                    if (gdb == null) return;
                    using (var fc = gdb.OpenDataset<FeatureClass>("_tmp_int_ab"))
                    {
                        using (var cursor = fc.Search(new QueryFilter { SubFields = $"{fieldMap.ABsm},{fieldMap.ACmp},{fieldMap.BCmp}" }, false))
                        {
                            while (cursor.MoveNext())
                            {
                                Row row = cursor.Current;
                                string aCmp = row[fieldMap.ACmp]?.ToString() ?? "";
                                string bCmp = row[fieldMap.BCmp]?.ToString() ?? "";
                                if (aCmp != bCmp)
                                    aBsmSet.Add(row[fieldMap.ABsm]?.ToString() ?? "");
                            }
                        }
                    }
                }
            });
            await RunGpAsync("management.Delete",
                Geoprocessing.MakeValueArray(intersectAB), ct);

            // ---- 2. 维护前图层 WHQ ----
            Log($"    制作维护前图层：{qName}");
            string qFc = Path.Combine(cPath, qName);
            if (aBsmSet.Count > 0)
            {
                // 构造 IN 条件
                string bsmList = string.Join(",", aBsmSet.Select(b => $"'{b}'"));
                string where = $"BSM IN ({bsmList})";
                // 直接用 Select 工具按条件导出，无需 MakeFeatureLayer（GP 调用间临时图层不持久）
                await RunGpAsync("analysis.Select",
                    Geoprocessing.MakeValueArray(fcA, qFc, where), ct);
                await DefineProjectionAsync(qFc, ct);
            }
            else
            {
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, qName, "POLYGON", fcA, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }
            long cntQ = await GpHelper.GetCountAsync(qFc, ct);
            Log($"    维护前图层记录数：{cntQ}");

            // ---- 3. 维护后图层 WHH = B ∩ WHQ → 拆单部件 ----
            Log($"    制作维护后图层：{hName}");
            string intersectBQ = Path.Combine(cPath, "_tmp_int_b_q");
            if (cntQ > 0)
            {
                await RunGpAsync("analysis.PairwiseIntersect",
                    Geoprocessing.MakeValueArray(
                        string.Join(";", fcB, qFc), intersectBQ, "ALL", "", "INPUT"), ct);
            }
            else
            {
                // 空结果：创建空面要素类
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, "_tmp_int_b_q", "POLYGON", fcB, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }

            string hFc = Path.Combine(cPath, hName);
            await RunGpAsync("conversion.FeatureClassToFeatureClass",
                Geoprocessing.MakeValueArray(intersectBQ, cPath, hName), ct);

            // 多部件转单部件
            string hFcSingle = hFc + "_single";
            await RunGpAsync("management.MultipartToSinglepart",
                Geoprocessing.MakeValueArray(hFc, hFcSingle), ct);
            await RunGpAsync("management.Delete",
                Geoprocessing.MakeValueArray(hFc), ct);
            await RunGpAsync("management.Rename",
                Geoprocessing.MakeValueArray(hFcSingle, hName), ct);
            hFc = Path.Combine(cPath, hName);
            await DefineProjectionAsync(hFc, ct);
            await RunGpAsync("management.Delete",
                Geoprocessing.MakeValueArray(intersectBQ), ct);

            // 清理多余字段：成对相交后 WHH 包含了 WHQ（A的）字段，需要删除只保留 B 的原始字段
            await RemoveExtraFieldsAsync(hFc, fcB, ct);

            // 确保BSM和面积字段存在
            await EnsureFieldAsync(hFc, "BSM", "TEXT", 18, "标识码", ct);
            foreach (string af in cfg.AreaFields)
                await EnsureFieldAsync(hFc, af, "DOUBLE", 0, af, ct);

            // 重编BSM：用 CalculateField + Python 自增函数（完全不使用 EditOperation，避免"表正在编辑中"）
            string maxBsm = await GetMaxBsmAsync(fcA, ct);
            long startSeqH = (maxBsm != null && maxBsm.Length >= 8 && long.TryParse(maxBsm.Substring(maxBsm.Length - 8), out long lastSeq))
                ? lastSeq + 1 : 1;
            string bsmPrefix = xzqdm + "0000";
            // Python 代码块：自增序列号，拼接完整 BSM
            string pyBlock = $"autoInc = {startSeqH - 1}\ndef genBsm():\n    global autoInc\n    autoInc += 1\n    return \"{bsmPrefix}\" + str(autoInc).zfill(8)";
            await RunGpAsync("management.CalculateField",
                Geoprocessing.MakeValueArray(hFc, "BSM", "genBsm()", "PYTHON3", pyBlock), ct);

            // 算面积：字段计算器 round(!shape.geodesicArea!, 2)（测地线面积，平方米，保留2位小数）
            foreach (string af in cfg.AreaFields)
            {
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(hFc, af, "round(!shape.geodesicArea!, 2)", "PYTHON3"), ct);
            }

            long cntH = await GpHelper.GetCountAsync(hFc, ct);
            Log($"    维护后图层记录数：{cntH}");

            // ---- 4. 维护层 WHC = WHQ ∩ WHH → 筛选属性变化记录 ----
            Log($"    制作维护层图层：{cName}");
            string intersectQH = Path.Combine(cPath, "_tmp_int_qh");
            if (cntQ > 0 && cntH > 0)
            {
                await RunGpAsync("analysis.PairwiseIntersect",
                    Geoprocessing.MakeValueArray(
                        string.Join(";", qFc, hFc), intersectQH, "ALL", "", "INPUT"), ct);
            }
            else
            {
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, "_tmp_int_qh", "POLYGON", fcA, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }

            // 步骤0：获取QH交集中的字段映射
            var qhFieldMap = await GetQhFieldMapAsync(intersectQH, compareField, ct);

            // 步骤1：用 analysis.Select 筛选属性变化的记录，直接输出到 WHC
            string qhQ = qhFieldMap.QCmpField;
            string qhH = qhFieldMap.HCmpField;
            string whereClause = $"{qhQ} <> {qhH}";
            string cFc = Path.Combine(cPath, cName);
            long cntC = 0;
            try
            {
                await RunGpAsync("analysis.Select",
                    Geoprocessing.MakeValueArray(intersectQH, cFc, whereClause), ct);
                cntC = await GpHelper.GetCountAsync(cFc, ct);
            }
            catch { cntC = 0; }

            if (cntC > 0)
            {
                // 步骤2：确保所有预定义字段存在（含正确长度和别名）
                foreach (var (fname, ftype, flen, falias) in WhcFieldDefs)
                    await EnsureFieldAsync(cFc, fname, ftype, flen, falias, ct);

                // 步骤3：删除 A/B 带入的多余字段
                await RemoveWhcExtraFieldsAsync(cFc, ct);

                // 步骤4：填充固定值字段（对应 .pyt InsertCursor 中的固定值）
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "YSDM", $"'{cfg.Ysdm}'", "PYTHON3"), ct);
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "XZQDM", $"'{xzqdm}'", "PYTHON3"), ct);
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "XZQMC", $"'{xzqmc}'", "PYTHON3"), ct);
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "WHLX", "'2'", "PYTHON3"), ct);

                // BSM 自增（对应 .pyt _generate_bsm）
                string bsmPrefix2 = xzqdm + "0000";
                string pyBlockC = $"autoInc = 0\ndef genBsm():\n    global autoInc\n    autoInc += 1\n    return \"{bsmPrefix2}\" + str(autoInc).zfill(8)";
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "BSM", "genBsm()", "PYTHON3", pyBlockC), ct);

                // WHBH 自增（每个图层独立从1开始）
                string pyBlockWhbh = "autoInc = 0\ndef genWhbh():\n    global autoInc\n    autoInc += 1\n    return str(autoInc).zfill(6)";
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "WHBH", $"\"{datePrefix}\" + genWhbh()", "PYTHON3", pyBlockWhbh), ct);
            }

            Log($"    维护层图层生成，记录数：{cntC}");

            // 清理
            await SafeDeleteAsync(intersectQH, ct);
        }

        // ==================== Type 13（调入/调出）====================

        /// <summary>
        /// 处理类型13（调入/调出）：Erase差集→维护层→维护前→维护后。
        /// 对应 .pyt process_type13。
        /// </summary>
        private async Task ProcessType13Async(
            LayerConfig cfg, string fcA, string fcB, string cPath,
            string xzqdm, string xzqmc, string datePrefix,
            CancellationToken ct)
        {
            string prefix = cfg.LayerName;
            string qName = $"WHQ{prefix}";    // 维护前
            string hName = $"WHH{prefix}";    // 维护后
            string cName = $"WHC{prefix}";    // 维护层

            // =================================================================
            // 步骤1：生成维护层 WHC
            // =================================================================
            Log("    制作维护层图层...");

            // 结果1：A - B = 调出（WHLX=3）
            string erase1 = Path.Combine(cPath, "_tmp_erase1");
            await RunGpAsync("analysis.PairwiseErase",
                Geoprocessing.MakeValueArray(fcA, fcB, erase1), ct);

            // 结果2：B - A = 调入（WHLX=1）
            string erase2 = Path.Combine(cPath, "_tmp_erase2");
            await RunGpAsync("analysis.PairwiseErase",
                Geoprocessing.MakeValueArray(fcB, fcA, erase2), ct);

            // 给结果1和结果2加WHLX字段并赋值
            await RunGpAsync("management.AddField",
                Geoprocessing.MakeValueArray(erase1, "WHLX", "TEXT", null, null, 2, "维护类型"), ct);
            await RunGpAsync("management.AddField",
                Geoprocessing.MakeValueArray(erase2, "WHLX", "TEXT", null, null, 2, "维护类型"), ct);
            await SetFieldValueAsync(erase1, "WHLX", "3", ct);  // 调出
            await SetFieldValueAsync(erase2, "WHLX", "1", ct);  // 调入

            // 合并结果1+结果2 → WHC
            string merged = Path.Combine(cPath, "_tmp_merged");
            await RunGpAsync("management.Merge",
                Geoprocessing.MakeValueArray(string.Join(";", erase1, erase2), merged), ct);

            string cFc = Path.Combine(cPath, cName);
            await RunGpAsync("analysis.Select",
                Geoprocessing.MakeValueArray(merged, cFc, ""), ct);

            // 拆分多部件
            string cFcSingle = cFc + "_single";
            await RunGpAsync("management.MultipartToSinglepart",
                Geoprocessing.MakeValueArray(cFc, cFcSingle), ct);
            await RunGpAsync("management.Delete", Geoprocessing.MakeValueArray(cFc), ct);
            await RunGpAsync("management.Rename", Geoprocessing.MakeValueArray(cFcSingle, cName), ct);
            cFc = Path.Combine(cPath, cName);
            await DefineProjectionAsync(cFc, ct);

            // 确保预定义字段存在并删除多余字段
            foreach (var (fname, ftype, flen, falias) in WhcFieldDefs)
                await EnsureFieldAsync(cFc, fname, ftype, flen, falias, ct);
            await RemoveWhcExtraFieldsAsync(cFc, ct);

            long cntC = await GpHelper.GetCountAsync(cFc, ct);

            // 赋值（对应 .pyt InsertCursor 中的固定值）
            if (cntC > 0)
            {
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "YSDM", $"'{cfg.Ysdm}'", "PYTHON3"), ct);
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "XZQDM", $"'{xzqdm}'", "PYTHON3"), ct);
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "XZQMC", $"'{xzqmc}'", "PYTHON3"), ct);

                // BSM 从1开始编码
                string bsmPfx = xzqdm + "0000";
                string pyBlockBsm = $"autoInc = 0\ndef genBsm():\n    global autoInc\n    autoInc += 1\n    return \"{bsmPfx}\" + str(autoInc).zfill(8)";
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "BSM", "genBsm()", "PYTHON3", pyBlockBsm), ct);

                // WHBH 年月日+6位顺序码，全局递增
                // WHBH 自增（每个图层独立从1开始）
                string pyBlockWhbh = "autoInc = 0\ndef genWhbh():\n    global autoInc\n    autoInc += 1\n    return str(autoInc).zfill(6)";
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(cFc, "WHBH", $"\"{datePrefix}\" + genWhbh()", "PYTHON3", pyBlockWhbh), ct);
            }

            Log($"    维护层图层生成，记录数：{cntC}");

            // =================================================================
            // 步骤2：生成维护前图层 WHQ（调出和未变化的部分）
            // =================================================================
            Log($"    制作维护前图层：{qName}");

            // 复用结果1（erase1 = A-B），频数统计其BSM → 从A库中选对应图斑作为WHQ
            // 频数统计（不传 scratchWorkspace，避免干扰 Frequency 工具）
            string freq = Path.Combine(cPath, "_tmp_freq");
            await RunGpNoWkAsync("analysis.Frequency",
                Geoprocessing.MakeValueArray(erase1, freq, "BSM"), ct);

            var bsmList13 = new List<string>();
            await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(cPath))
                {
                    if (gdb == null) return;
                    using (var tbl = gdb.OpenDataset<Table>("_tmp_freq"))
                    {
                        // Frequency 输出表字段顺序：[0]=FREQUENCY（频次），[1]=BSM（标识码）
                        // 必须通过字段名定位 BSM 的索引，不能用固定索引 0
                        int bsmIdx = tbl.GetDefinition().FindField("BSM");
                        if (bsmIdx < 0) return;
                        using (var cursor = tbl.Search(null, false))
                        {
                            while (cursor.MoveNext())
                            {
                                string bsm = cursor.Current[bsmIdx]?.ToString();
                                if (!string.IsNullOrEmpty(bsm)) bsmList13.Add(bsm);
                            }
                        }
                    }
                }
            });

            string qFc = Path.Combine(cPath, qName);
            if (bsmList13.Count > 0)
            {
                string where = $"BSM IN ({string.Join(",", bsmList13.Select(b => $"'{b}'"))})";
                await RunGpAsync("analysis.Select",
                    Geoprocessing.MakeValueArray(fcA, qFc, where), ct);
                await DefineProjectionAsync(qFc, ct);
            }
            else
            {
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, qName, "POLYGON", fcA, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }

            long cntQ = await GpHelper.GetCountAsync(qFc, ct);
            Log($"    维护前图层记录数：{cntQ}");
            await SafeDeleteAsync(freq, ct);

            // =================================================================
            // 步骤3：生成维护后图层 WHH
            // =================================================================
            Log($"    制作维护后图层：{hName}");

            // 结果3：WHQ - WHC（维护前去掉维护层中的调出部分）
            string whqErasedByWhc = Path.Combine(cPath, "_tmp_whq_erase_whc");
            if (cntQ > 0 && cntC > 0)
            {
                await RunGpAsync("analysis.PairwiseErase",
                    Geoprocessing.MakeValueArray(qFc, cFc, whqErasedByWhc), ct);
            }
            else if (cntQ > 0)
            {
                // WHC为空，结果3 = WHQ
                await RunGpAsync("management.Copy",
                    Geoprocessing.MakeValueArray(qFc, whqErasedByWhc), ct);
            }
            else
            {
                // WHQ为空，结果3 = 空
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, "_tmp_whq_erase_whc", "POLYGON", fcA, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }

            // 结果4：B ∩ 结果3
            string bIntResult3 = Path.Combine(cPath, "_tmp_b_int_result3");
            long cntResult3 = await GpHelper.GetCountAsync(whqErasedByWhc, ct);
            if (cntResult3 > 0)
            {
                await RunGpAsync("analysis.PairwiseIntersect",
                    Geoprocessing.MakeValueArray(
                        string.Join(";", fcB, whqErasedByWhc), bIntResult3, "ALL", "", "INPUT"), ct);

                // 结果4只保留B的原始字段（去掉结果3带入的多余字段，否则与结果2合并失败）
                await RemoveExtraFieldsAsync(bIntResult3, fcB, ct);

                // 给结果4加 WHLX 字段，确保与结果2（erase2）字段结构一致
                await EnsureFieldAsync(bIntResult3, "WHLX", "TEXT", 2, "维护类型", ct);
                await SetFieldValueAsync(bIntResult3, "WHLX", "", ct);
            }
            else
            {
                await RunGpAsync("management.CreateFeatureclass",
                    Geoprocessing.MakeValueArray(cPath, "_tmp_b_int_result3", "POLYGON", fcB, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", TargetSR), ct);
            }

            // WHH = 结果2(erase2/B-A) + 结果4 合并
            string mergedWHH = Path.Combine(cPath, "_tmp_merged_whh");
            await RunGpNoWkAsync("management.Merge",
                Geoprocessing.MakeValueArray(string.Join(";", erase2, bIntResult3), mergedWHH), ct);

            string hFc = Path.Combine(cPath, hName);
            await RunGpAsync("conversion.FeatureClassToFeatureClass",
                Geoprocessing.MakeValueArray(mergedWHH, cPath, hName), ct);

            // 拆分多部件
            string hFcSingle = hFc + "_single";
            await RunGpAsync("management.MultipartToSinglepart",
                Geoprocessing.MakeValueArray(hFc, hFcSingle), ct);
            await RunGpAsync("management.Delete", Geoprocessing.MakeValueArray(hFc), ct);
            await RunGpAsync("management.Rename", Geoprocessing.MakeValueArray(hFcSingle, hName), ct);
            hFc = Path.Combine(cPath, hName);
            await DefineProjectionAsync(hFc, ct);

            // 确保BSM和面积字段存在
            await EnsureFieldAsync(hFc, "BSM", "TEXT", 18, "标识码", ct);
            foreach (string af in cfg.AreaFields)
                await EnsureFieldAsync(hFc, af, "DOUBLE", 0, af, ct);

            // 重编BSM：按A的最大值续编
            string maxBsm13 = await GetMaxBsmAsync(fcA, ct);
            long startSeqH13 = (maxBsm13 != null && maxBsm13.Length >= 8 && long.TryParse(maxBsm13.Substring(maxBsm13.Length - 8), out long lastSeq13))
                ? lastSeq13 + 1 : 1;
            string bsmPrefix13 = xzqdm + "0000";
            string pyBlock13 = $"autoInc = {startSeqH13 - 1}\ndef genBsm():\n    global autoInc\n    autoInc += 1\n    return \"{bsmPrefix13}\" + str(autoInc).zfill(8)";
            await RunGpAsync("management.CalculateField",
                Geoprocessing.MakeValueArray(hFc, "BSM", "genBsm()", "PYTHON3", pyBlock13), ct);

            // 算面积（保留2位小数）
            foreach (string af in cfg.AreaFields)
            {
                await RunGpAsync("management.CalculateField",
                    Geoprocessing.MakeValueArray(hFc, af, "round(!shape.geodesicArea!, 2)", "PYTHON3"), ct);
            }

            long cntH = await GpHelper.GetCountAsync(hFc, ct);
            Log($"    维护后图层记录数：{cntH}");

            // 清理中间数据
            await SafeDeleteAsync(erase1, ct);
            await SafeDeleteAsync(erase2, ct);
            await SafeDeleteAsync(merged, ct);
            await SafeDeleteAsync(whqErasedByWhc, ct);
            await SafeDeleteAsync(bIntResult3, ct);
            await SafeDeleteAsync(mergedWHH, ct);
        }

        // ==================== 辅助方法 ====================

        /// <summary>字段映射信息（区分A/B输入的BSM和比较字段）</summary>
        private sealed class FieldMapInfo
        {
            public string ABsm, BBsm, ACmp, BCmp;
        }

        /// <summary>
        /// 获取相交结果中的字段映射（对应 .pyt _get_field_map）。
        /// 相交后A的字段保持原名，B的字段加 _1 后缀。
        /// </summary>
        private static async Task<FieldMapInfo> GetFieldMapAsync(
            string intersectFc, string compareField, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                string gdbDir = Path.GetDirectoryName(intersectFc);
                string fcName = Path.GetFileName(intersectFc);
                using (var gdb = GpHelper.OpenGeodatabase(gdbDir))
                {
                    if (gdb == null) throw new InvalidOperationException("无法打开临时数据库。");
                    using (var fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        var fieldNames = fc.GetDefinition().GetFields().Select(f => f.Name).ToList();

                        // BSM 字段：A的保持 "BSM"，B的变为 "BSM_1" 或其他变体
                        var bsmCandidates = fieldNames.Where(f => f.IndexOf("BSM", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (bsmCandidates.Count < 2)
                            throw new InvalidOperationException("无法区分两个输入的BSM字段");

                        string aBsm = bsmCandidates.Contains("BSM") ? "BSM" : bsmCandidates[0];
                        string bBsm = bsmCandidates.First(f => f != aBsm);

                        // 比较字段
                        var cmpCandidates = fieldNames.Where(f => f.IndexOf(compareField, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (cmpCandidates.Count < 2)
                            throw new InvalidOperationException($"无法区分两个输入的比较字段：{compareField}");

                        string aCmp = cmpCandidates.Contains(compareField) ? compareField : cmpCandidates[0];
                        string bCmp = cmpCandidates.First(f => f != aCmp);

                        return new FieldMapInfo { ABsm = aBsm, BBsm = bBsm, ACmp = aCmp, BCmp = bCmp };
                    }
                }
            });
        }

        /// <summary>WHQ∩WHH交集中的字段映射</summary>
        private sealed class QhFieldMapInfo
        {
            public string QCmpField, HCmpField;
        }

        /// <summary>
        /// 获取WHQ∩WHH交集中的比较字段映射。
        /// q_fc 的字段为原名，h_fc 的字段带 _1 后缀。
        /// </summary>
        private static async Task<QhFieldMapInfo> GetQhFieldMapAsync(
            string intersectFc, string compareField, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                string gdbDir = Path.GetDirectoryName(intersectFc);
                string fcName = Path.GetFileName(intersectFc);
                using (var gdb = GpHelper.OpenGeodatabase(gdbDir))
                {
                    if (gdb == null) return new QhFieldMapInfo { QCmpField = compareField, HCmpField = compareField + "_1" };
                    using (var fc = gdb.OpenDataset<FeatureClass>(fcName))
                    {
                        var fieldNames = fc.GetDefinition().GetFields().Select(f => f.Name).ToList();

                        // Q的比较字段 = 原名
                        string qCmp = compareField;
                        // H的比较字段 = 原名_1
                        string hCmp = fieldNames.Contains(compareField + "_1") ? compareField + "_1" : compareField;

                        return new QhFieldMapInfo { QCmpField = qCmp, HCmpField = hCmp };
                    }
                }
            });
        }

        /// <summary>获取A库中BSM字段的最大值（对应 .pyt _get_max_bsm）</summary>
        private static async Task<string> GetMaxBsmAsync(string fcPath, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                string dir = Path.GetDirectoryName(fcPath);
                string name = Path.GetFileName(fcPath);
                using (var gdb = GpHelper.OpenGeodatabase(dir))
                {
                    if (gdb == null) return null;
                    using (var fc = gdb.OpenDataset<FeatureClass>(name))
                    {
                        var def = fc.GetDefinition();
                        int bsmIdx = def.FindField("BSM");
                        if (bsmIdx < 0) return null;

                        string maxVal = null;
                        using (var cursor = fc.Search(new QueryFilter { SubFields = "BSM" }, false))
                        {
                            while (cursor.MoveNext())
                            {
                                string val = cursor.Current[bsmIdx]?.ToString();
                                if (!string.IsNullOrEmpty(val) && (maxVal == null || string.Compare(val, maxVal, StringComparison.Ordinal) > 0))
                                    maxVal = val;
                            }
                        }
                        return maxVal;
                    }
                }
            });
        }

        /// <summary>生成BSM：{行政区代码}0000{序号:08d}（对应 .pyt _generate_bsm）</summary>
        private static string GenerateBsm(string xzqdm, long seq)
            => $"{xzqdm}0000{seq.ToString().PadLeft(8, '0')}";

        /// <summary>生成维护编号WHBH：{日期前缀}{序号:06d}（对应 .pyt _generate_whbh）</summary>
        private static string GenerateWhbh(string datePrefix, long seq)
            => $"{datePrefix}{seq.ToString().PadLeft(6, '0')}";

        /// <summary>定义坐标系（对应 .pyt _define_sr = arcpy.management.DefineProjection）
        /// 原地定义投影，不改变数据坐标，输入可等于输出。</summary>
        private static async Task DefineProjectionAsync(string fcPath, CancellationToken ct)
        {
            await RunGpAsync("management.DefineProjection",
                Geoprocessing.MakeValueArray(fcPath, TargetSR), ct);
        }

        /// <summary>确保字段存在（对应 .pyt _ensure_fields_exist）
        /// 直接尝试 AddField，若字段已存在则捕获错误忽略。
        /// 不打开 GDB 句柄检查，避免"表正在编辑中"冲突。</summary>
        private static async Task EnsureFieldAsync(
            string fcPath, string fieldName, string fieldType, int fieldLength, string fieldAlias, CancellationToken ct)
        {
            try
            {
                // AddField 参数：in_table, field_name, field_type, field_precision, field_scale, field_length, field_alias
                var args = new List<string> { fcPath, fieldName, fieldType };
                args.Add(null); // field_precision
                args.Add(null); // field_scale
                args.Add(fieldLength > 0 ? fieldLength.ToString() : null); // field_length
                if (!string.IsNullOrEmpty(fieldAlias)) args.Add(fieldAlias);
                await RunGpAsync("management.AddField",
                    Geoprocessing.MakeValueArray(args.ToArray()), ct);
            }
            catch (Exception ex)
            {
                // 错误 502 = "字段已存在"；错误 501 = "字段名无效"等
                // 只忽略"已存在"的情况，其他异常继续抛出
                string msg = ex.Message ?? "";
                if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("已存在", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("ERROR 00502", StringComparison.OrdinalIgnoreCase))
                    return;
                throw;
            }
        }

        /// <summary>
        /// 删除 WHC 维护层中由 Select 带入的 A/B 多余字段。
        /// 保留：OID、Shape、Shape_Length、Shape_Area（系统必需字段）+ WhcFieldDefs 预定义的 10 个字段。
        /// </summary>
        private static async Task RemoveWhcExtraFieldsAsync(string whcPath, CancellationToken ct)
        {
            // 预定义字段 + 系统字段（不可删除）
            var allowedFields = new HashSet<string>(
                WhcFieldDefs.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            // 系统字段始终保留
            string[] sysFields = { "OBJECTID", "Shape", "Shape_Length", "Shape_Area", "FID" };
            foreach (string sf in sysFields)
                allowedFields.Add(sf);

            var extraFields = await QueuedTask.Run(() =>
            {
                var extras = new List<string>();
                string dir = Path.GetDirectoryName(whcPath);
                string name = Path.GetFileName(whcPath);
                using (var gdb = GpHelper.OpenGeodatabase(dir))
                {
                    if (gdb == null) return extras;
                    using (var fc = gdb.OpenDataset<FeatureClass>(name))
                    {
                        var def = fc.GetDefinition();
                        foreach (var f in def.GetFields())
                        {
                            // 跳过系统字段类型
                            if (f.FieldType == FieldType.OID || f.FieldType == FieldType.Geometry)
                                continue;
                            // 跳过预定义字段和已知系统字段
                            if (!allowedFields.Contains(f.Name))
                                extras.Add(f.Name);
                        }
                    }
                }
                return extras;
            });

            if (extraFields.Count > 0)
            {
                foreach (string fieldName in extraFields)
                {
                    try
                    {
                        await RunGpAsync("management.DeleteField",
                            Geoprocessing.MakeValueArray(whcPath, fieldName), ct);
                    }
                    catch { /* 跳过不可删除的字段 */ }
                }
            }
        }

        /// <summary>给图层所有要素的指定字段赋固定值（用 CalculateField，避免 EditOperation 锁定）</summary>
        private static async Task SetFieldValueAsync(string fcPath, string fieldName, string value, CancellationToken ct)
        {
            await RunGpAsync("management.CalculateField",
                Geoprocessing.MakeValueArray(fcPath, fieldName, $"'{value}'", "PYTHON3"), ct);
        }

        /// <summary>
        /// 删除 WHH 中多余的字段，只保留源要素类 B 的原始字段。
        /// 成对相交（PairwiseIntersect）会把两个输入的所有字段都带入结果，
        /// WHH 只应保留 B 的字段，WHQ（A的）字段需要删除。
        /// </summary>
        private static async Task RemoveExtraFieldsAsync(string whhPath, string sourceBPath, CancellationToken ct)
        {
            // 获取源 B 的字段名集合
            var bFieldNames = await QueuedTask.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string dir = Path.GetDirectoryName(sourceBPath);
                string name = Path.GetFileName(sourceBPath);
                using (var gdb = GpHelper.OpenGeodatabase(dir))
                {
                    if (gdb == null) return names;
                    using (var fc = gdb.OpenDataset<FeatureClass>(name))
                    {
                        foreach (var f in fc.GetDefinition().GetFields())
                            names.Add(f.Name);
                    }
                }
                return names;
            });

            // 获取 WHH 的字段名，找出不在 B 中的多余字段
            var extraFields = await QueuedTask.Run(() =>
            {
                var extras = new List<string>();
                string dir = Path.GetDirectoryName(whhPath);
                string name = Path.GetFileName(whhPath);
                using (var gdb = GpHelper.OpenGeodatabase(dir))
                {
                    if (gdb == null) return extras;
                    using (var fc = gdb.OpenDataset<FeatureClass>(name))
                    {
                        var def = fc.GetDefinition();
                        string oidField = def.GetObjectIDField();
                        string shapeField = def.GetShapeField();
                        foreach (var f in def.GetFields())
                        {
                            // 保留：B 中存在的字段、OID 字段、Shape 字段、以及后续会添加的 BSM/面积字段
                            if (f.Name.Equals(oidField, StringComparison.OrdinalIgnoreCase) ||
                                f.Name.Equals(shapeField, StringComparison.OrdinalIgnoreCase) ||
                                f.FieldType == FieldType.OID ||
                                f.FieldType == FieldType.Geometry)
                                continue;
                            if (!bFieldNames.Contains(f.Name))
                                extras.Add(f.Name);
                        }
                    }
                }
                return extras;
            });

            // 删除多余字段
            if (extraFields.Count > 0)
            {
                LogStatic($"  清理 {extraFields.Count} 个多余字段：{string.Join(", ", extraFields)}");
                foreach (string fieldName in extraFields)
                {
                    await RunGpAsync("management.DeleteField",
                        Geoprocessing.MakeValueArray(whhPath, fieldName), ct);
                }
            }
        }

        /// <summary>安全删除数据集（不存在时忽略）</summary>
        private static async Task SafeDeleteAsync(string path, CancellationToken ct)
        {
            try
            {
                if (await GpHelper.ExistsDatasetAsync(path))
                    await RunGpAsync("management.Delete",
                        Geoprocessing.MakeValueArray(path), ct);
            }
            catch { /* 清理失败不影响主流程 */ }
        }

        /// <summary>
        /// 按已知路径直接删除所有 _tmp_ 中间图层/表，不依赖 ListFeatureClasses/ListTables。
        /// 支持同时传入额外需要清理的路径。
        /// </summary>
        private static async Task CleanupTmpLayersAsync(string gdbPath, CancellationToken ct)
        {
            // 所有可能出现的临时名称（要素类+表）
            string[] tmpNames = {
                "_tmp_int_ab", "_tmp_int_b_q", "_tmp_int_qh",         // Type2
                "_tmp_erase1", "_tmp_erase2", "_tmp_merged",           // Type13 WHC
                "_tmp_freq",                                            // Type13 Frequency表
                "_tmp_whq_erase_whc", "_tmp_b_int_result3",           // Type13 WHH 中间
                "_tmp_merged_whh"                                      // Type13 WHH 合并
            };

            int count = 0;
            foreach (string name in tmpNames)
            {
                string fullPath = Path.Combine(gdbPath, name);
                try
                {
                    await RunGpAsync("management.Delete",
                        Geoprocessing.MakeValueArray(fullPath), ct);
                    count++;
                }
                catch { /* 不存在则跳过 */ }
            }
            if (count > 0)
                LogStatic($"  已清理 {count} 个中间图层");
        }

        /// <summary>线程安全写日志（静态方法用）</summary>
        private static void LogStatic(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DynamicMaintenance] {message}");
        }

        /// <summary>
        /// 在 GDB 中按名称查找要素类的完整路径（先查根级别，再递归查要素数据集内）。
        /// 找到返回完整路径（如 GDB\FD\LayerName），找不到返回 null。
        /// 对应 .pyt 中 arcpy.Exists(os.path.join(db, layer_name)) 的隐式搜索逻辑。
        /// </summary>
        private static async Task<string> FindFeatureClassPathAsync(string gdbPath, string layerName, CancellationToken ct)
        {
            return await QueuedTask.Run(() =>
            {
                using (var gdb = GpHelper.OpenGeodatabase(gdbPath))
                {
                    if (gdb == null) return null;

                    // 1. 先查根级别
                    using (var def = gdb.GetDefinition<FeatureClassDefinition>(layerName))
                    {
                        if (def != null) return Path.Combine(gdbPath, layerName);
                    }

                    // 2. 递归查要素数据集内
                    foreach (var dsDef in gdb.GetDefinitions<FeatureDatasetDefinition>())
                    {
                        string dsName = dsDef.GetName();
                        try
                        {
                            using (var ds = gdb.OpenDataset<FeatureDataset>(dsName))
                            using (var fcDef = ds.GetDefinition<FeatureClassDefinition>(layerName))
                            {
                                if (fcDef != null) return Path.Combine(gdbPath, dsName, layerName);
                            }
                        }
                        catch { /* 要素数据集打不开，继续下一个 */ }
                    }

                    return null;
                }
            });
        }

        // ==================== 界面辅助 ====================

        /// <summary>选择GDB数据库对话框</summary>
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

        /// <summary>切换运行/空闲状态</summary>
        private void SetRunning(bool running)
        {
            BtnRun.IsEnabled = !running;
            TextDbA.IsEnabled = !running;
            TextDbB.IsEnabled = !running;
            TextOutputFolder.IsEnabled = !running;
            ListLayers.IsEnabled = !running;
            TextXzqdm.IsEnabled = !running;
            TextXzqmc.IsEnabled = !running;
            TextYear.IsEnabled = !running;
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

        /// <summary>线程安全写警告日志</summary>
        private void LogWarning(string message) => Log("[警告] " + message);
    }
}
