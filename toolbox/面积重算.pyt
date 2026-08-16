# -*- coding: utf-8 -*-
# ============================================================
# 面积重算（原始 .pyt 参考版）
# 用途：GHBOX AddIn「面积重算」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/AreaCalc.xaml.cs）与本文件保持一致。
# 说明：ArcGIS Pro AddIn 版不需要本文件即可运行；本文件用于
#       1) 保留业务逻辑供对照与回归
#       2) 在没有安装 AddIn 的环境下直接用 Python 工具箱执行
# 口径：椭球面积（测地线），与 Pro「计算几何属性-AREA_GEODESIC」一致
# 换算（平方米基准）：公顷=1e4；平方公里=1e6；亩=2000/3；万亩=1e4 亩
# ============================================================

import arcpy
import decimal
import os

# 单位 → (显示名, CalculateGeometryAttributes 单位串, 换算系数 m²→该单位)
UNITS = [
    ("平方米", "SQUARE_METERS", 1.0),
    ("公顷", "HECTARES", 1.0 / 10000.0),
    ("平方公里", "SQUARE_KILOMETERS", 1.0 / 1000000.0),
    ("亩", None, 3.0 / 2000.0),          # GP 无“亩”单位：先算平方米再自行换算
    ("万亩", None, 3.0 / 20000000.0),
]


class Toolbox(object):
    """ArcGIS Pro Python 工具箱入口类。"""

    def __init__(self):
        self.label = "面积重算工具箱"
        self.alias = "ghbox_area_calc"
        self.tools = [AreaCalcTool]


class AreaCalcTool(object):
    """按椭球面积重算所选图层（可多选）要素面积并写入字段。"""

    def __init__(self):
        self.label = "面积重算"
        self.description = (
            "按椭球面积（测地线）重算所选图层的要素面积并写入指定字段。\n"
            "面积字段只接受双精度或浮点型；单位支持平方米/公顷/平方公里/亩/万亩。\n"
            "空几何跳过；单图层失败不影响其他图层。"
        )
        self.canRunInBackground = False

    def getParameterInfo(self):
        p0 = arcpy.Parameter(displayName="输入数据库（.gdb）", name="input_gdb",
                             datatype="DEWorkspace", parameterType="Required", direction="Input")

        p1 = arcpy.Parameter(displayName="图层（可多选）", name="layers",
                             datatype="GPString", parameterType="Required",
                             direction="Input", multiValue=True)
        p1.filter.type = "ValueList"
        p1.filter.list = []

        p2 = arcpy.Parameter(displayName="面积字段（双精度/浮点）", name="area_field",
                             datatype="Field", parameterType="Required", direction="Input")
        p2.filter.list = ["Double", "Single"]
        p2.parameterDependencies = ["layers"]

        p3 = arcpy.Parameter(displayName="面积单位", name="area_unit",
                             datatype="GPString", parameterType="Required", direction="Input")
        p3.filter.type = "ValueList"
        p3.filter.list = [u[0] for u in UNITS]
        p3.value = "平方米"

        # 小数位数（与 C# 版 RoundingOptions 对应：-1 = 不保留原始值）
        p4 = arcpy.Parameter(displayName="小数位数", name="digits",
                             datatype="GPString", parameterType="Required", direction="Input")
        p4.filter.type = "ValueList"
        p4.filter.list = ["不保留（原始值）", "保留 2 位小数", "保留 4 位小数"]
        p4.value = "不保留（原始值）"

        return [p0, p1, p2, p3, p4]

    def updateParameters(self, parameters):
        # 选库后联动填充图层列表（对应 C# LoadLayersAsync）
        if parameters[0].altered and parameters[0].valueAsText:
            gdb = parameters[0].valueAsText
            if gdb.lower().endswith(".gdb") and arcpy.Exists(gdb):
                fcs = sorted(
                    fc for fc in arcpy.ListFeatureClasses(None, None, gdb)
                    if not fc.upper().startswith("GDB_")
                )
                if parameters[1].filter.list != fcs:
                    parameters[1].filter.list = fcs

    def updateMessages(self, parameters):
        # 字段类型校验（对应 C# IsAreaField）
        if parameters[2].valueAsText and parameters[1].valueAsText:
            gdb = parameters[0].valueAsText
            field = parameters[2].valueAsText
            for fc in parameters[1].valueAsText.split(";"):
                fc_path = os.path.join(gdb, fc)
                f = next((x for x in arcpy.ListFields(fc_path)
                          if x.name.lower() == field.lower()), None)
                if f is not None and f.type not in ("Double", "Single"):
                    parameters[2].setErrorMessage(
                        "字段 {0} 为 {1} 型，面积字段须为双精度或浮点。".format(field, f.type))
                    break

    def execute(self, parameters, messages):
        gdb = parameters[0].valueAsText
        layers_text = parameters[1].valueAsText or ""
        layers = [s.strip() for s in layers_text.replace(";", ",").split(",") if s.strip()]
        field = parameters[2].valueAsText
        unit_name = parameters[3].valueAsText
        digits_text = parameters[4].valueAsText

        unit = next(u for u in UNITS if u[0] == unit_name)
        _, gp_unit, factor = unit

        # 小数位数（与 C# 版 RoundingOptions/CalcOneLayerAsync 一致）
        if digits_text is None or "不保留" in digits_text:
            digits = -1
        elif "2" in digits_text:
            digits = 2
        else:
            digits = 4

        messages.addMessage("图层数：{0}（{1}）".format(len(layers), ", ".join(layers)))
        digits_note = "不保留（原始值）" if digits < 0 else "保留 {0} 位小数".format(digits)
        messages.addMessage("面积字段：{0}；面积单位：{1}；小数位：{2}；口径：椭球面积（测地线）".format(
            field, unit_name, digits_note))

        def round_value(v):
            # 四舍五入（银行家舍入会与 C# AwayFromZero 口径不一致，用 decimal 修正）
            if digits < 0 or v is None:
                return v
            q = decimal.Decimal("1") if digits == 0 else decimal.Decimal("1").scaleb(-digits)
            return float(decimal.Decimal(str(v)).quantize(q, rounding=decimal.ROUND_HALF_UP))

        total = 0
        skipped_total = 0
        # 与 C# 版一致：图层按名称排序后依次处理
        for idx, fc in enumerate(sorted(layers), 1):
            fc_path = os.path.join(gdb, fc)
            messages.addMessage("[{0}/{1}] 计算图层：{2}".format(idx, len(layers), fc))

            try:
                # GP 支持“公顷/平方公里”等面积单位直接输出；
                # “亩/万亩”GP 无对应单位 → 先算平方米再换算（对应 C# factor 统一换算）
                if gp_unit is not None:
                    arcpy.management.CalculateGeometryAttributes(
                        fc_path, [[field, "AREA_GEODESIC", "", gp_unit]])
                else:
                    arcpy.management.CalculateGeometryAttributes(
                        fc_path, [[field, "AREA_GEODESIC", "", "SQUARE_METERS"]])
                    with arcpy.da.UpdateCursor(fc_path, [field]) as cur:
                        for row in cur:
                            row[0] = (row[0] or 0.0) * factor
                            cur.updateRow(row)

                # 统一在换算完成后做小数位保留（覆盖所有单位路径，与 C# 一致：
                # C# 是 areaM2*factor 后再 Round）
                if digits >= 0:
                    with arcpy.da.UpdateCursor(fc_path, [field]) as cur:
                        for row in cur:
                            if row[0] is not None:
                                row[0] = round_value(row[0])
                                cur.updateRow(row)

                count = int(arcpy.management.GetCount(fc_path)[0])
                total += count
                messages.addMessage("  {0}：完成 {1} 条".format(fc, count))
            except Exception as ex:
                messages.addErrorMessage("  图层 {0} 计算失败：{1}".format(fc, str(ex)))
                continue

        messages.addMessage("面积重算完成：{0} 个图层共处理 {1} 条。".format(len(layers), total))
