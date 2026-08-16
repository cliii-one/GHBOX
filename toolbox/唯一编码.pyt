# -*- coding: utf-8 -*-
# ============================================================
# 唯一编码（原始 .pyt 参考版）
# 用途：GHBOX AddIn「唯一编码」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/UniqueCode.xaml.cs）与本文件保持一致。
# 说明：ArcGIS Pro AddIn 版不需要本文件即可运行；本文件用于
#       1) 保留业务逻辑供对照与回归
#       2) 在没有安装 AddIn 的环境下直接用 Python 工具箱执行
# 编码规则：编码 = 编码开头 + 序号（左补零至 编码长度-开头长度 位）
#           示例：长度18、开头4201232026、起始100 → 420123202600000100 起递增
# ============================================================

import arcpy
import os


class Toolbox(object):
    """ArcGIS Pro Python 工具箱入口类。"""

    def __init__(self):
        self.label = "唯一编码工具箱"
        self.alias = "ghbox_unique_code"
        self.tools = [UniqueCodeTool]


class UniqueCodeTool(object):
    """按顺序给所选图层（可多选）的要素写入唯一编码。"""

    def __init__(self):
        self.label = "唯一编码"
        self.description = (
            "按顺序给所选图层的要素写入唯一编码。\n"
            "编码 = 编码开头 + 序号（序号左补零至编码总长度）。\n"
            "示例：编码长度 18、开头 4201232026、起始值 100 → 首码 420123202600000100。\n"
            "支持每图层独立编号或跨图层连续编号；按 OBJECTID 升序编码。"
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

        p2 = arcpy.Parameter(displayName="编码字段", name="code_field",
                             datatype="Field", parameterType="Required", direction="Input")
        p2.filter.list = ["Text", "Integer", "BigInteger"]
        p2.parameterDependencies = ["layers"]

        p3 = arcpy.Parameter(displayName="编码长度（编码总位数）", name="code_length",
                             datatype="GPLong", parameterType="Required", direction="Input")
        p3.value = 18

        p4 = arcpy.Parameter(displayName="编码开头（数字前缀）", name="code_prefix",
                             datatype="GPString", parameterType="Required", direction="Input")

        p5 = arcpy.Parameter(displayName="编码起始值", name="start_value",
                             datatype="GPLong", parameterType="Required", direction="Input")
        p5.value = 1

        p6 = arcpy.Parameter(displayName="编号方式", name="numbering_mode",
                             datatype="GPString", parameterType="Required", direction="Input")
        p6.filter.type = "ValueList"
        p6.filter.list = ["每图层独立编号", "跨图层连续编号"]
        p6.value = "每图层独立编号"

        return [p0, p1, p2, p3, p4, p5, p6]

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
        # 前置校验（对应 C# RunButton_Click 校验）
        if parameters[3].value is not None:
            length = int(parameters[3].value)
            if length < 2 or length > 40:
                parameters[3].setErrorMessage("编码长度须为 2~40。")
        prefix = parameters[4].valueAsText
        if prefix:
            if not prefix.isdigit():
                parameters[4].setErrorMessage("编码开头必须为数字。")
            elif parameters[3].value is not None and len(prefix) >= int(parameters[3].value):
                parameters[4].setErrorMessage("编码开头长度必须小于编码长度（至少留 1 位序号）。")
        if parameters[5].value is not None and int(parameters[5].value) < 0:
            parameters[5].setErrorMessage("起始值须为非负整数。")

    def execute(self, parameters, messages):
        gdb = parameters[0].valueAsText
        layers_text = parameters[1].valueAsText or ""
        layers = [s.strip() for s in layers_text.replace(";", ",").split(",") if s.strip()]
        field = parameters[2].valueAsText
        code_length = int(parameters[3].value)
        prefix = parameters[4].valueAsText
        start_value = long(parameters[5].value)
        per_layer = "独立" in parameters[6].valueAsText

        seq_digits = code_length - len(prefix)
        capacity = 10 ** seq_digits - 1

        messages.addMessage("图层数：{0}（{1}）".format(len(layers), ", ".join(layers)))
        messages.addMessage("编码字段：{0}；长度 {1}；开头 {2}；起始 {3}；{4}".format(
            field, code_length, prefix, start_value,
            "每图层独立编号" if per_layer else "跨图层连续编号"))

        # 字段类型校验（对应 C# IsWritableCodeField + 长度校验）
        for fc in layers:
            fc_path = os.path.join(gdb, fc)
            f = next((x for x in arcpy.ListFields(fc_path) if x.name.lower() == field.lower()), None)
            if f is None:
                messages.addErrorMessage("图层 {0} 不存在字段 {1}。".format(fc, field))
                return
            if f.type == "String" and f.length < code_length:
                messages.addErrorMessage("字段 {0} 长度 {1} 不足以存放 {2} 位编码。".format(field, f.length, code_length))
                return

        seq = start_value
        total = 0
        # 与 C# 版一致：图层按名称排序后依次处理
        for idx, fc in enumerate(sorted(layers), 1):
            fc_path = os.path.join(gdb, fc)
            layer_start = start_value if per_layer else seq

            count = int(arcpy.management.GetCount(fc_path)[0])
            if count == 0:
                messages.addWarningMessage("[{0}/{1}] 图层 {2} 无要素，跳过".format(idx, len(layers), fc))
                continue

            # 容量校验（对应 C# CodeOneLayerAsync）
            if layer_start + count - 1 > capacity:
                messages.addErrorMessage(
                    "图层 {0}：序号将达 {1}，超出 {2} 位容量（最大 {3}）。请增大编码长度或减小起始值。".format(
                        fc, layer_start + count - 1, seq_digits, capacity))
                if per_layer:
                    continue
                return

            # 按 OBJECTID 升序编码（对应 C# OID 升序 = 编码顺序）；
            # 写入异常时抛出终止该图层后续写入（gp 框架自动回滚本图层未提交部分）
            with arcpy.da.UpdateCursor(fc_path, ["OID@", field], sql_clause=(None, "ORDER BY OBJECTID")) as cur:
                n = layer_start
                for row in cur:
                    row[1] = prefix + str(n).zfill(seq_digits)
                    cur.updateRow(row)
                    n += 1

            written = n - layer_start
            total += written
            if not per_layer:
                seq += written
            messages.addMessage("[{0}/{1}] 图层 {2}：完成 {3} 条，编码 {4} ~ {5}".format(
                idx, len(layers), fc, written,
                prefix + str(layer_start).zfill(seq_digits),
                prefix + str(layer_start + written - 1).zfill(seq_digits)))

        messages.addMessage("唯一编码完成：{0} 个图层共写入 {1} 条。".format(len(layers), total))


def long(v):
    """Py3 兼容（无 long 类型时即 int）"""
    try:
        return __builtins__["long"](v) if isinstance(__builtins__, dict) else __builtins__.long(v)
    except Exception:
        return int(v)
