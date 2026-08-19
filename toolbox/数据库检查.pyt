# -*- coding: utf-8 -*-
"""数据库检查工具箱（与 GHBox AddIn「数据库检查」分组一一对应留档）。

查找弧线段：SearchArc.xaml.cs
查找尖锐角：FindAngle.xaml.cs
检查空洞：FindHole.xaml.cs
"""
import math
import os
import time

import arcpy


class Toolbox(object):
    def __init__(self):
        self.label = u"数据库检查工具箱"
        self.alias = "DbCheck"
        self.tools = [SearchArc, FindAcuteAngle, FindHole]


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
            hits = self._scan_one(gdb, fc, messages)
            total += len(hits)

            if output_gdb and hits:
                self._write_hits(gdb, fc, output_gdb, hits, messages)
                messages.addMessage(u"  {0}：发现 {1} 个曲线段（已写入 弧线段_{0}）".format(fc, len(hits)))
            else:
                messages.addMessage(u"  {0}：{1}".format(
                    fc, u"发现 {0} 个曲线段".format(len(hits)) if hits else u"未发现曲线段"))

        messages.addMessage(u"检查完成：共发现 {0} 个曲线段。".format(total)
                            if total else u"检查完成：全部图层均未发现曲线段。")

    def _scan_one(self, gdb, fc, messages):
        """单图层扫描：通过几何 JSON 的 curve 键判定曲线段。

        对应 C# ScanOneLayerAsync：C# 遍历 SegmentType == EllipticArc/Bezier，
        逐段记录命中（oid, 段类型, 线几何）；arcpy GP 环境无段级类型 API，
        但曲线段在 SHAPE@JSON 中会带 "curve" 键（直线段没有），以此等价判定。
        返回命中列表 [(oid, 段类型, 线几何), ...]。
        """
        fc_path = os.path.join(gdb, fc)
        hits = []
        with arcpy.da.SearchCursor(fc_path, ["OID@", "SHAPE@JSON", "SHAPE@"]) as cur:
            for oid, js, geom in cur:
                if js and '"curve"' in js:
                    # arcpy 无段级拆分 API，整条要素作为命中几何输出
                    # 段类型统一记"曲线段"（无法区分圆弧/贝塞尔）
                    hits.append((oid, u"曲线段", geom))
        return hits

    def _write_hits(self, gdb, fc, output_gdb, hits, messages):
        """命中要素写入输出库线要素类 弧线段_{图层名}（先删后建）。

        对应 C# WriteHitsAsync：C# 用 EditOperation.Callback 逐段插入；
        arcpy 改用 InsertCursor 逐条写入（与 FindAcuteAngle 统一风格）。
        字段：来源OBJECTID、段类型。

        关键：空间参考必须从源数据获取，并传给 CreateFeatureclass。
        若输出 FC 无空间参考，写入的几何加载到地图后无法与源图层套合。
        """
        out_name = u"弧线段_{0}".format(fc)
        out_path = os.path.join(output_gdb, out_name)

        if arcpy.Exists(out_path):
            arcpy.management.Delete(out_path)
        # 从源数据获取 SR（而非从输出 FC）——避免输出 FC 无 SR 导致几何退化/不套合
        sr = arcpy.Describe(os.path.join(gdb, fc)).spatialReference
        arcpy.management.CreateFeatureclass(output_gdb, out_name, "POLYLINE", spatial_reference=sr)
        arcpy.management.AddField(out_path, u"来源OBJECTID", "LONG")
        arcpy.management.AddField(out_path, u"段类型", "TEXT", field_length=20)

        with arcpy.da.InsertCursor(out_path, [u"来源OBJECTID", u"段类型", "SHAPE@"]) as icur:
            for oid, seg_type, geom in hits:
                icur.insertRow([oid, seg_type, geom])


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
                self._write_hits(gdb, fc, output_gdb, hits, messages)
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

    def _write_hits(self, gdb, fc, output_gdb, hits, messages):
        """命中顶点写点要素类 尖锐角_图层名（先删后建）——对应 C# WriteHitsAsync。

        关键：空间参考必须从源数据获取，并传给 CreateFeatureclass。
        若输出 FC 无空间参考，写入的点几何加载到地图后无法与源图层套合。
        """
        out_name = u"尖锐角_{0}".format(fc)
        out_path = os.path.join(output_gdb, out_name)

        if arcpy.Exists(out_path):
            arcpy.management.Delete(out_path)
        # 从源数据获取 SR（而非从输出 FC）——避免输出 FC 无 SR 导致几何退化/不套合
        sr = arcpy.Describe(os.path.join(gdb, fc)).spatialReference
        arcpy.management.CreateFeatureclass(output_gdb, out_name, "POINT", spatial_reference=sr)
        arcpy.management.AddField(out_path, u"来源OBJECTID", "LONG")
        arcpy.management.AddField(out_path, u"夹角度数", "DOUBLE")

        with arcpy.da.InsertCursor(out_path, [u"来源OBJECTID", u"夹角度数", "SHAPE@XY"]) as icur:
            for oid, (x, y), ang in hits:
                pnt = arcpy.PointGeometry(arcpy.Point(x, y), sr)
                icur.insertRow([oid, round(ang, 2), (x, y)])


class FindHole(object):
    """检查空洞：按"融合→线→面→融合→擦除→拆单部件"工具链提取空洞图斑。

    对应 C#：GHBoxAddIn.Scripts.Check.FindHole
    空洞提取原理（与用户手工流程一致，两边实现完全相同）：
      ① 融合面图层（结果1）——所有面合并为一个大面，内环空洞仍保留（用成对融合 PairwiseDissolve 加速）
      ② 结果1 要素转线 ——面边界全部转为线，含空洞边界
      ③ 线 要素转面 ——所有封闭区域变独立面，空洞区域变实心面
      ④ 转面结果再融合（结果2）——实心面合并，空洞被填充（用成对融合 PairwiseDissolve 加速）
      ⑤ 结果2 擦除 结果1 ——差集即所有空洞图斑（可能为多部件）
      ⑥ 空洞 多部件转单部件 ——每个空洞独立成行，数量与面积才准确
    铁律：不改写源数据；临时数据集用 _tmp_ 前缀+标记，运行后自动清理。
    """

    def __init__(self):
        self.label = u"检查空洞"
        self.description = (u"按融合→线→面→融合→擦除工具链提取面图层中的空洞图斑，"
                            u"输出要素类 空洞_图层名（字段：空洞面积=椭球面积·平方米）。")
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
        messages.addMessage(u"检查项：面要素空洞图斑（融合→线→面→融合→擦除→拆单部件）")

        total = 0
        for idx, fc in enumerate(sorted(layers, key=str.lower), 1):
            messages.addMessage(u"[{0}/{1}] 检查图层：{2}".format(idx, len(layers), fc))
            hits = self._scan_one(gdb, fc, output_gdb)
            # 非面图层返回 -1：只提示跳过，不显示"未发现空洞"（与 C# 一致）
            if hits == -1:
                messages.addMessage(u"  {0}：非面图层，跳过".format(fc))
                continue
            total += hits

            if output_gdb and hits:
                messages.addMessage(u"  {0}：发现 {1} 个空洞（已写入 空洞_{0}）".format(fc, hits))
            elif output_gdb:
                messages.addMessage(u"  {0}：未发现空洞图斑".format(fc))
            else:
                messages.addMessage(u"  {0}：{1}".format(
                    fc, u"发现 {0} 个空洞".format(hits) if hits else u"未发现空洞"))

        messages.addMessage(u"检查完成：共发现 {0} 个空洞。".format(total)
                            if total else u"检查完成：全部图层均未发现空洞。")

    def _scan_one(self, gdb, fc, output_gdb):
        """单图层空洞提取：用 GP 工具链"融合→要素转线→要素转面→融合→擦除→拆单部件"实现。

        对应 C# ScanOneLayerAsync：两边用完全相同的 GP 工具链。
        融合用"成对融合"PairwiseDissolve（Analysis 工具箱，默认并行处理，比经典 Dissolve 更快）；
        官方说明其输出与 Dissolve 相似可互换，但内部实现不同、输出几何会有细微差异。
        不指定字段时全部要素一次融合成一个大面（内环空洞保留），等效且速度更快。
        源面直接参与融合，无需预先拆单部件；拆单部件仅在最后一步对空洞结果执行一次。
        非面图层返回 -1（跳过）；否则返回空洞个数。
        """
        fc_path = os.path.join(gdb, fc)
        desc = arcpy.Describe(fc_path)
        if desc.shapeType.upper() != "POLYGON":
            # 非面图层：返回 -1，调用方只打"非面图层，跳过"
            return -1

        # 源 SR：用于保证输出要素类坐标系正确（并参与椭球面积计算）
        sr = desc.spatialReference

        # 临时库：结果输出库优先，否则放源库（不污染源数据，最后会清理）
        tmp_gdb = output_gdb or gdb
        tag = time.strftime("%H%M%S")
        s1 = u"_tmp_diss_{0}".format(tag)    # ① 融合结果1
        s2 = u"_tmp_lin_{0}".format(tag)     # ② 要素转线
        s3 = u"_tmp_fac_{0}".format(tag)     # ③ 要素转面
        s4 = u"_tmp_dis2_{0}".format(tag)    # ④ 融合结果2
        s5 = u"_tmp_hole_{0}".format(tag)    # ⑤ 擦除结果（可能多部件）
        s_hole = u"空洞_{0}".format(fc)      # ⑥ 拆单部件后的最终空洞结果

        # 临时数据存在先删（防止上次残留）
        for tmp in (s1, s2, s3, s4, s5, s_hole):
            p = os.path.join(tmp_gdb, tmp)
            if arcpy.Exists(p):
                arcpy.management.Delete(p)

        # ① 融合源面（整面合并，PairwiseDissolve 不指定字段 → 全部要素融合成一个面，内环空洞保留）
        #    源面直接参与融合：多部件图斑的间隙本来就是要素内部空隙，融合后并不会因此漏判，
        #    无需预先拆单部件（拆单部件仅在最后一步对空洞结果执行一次）
        arcpy.analysis.PairwiseDissolve(fc_path, os.path.join(tmp_gdb, s1))

        # ② 结果1 要素转线 → 线（面边界全部转线，含空洞边界）
        arcpy.management.FeatureToLine(os.path.join(tmp_gdb, s1), os.path.join(tmp_gdb, s2))

        # ③ 线 要素转面 → 面2（所有封闭区域变实心面，空洞被填充）
        arcpy.management.FeatureToPolygon(os.path.join(tmp_gdb, s2), os.path.join(tmp_gdb, s3))

        # ④ 面2 融合（整面合并，PairwiseDissolve）→ 结果2（实心面合并为大面）
        arcpy.analysis.PairwiseDissolve(os.path.join(tmp_gdb, s3), os.path.join(tmp_gdb, s4))

        # ⑤ 结果2 擦除 结果1 → 空洞（差集即空洞图斑，可能为多部件）
        arcpy.analysis.Erase(os.path.join(tmp_gdb, s4),   # 输入：结果2（实心大面）
                             os.path.join(tmp_gdb, s1),   # 擦除要素：结果1（原融合面，空洞保留）
                             os.path.join(tmp_gdb, s5))   # 输出：空洞（中间结果）

        # ⑥ 空洞 多部件转单部件：每个空洞独立成行，数量与面积才准确
        #    （否则一个多部件空洞只算 1 个，面积却是多部件合计）
        arcpy.management.MultipartToSinglepart(os.path.join(tmp_gdb, s5), os.path.join(tmp_gdb, s_hole))

        # 清理临时数据（①~⑤：s1~s5，s_hole 是最终结果保留）
        for tmp in (s1, s2, s3, s4, s5):
            p = os.path.join(tmp_gdb, tmp)
            if arcpy.Exists(p):
                arcpy.management.Delete(p)

        # 算椭球面积 + 补坐标系 + 计数
        count = self._finalize(tmp_gdb, s_hole, sr)

        # 若用户没指定输出库，临时空洞结果也清理掉（已统计完即可）
        if not output_gdb:
            p = os.path.join(tmp_gdb, s_hole)
            if arcpy.Exists(p):
                arcpy.management.Delete(p)

        return count

    @staticmethod
    def _finalize(tmp_gdb, hole_fc_name, sr):
        """给空洞结果要素类补坐标系和"空洞面积"字段，并统计行数。

        椭球面积用 AddGeometryAttributes 的 AREA_GEODESIC：
        该选项在任意坐标系下都按测地线算法计算椭球面积（平方米），
        输出字段名固定为 AREA_GEO。
        """
        hole_path = os.path.join(tmp_gdb, hole_fc_name)

        # 若源 SR 已知，用 DefineProjection 确保输出 FC 坐标系正确
        if sr and sr.factoryCode > 0:
            try:
                arcpy.management.DefineProjection(hole_path, sr)
            except Exception:
                pass  # DefineProjection 失败不致命，仅影响坐标系元数据

        # 加"空洞面积"字段，用 AddGeometryAttributes 的 AREA_GEODESIC 填充
        arcpy.management.AddField(hole_path, u"空洞面积", "DOUBLE")
        arcpy.management.AddGeometryAttributes(hole_path, "AREA_GEODESIC")
        # AddGeometryAttributes 生成的字段名固定为 AREA_GEO，拷贝到"空洞面积"并保留两位小数
        arcpy.management.CalculateField(hole_path, u"空洞面积", "round(!AREA_GEO!, 2)", "PYTHON3")

        # 统计行数
        return int(arcpy.management.GetCount(hole_path)[0])
