using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 数据库比对核心逻辑（纯逻辑无 UI，由 DbCompare 窗口调度）。
    ///
    /// 比对维度（由浅到深）：
    /// 1. 图层名称集合：A独有 / B独有 / 共有
    /// 2. 图层范围：Extent 四角差 ≤ 容差（粗筛指标，中间差异由图斑级几何比对兜底）
    /// 3. 图斑配对：按唯一标识字段（默认 BSM）建字典
    /// 4. 图斑几何：部件数 → 顶点数 → 逐顶点 XY 容差
    /// 5. 图斑属性：字段名集合 + 逐字段值（跳过系统字段）
    ///
    /// 容差自动确定：按图层空间参考 XYResolution × 100
    /// （GCS 单位度、PCS 单位坐标单位，自动处理，无需手填）。
    ///
    /// 报告：Excel（汇总 / 图层明细 / 差异图斑清单 三张表），ClosedXML 生成。
    /// </summary>
    internal sealed class DbCompareCore
    {
        // ---------------- 回调 ----------------

        /// <summary>进度回调：0~100 + 消息</summary>
        public Action<double, string> Progress { get; set; }

        /// <summary>普通日志回调</summary>
        public Action<string> Log { get; set; }

        /// <summary>警告日志回调</summary>
        public Action<string> LogWarning { get; set; }

        // ---------------- 结构化结果（供 Excel 报告） ----------------

        /// <summary>图层级比对结果（Excel"图层明细"表的一行）</summary>
        private sealed class LayerResult
        {
            public string LayerName;
            public string Status = "一致";            // 一致 / 存在差异 / 无法比对
            public string FailReason;                 // 无法比对的原因
            public string CsName;                     // 坐标系名称
            public string ToleranceDesc;              // 容差说明
            public bool SameCoordSystem = true;       // 两库坐标系是否一致
            public bool ExtentEqual = true;           // 图层范围是否一致
            public string ExtentA, ExtentB;           // 两库范围文本
            public int CountA, CountB;                // 图斑数
            public int Matched, OnlyA, OnlyB;         // 配对统计
            public int GeomDiffCount, AttrDiffCount;  // 几何/属性不一致数
            public List<string> FieldsOnlyInA = new List<string>();
            public List<string> FieldsOnlyInB = new List<string>();
            public string DiffSummary;                // 差异摘要（存在差异时）
        }

        /// <summary>差异图斑明细（Excel"差异图斑清单"表的一行）</summary>
        private sealed class DiffRecord
        {
            public string LayerName;
            public string DiffType;    // A库独有 / B库独有 / 几何不一致 / 属性不一致
            public string IdValue;     // 标识字段值
            public long OidA;          // A 库 OBJECTID（独有/不一致图斑来自 A 库；B库独有时为空）
            public long OidB;
            public string Detail;      // 差异明细（属性差异列出的字段）
        }

        private readonly List<LayerResult> _layers = new List<LayerResult>();
        private readonly List<DiffRecord> _diffs = new List<DiffRecord>();
        private List<string> _layersOnlyInA, _layersOnlyInB, _layersCommon;

        // ---------------- 入口 ----------------

        /// <summary>
        /// 执行全流程比对。
        /// </summary>
        /// <param name="gdbA">A 库路径（.gdb）</param>
        /// <param name="gdbB">B 库路径（.gdb）</param>
        /// <param name="idField">唯一标识字段名（默认 BSM）</param>
        /// <param name="layerFilter">图层名过滤；null 或空 = 比对两库共有全部图层</param>
        /// <param name="outputGdb">差异图斑输出库（已存在的 .gdb，可为 null）</param>
        /// <param name="ct">取消令牌</param>
        public async Task Run(string gdbA, string gdbB, string idField,
                              IEnumerable<string> layerFilter, string outputGdb, CancellationToken ct)
        {
            // 记录库路径供差异落库的 GP 工具使用
            gdbPathForLayer_A = gdbA;
            gdbPathForLayer_B = gdbB;

            // ---- 1. 库级：打开两库 ----
            using var geoA = GpHelper.OpenGeodatabase(gdbA);
            using var geoB = GpHelper.OpenGeodatabase(gdbB);
            if (geoA == null || geoB == null)
                throw new InvalidOperationException("A 库或 B 库无法打开（仅支持 .gdb）。");

            // ---- 2. 图层名称集合比对 ----
            Report(5, "枚举两库图层...");
            var namesA = ListFeatureClasses(geoA);
            var namesB = ListFeatureClasses(geoB);

            _layersOnlyInA = namesA.Where(n => !namesB.Contains(n)).OrderBy(n => n).ToList();
            _layersOnlyInB = namesB.Where(n => !namesA.Contains(n)).OrderBy(n => n).ToList();
            _layersCommon = namesA.Where(namesB.Contains).OrderBy(n => n).ToList();

            Log($"图层名称比对：A库 {namesA.Count} 个，B库 {namesB.Count} 个，共有 {_layersCommon.Count} 个。");
            if (_layersOnlyInA.Count > 0)
                LogWarning($"A库独有图层 {_layersOnlyInA.Count} 个：{string.Join("、", _layersOnlyInA)}");
            if (_layersOnlyInB.Count > 0)
                LogWarning($"B库独有图层 {_layersOnlyInB.Count} 个：{string.Join("、", _layersOnlyInB)}");

            // ---- 3. 确定要比对的图层（用户指定则过滤到共有集合）----
            List<string> targets;
            if (layerFilter != null && layerFilter.Any())
            {
                targets = layerFilter.Where(n => _layersCommon.Contains(n)).ToList();
                var missing = layerFilter.Where(n => !_layersCommon.Contains(n)).ToList();
                if (missing.Count > 0)
                    LogWarning($"以下图层不是两库共有，无法比对：{string.Join("、", missing)}");
            }
            else
            {
                targets = _layersCommon;
            }

            if (targets.Count == 0)
            {
                LogWarning("没有可比对的图层。");
                WriteExcelReport(gdbA, gdbB, idField, outputGdb);
                Report(100, "比对完成");
                return;
            }
            Log($"开始逐图层比对，共 {targets.Count} 个：{string.Join("、", targets)}");

            // ---- 4. 逐图层比对 ----
            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string layer = targets[i];
                Report(5 + 88.0 * i / targets.Count, $"[{i + 1}/{targets.Count}] 比对图层：{layer}");
                var result = await CompareOneLayerAsync(geoA, geoB, layer, idField, outputGdb, ct);
                _layers.Add(result);
                Log($"图层 {layer}：{result.Status}" +
                    (string.IsNullOrEmpty(result.DiffSummary) ? "" : $"（{result.DiffSummary}）"));
            }

            // ---- 5. 汇总与 Excel 报告 ----
            Report(96, "生成 Excel 报告...");
            WriteExcelReport(gdbA, gdbB, idField, outputGdb);
            // 全部比对完成，进度条置满（循环内最后一次进度只到 5+88*(N-1)/N）
            Report(100, "比对完成");
        }

        // ---------------- 图层级比对 ----------------

        /// <summary>比对单个图层（两库共有），返回结构化结果</summary>
        private async Task<LayerResult> CompareOneLayerAsync(Geodatabase geoA, Geodatabase geoB,
            string layerName, string idField, string outputGdb, CancellationToken ct)
        {
            var result = new LayerResult { LayerName = layerName };
            try
            {
                using var fcA = geoA.OpenDataset<FeatureClass>(layerName);
                using var fcB = geoB.OpenDataset<FeatureClass>(layerName);

                // ---- 坐标系与容差（自动确定）----
                var srA = fcA.GetDefinition().GetSpatialReference();
                var srB = fcB.GetDefinition().GetSpatialReference();
                result.SameCoordSystem = string.Equals(srA?.Wkt, srB?.Wkt, StringComparison.OrdinalIgnoreCase);
                result.CsName = srA?.Name ?? "未知";
                double tol = ResolveTolerance(srA, out string tolDesc);
                result.ToleranceDesc = tolDesc;

                // ---- 图层范围比对（粗筛指标：外包矩形一致不代表内部一致，
                //      内部差异由下方图斑级几何比对逐顶点兜底，不会漏）----
                var extA = fcA.GetDefinition().GetExtent();
                var extB = fcB.GetDefinition().GetExtent();
                result.ExtentA = $"{extA.XMin:F4}, {extA.YMin:F4} ~ {extA.XMax:F4}, {extA.YMax:F4}";
                result.ExtentB = $"{extB.XMin:F4}, {extB.YMin:F4} ~ {extB.XMax:F4}, {extB.YMax:F4}";
                result.ExtentEqual = Math.Abs(extA.XMin - extB.XMin) <= tol &&
                                     Math.Abs(extA.YMin - extB.YMin) <= tol &&
                                     Math.Abs(extA.XMax - extB.XMax) <= tol &&
                                     Math.Abs(extA.YMax - extB.YMax) <= tol;

                // ---- 图斑配对 ----
                string findErr = null;
                var dictA = BuildIdMap(fcA, idField, out int dupA, ref findErr);
                var dictB = BuildIdMap(fcB, idField, out int dupB, ref findErr);
                if (dictA == null || dictB == null)
                {
                    result.Status = "无法比对";
                    result.FailReason = $"标识字段 {idField} 在{(dictA == null ? "A" : "B")}库不存在";
                    return result;
                }
                if (dupA > 0) LogWarnCore($"图层 {layerName}：A库标识重复 {dupA} 个（后者覆盖前者）");
                if (dupB > 0) LogWarnCore($"图层 {layerName}：B库标识重复 {dupB} 个（后者覆盖前者）");

                result.CountA = dictA.Count;
                result.CountB = dictB.Count;
                var commonIds = dictA.Keys.Where(dictB.ContainsKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var onlyAIds = dictA.Keys.Where(k => !dictB.ContainsKey(k)).OrderBy(k => k).ToList();
                var onlyBIds = dictB.Keys.Where(k => !dictA.ContainsKey(k)).OrderBy(k => k).ToList();
                result.Matched = commonIds.Count;
                result.OnlyA = onlyAIds.Count;
                result.OnlyB = onlyBIds.Count;

                // ---- 字段集合比较 ----
                CompareFieldSets(fcA, fcB, result);

                // ---- 逐配对比对几何与属性 ----
                var geomDiff = new List<string>();
                var attrDiff = new Dictionary<string, List<string>>(); // 标识 → 差异字段
                foreach (string id in commonIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var recA = dictA[id];
                    var recB = dictB[id];

                    if (!GeometryEqual(recA.Geometry, recB.Geometry, tol))
                        geomDiff.Add(id);

                    List<string> fields = DiffAttributes(recA, recB);
                    if (fields.Count > 0)
                        attrDiff[id] = fields;
                }
                result.GeomDiffCount = geomDiff.Count;
                result.AttrDiffCount = attrDiff.Count;

                // ---- 差异明细收集（Excel"差异图斑清单"）----
                foreach (string id in onlyAIds)
                    _diffs.Add(new DiffRecord { LayerName = layerName, DiffType = "A库独有",
                        IdValue = id, OidA = dictA[id].Oid });
                foreach (string id in onlyBIds)
                    _diffs.Add(new DiffRecord { LayerName = layerName, DiffType = "B库独有",
                        IdValue = id, OidB = dictB[id].Oid });
                foreach (string id in geomDiff)
                    _diffs.Add(new DiffRecord { LayerName = layerName, DiffType = "几何不一致",
                        IdValue = id, OidA = dictA[id].Oid, OidB = dictB[id].Oid });
                foreach (var kv in attrDiff)
                    _diffs.Add(new DiffRecord { LayerName = layerName, DiffType = "属性不一致",
                        IdValue = kv.Key, OidA = dictA[kv.Key].Oid, OidB = dictB[kv.Key].Oid,
                        Detail = string.Join("、", kv.Value) });

                // ---- 差异落库 ----
                if (outputGdb != null && (onlyAIds.Count + onlyBIds.Count + geomDiff.Count + attrDiff.Count) > 0)
                    await ExportDiffFeaturesAsync(layerName,
                        onlyAIds, onlyBIds, geomDiff, attrDiff.Keys.ToList(), dictA, dictB, outputGdb, ct);

                // ---- 状态判定：任一维度有差异即"存在差异" ----
                bool hasDiff = !result.ExtentEqual || onlyAIds.Count > 0 || onlyBIds.Count > 0 ||
                               geomDiff.Count > 0 || attrDiff.Count > 0;
                result.Status = hasDiff ? "存在差异" : "一致";
                if (hasDiff)
                {
                    var parts = new List<string>();
                    if (!result.ExtentEqual) parts.Add("范围不一致");
                    if (onlyAIds.Count > 0) parts.Add($"A库独有{onlyAIds.Count}个");
                    if (onlyBIds.Count > 0) parts.Add($"B库独有{onlyBIds.Count}个");
                    if (geomDiff.Count > 0) parts.Add($"几何不一致{geomDiff.Count}个");
                    if (attrDiff.Count > 0) parts.Add($"属性不一致{attrDiff.Count}个");
                    if (result.FieldsOnlyInA.Count > 0) parts.Add("A库多字段");
                    if (result.FieldsOnlyInB.Count > 0) parts.Add("B库多字段");
                    result.DiffSummary = string.Join("；", parts);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Status = "无法比对";
                result.FailReason = ex.Message;
            }
            return result;
        }

        // ---------------- 容差自动确定 ----------------

        /// <summary>
        /// 按图层坐标系自动确定 XY 容差：
        /// 优先用空间参考自身的 XYResolution（数据采集精度，GCS 为度、PCS 为坐标单位），
        /// 取其 100 倍作为比对容差（与 ArcGIS 默认聚类容差同量级，屏蔽浮点存储微差）。
        /// 无法读取时按坐标系类型兜底（PCS=0.001米，GCS=0.001/111320 度）。
        /// </summary>
        private static double ResolveTolerance(SpatialReference sr, out string desc)
        {
            string csType = sr?.IsProjected == true ? "投影坐标系(PCS)" : "地理坐标系(GCS)";
            string unit = sr?.IsProjected == true ? "米" : "度";

            double tol;
            if (sr != null && sr.XYResolution > 0)
            {
                tol = sr.XYResolution * 100;
                desc = $"{sr.XYResolution:G6}{unit} × 100 = {tol:G6}{unit}";
            }
            else
            {
                tol = sr?.IsProjected == true ? 0.001 : 0.001 / 111320;
                desc = $"{tol:G6}{unit}（默认）";
            }
            return tol;
        }

        // ---------------- 图斑配对与属性 ----------------

        /// <summary>图斑记录：标识 → 几何 + 字段值（供配对比对与差异落库）</summary>
        private sealed class FeatureRecord
        {
            public long Oid;
            public Geometry Geometry;
            public Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>按标识字段建字典。标识字段不存在返回 null（findErr 记录原因）。</summary>
        private static Dictionary<string, FeatureRecord> BuildIdMap(FeatureClass fc, string idField,
            out int duplicates, ref string findErr)
        {
            duplicates = 0;
            var map = new Dictionary<string, FeatureRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var def = fc.GetDefinition();
                int idx = def.FindField(idField);
                if (idx < 0)
                {
                    findErr = $"标识字段不存在：{idField}";
                    return null;
                }

                using var cur = fc.Search(null, false);
                while (cur.MoveNext())
                {
                    // FeatureClass 检索返回的 Row 即 Feature，取几何需转 Feature
                    using var f = cur.Current as Feature;
                    if (f == null) continue;
                    string key = ToKey(f[idx]);
                    if (map.ContainsKey(key)) { duplicates++; continue; }
                    var rec = new FeatureRecord { Oid = f.GetObjectID(), Geometry = f.GetShape() };
                    foreach (Field field in def.GetFields())
                        rec.Values[field.Name] = f.FindField(field.Name) >= 0 ? f[field.Name] : null;
                    map[key] = rec;
                }
                return map;
            }
            catch (Exception ex)
            {
                findErr = ex.Message;
                return null;
            }
        }

        /// <summary>标识值转字典键：数值用 InvariantCulture，NULL 视为空串</summary>
        private static string ToKey(object v)
            => v == null || v == DBNull.Value ? string.Empty
             : v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture)
             : v.ToString();

        /// <summary>比较两图层的字段名集合，写回 result（A 多 / B 多）</summary>
        private static void CompareFieldSets(FeatureClass fcA, FeatureClass fcB, LayerResult result)
        {
            var fieldsA = fcA.GetDefinition().GetFields().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fieldsB = fcB.GetDefinition().GetFields().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.FieldsOnlyInA = fieldsA.Where(n => !fieldsB.Contains(n)).OrderBy(n => n).ToList();
            result.FieldsOnlyInB = fieldsB.Where(n => !fieldsA.Contains(n)).OrderBy(n => n).ToList();
        }

        /// <summary>比对单图斑属性，返回差异字段名列表</summary>
        private static List<string> DiffAttributes(FeatureRecord a, FeatureRecord b)
        {
            var diffFields = new List<string>();
            foreach (var kv in a.Values)
            {
                if (IsSystemField(kv.Key)) continue;
                b.Values.TryGetValue(kv.Key, out object valB);
                if (!ValueEqual(kv.Value, valB))
                    diffFields.Add(kv.Key);
            }
            return diffFields;
        }

        /// <summary>系统字段不参与属性比对（自动维护，比了必误报）</summary>
        private static bool IsSystemField(string name)
        {
            switch (name.ToUpperInvariant())
            {
                case "OBJECTID":
                case "OID":
                case "FID":
                case "GLOBALID":
                case "SHAPE":
                case "SHAPE_LENGTH":
                case "SHAPE_AREA":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>字段值相等判断：NULL/空串视为相等，数值按不变文化字符串，其余 ToString</summary>
        private static bool ValueEqual(object x, object y)
        {
            string sx = x == null || x == DBNull.Value ? "" : ToKey(x);
            string sy = y == null || y == DBNull.Value ? "" : ToKey(y);
            return string.Equals(sx, sy, StringComparison.Ordinal);
        }

        // ---------------- 几何比对 ----------------

        /// <summary>
        /// 几何逐顶点比对：类型 → 部件数 → 顶点数 → 逐顶点 XY 容差。
        /// 已知限制：顶点顺序/环排列不同的等价几何会判为不一致（编辑差异场景下两版本同源，顺序一致）。
        /// </summary>
        private static bool GeometryEqual(Geometry ga, Geometry gb, double tol)
        {
            if (ga == null && gb == null) return true;
            if (ga == null || gb == null) return false;
            if (ga.GeometryType != gb.GeometryType) return false;

            int partCountA = GetPartCount(ga);
            int partCountB = GetPartCount(gb);
            if (partCountA != partCountB) return false;

            var ptsA = GetAllVertices(ga);
            var ptsB = GetAllVertices(gb);
            if (ptsA.Count != ptsB.Count) return false;

            for (int i = 0; i < ptsA.Count; i++)
            {
                if (Math.Abs(ptsA[i].x - ptsB[i].x) > tol ||
                    Math.Abs(ptsA[i].y - ptsB[i].y) > tol)
                    return false;
            }
            return true;
        }

        /// <summary>取部件数（多部件）</summary>
        private static int GetPartCount(Geometry g)
            => g is Multipart mp ? mp.PartCount : 1;

        /// <summary>展平全部顶点（仅 XY）</summary>
        private static List<(double x, double y)> GetAllVertices(Geometry g)
        {
            var list = new List<(double, double)>();
            if (g is Multipart mp)
            {
                foreach (ReadOnlySegmentCollection part in mp.Parts)
                    foreach (Segment seg in part)
                    {
                        list.Add((seg.StartCoordinate.X, seg.StartCoordinate.Y));
                        list.Add((seg.EndCoordinate.X, seg.EndCoordinate.Y));
                    }
            }
            else if (g is MapPoint p)
            {
                list.Add((p.X, p.Y));
            }
            return list;
        }

        // ---------------- 差异落库 ----------------

        /// <summary>记录 GP 导出所需的库路径（Run 时赋值）</summary>
        private string gdbPathForLayer_A, gdbPathForLayer_B;

        /// <summary>
        /// 差异图斑导出到结果库（每图层 4 类，有则先删）：
        /// A库独有 / B库独有 / 几何不一致 / 属性不一致。
        /// 用 GP 工具链（MakeFeatureLayer 按 OBJECTID 筛选 + CopyFeatures/Append），
        /// 继承原图层全部字段与坐标系，不依赖 DDL API。
        /// </summary>
        private async Task ExportDiffFeaturesAsync(string layerName,
            List<string> onlyAIds, List<string> onlyBIds, List<string> geomDiff, List<string> attrDiff,
            Dictionary<string, FeatureRecord> dictA, Dictionary<string, FeatureRecord> dictB,
            string outputGdb, CancellationToken ct)
        {
            string srcA = $"{gdbPathForLayer_A}\\{layerName}";
            string srcB = $"{gdbPathForLayer_B}\\{layerName}";

            await ExportOneByOidAsync(srcA, outputGdb, layerName,
                onlyAIds.Select(id => dictA[id].Oid).ToList(), "A库独有图斑", ct);
            await ExportOneByOidAsync(srcB, outputGdb, layerName,
                onlyBIds.Select(id => dictB[id].Oid).ToList(), "B库独有图斑", ct);
            await ExportOneByOidAsync(srcA, outputGdb, layerName,
                geomDiff.Select(id => dictA[id].Oid).ToList(), "几何不一致", ct);
            await ExportOneByOidAsync(srcA, outputGdb, layerName,
                attrDiff.Select(id => dictA[id].Oid).ToList(), "属性不一致", ct);
        }

        /// <summary>
        /// 按 OBJECTID 集合导出差异图斑到结果库：
        /// 结果要素类已存在先删；每 500 个 OID 一批（避免 SQL 过长），
        /// 首批 CopyFeatures 创建，后续批 Append 追加。
        /// </summary>
        private static async Task ExportOneByOidAsync(string srcPath, string outputGdb, string layerName,
            List<long> oids, string diffType, CancellationToken ct)
        {
            if (oids == null || oids.Count == 0) return;
            string outName = $"差异_{diffType}_{layerName}";
            if (outName.Length > 160) outName = outName.Substring(0, 160);
            string outPath = $"{outputGdb.TrimEnd('\\')}\\{outName}";

            // 结果要素类已存在则先删（重复运行覆盖旧结果）
            if (await GpHelper.ExistsDatasetAsync(outPath))
                await GpHelper.RunToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(outPath), ct);

            const int batchSize = 500;
            for (int start = 0; start < oids.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                string inList = string.Join(",", oids.Skip(start).Take(batchSize));
                string where = $"OBJECTID IN ({inList})";
                string lyrName = $"ghbox_diff_{start}";

                await GpHelper.RunToolAsync("management.MakeFeatureLayer",
                    Geoprocessing.MakeValueArray(srcPath, lyrName, where), ct);

                if (start == 0)
                    await GpHelper.RunToolAsync("management.CopyFeatures",
                        Geoprocessing.MakeValueArray(lyrName, outPath), ct);
                else
                    await GpHelper.RunToolAsync("management.Append",
                        Geoprocessing.MakeValueArray(lyrName, outPath, "NO_TEST"), ct);

                await GpHelper.RunToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(lyrName), ct);
            }
        }

        // ---------------- 图层枚举 ----------------

        /// <summary>枚举库内全部要素类名（含要素数据集内、跳过 GDB_ 系统表），忽略大小写去重</summary>
        private static List<string> ListFeatureClasses(Geodatabase gdb)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in gdb.GetDefinitions<FeatureClassDefinition>())
                if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                    names.Add(def.GetName());

            foreach (var dsDef in gdb.GetDefinitions<FeatureDatasetDefinition>())
            {
                using FeatureDataset ds = gdb.OpenDataset<FeatureDataset>(dsDef.GetName());
                foreach (var def in ds.GetDefinitions<FeatureClassDefinition>())
                    if (!def.GetName().StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                        names.Add(def.GetName());
            }
            return names.ToList();
        }

        // ---------------- Excel 报告 ----------------

        // 报告配色（克制三色：标题深蓝 / 表头浅蓝 / 状态红绿）
        private static readonly XLColor TitleBg = XLColor.FromHtml("#2F5597");
        private static readonly XLColor HeaderBg = XLColor.FromHtml("#D9E2F3");
        private static readonly XLColor OkGreen = XLColor.FromHtml("#548235");
        private static readonly XLColor BadRed = XLColor.FromHtml("#C00000");
        private static readonly XLColor GrayText = XLColor.FromHtml("#7F7F7F");

        /// <summary>
        /// 生成 Excel 报告（三张表）：
        /// ① 比对汇总：库信息 + 图层集合对比 + 逐层结论统计
        /// ② 图层明细：每个图层一行，各维度比对数字一目了然
        /// ③ 差异图斑清单：每个差异图斑一行（图层/类型/标识/OID/差异字段）
        /// 保存到输出库同级目录；未指定输出库时保存到 A 库同级目录。
        /// </summary>
        private void WriteExcelReport(string gdbA, string gdbB, string idField, string outputGdb)
        {
            int consistent = _layers.Count(r => r.Status == "一致");
            int diff = _layers.Count(r => r.Status == "存在差异");
            int failed = _layers.Count(r => r.Status == "无法比对");

            Log($"####### 比对汇总 ####### 一致 {consistent} 层，存在差异 {diff} 层，无法比对 {failed} 层");

            string folder = outputGdb != null
                ? Path.GetDirectoryName(outputGdb.TrimEnd('\\'))
                : Path.GetDirectoryName(gdbA.TrimEnd('\\'));
            string path = Path.Combine(folder, $"数据库比对报告_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            try
            {
                using var wb = new XLWorkbook();

                BuildSummarySheet(wb, gdbA, gdbB, idField, outputGdb, consistent, diff, failed);
                BuildLayerSheet(wb);
                BuildDiffSheet(wb);

                wb.SaveAs(path);
                Log($"Excel 报告已保存：{path}");
            }
            catch (Exception ex)
            {
                LogWarnCore($"Excel 报告保存失败：{ex.Message}");
            }
        }

        /// <summary>① 汇总表：标题 + 库信息 + 图层集合 + 结论统计</summary>
        private void BuildSummarySheet(XLWorkbook wb, string gdbA, string gdbB,
            string idField, string outputGdb, int consistent, int diff, int failed)
        {
            var ws = wb.Worksheets.Add("比对汇总");

            // 标题（合并 A1:F1，深蓝底白字）
            ws.Range(1, 1, 1, 6).Merge().SetValue("数据库比对报告")
              .Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(XLColor.White)
              .Fill.SetBackgroundColor(TitleBg).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            ws.Row(1).Height = 30;

            // 基本信息区（标签列灰字，值列正常）
            int row = 3;
            row = WriteInfo(ws, row, "A 版本数据库", gdbA);
            row = WriteInfo(ws, row, "B 版本数据库", gdbB);
            row = WriteInfo(ws, row, "唯一标识字段", idField);
            row = WriteInfo(ws, row, "比对时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            row = WriteInfo(ws, row, "差异图斑输出库", outputGdb ?? "未指定（仅生成报告）");
            row++;

            // 图层集合对比
            ws.Cell(row, 1).SetValue("图层名称比对").Style.Font.SetBold().Font.SetFontSize(12);
            ws.Range(row, 1, row, 6).Merge().Style.Fill.SetBackgroundColor(HeaderBg);
            row++;
            row = WriteInfo(ws, row, "A库图层总数", _layersOnlyInA.Count + _layersCommon.Count);
            row = WriteInfo(ws, row, "B库图层总数", _layersOnlyInB.Count + _layersCommon.Count);
            row = WriteInfo(ws, row, "两库共有图层", _layersCommon.Count);
            row = WriteInfo(ws, row, "A库独有图层",
                _layersOnlyInA.Count == 0 ? "无" : $"{_layersOnlyInA.Count} 个：{string.Join("、", _layersOnlyInA)}",
                _layersOnlyInA.Count > 0);
            row = WriteInfo(ws, row, "B库独有图层",
                _layersOnlyInB.Count == 0 ? "无" : $"{_layersOnlyInB.Count} 个：{string.Join("、", _layersOnlyInB)}",
                _layersOnlyInB.Count > 0);
            row++;

            // 结论统计（大字号 + 红绿着色）
            ws.Cell(row, 1).SetValue("比对结论").Style.Font.SetBold().Font.SetFontSize(12);
            ws.Range(row, 1, row, 6).Merge().Style.Fill.SetBackgroundColor(HeaderBg);
            row++;
            row = WriteInfo(ws, row, "完全一致图层", consistent + " 个", consistent == _layers.Count && _layers.Count > 0);
            row = WriteInfo(ws, row, "存在差异图层", diff + " 个" +
                (diff > 0 ? "：" + string.Join("、", _layers.Where(r => r.Status == "存在差异").Select(r => r.LayerName)) : ""),
                diff > 0);
            row = WriteInfo(ws, row, "无法比对图层", failed + " 个" +
                (failed > 0 ? "：" + string.Join("、", _layers.Where(r => r.Status == "无法比对").Select(r => r.LayerName)) : ""),
                failed > 0);

            // 说明脚注
            row += 2;
            ws.Cell(row, 1).SetValue("说明：图层范围为外包矩形粗筛指标（四角一致不代表内部一致），内部差异以图斑级几何比对为准；容差按图层坐标系自动确定（见图层明细表）。")
              .Style.Font.SetFontColor(GrayText).Font.SetItalic().Font.SetFontSize(9);
            ws.Range(row, 1, row, 6).Merge();

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 20;
        }

        /// <summary>② 图层明细表：每图层一行，全维度比对数字</summary>
        private void BuildLayerSheet(XLWorkbook wb)
        {
            var ws = wb.Worksheets.Add("图层明细");

            // 表头（浅蓝底加粗）
            string[] headers =
            {
                "图层名", "比对结论", "坐标系", "XY容差(自动)", "坐标系一致", "图层范围(粗筛)",
                "A库范围", "B库范围", "图斑数A", "图斑数B", "配对成功", "A库独有", "B库独有",
                "几何不一致", "属性不一致", "A库多字段", "B库多字段", "无法比对原因"
            };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.SetValue(headers[i]);
                cell.Style.Font.SetBold().Fill.SetBackgroundColor(HeaderBg)
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }
            // 表头行筛选
            ws.Range(1, 1, 1, headers.Length).SetAutoFilter();
            ws.Row(1).Height = 22;

            // 数据行
            for (int r = 0; r < _layers.Count; r++)
            {
                var item = _layers[r];
                int row = r + 2;
                ws.Cell(row, 1).SetValue(item.LayerName);
                ws.Cell(row, 2).SetValue(item.Status);
                ws.Cell(row, 3).SetValue(item.CsName);
                ws.Cell(row, 4).SetValue(item.ToleranceDesc);
                ws.Cell(row, 5).SetValue(item.SameCoordSystem ? "是" : "否（几何比对仅供参考）");
                ws.Cell(row, 6).SetValue(item.ExtentEqual ? "一致" : "不一致");
                ws.Cell(row, 7).SetValue(item.ExtentA);
                ws.Cell(row, 8).SetValue(item.ExtentB);
                ws.Cell(row, 9).SetValue(item.CountA);
                ws.Cell(row, 10).SetValue(item.CountB);
                ws.Cell(row, 11).SetValue(item.Matched);
                ws.Cell(row, 12).SetValue(item.OnlyA);
                ws.Cell(row, 13).SetValue(item.OnlyB);
                ws.Cell(row, 14).SetValue(item.GeomDiffCount);
                ws.Cell(row, 15).SetValue(item.AttrDiffCount);
                ws.Cell(row, 16).SetValue(item.FieldsOnlyInA.Count == 0 ? "无" : string.Join("、", item.FieldsOnlyInA));
                ws.Cell(row, 17).SetValue(item.FieldsOnlyInB.Count == 0 ? "无" : string.Join("、", item.FieldsOnlyInB));
                ws.Cell(row, 18).SetValue(item.FailReason ?? "");

                // 状态着色：一致=绿、存在差异/无法比对=红
                var statusCell = ws.Cell(row, 2);
                statusCell.Style.Font.SetBold().Font.SetFontColor(
                    item.Status == "一致" ? OkGreen : BadRed);
                // 有差异的数字列标红（用已写入的整数值判断，不从单元格回读）
                foreach (int col in new[] { 12, 13, 14, 15 })
                {
                    int num = col == 12 ? item.OnlyA : col == 13 ? item.OnlyB
                            : col == 14 ? item.GeomDiffCount : item.AttrDiffCount;
                    if (num > 0)
                        ws.Cell(row, col).Style.Font.SetFontColor(BadRed).Font.SetBold();
                }
                if (!item.ExtentEqual)
                    ws.Cell(row, 6).Style.Font.SetFontColor(BadRed).Font.SetBold();
            }

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, 5);   // 前几列自适应，宽文本列不撑爆
        }

        /// <summary>③ 差异图斑清单表：每差异图斑一行</summary>
        private void BuildDiffSheet(XLWorkbook wb)
        {
            var ws = wb.Worksheets.Add("差异图斑清单");

            string[] headers = { "图层名", "差异类型", "标识值", "A库OBJECTID", "B库OBJECTID", "差异字段/说明" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.SetValue(headers[i]);
                cell.Style.Font.SetBold().Fill.SetBackgroundColor(HeaderBg)
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }
            ws.Range(1, 1, 1, headers.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(1);

            for (int r = 0; r < _diffs.Count; r++)
            {
                var item = _diffs[r];
                int row = r + 2;
                ws.Cell(row, 1).SetValue(item.LayerName);
                ws.Cell(row, 2).SetValue(item.DiffType);
                ws.Cell(row, 3).SetValue(item.IdValue);
                if (item.OidA > 0) ws.Cell(row, 4).SetValue(item.OidA); else ws.Cell(row, 4).SetValue("");
                if (item.OidB > 0) ws.Cell(row, 5).SetValue(item.OidB); else ws.Cell(row, 5).SetValue("");
                ws.Cell(row, 6).SetValue(item.Detail ?? "");
                // 差异类型着色：独有/几何/属性差异统一红字提示
                ws.Cell(row, 2).Style.Font.SetFontColor(BadRed);
            }

            if (_diffs.Count == 0)
                ws.Cell(2, 1).SetValue("（无差异图斑）").Style.Font.SetFontColor(OkGreen);

            ws.Columns().AdjustToContents();
        }

        /// <summary>写一行"标签: 值"信息（值统一按文本写入）；warning=true 时值标红</summary>
        private static int WriteInfo(IXLWorksheet ws, int row, string label, object value, bool warning = false)
        {
            ws.Cell(row, 1).SetValue(label).Style.Font.SetBold().Font.SetFontColor(GrayText);
            var v = ws.Cell(row, 2).SetValue(value?.ToString() ?? "");
            if (warning) v.Style.Font.SetFontColor(BadRed).Font.SetBold();
            return row + 1;
        }

        // ---------------- 小工具 ----------------

        private void Report(double percent, string message) => Progress?.Invoke(percent, message);
        private void LogWarnCore(string message) => LogWarning?.Invoke(message);
    }
}
