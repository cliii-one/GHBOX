# -*- coding: utf-8 -*-
import arcpy
import os

class Toolbox(object):
    def __init__(self):
        self.label = "县级拆分工具箱"
        self.alias = "CountySplit"
        self.tools = [SplitByCounty]

class SplitByCounty(object):
    def __init__(self):
        self.label = "按县拆分数据库"
        self.description = (
            "根据要素类中的 XDM（县代码）和 XMC（县名称）字段，将省级地理数据库拆分为各县独立的 GDB。\n"
            "县级数据库命名：XDM+XMC.gdb；图层命名支持两种格式。"
        )
        self.canRunInBackground = False

    def getParameterInfo(self):
        param_input_gdb = arcpy.Parameter(
            displayName="输入省级地理数据库",
            name="input_gdb",
            datatype="DEWorkspace",
            parameterType="Required",
            direction="Input"
        )
        param_input_gdb.value = r"C:\path\to\421000湖北省.gdb"

        param_output_folder = arcpy.Parameter(
            displayName="输出根文件夹",
            name="output_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )
        param_output_folder.value = r"C:\output"

        param_prefix = arcpy.Parameter(
            displayName="原图层名前缀（需移除的部分）",
            name="prefix",
            datatype="GPString",
            parameterType="Optional",
            direction="Input"
        )
        param_prefix.value = "湖北省"
        param_prefix.description = (
            "仅当选择格式②时生效。例如：原图层名为'湖北省zqcsfw'，填写'湖北省'后，"
            "图层名称基础部分变为'zqcsfw'。格式①完全不使用此前缀。"
        )

        param_naming = arcpy.Parameter(
            displayName="输出图层命名格式",
            name="naming_format",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )
        # 只保留两种格式
        param_naming.filter.type = "ValueList"
        param_naming.filter.list = [
            "① 与原始图层名称保持一致（不添加XMC，不删除前缀）",
            "② XMC + 图层名称（图层名称 = 移除前缀后的原始名）"
        ]
        param_naming.value = param_naming.filter.list[1]  # 默认选择②
        param_naming.description = (
            "选择输出图层的命名规则。其中「图层名称」指移除「原图层名前缀」后的名称（格式①不使用移除操作）。\n"
            "拆分依据：XDM（县代码）和 XMC（县名称）字段。"
        )

        params = [param_input_gdb, param_output_folder, param_prefix, param_naming]
        return params

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        return

    def updateMessages(self, parameters):
        return

    def execute(self, parameters, messages):
        input_gdb = parameters[0].valueAsText
        output_folder = parameters[1].valueAsText
        prefix = parameters[2].valueAsText
        naming_option = parameters[3].valueAsText

        # 根据选项内容确定命名模式
        if naming_option.startswith("①"):
            naming_format = "1"
        elif naming_option.startswith("②"):
            naming_format = "2"
        else:
            naming_format = "2"  # 默认②

        arcpy.env.workspace = input_gdb
        arcpy.env.overwriteOutput = True

        fcs = arcpy.ListFeatureClasses()
        if not fcs:
            messages.addErrorMessage("输入数据库中未找到任何要素类！")
            return

        messages.addMessage(f"找到 {len(fcs)} 个要素类: {', '.join(fcs)}")
        messages.addMessage("拆分依据：字段 XDM（县代码）和 XMC（县名称）")

        # 收集所有唯一县 (XDM, XMC)
        counties = set()
        for fc in fcs:
            fields = [f.name for f in arcpy.ListFields(fc)]
            if "XDM" not in fields or "XMC" not in fields:
                messages.addWarningMessage(f"要素类 {fc} 缺少 XDM 或 XMC 字段，已跳过")
                continue
            with arcpy.da.SearchCursor(fc, ["XDM", "XMC"]) as cursor:
                for row in cursor:
                    xdm = str(row[0])
                    xmc = str(row[1])
                    if xdm and xmc:
                        counties.add((xdm, xmc))
        if not counties:
            messages.addErrorMessage("未找到任何有效的县代码/名称组合！")
            return
        messages.addMessage(f"共发现 {len(counties)} 个县：{counties}")

        # 预处理：计算每个要素类的“基名”（用于格式②）
        base_names = {}
        for fc in fcs:
            if prefix and naming_format == "2" and fc.startswith(prefix):
                base_name = fc[len(prefix):]
            else:
                base_name = fc
            base_names[fc] = base_name
            messages.addMessage(f"原始图层 {fc} -> 基名: '{base_name}'")

        total = len(counties)
        for idx, (xdm, xmc) in enumerate(counties, 1):
            gdb_name = f"{xdm}{xmc}.gdb"
            target_gdb = os.path.join(output_folder, gdb_name)
            messages.addMessage(f"\n[{idx}/{total}] 处理 {xmc} ({xdm})")

            if not arcpy.Exists(target_gdb):
                arcpy.management.CreateFileGDB(output_folder, gdb_name)
                messages.addMessage(f"创建数据库: {target_gdb}")

            for fc in fcs:
                fields = [f.name for f in arcpy.ListFields(fc)]
                if "XDM" not in fields or "XMC" not in fields:
                    continue

                # 根据格式生成最终要素类名称
                if naming_format == "1":
                    target_name = fc
                else:  # naming_format == "2"
                    target_name = f"{xmc}{base_names[fc]}"

                target_fc = os.path.join(target_gdb, target_name)
                where_clause = f"XDM = '{xdm}'"

                try:
                    arcpy.analysis.Select(fc, target_fc, where_clause)
                    count = int(arcpy.management.GetCount(target_fc)[0])
                    if count > 0:
                        messages.addMessage(f"  导出 {target_name} ({count} 个要素)")
                    else:
                        arcpy.management.Delete(target_fc)
                        messages.addMessage(f"  警告：{xmc} 在 {fc} 中无要素，未创建空图层")
                except Exception as e:
                    messages.addErrorMessage(f"  导出失败 {fc} -> {target_name}: {str(e)}")

        messages.addMessage("\n县级拆分完成！")