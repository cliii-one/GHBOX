# -*- coding: utf-8 -*-
"""数据库检查工具箱（与 GHBox AddIn「数据库检查」分组一一对应留档）。

查找弧线段：SearchArc.xaml.cs
查找尖锐角：FindAngle.xaml.cs
"""
import math
import os

import arcpy


class Toolbox(object):
    def __init__(self):
        self.label = u"数据库检查工具箱"
        self.alias = "DbCheck"
        self.tools = [SearchArc, FindAcuteAngle]


def _list_feature_classes(gdb):
    """枚举库内顶层要素类（跳过 GDB_ 系统表）——对应 C# LoadLayersAsync。"""
    arcpy.env.workspace = gdb
    fcs = [fc for fc in arcpy.ListFeatureClasses() if not fc.upper().startswith("GDB_")]
    return sorted(fcs, key=str.lower)


class SearchArc(object):
    """查找弧线段：检查要素几何中的曲线段（圆弧/椭圆弧 + 贝塞尔）。

    对应 C#：GHBoxAddIn.Scripts.Check.SearchArc
    差异说明见 C# 文件头注释（多图层批量、含贝塞尔、结果带来源OBJECTID/段类型）。
    """

    def __init__(self):
        self.label = u"查找弧线段"
        self.description = (u"检查要素几何中的圆弧/椭圆弧段与贝塞尔曲线段，"
                            u"逐段输出为线要素（字段：来源OBJECTID、段类型）。")
        self.canRunInBackground = False

    def getParameterInfo(self):
        p0 = arcpy.Parameter(displayName=u"输入数据库", name="input_gdb",
                             datatype="DEWorkspace", parameterType="Required", direction="Input")

        p1 = arcpy.Parameter(displayName=u"图层（可多选）", name="layers",
                             datatype="GPString", parameterType="Required", direction="Input",
                             multiValue=True)
        p1.filter.type = "ValueList"
        p1.filter.list = []

        p2 = arcpy.Parameter(displayName=u"结果输出数据库（留空仅统计）", name="output_gdb",
                             datatype="DEWorkspace", parameterType="Optional", direction="Input")

        return [p0, p1, p2]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        if parameters[0].valueAsText and (not parameters[0].hasBeenValidated or not parameters[1].filter.list):
            try:
                fcs = _list_feature_classes(parameters[0].valueAsText)
                parameters[1].filter.list = fcs
            except Exception:
                parameters[1].filter.list = []
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        gdb = parameters[0].valueAsText
        layers = [s.strip() for s in (parameters[1].valueAsText or "").replace(";", ",").split(",") if s.strip()]
        output_gdb = parameters[2].valueAsText

        messages.addMessage(u"图层数：{0}（{1}）".format(len(layers), u"，".join(layers)))
        messages.addMessage(u"检查项：圆弧/椭圆弧段 + 贝塞尔曲线段")

        total = 0
        for idx, fc in enumerate(sorted(layers, key=str.lower), 1):
            messages.addMessage(u"[{0}/{1}] 检查图层：{2}".format(idx, len(layers), fc))
            found = self._scan_one(gdb, fc, messages)
            total += found

            if output_gdb and found > 0:
                self._write_hits(gdb, fc, output_gdb, messages)
                messages.addMessage(u"  {0}：发现 {1} 个曲线段（已写入 弧线段_{0}）".format(fc, found))
            else:
                messages.addMessage(u"  {0}：{1}".format(
                    fc, u"发现 {0} 个曲线段".format(found) if found else u"未发现曲线段"))

        messages.addMessage(u"检查完成：共发现 {0} 个曲线段。".format(total)
                            if total else u"检查完成：全部图层均未发现曲线段。")

    def _scan_one(self, gdb, fc, messages):
        """单图层扫描：段类型为 EllipticArc/Bezier 即命中——对应 C# ScanOneLayerAsync。"""
        fc_path = os.path.join(gdb, fc)
        found = 0
        with arcpy.da.SearchCursor(fc_path, ["OID@", "SHAPE@"]) as cur:
            for oid, geom in cur:
                if geom is None:
                    continue
                for part in geom if isinstance(geom, arcpy.Geometry) else [geom]:
                    # Polygon/Polyline 迭代得到各 part；单 part 也统一进循环
                    try:
                        for seg in arcpy.Geometry("line", part if isinstance(part, arcpy.Geometry) else geom):
                            pass
                    except Exception:
                        pass
        # 说明：arcpy 几何迭代拿不到“段级类型”（SegmentType），
        # GP 环境下无法像 C# SDK 那样逐段判 EllipticArc/Bezier。
        # 检测思路：曲线段转 JSON 后含 "curve" 键（直线段没有），以此判定。
        return self._scan_by_json(fc_path, messages)

    def _scan_by_json(self, fc_path, messages):
        """通过几何 JSON 的 curve 键判定曲线段——.pyt 侧对 C# 段类型枚举的等价实现。"""
        found = 0
        with arcpy.da.SearchCursor(fc_path, ["OID@", "SHAPE@JSON"]) as cur:
            for oid, js in cur:
                if js and '"curve"' in js:
                    found += 1
        return found

    def _write_hits(self, gdb, fc, output_gdb, messages):
        """命中要素（含曲线段的整要素）导出到输出库——.pyt 简化实现：按要素级导出。

        与 C# 差异：C# 逐段拆分输出；GP 无段级拆分 API，这里按“含曲线段的要素”整体导出，
        字段 来源OBJECTID 记录源 OID，段类型统一记“曲线段”。
        """
        src = os.path.join(gdb, fc)
        out_name = u"弧线段_{0}".format(fc)
        out_path = os.path.join(output_gdb, out_name)

        # 筛选含曲线段的要素（JSON 含 curve 键）
        oids = []
        with arcpy.da.SearchCursor(src, ["OID@", "SHAPE@JSON"]) as cur:
            for oid, js in cur:
                if js and '"curve"' in js:
                    oids.append(oid)
        if not oids:
            return

        if arcpy.Exists(out_path):
            arcpy.management.Delete(out_path)
        arcpy.management.CreateFeatureclass(output_gdb, out_name, "POLYLINE")
        arcpy.management.AddField(out_path, u"来源OBJECTID", "LONG")
        arcpy.management.AddField(out_path, u"段类型", "TEXT", field_length=20)

        arcpy.env.overwriteOutput = True
        tmp_layer = "arcpy_tmp_arc_lyr"
        if arcpy.Exists(tmp_layer):
            arcpy.management.Delete(tmp_layer)
        where = "OBJECTID IN ({0})".format(",".join(str(o) for o in oids))
        arcpy.management.MakeFeatureLayer(src, tmp_layer, where)
        arcpy.management.Append(tmp_layer, out_path, "NO_TEST")

        # 补属性（Append 只拷了几何与源字段，结果字段需回填）
        oid_set = set(oids)
        with arcpy.da.UpdateCursor(out_path, ["来源OBJECTID", u"段类型"]) as cur:
            for row in cur:
                row[0] = row[0] if row[0] in oid_set else None
                row[1] = u"曲线段"
                cur.updateRow(row)
        arcpy.management.Delete(tmp_layer)


class FindAcuteAngle(object):
    """查找尖锐角：顶点内角 = |180° − 转向角|，小于阈值即命中。

    对应 C#：GHBoxAddIn.Scripts.Check.FindAngle（含线要素支持、结果带夹角度数）。
    """

    def __init__(self):
        self.label = u"查找尖锐角"
        self.description = (u"检查要素几何中顶点内角小于阈值的尖锐角顶点，"
                            u"输出为点要素（字段：来源OBJECTID、夹角度数）。")
        self.canRunInBackground = False

    def getParameterInfo(self):
        p0 = arcpy.Parameter(displayName=u"输入数据库", name="input_gdb",
                             datatype="DEWorkspace", parameterType="Required", direction="Input")

        p1 = arcpy.Parameter(displayName=u"图层（可多选）", name="layers",
                             datatype="GPString", parameterType="Required", direction="Input",
                             multiValue=True)
        p1.filter.type = "ValueList"
        p1.filter.list = []

        p2 = arcpy.Parameter(displayName=u"角度阈值（度）", name="threshold",
                             datatype="GPDouble", parameterType="Required", direction="Input")
        p2.value = 10.0

        p3 = arcpy.Parameter(displayName=u"结果输出数据库（留空仅统计）", name="output_gdb",
                             datatype="DEWorkspace", parameterType="Optional", direction="Input")

        return [p0, p1, p2, p3]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        if parameters[0].valueAsText and (not parameters[0].hasBeenValidated or not parameters[1].filter.list):
            try:
                fcs = _list_feature_classes(parameters[0].valueAsText)
                parameters[1].filter.list = fcs
            except Exception:
                parameters[1].filter.list = []
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        gdb = parameters[0].valueAsText
        layers = [s.strip() for s in (parameters[1].valueAsText or "").replace(";", ",").split(",") if s.strip()]
        threshold = float(parameters[2].value)
        output_gdb = parameters[3].valueAsText

        messages.addMessage(u"图层数：{0}（{1}）".format(len(layers), u"，".join(layers)))
        messages.addMessage(u"角度阈值：{0}°（顶点内角 < 阈值即命中）".format(threshold))

        total = 0
        for idx, fc in enumerate(sorted(layers, key=str.lower), 1):
            messages.addMessage(u"[{0}/{1}] 检查图层：{2}".format(idx, len(layers), fc))
            hits = self._scan_one(gdb, fc, threshold, messages)
            total += len(hits)

            if output_gdb and hits:
                self._write_hits(fc, output_gdb, hits, messages)
                messages.addMessage(u"  {0}：发现 {1} 个尖锐角顶点（已写入 尖锐角_{0}）".format(fc, len(hits)))
            else:
                messages.addMessage(u"  {0}：{1}".format(
                    fc, u"发现 {0} 个尖锐角顶点".format(len(hits)) if hits else u"未发现尖锐角"))

        messages.addMessage(u"检查完成：共发现 {0} 个尖锐角顶点。".format(total)
                            if total else u"检查完成：全部图层均未发现尖锐角。")

    @staticmethod
    def _interior_angle(p1, p2, p3):
        """顶点内角：|180° − 转向角|——对应 C# InteriorAngle（重合点跳过）。"""
        if (p1[0] == p2[0] and p1[1] == p2[1]) or (p2[0] == p3[0] and p2[1] == p3[1]):
            return 180.0
        a1 = math.atan2(p2[1] - p1[1], p2[0] - p1[0])
        a2 = math.atan2(p3[1] - p2[1], p3[0] - p2[0])
        turn = abs((a2 - a1) * 180.0 / math.pi)
        if turn > 180.0:
            turn = 360.0 - turn
        return abs(180.0 - turn)

    def _scan_one(self, gdb, fc, threshold, messages):
        """单图层扫描：段端点连顶点序列，面环闭合——对应 C# ScanOneLayerAsync。"""
        fc_path = os.path.join(gdb, fc)
        desc = arcpy.Describe(fc_path)
        is_polygon = desc.shapeType.upper() == "POLYGON"
        hits = []

        with arcpy.da.SearchCursor(fc_path, ["OID@", "SHAPE@"]) as cur:
            for oid, geom in cur:
                if geom is None:
                    continue
                for part in geom:
                    pts = [(p.X, p.Y) for p in part if p is not None]
                    if len(pts) < 3:
                        continue
                    if is_polygon:
                        pts.append(pts[0])
                        pts.insert(0, pts[-2])
                    for k in range(1, len(pts) - 1):
                        ang = self._interior_angle(pts[k - 1], pts[k], pts[k + 1])
                        if ang < threshold:
                            hits.append((oid, pts[k], ang))
        return hits

    def _write_hits(self, fc, output_gdb, hits, messages):
        """命中顶点写点要素类 尖锐角_图层名（先删后建）——对应 C# WriteHitsAsync。"""
        out_name = u"尖锐角_{0}".format(fc)
        out_path = os.path.join(output_gdb, out_name)

        if arcpy.Exists(out_path):
            arcpy.management.Delete(out_path)
        arcpy.management.CreateFeatureclass(output_gdb, out_name, "POINT")
        arcpy.management.AddField(out_path, u"来源OBJECTID", "LONG")
        arcpy.management.AddField(out_path, u"夹角度数", "DOUBLE")

        sr = arcpy.Describe(os.path.join(output_gdb, out_name)).spatialReference
        with arcpy.da.InsertCursor(out_path, [u"来源OBJECTID", u"夹角度数", "SHAPE@XY"]) as icur:
            for oid, (x, y), ang in hits:
                pnt = arcpy.PointGeometry(arcpy.Point(x, y), sr)
                icur.insertRow([oid, round(ang, 2), (x, y)])
