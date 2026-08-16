# -*- coding: utf-8 -*-
# ============================================================
# 数据库比对（原始 .pyt 参考版）
# 用途：GHBOX AddIn「数据库比对」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/DbCompareCore.cs）与本文件保持一致。
# 说明：ArcGIS Pro AddIn 版不需要本文件即可运行；本文件用于
#       1) 保留业务逻辑供对照与回归
#       2) 在没有安装 AddIn 的环境下直接用 Python 工具箱执行
# 依赖：Excel 报告使用 openpyxl（ArcGIS Pro 自带 Python 环境已含）
# ============================================================

import datetime
import os
import traceback

import arcpy

try:
    from openpyxl import Workbook
    from openpyxl.styles import Font, PatternFill, Alignment
    HAS_OPENPYXL = True
except ImportError:
    HAS_OPENPYXL = False

try:
    UNICODE_TYPE = type(u"")
except Exception:
    UNICODE_TYPE = str


class Toolbox(object):
    """ArcGIS Pro Python 工具箱入口类。"""

    def __init__(self):
        self.label = "数据库比对工具箱"
        self.alias = "ghbox_db_compare"
        self.tools = [DatabaseCompareTool]


class DatabaseCompareTool(object):
    """比对两个版本数据库（A/B）的图层名称、范围、图斑几何与属性差异。"""

    def __init__(self):
        self.label = "数据库比对"
        self.description = "比对 A/B 两个版本 GDB：图层名称、图层范围、图斑几何（逐顶点容差）、图斑属性，容差按坐标系自动确定，差异图斑可输出到结果库，生成 Excel 三表报告。"
        self.canRunInBackground = False

    def getParameterInfo(self):
        p0 = arcpy.Parameter(displayName="A 版本数据库", name="gdb_a",
                             datatype="DEWorkspace", parameterType="Required", direction="Input")

        p1 = arcpy.Parameter(displayName="B 版本数据库", name="gdb_b",
                             datatype="DEWorkspace", parameterType="Required", direction="Input")

        p2 = arcpy.Parameter(displayName="要比对的图层名（逗号分隔，留空=全部共有图层）", name="layer_names",
                             datatype="GPString", parameterType="Optional", direction="Input")

        p3 = arcpy.Parameter(displayName="唯一标识字段", name="id_field",
                             datatype="GPString", parameterType="Required", direction="Input")
        p3.value = "BSM"

        p4 = arcpy.Parameter(displayName="差异图斑输出数据库（可选）", name="output_gdb",
                             datatype="DEWorkspace", parameterType="Optional", direction="Input")

        return [p0, p1, p2, p3, p4]

    def updateMessages(self, parameters):
        if parameters[0].altered and parameters[0].valueAsText:
            if not parameters[0].valueAsText.lower().endswith(".gdb"):
                parameters[0].setErrorMessage("仅支持 .gdb。")
        if parameters[1].altered and parameters[1].valueAsText:
            if not parameters[1].valueAsText.lower().endswith(".gdb"):
                parameters[1].setErrorMessage("仅支持 .gdb。")
        if parameters[4].altered and parameters[4].valueAsText:
            if not parameters[4].valueAsText.lower().endswith(".gdb"):
                parameters[4].setErrorMessage("差异输出库仅支持 .gdb。")

    def execute(self, parameters, messages):
        gdb_a = parameters[0].valueAsText
        gdb_b = parameters[1].valueAsText
        layer_text = parameters[2].valueAsText
        id_field = parameters[3].valueAsText
        output_gdb = parameters[4].valueAsText

        # ---- 1. 图层名称集合比对（对应 C# ListFeatureClasses）----
        names_a = self._list_feature_classes(gdb_a)
        names_b = self._list_feature_classes(gdb_b)

        lower_a = set(n.lower() for n in names_a)
        lower_b = set(n.lower() for n in names_b)
        only_a_layers = sorted(n for n in names_a if n.lower() not in lower_b)
        only_b_layers = sorted(n for n in names_b if n.lower() not in lower_a)
        common_map = {}  # 小写名 -> A 库名（按 A 库名比对）
        for n in names_a:
            if n.lower() in lower_b:
                common_map[n.lower()] = n
        common_layers = sorted(common_map.values())

        self._log("图层名称比对：A库 {0} 个，B库 {1} 个，共有 {2} 个。".format(len(names_a), len(names_b), len(common_layers)))
        if only_a_layers:
            self._warn("A库独有图层 {0} 个：{1}".format(len(only_a_layers), "、".join(only_a_layers)))
        if only_b_layers:
            self._warn("B库独有图层 {0} 个：{1}".format(len(only_b_layers), "、".join(only_b_layers)))

        # ---- 2. 确定要比对的图层 ----
        if layer_text:
            wanted = [s.strip() for s in layer_text.replace("，", ",").split(",") if s.strip()]
            targets = [common_map[w.lower()] for w in wanted if w.lower() in common_map]
            missing = [w for w in wanted if w.lower() not in common_map]
            if missing:
                self._warn("以下图层不是两库共有，无法比对：{0}".format("、".join(missing)))
        else:
            targets = common_layers

        if not targets:
            self._warn("没有可比对的图层。")
            return

        self._log("开始逐图层比对，共 {0} 个：{1}".format(len(targets), "、".join(targets)))

        layer_results = []   # 结构化结果（对应 C# LayerResult）
        diff_records = []    # 差异图斑清单（对应 C# DiffRecord）

        # ---- 3. 逐图层比对 ----
        for idx, layer in enumerate(targets, 1):
            self._log("[{0}/{1}] 比对图层：{2}".format(idx, len(targets), layer))
            result, records = self._compare_one_layer(gdb_a, gdb_b, layer, id_field, output_gdb)
            layer_results.append(result)
            diff_records.extend(records)
            self._log("图层 {0}：{1}{2}".format(layer, result["status"],
                                              "（" + result["diff_summary"] + "）" if result["diff_summary"] else ""))

        # ---- 4. Excel 报告（对应 C# WriteExcelReport）----
        report_dir = os.path.dirname((output_gdb or gdb_a).rstrip("\\"))
        self._write_excel_report(report_dir, gdb_a, gdb_b, id_field, output_gdb,
                                 only_a_layers, only_b_layers, common_layers,
                                 layer_results, diff_records)
        self._log("数据库比对执行完成。")

    # -----------------------------------------------------------------
    # 图层枚举（对应 C# ListFeatureClasses：含要素数据集内、跳过 GDB_ 系统表）
    # -----------------------------------------------------------------

    def _list_feature_classes(self, gdb):
        names = set()
        for dirpath, dirnames, filenames in arcpy.da.Walk(gdb, datatype="FeatureClass"):
            for name in filenames:
                if not name.upper().startswith("GDB_"):
                    names.add(name)
        return sorted(names)

    # -----------------------------------------------------------------
    # 单图层比对（对应 C# CompareOneLayerAsync）
    # -----------------------------------------------------------------

    def _compare_one_layer(self, gdb_a, gdb_b, layer, id_field, output_gdb):
        result = {
            "layer": layer, "status": "一致", "fail_reason": "", "cs_name": "", "tol_desc": "",
            "same_cs": True, "extent_equal": True, "extent_a": "", "extent_b": "",
            "count_a": 0, "count_b": 0, "matched": 0, "only_a": 0, "only_b": 0,
            "geom_diff": 0, "attr_diff": 0, "fields_only_a": [], "fields_only_b": "",
            "diff_summary": "",
        }
        records = []
        try:
            fc_a = os.path.join(gdb_a, layer)
            fc_b = os.path.join(gdb_b, layer)

            # ---- 坐标系与容差（自动确定，对应 C# ResolveTolerance）----
            desc_a = arcpy.Describe(fc_a)
            desc_b = arcpy.Describe(fc_b)
            sr_a = desc_a.spatialReference
            sr_b = desc_b.spatialReference
            result["same_cs"] = (sr_a.name == sr_b.name)
            result["cs_name"] = sr_a.name
            tol = self._resolve_tolerance(sr_a)
            unit = u"米" if sr_a.type == "Projected" else u"度"
            result["tol_desc"] = u"{0:.6g}{1} × 100 = {2:.6g}{1}".format(sr_a.XYResolution, unit, tol) \
                if sr_a.XYResolution and sr_a.XYResolution > 0 else u"{0:.6g}{1}（默认）".format(tol, unit)

            # ---- 图层范围（外包矩形粗筛，对应 C# Extent 四角比较）----
            ext_a = desc_a.extent
            ext_b = desc_b.extent
            result["extent_a"] = u"{0:.4f}, {1:.4f} ~ {2:.4f}, {3:.4f}".format(ext_a.XMin, ext_a.YMin, ext_a.XMax, ext_a.YMax)
            result["extent_b"] = u"{0:.4f}, {1:.4f} ~ {2:.4f}, {3:.4f}".format(ext_b.XMin, ext_b.YMin, ext_b.XMax, ext_b.YMax)
            result["extent_equal"] = (abs(ext_a.XMin - ext_b.XMin) <= tol and abs(ext_a.YMin - ext_b.YMin) <= tol and
                                      abs(ext_a.XMax - ext_b.XMax) <= tol and abs(ext_a.YMax - ext_b.YMax) <= tol)

            # ---- 图斑配对（按标识字段建字典，对应 C# BuildIdMap）----
            dict_a, dup_a, err_a = self._build_id_map(fc_a, id_field)
            dict_b, dup_b, err_b = self._build_id_map(fc_b, id_field)
            if dict_a is None or dict_b is None:
                result["status"] = "无法比对"
                result["fail_reason"] = u"标识字段 {0} 在{1}库不存在".format(id_field, u"A" if dict_a is None else u"B")
                return result, records
            if dup_a:
                self._warn(u"图层 {0}：A库标识重复 {1} 个（后者覆盖前者）".format(layer, dup_a))
            if dup_b:
                self._warn(u"图层 {0}：B库标识重复 {1} 个（后者覆盖前者）".format(layer, dup_b))

            result["count_a"] = len(dict_a)
            result["count_b"] = len(dict_b)
            common_ids = [k for k in dict_a if k in dict_b]
            only_a_ids = [k for k in dict_a if k not in dict_b]
            only_b_ids = [k for k in dict_b if k not in dict_a]
            result["matched"] = len(common_ids)
            result["only_a"] = len(only_a_ids)
            result["only_b"] = len(only_b_ids)

            # ---- 字段集合（对应 C# CompareFieldSets）----
            fields_a = [f.name for f in arcpy.ListFields(fc_a)]
            fields_b = [f.name for f in arcpy.ListFields(fc_b)]
            set_b = set(fn.lower() for fn in fields_b)
            set_a = set(fn.lower() for fn in fields_a)
            only_fields_a = sorted(fn for fn in fields_a if fn.lower() not in set_b)
            only_fields_b = sorted(fn for fn in fields_b if fn.lower() not in set_a)
            result["fields_only_a"] = only_fields_a
            result["fields_only_b"] = only_fields_b

            # ---- 逐配对比对几何与属性（对应 C# GeometryEqual/DiffAttributes）----
            geom_diff_ids = []
            attr_diff_map = {}   # 标识 -> 差异字段列表
            for key in common_ids:
                rec_a, rec_b = dict_a[key], dict_b[key]
                if not self._geometry_equal(rec_a["shape"], rec_b["shape"], tol):
                    geom_diff_ids.append(key)
                diff_fields = [fn for fn in rec_a["values"]
                               if not self._is_system_field(fn)
                               and not self._value_equal(rec_a["values"][fn], rec_b["values"].get(fn))]
                if diff_fields:
                    attr_diff_map[key] = diff_fields

            result["geom_diff"] = len(geom_diff_ids)
            result["attr_diff"] = len(attr_diff_map)

            # ---- 差异清单收集（对应 C# DiffRecord）----
            for key in only_a_ids:
                records.append({"layer": layer, "type": u"A库独有", "id": key,
                                "oid_a": dict_a[key]["oid"], "oid_b": "", "detail": ""})
            for key in only_b_ids:
                records.append({"layer": layer, "type": u"B库独有", "id": key,
                                "oid_a": "", "oid_b": dict_b[key]["oid"], "detail": ""})
            for key in geom_diff_ids:
                records.append({"layer": layer, "type": u"几何不一致", "id": key,
                                "oid_a": dict_a[key]["oid"], "oid_b": dict_b[key]["oid"], "detail": ""})
            for key, fields in attr_diff_map.items():
                records.append({"layer": layer, "type": u"属性不一致", "id": key,
                                "oid_a": dict_a[key]["oid"], "oid_b": dict_b[key]["oid"],
                                "detail": "、".join(fields)})

            # ---- 差异落库（对应 C# ExportOneByOidAsync：500 一批）----
            if output_gdb and (only_a_ids or only_b_ids or geom_diff_ids or attr_diff_map):
                src_a, src_b = fc_a, fc_b
                self._export_by_oids(src_a, output_gdb, layer, u"A库独有图斑", [dict_a[k]["oid"] for k in only_a_ids])
                self._export_by_oids(src_b, output_gdb, layer, u"B库独有图斑", [dict_b[k]["oid"] for k in only_b_ids])
                self._export_by_oids(src_a, output_gdb, layer, u"几何不一致", [dict_a[k]["oid"] for k in geom_diff_ids])
                self._export_by_oids(src_a, output_gdb, layer, u"属性不一致", [dict_a[k]["oid"] for k in attr_diff_map])

            # ---- 状态判定（任一维度有差异即"存在差异"，对应 C#）----
            has_diff = (not result["extent_equal"] or only_a_ids or only_b_ids or
                        geom_diff_ids or attr_diff_map or only_fields_a or only_fields_b)
            result["status"] = u"存在差异" if has_diff else u"一致"
            if has_diff:
                parts = []
                if not result["extent_equal"]:
                    parts.append(u"范围不一致")
                if only_a_ids:
                    parts.append(u"A库独有{0}个".format(len(only_a_ids)))
                if only_b_ids:
                    parts.append(u"B库独有{0}个".format(len(only_b_ids)))
                if geom_diff_ids:
                    parts.append(u"几何不一致{0}个".format(len(geom_diff_ids)))
                if attr_diff_map:
                    parts.append(u"属性不一致{0}个".format(len(attr_diff_map)))
                if only_fields_a:
                    parts.append(u"A库多字段")
                if only_fields_b:
                    parts.append(u"B库多字段")
                result["diff_summary"] = u"；".join(parts)
        except Exception:
            result["status"] = u"无法比对"
            result["fail_reason"] = traceback.format_exc()
        return result, records

    # -----------------------------------------------------------------
    # 容差自动确定（对应 C# ResolveTolerance：XYResolution × 100，GCS/PCS 自动）
    # -----------------------------------------------------------------

    def _resolve_tolerance(self, sr):
        if sr and sr.XYResolution and sr.XYResolution > 0:
            return sr.XYResolution * 100
        if sr and sr.type == "Projected":
            return 0.001
        return 0.001 / 111320.0

    # -----------------------------------------------------------------
    # 图斑配对（对应 C# BuildIdMap：标识 -> OID/几何/字段值）
    # -----------------------------------------------------------------

    def _build_id_map(self, fc, id_field):
        fields = [f.name for f in arcpy.ListFields(fc)]
        if id_field not in fields:
            return None, 0, u"标识字段不存在：{0}".format(id_field)

        map_dict, duplicates = {}, 0
        try:
            with arcpy.da.SearchCursor(fc, ["OID@", id_field, "SHAPE@"] + fields) as cur:
                for row in cur:
                    key = self._to_key(row[1])
                    if key in map_dict:
                        duplicates += 1
                        continue
                    values = {}
                    for i, fn in enumerate(fields, start=3):
                        values[fn] = row[i]
                    map_dict[key] = {"oid": row[0], "shape": row[2], "values": values}
            return map_dict, duplicates, ""
        except Exception:
            return None, 0, traceback.format_exc()

    def _to_key(self, v):
        """标识值转字典键：None 视为空串，数值按 str（对应 C# InvariantCulture）"""
        if v is None:
            return u""
        if isinstance(v, float) and v == int(v):
            return str(int(v))   # 1.0 -> "1"，与整型 BSM 对齐
        return UNICODE_TYPE(v)

    # -----------------------------------------------------------------
    # 几何比对（对应 C# GeometryEqual：部件数 → 顶点数 → 逐顶点 XY 容差）
    # -----------------------------------------------------------------

    def _geometry_equal(self, ga, gb, tol):
        if ga is None and gb is None:
            return True
        if ga is None or gb is None:
            return False
        if ga.type != gb.type:
            return False
        if ga.partCount != gb.partCount:
            return False
        pts_a = self._all_vertices(ga)
        pts_b = self._all_vertices(gb)
        if len(pts_a) != len(pts_b):
            return False
        for (ax, ay), (bx, by) in zip(pts_a, pts_b):
            if abs(ax - bx) > tol or abs(ay - by) > tol:
                return False
        return True

    def _all_vertices(self, geom):
        pts = []
        for i in range(geom.partCount):
            for p in geom.getPart(i):
                if p:
                    pts.append((p.X, p.Y))
        return pts

    # -----------------------------------------------------------------
    # 属性比对（对应 C# IsSystemField / ValueEqual：NULL 与空串相等）
    # -----------------------------------------------------------------

    def _is_system_field(self, name):
        return name.upper() in ("OBJECTID", "OID", "FID", "GLOBALID",
                                "SHAPE", "SHAPE_LENGTH", "SHAPE_AREA")

    def _value_equal(self, x, y):
        sx = u"" if x is None else self._to_key(x)
        sy = u"" if y is None else self._to_key(y)
        return sx == sy

    # -----------------------------------------------------------------
    # 差异落库（对应 C# ExportOneByOidAsync：MakeFeatureLayer+CopyFeatures/Append，500 一批）
    # -----------------------------------------------------------------

    def _export_by_oids(self, src_fc, output_gdb, layer, diff_type, oids):
        if not oids:
            return
        out_name = u"差异_{0}_{1}".format(diff_type, layer)[:160]
        out_path = os.path.join(output_gdb, out_name)
        if arcpy.Exists(out_path):
            arcpy.management.Delete(out_path)

        batch = 500
        for start in range(0, len(oids), batch):
            in_list = ",".join(str(o) for o in oids[start:start + batch])
            where = "OBJECTID IN ({0})".format(in_list)
            lyr = u"ghbox_diff_{0}".format(start)
            arcpy.management.MakeFeatureLayer(src_fc, lyr, where)
            if start == 0:
                arcpy.management.CopyFeatures(lyr, out_path)
            else:
                arcpy.management.Append(lyr, out_path, "NO_TEST")
            arcpy.management.Delete(lyr)

    # -----------------------------------------------------------------
    # Excel 报告（对应 C# 三张表：比对汇总 / 图层明细 / 差异图斑清单）
    # -----------------------------------------------------------------

    def _write_excel_report(self, report_dir, gdb_a, gdb_b, id_field, output_gdb,
                            only_a_layers, only_b_layers, common_layers, layer_results, diff_records):
        consistent = sum(1 for r in layer_results if r["status"] == u"一致")
        diff = sum(1 for r in layer_results if r["status"] == u"存在差异")
        failed = sum(1 for r in layer_results if r["status"] == u"无法比对")
        self._log(u"####### 比对汇总 ####### 一致 {0} 层，存在差异 {1} 层，无法比对 {2} 层".format(consistent, diff, failed))

        if not HAS_OPENPYXL:
            self._warn("未安装 openpyxl，跳过 Excel 报告生成。")
            return

        path = os.path.join(report_dir, u"数据库比对报告_{0}.xlsx".format(
            datetime.datetime.now().strftime("%Y%m%d_%H%M%S")))

        wb = Workbook()
        title_fill = PatternFill("solid", fgColor="2F5597")
        header_fill = PatternFill("solid", fgColor="D9E2F3")
        title_font = Font(bold=True, size=16, color="FFFFFF")
        label_font = Font(bold=True, color="7F7F7F")
        ok_font = Font(bold=True, color="548235")
        bad_font = Font(bold=True, color="C00000")
        note_font = Font(italic=True, size=9, color="7F7F7F")

        # ---- ① 比对汇总 ----
        ws = wb.active
        ws.title = u"比对汇总"
        ws.merge_cells("A1:F1")
        c = ws["A1"]
        c.value = u"数据库比对报告"
        c.font = title_font
        c.fill = title_fill
        c.alignment = Alignment(horizontal="center")
        ws.row_dimensions[1].height = 30

        row = 3

        def info(label, value, warn=False):
            nonlocal row
            ws.cell(row=row, column=1, value=label).font = label_font
            cell = ws.cell(row=row, column=2, value=UNICODE_TYPE(value))
            if warn:
                cell.font = bad_font
            row += 1

        info(u"A 版本数据库", gdb_a)
        info(u"B 版本数据库", gdb_b)
        info(u"唯一标识字段", id_field)
        info(u"比对时间", datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
        info(u"差异图斑输出库", output_gdb or u"未指定（仅生成报告）")
        row += 1
        ws.cell(row=row, column=1, value=u"图层名称比对").font = Font(bold=True, size=12)
        for col in range(1, 7):
            ws.cell(row=row, column=col).fill = header_fill
        row += 1
        info(u"A库图层总数", len(only_a_layers) + len(common_layers))
        info(u"B库图层总数", len(only_b_layers) + len(common_layers))
        info(u"两库共有图层", len(common_layers))
        info(u"A库独有图层", u"无" if not only_a_layers else u"{0} 个：{1}".format(len(only_a_layers), "、".join(only_a_layers)), bool(only_a_layers))
        info(u"B库独有图层", u"无" if not only_b_layers else u"{0} 个：{1}".format(len(only_b_layers), "、".join(only_b_layers)), bool(only_b_layers))
        row += 1
        ws.cell(row=row, column=1, value=u"比对结论").font = Font(bold=True, size=12)
        for col in range(1, 7):
            ws.cell(row=row, column=col).fill = header_fill
        row += 1
        info(u"完全一致图层", u"{0} 个".format(consistent), consistent == len(layer_results) and bool(layer_results))
        info(u"存在差异图层", u"{0} 个{1}".format(diff, u"：" + "、".join(r["layer"] for r in layer_results if r["status"] == u"存在差异") if diff else u""), diff > 0)
        info(u"无法比对图层", u"{0} 个{1}".format(failed, u"：" + "、".join(r["layer"] for r in layer_results if r["status"] == u"无法比对") if failed else u""), failed > 0)
        row += 2
        ws.merge_cells(start_row=row, start_column=1, end_row=row, end_column=6)
        ws.cell(row=row, column=1,
                value=u"说明：图层范围为外包矩形粗筛指标（四角一致不代表内部一致），内部差异以图斑级几何比对为准；容差按图层坐标系自动确定（见图层明细表）。").font = note_font
        ws.column_dimensions["A"].width = 20

        # ---- ② 图层明细 ----
        ws2 = wb.create_sheet(u"图层明细")
        headers = [u"图层名", u"比对结论", u"坐标系", u"XY容差(自动)", u"坐标系一致", u"图层范围(粗筛)",
                   u"A库范围", u"B库范围", u"图斑数A", u"图斑数B", u"配对成功", u"A库独有", u"B库独有",
                   u"几何不一致", u"属性不一致", u"A库多字段", u"B库多字段", u"无法比对原因"]
        for col, h in enumerate(headers, start=1):
            cell = ws2.cell(row=1, column=col, value=h)
            cell.font = Font(bold=True)
            cell.fill = header_fill
            cell.alignment = Alignment(horizontal="center")
        ws2.row_dimensions[1].height = 22

        for r_idx, item in enumerate(layer_results, start=2):
            vals = [item["layer"], item["status"], item["cs_name"], item["tol_desc"],
                    u"是" if item["same_cs"] else u"否（几何比对仅供参考）",
                    u"一致" if item["extent_equal"] else u"不一致",
                    item["extent_a"], item["extent_b"],
                    item["count_a"], item["count_b"], item["matched"], item["only_a"], item["only_b"],
                    item["geom_diff"], item["attr_diff"],
                    u"、".join(item["fields_only_a"]) or u"无", u"、".join(item["fields_only_b"]) or u"无",
                    item["fail_reason"].split("\n")[0]]
            for col, v in enumerate(vals, start=1):
                ws2.cell(row=r_idx, column=col, value=v)
            status_cell = ws2.cell(row=r_idx, column=2)
            status_cell.font = ok_font if item["status"] == u"一致" else bad_font
            for col, num in ((12, item["only_a"]), (13, item["only_b"]),
                             (14, item["geom_diff"]), (15, item["attr_diff"])):
                if num > 0:
                    cell = ws2.cell(row=r_idx, column=col)
                    cell.font = bad_font
            if not item["extent_equal"]:
                ws2.cell(row=r_idx, column=6).font = bad_font
        ws2.freeze_panes = "A2"
        ws2.auto_filter.ref = "A1:{0}{1}".format(chr(64 + len(headers)), max(len(layer_results) + 1, 2))

        # ---- ③ 差异图斑清单 ----
        ws3 = wb.create_sheet(u"差异图斑清单")
        headers3 = [u"图层名", u"差异类型", u"标识值", u"A库OBJECTID", u"B库OBJECTID", u"差异字段/说明"]
        for col, h in enumerate(headers3, start=1):
            cell = ws3.cell(row=1, column=col, value=h)
            cell.font = Font(bold=True)
            cell.fill = header_fill
            cell.alignment = Alignment(horizontal="center")
        if diff_records:
            for r_idx, item in enumerate(diff_records, start=2):
                ws3.cell(row=r_idx, column=1, value=item["layer"])
                ws3.cell(row=r_idx, column=2, value=item["type"]).font = bad_font
                ws3.cell(row=r_idx, column=3, value=UNICODE_TYPE(item["id"]))
                ws3.cell(row=r_idx, column=4, value=item["oid_a"])
                ws3.cell(row=r_idx, column=5, value=item["oid_b"])
                ws3.cell(row=r_idx, column=6, value=item["detail"])
        else:
            ws3.cell(row=2, column=1, value=u"（无差异图斑）").font = ok_font
        ws3.freeze_panes = "A2"
        ws3.auto_filter.ref = "A1:F{0}".format(max(len(diff_records) + 1, 2))

        wb.save(path)
        self._log(u"Excel 报告已保存：{0}".format(path))

    # -----------------------------------------------------------------
    # 日志
    # -----------------------------------------------------------------

    def _log(self, message):
        arcpy.AddMessage(message)

    def _warn(self, message):
        arcpy.AddWarning(message)
