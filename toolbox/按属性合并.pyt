# -*- coding: utf-8 -*-
# ============================================================
# 按属性合并（原始 .pyt 参考版）
# 来源：从总规动态维护工具.pyt 中抽取向相邻要素合并工具，仅保留本工具所需方法。
# 用途：GHBOX AddIn「按属性合并」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/PolygonDissolve.xaml.cs）与本文件保持一致。
# 说明：ArcGIS Pro AddIn 版不需要本文件即可运行；本文件用于
#       1) 保留原始业务逻辑供对照与回归
#       2) 在没有安装 AddIn 的环境下直接用 Python 工具箱执行
# ============================================================

import datetime
import io
import os
import re
import traceback
import uuid

import arcpy


try:
    UNICODE_TYPE = type(u"")
except Exception:
    UNICODE_TYPE = str


class Toolbox(object):
    """ArcGIS Pro Python 工具箱入口类。"""

    def __init__(self):
        self.label = "相邻要素按属性合并工具箱"
        self.alias = "ghbox_polygon_dissolve"
        self.tools = [PolygonAttributeDissolveTool]


class PolygonAttributeDissolveTool(object):
    """对相邻图斑中指定字段值相同的要素进行空间合并。"""

    def __init__(self):
        self.label = "按属性合并"
        self.description = "对相邻图斑中指定字段值相同的要素进行空间合并，支持按公共边最长或面积最大规则合并。"
        self.canRunInBackground = False

    def getParameterInfo(self):
        input_fc_param = arcpy.Parameter(
            displayName="输入要素类",
            name="input_feature_class",
            datatype="DEFeatureClass",
            parameterType="Required",
            direction="Input"
        )

        merge_fields_param = arcpy.Parameter(
            displayName="合并字段",
            name="merge_fields",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )

        merge_rule_param = arcpy.Parameter(
            displayName="合并规则",
            name="merge_rule",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )
        merge_rule_param.filter.list = [u"向公共边最长的图斑合并", u"向面积最大的图斑合并"]
        merge_rule_param.value = u"向公共边最长的图斑合并"

        output_fc_param = arcpy.Parameter(
            displayName="输出要素类",
            name="output_feature_class",
            datatype="DEFeatureClass",
            parameterType="Required",
            direction="Output"
        )

        return [input_fc_param, merge_fields_param, merge_rule_param, output_fc_param]

    def updateMessages(self, parameters):
        if parameters[0].altered and parameters[0].valueAsText:
            input_fc = parameters[0].valueAsText
            if not arcpy.Exists(input_fc):
                parameters[0].setErrorMessage("输入要素类不存在，请检查路径是否正确。")

        if parameters[1].altered and parameters[1].valueAsText:
            if not parameters[1].valueAsText.strip():
                parameters[1].setErrorMessage("请输入合并字段。")

    def execute(self, parameters, messages):
        input_fc = parameters[0].valueAsText
        merge_fields_text = parameters[1].valueAsText.strip()
        merge_rule = parameters[2].valueAsText.strip()
        output_fc = parameters[3].valueAsText

        if not arcpy.Exists(input_fc):
            raise arcpy.ExecuteError("输入要素类不存在：{0}".format(input_fc))

        merge_fields = self._parse_merge_fields(merge_fields_text)
        if not merge_fields:
            raise arcpy.ExecuteError("合并字段不能为空。")

        desc = arcpy.Describe(input_fc)
        if desc.shapeType not in ["Polygon", "多边形"]:
            raise arcpy.ExecuteError("输入要素类必须是面要素类，当前类型：{0}".format(desc.shapeType))

        self._validate_merge_fields(input_fc, merge_fields)

        # 输入与输出相同时直接拒绝（否则先 Delete 会删掉输入要素类，导致后续 CopyFeatures 失败）
        if input_fc and output_fc and os.path.normcase(os.path.abspath(input_fc)) == os.path.normcase(os.path.abspath(output_fc)):
            raise arcpy.ExecuteError("输入要素类与输出要素类不能相同，请指定其他输出路径。")

        output_folder = os.path.dirname(output_fc)
        if output_folder and not os.path.exists(output_folder):
            self._ensure_folder(output_folder)

        self._log_message("####### 按属性合并开始 #######")
        self._log_message("输入要素类：{0}".format(input_fc))
        self._log_message("合并字段：{0}".format(u", ".join(merge_fields)))
        self._log_message("合并规则：{0}".format(merge_rule))
        self._log_message("输出要素类：{0}".format(output_fc))

        source_count = int(arcpy.management.GetCount(input_fc)[0])
        self._log_message("合并前总条数：{0}".format(source_count))

        if arcpy.Exists(output_fc):
            self._log_warning("输出要素类已存在，已先删除：{0}".format(output_fc))
            arcpy.management.Delete(output_fc)

        arcpy.management.CopyFeatures(input_fc, output_fc)

        merge_count = self._dissolve_adjacent_features(output_fc, merge_fields, merge_rule)

        if merge_count > 0:
            self._log_message("正在修复几何拓扑...")
            arcpy.management.RepairGeometry(output_fc, "DELETE_NULL")
            self._log_message("几何拓扑修复完成。")

        final_count = int(arcpy.management.GetCount(output_fc)[0])
        self._log_message("合并后总条数：{0}".format(final_count))
        self._log_message("共合并了 {0} 对相邻图斑。".format(merge_count))
        self._log_message("输出要素类：{0}".format(output_fc))
        self._log_message("####### 按属性合并完成 #######")

    # -----------------------------------------------------------------
    # 字段解析与验证
    # -----------------------------------------------------------------

    def _parse_merge_fields(self, merge_fields_text):
        """解析合并字段：中文逗号转英文、去空项、去重。"""
        normalized_text = merge_fields_text.replace(u"，", u",")
        fields = []
        for item in normalized_text.split(u","):
            clean_name = item.strip()
            if clean_name and clean_name not in fields:
                fields.append(clean_name)
        return fields

    def _validate_merge_fields(self, feature_class, merge_fields):
        """校验合并字段在输入要素类中均存在。"""
        existing_field_names = [field.name.upper() for field in arcpy.ListFields(feature_class)]
        missing_fields = []
        for field_name in merge_fields:
            if field_name.upper() not in existing_field_names:
                missing_fields.append(field_name)
        if missing_fields:
            raise arcpy.ExecuteError("输入要素类中不存在以下字段：{0}".format(u", ".join(missing_fields)))

    # -----------------------------------------------------------------
    # 相邻图斑合并（核心）
    # -----------------------------------------------------------------

    def _dissolve_adjacent_features(self, feature_class, merge_fields, merge_rule):
        """使用 ArcPy 内置的分割+消除+合并流程，大幅提速。"""
        original_count = int(arcpy.management.GetCount(feature_class).getOutput(0))

        # 如果只有一个合并字段，直接用；多个字段则拼接为临时字段
        if len(merge_fields) == 1:
            split_field = merge_fields[0]
            temp_combined_field = None
        else:
            temp_combined_field = "ZZ_COMBINED_KEY"
            arcpy.management.AddField(feature_class, temp_combined_field, "TEXT", field_length=500)
            parts = ["str(!{0}!)".format(f) for f in merge_fields]
            expression = " + '|' + ".join(parts)
            arcpy.management.CalculateField(feature_class, temp_combined_field, expression, "PYTHON")
            split_field = temp_combined_field
            self._log_message("已创建临时合并字段：{0}".format(temp_combined_field))

        # 创建临时 GDB 存放分割结果（scratchFolder 是文件夹，scratchGDB 是 GDB 文件）
        # CreateFileGDB 会自动加 .gdb 后缀，传入时不带；路径变量需要带
        scratch_folder = arcpy.env.scratchFolder
        temp_gdb_stem = "dissolve_temp_{0}".format(uuid.uuid4().hex[:8])
        temp_gdb_path = os.path.join(scratch_folder, temp_gdb_stem + ".gdb")
        arcpy.management.CreateFileGDB(scratch_folder, temp_gdb_stem)

        split_gdb_stem = "dissolve_splits_{0}".format(uuid.uuid4().hex[:8])
        split_workspace = os.path.join(scratch_folder, split_gdb_stem + ".gdb")
        arcpy.management.CreateFileGDB(scratch_folder, split_gdb_stem)

        try:
            # 第1步：按属性分割
            self._log_message("正在按字段 [{0}] 分割要素...".format(split_field))
            arcpy.analysis.Split(feature_class, split_workspace, split_field)
            split_list = arcpy.ListFeatureClasses(feature_class_type="POLYGON", workspace=split_workspace)
            self._log_message("分割完成，共 {0} 个子图层。".format(len(split_list)))

            # 第2步：对每个子图层执行消除
            eliminate_rule = "LENGTH" if merge_rule == u"向公共边最长的图斑合并" else "AREA"
            self._log_message("消除规则：{0}。".format(u"按边界最长" if eliminate_rule == "LENGTH" else u"按面积最大"))

            eliminated_list = []
            total_processed = 0

            for split_name in split_list:
                split_fc = os.path.join(split_workspace, split_name)
                fc_count = int(arcpy.management.GetCount(split_fc).getOutput(0))

                if fc_count <= 1:
                    eliminated_list.append(split_fc)
                    total_processed += 1
                    continue

                # 循环消除：每轮选中除面积最大外的所有要素执行消除，
                # 处理链式相邻（A-B-C，第一轮B并入A，第二轮C并入A）。
                current_fc = split_fc
                round_index = 0

                while True:
                    round_index += 1
                    current_count = int(arcpy.management.GetCount(current_fc).getOutput(0))
                    if current_count <= 1:
                        break

                    oid_field = arcpy.Describe(current_fc).OIDFieldName

                    # 找到面积最大的要素
                    max_oid = None
                    with arcpy.da.SearchCursor(
                        current_fc, [oid_field], sql_clause=(None, "ORDER BY SHAPE_AREA DESC")
                    ) as cur:
                        for r in cur:
                            max_oid = r[0]
                            break

                    # 创建图层并选中除最大外的所有要素
                    layer_name = "lyr_{0}".format(uuid.uuid4().hex[:8])
                    arcpy.management.MakeFeatureLayer(current_fc, layer_name)

                    arcpy.management.SelectLayerByAttribute(
                        layer_name, "NEW_SELECTION",
                        "{0} <> {1}".format(oid_field, max_oid)
                    )

                    # 检查是否有选中要素
                    selected_count = int(
                        arcpy.management.GetCount(layer_name).getOutput(0))
                    if selected_count <= 0:
                        arcpy.management.Delete(layer_name)
                        break

                    # 执行消除
                    round_output = os.path.join(
                        temp_gdb_path,
                        "elim_r{0}_{1}".format(round_index, uuid.uuid4().hex[:8]))
                    try:
                        arcpy.management.Eliminate(
                            layer_name, round_output, eliminate_rule)
                    except Exception as elim_exc:
                        self._log_warning("子图层 [{0}] 第{1}轮消除失败：{2}".format(
                            split_name, round_index, elim_exc))
                        arcpy.management.Delete(layer_name)
                        break

                    arcpy.management.Delete(layer_name)

                    # 如果当前FC是从split直接来的，不要删除原始分割结果
                    if current_fc != split_fc:
                        arcpy.management.Delete(current_fc)
                    current_fc = round_output

                    # 安全阀：避免无限循环
                    if round_index > 50:
                        self._log_warning("子图层 [{0}] 消除超过50轮，停止。".format(split_name))
                        break

                eliminated_list.append(current_fc)
                total_processed += 1
                if total_processed % 50 == 0:
                    self._log_message("已处理 {0}/{1} 个子图层...".format(total_processed, len(split_list)))

            self._log_message("全部子图层消除完成。")

            # 第3步：合并所有消除后的图层
            self._log_message("正在合并所有子图层...")
            arcpy.management.Merge(eliminated_list, feature_class)

        finally:
            # 清理临时 GDB
            try:
                arcpy.management.Delete(temp_gdb_path)
            except Exception:
                pass
            try:
                arcpy.management.Delete(split_workspace)
            except Exception:
                pass

        # 删除临时合并字段（如果有的话，需要在合并前删除，这里已经合并完了，字段已经不在）
        # 实际上合并后字段结构来自第一个输入图层，临时字段不会带过来

        final_count = int(arcpy.management.GetCount(feature_class).getOutput(0))
        merge_count = original_count - final_count
        return merge_count

    # -----------------------------------------------------------------
    # 日志与异常
    # -----------------------------------------------------------------

    def _ensure_folder(self, folder_path):
        """确保目录存在。"""
        if not os.path.exists(folder_path):
            os.makedirs(folder_path)

    def _sanitize_feature_class_name(self, feature_name):
        """清理名称中的非法字符，避免输出要素类无法创建。"""
        sanitized_name = re.sub(r'[\\/:*?"<>|\s]+', "_", feature_name)
        sanitized_name = sanitized_name.strip("_")
        if not sanitized_name:
            sanitized_name = "合并结果"
        return sanitized_name

    def _log_message(self, message):
        """输出 ArcGIS 消息。"""
        arcpy.AddMessage(message)

    def _log_warning(self, message):
        """输出 ArcGIS 警告。"""
        arcpy.AddWarning(message)

    def _build_exception_log_path(self, output_folder, tool_label):
        """构建本次运行的异常日志文件路径（输出要素类同级目录）。"""
        timestamp_text = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        safe_tool_label = self._sanitize_feature_class_name(tool_label)
        return os.path.join(output_folder, u"{0}_异常日志_{1}.txt".format(safe_tool_label, timestamp_text))

    def _append_exception_log(self, exception_log_path, log_text):
        """将异常详情追加写入日志文件（失败不影响主流程）。"""
        if not exception_log_path:
            return
        try:
            with io.open(exception_log_path, "a", encoding="utf-8") as log_file:
                log_file.write(log_text)
                if not log_text.endswith(u"\n"):
                    log_file.write(u"\n")
        except Exception:
            return

    def _record_dissolve_exception(self, exception_log_path, feature_class, exc):
        """记录合并处理异常：警告 + 异常日志，返回记录字典。"""
        error_text = exc if isinstance(exc, UNICODE_TYPE) else UNICODE_TYPE(exc)
        traceback_text = traceback.format_exc()
        self._log_warning("####### 相邻要素按属性合并异常 #######")
        self._log_warning("当前处理失败：{0}".format(error_text))
        exception_text = (
            u"####### 相邻要素按属性合并异常 #######\n"
            u"要素类：{0}\n"
            u"错误摘要：{1}\n"
            u"详细堆栈：\n{2}\n"
        ).format(feature_class, error_text, traceback_text)
        self._append_exception_log(exception_log_path, exception_text)
        return {"feature_class": feature_class, "error_text": error_text}