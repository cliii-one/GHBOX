# -*- coding: utf-8 -*-
# ============================================================
# 按属性拆库工具（原始 .pyt 参考版）
# 来源：从总规动态维护工具.pyt 中抽取按属性批量导出图层工具，仅保留本工具所需方法。
# 用途：GHBOX AddIn「按属性拆库工具」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/AttributeSplit.xaml.cs）与本文件保持一致。
# 说明：ArcGIS Pro AddIn 版不需要本文件即可运行；本文件用于
#       1) 保留原始业务逻辑供对照与回归
#       2) 在没有安装 AddIn 的环境下直接用 Python 工具箱执行
# ============================================================

import datetime
import io
import os
import re
import traceback

import arcpy


try:
    UNICODE_TYPE = type(u"")
except Exception:
    UNICODE_TYPE = str


class Toolbox(object):
    """ArcGIS Pro Python 工具箱入口类。"""

    def __init__(self):
        self.label = "按属性拆库工具箱"
        self.alias = "ghbox_attribute_split"
        self.tools = [WorkspaceAttributeSplitTool]


class WorkspaceAttributeSplitTool(object):
    """按属性条件拆库多个数据库中的指定图层结果。"""

    def __init__(self):
        self.label = "按属性拆库工具"
        self.description = "从输入文件夹下多个 GDB 或 MDB 中查找指定图层，按属性筛选后分别拆库。"   
        self.canRunInBackground = False

    def getParameterInfo(self):
        input_folder_param = arcpy.Parameter(
            displayName="输入文件夹",
            name="input_workspace_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )

        layer_name_param = arcpy.Parameter(
            displayName="图层名称",
            name="layer_name",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )

        where_clause_param = arcpy.Parameter(
            displayName="筛选条件",
            name="where_clause",
            datatype="GPString",
            parameterType="Optional",
            direction="Input"
        )

        output_folder_param = arcpy.Parameter(
            displayName="输出文件夹",
            name="output_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )

        export_to_gdb_param = arcpy.Parameter(
            displayName="导出为GDB",
            name="export_to_gdb",
            datatype="GPBoolean",
            parameterType="Required",
            direction="Input"
        )
        export_to_gdb_param.value = False

        return [input_folder_param, layer_name_param, where_clause_param, output_folder_param, export_to_gdb_param]

    def updateMessages(self, parameters):
        if parameters[0].altered and parameters[0].valueAsText:
            input_folder = parameters[0].valueAsText
            if not os.path.isdir(input_folder):
                parameters[0].setErrorMessage("输入文件夹不存在，请检查路径是否正确。")

        if parameters[1].altered and parameters[1].valueAsText:
            if not parameters[1].valueAsText.strip():
                parameters[1].setErrorMessage("请输入图层名称。")

    def execute(self, parameters, messages):
        input_folder = parameters[0].valueAsText
        layer_name = parameters[1].valueAsText.strip()
        where_clause_text = parameters[2].valueAsText
        where_clause = where_clause_text.strip() if where_clause_text else u""
        output_folder = parameters[3].valueAsText
        export_to_gdb = self._parse_boolean_parameter(parameters[4])
        export_mode_text = u"GDB" if export_to_gdb else u"SHP"
        filter_mode_text = u"整层导出" if not where_clause else u"按条件筛选导出"

        self._ensure_folder(output_folder)
        workspace_list = self._list_input_workspaces(input_folder)
        if not workspace_list:
            raise arcpy.ExecuteError("在输入文件夹下未找到任何 .gdb 或 .mdb 数据库。")

        self._log_message("####### 按属性拆库工具开始 #######")
        self._log_message("输入文件夹：{0}".format(input_folder))
        self._log_message("筛选方式：{0}".format(filter_mode_text))
        if where_clause:
            self._log_message("筛选条件：{0}".format(where_clause))
        else:
            self._log_message("筛选条件为空，当前按整层拆库处理。")
        self._log_message("共发现 {0} 个输入数据库。".format(len(workspace_list)))

        success_count = 0
        failed_workspace_records = []
        found_layer_count = 0
        exception_log_path = self._build_exception_log_path(output_folder, self.label)

        for workspace_path in workspace_list:
            workspace_name = os.path.basename(workspace_path)
            self._log_message("####### 开始处理数据库：{0} #######".format(workspace_name))
            try:
                export_result = self._export_single_workspace_by_attribute(
                    workspace_path=workspace_path,
                    layer_name=layer_name,
                    where_clause=where_clause,
                    output_folder=output_folder,
                    export_to_gdb=export_to_gdb
                )
                if export_result["layer_found"]:
                    found_layer_count += 1
                    success_count += 1
            except Exception as exc:
                failed_workspace_records.append(
                    self._record_workspace_exception(
                        exception_log_path=exception_log_path,
                        workspace_path=workspace_path,
                        exc=exc
                    )
                )
                continue

        if found_layer_count == 0:
            self._log_warning("所有输入数据库中都未找到图层：{0}".format(layer_name))

        self._log_run_summary(
            success_count=success_count,
            failed_count=len(failed_workspace_records),
            exception_log_path=exception_log_path
        )
        self._log_message("按属性拆库工具执行完成。")

    # -----------------------------------------------------------------
    # 参数与工作空间解析
    # -----------------------------------------------------------------

    def _parse_boolean_parameter(self, parameter):
        """解析布尔参数：优先取值本身，否则按文本判断。"""
        raw_value = getattr(parameter, "value", None)
        if isinstance(raw_value, bool):
            return raw_value

        raw_text = parameter.valueAsText
        if raw_text is None:
            return False

        normalized_text = UNICODE_TYPE(raw_text).strip().lower()
        return normalized_text in ["true", "1", "yes", "y"]

    def _list_input_workspaces(self, input_folder):
        """枚举输入文件夹（仅第一层）下全部 .gdb/.mdb，按名称排序。"""
        workspace_list = []
        for entry_name in os.listdir(input_folder):
            entry_path = os.path.join(input_folder, entry_name)
            if not os.path.isdir(entry_path):
                continue
            if self._is_supported_workspace(entry_path):
                workspace_list.append(entry_path)
        return sorted(workspace_list)

    def _is_supported_workspace(self, workspace_path):
        """仅支持 .gdb 和 .mdb 两种数据库。"""
        lower_path = workspace_path.lower()
        return lower_path.endswith(".gdb") or lower_path.endswith(".mdb")

    def _find_feature_class_by_name_in_workspace(self, workspace_path, target_name):
        """在单个数据库中按名称查找要素类（大小写不敏感，含要素数据集内）。"""
        for dirpath, dirnames, filenames in arcpy.da.Walk(workspace_path, datatype="FeatureClass"):
            for filename in filenames:
                if filename.upper() == target_name.upper():
                    return os.path.join(dirpath, filename), filename
        return None, None

    # -----------------------------------------------------------------
    # 输出路径构建
    # -----------------------------------------------------------------

    def _build_workspace_output_shp_path(self, output_folder, workspace_path):
        """构建 SHP 输出路径：按库名生成 {库名}.shp。"""
        workspace_name = os.path.splitext(os.path.basename(workspace_path))[0]
        sanitized_name = self._sanitize_feature_class_name(workspace_name)
        return os.path.join(output_folder, "{0}.shp".format(sanitized_name))

    def _build_workspace_output_gdb_path(self, output_folder, workspace_path):
        """构建 GDB 输出路径：库名是 .gdb 则同名，否则加 .gdb 后缀。"""
        workspace_name = os.path.basename(workspace_path)
        if workspace_name.lower().endswith(".gdb"):
            return os.path.join(output_folder, workspace_name)

        workspace_base_name = os.path.splitext(workspace_name)[0]
        return os.path.join(output_folder, u"{0}.gdb".format(workspace_base_name))

    def _ensure_output_file_gdb(self, output_gdb_path):
        """输出 GDB 不存在时先创建。"""
        if arcpy.Exists(output_gdb_path):
            return

        output_parent_folder = os.path.dirname(output_gdb_path)
        output_gdb_name = os.path.splitext(os.path.basename(output_gdb_path))[0]
        arcpy.management.CreateFileGDB(output_parent_folder, output_gdb_name)

    def _build_workspace_output_feature_class_path(self, output_gdb_path, layer_name):
        """构建输出要素类路径：{输出GDB}/{图层名}。"""
        return os.path.join(output_gdb_path, layer_name)

    # -----------------------------------------------------------------
    # 单库导出（核心）
    # -----------------------------------------------------------------

    def _export_single_workspace_by_attribute(self, workspace_path, layer_name, where_clause, output_folder, export_to_gdb):
        """单个数据库：查找图层 → 按条件或整层导出到 SHP/GDB，并记录条数。"""
        feature_class_path, real_name = self._find_feature_class_by_name_in_workspace(workspace_path, layer_name)
        workspace_name = os.path.basename(workspace_path)
        if not feature_class_path:
            self._log_warning("数据库 {0} 中未找到图层：{1}".format(workspace_name, layer_name))
            return {"layer_found": False}

        source_count = int(arcpy.management.GetCount(feature_class_path)[0])
        self._log_message("数据库 {0} 中图层 {1} 原始条数：{2}".format(workspace_name, real_name, source_count))
        if where_clause:
            self._log_message("数据库 {0} 开始按条件导出，当前模式：{1}".format(workspace_name, u"GDB" if export_to_gdb else u"SHP"))
        else:
            self._log_message("数据库 {0} 筛选条件为空，当前按整层导出，导出模式：{1}".format(workspace_name, u"GDB" if export_to_gdb else u"SHP"))

        if export_to_gdb:
            output_gdb_path = self._build_workspace_output_gdb_path(output_folder, workspace_path)
            self._ensure_output_file_gdb(output_gdb_path)
            output_feature_class_path = self._build_workspace_output_feature_class_path(output_gdb_path, real_name)
            if arcpy.Exists(output_feature_class_path):
                self._log_warning("输出 GDB 中已存在同名图层，已先删除：{0}".format(output_feature_class_path))
                arcpy.management.Delete(output_feature_class_path)

            self._log_message("数据库 {0} 输出 GDB：{1}".format(workspace_name, output_gdb_path))
            if where_clause:
                arcpy.analysis.Select(feature_class_path, output_feature_class_path, where_clause)
            else:
                arcpy.management.CopyFeatures(feature_class_path, output_feature_class_path)

            export_count = int(arcpy.management.GetCount(output_feature_class_path)[0])
            if where_clause:
                self._log_message("数据库 {0} 筛选后条数：{1}".format(workspace_name, export_count))
            else:
                self._log_message("数据库 {0} 导出条数：{1}".format(workspace_name, export_count))
            self._log_message("数据库 {0} 导出图层：{1}".format(workspace_name, output_feature_class_path))
            if export_count == 0:
                if where_clause:
                    self._log_warning("数据库 {0} 筛选结果为空，已导出空要素类。".format(workspace_name))
                else:
                    self._log_warning("数据库 {0} 当前图层为空，已导出空要素类。".format(workspace_name))

            return {
                "layer_found": True,
                "source_count": source_count,
                "export_count": export_count,
                "output_path": output_feature_class_path
            }

        output_shp_path = self._build_workspace_output_shp_path(output_folder, workspace_path)
        if arcpy.Exists(output_shp_path):
            self._log_warning("输出文件夹中已存在同名 shp，已先删除：{0}".format(output_shp_path))
            arcpy.management.Delete(output_shp_path)

        if where_clause:
            arcpy.analysis.Select(feature_class_path, output_shp_path, where_clause)
        else:
            arcpy.management.CopyFeatures(feature_class_path, output_shp_path)

        export_count = int(arcpy.management.GetCount(output_shp_path)[0])
        if where_clause:
            self._log_message("数据库 {0} 筛选后条数：{1}".format(workspace_name, export_count))
        else:
            self._log_message("数据库 {0} 导出条数：{1}".format(workspace_name, export_count))
        self._log_message("数据库 {0} 导出结果：{1}".format(workspace_name, output_shp_path))
        if export_count == 0:
            if where_clause:
                self._log_warning("数据库 {0} 筛选结果为空，已导出空 SHP。".format(workspace_name))
            else:
                self._log_warning("数据库 {0} 当前图层为空，已导出空 SHP。".format(workspace_name))

        return {
            "layer_found": True,
            "source_count": source_count,
            "export_count": export_count,
            "output_path": output_shp_path
        }

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
            sanitized_name = "导出结果"
        return sanitized_name

    def _log_message(self, message):
        """输出 ArcGIS 消息。"""
        arcpy.AddMessage(message)

    def _log_warning(self, message):
        """输出 ArcGIS 警告。"""
        arcpy.AddWarning(message)

    def _build_exception_log_path(self, output_folder, tool_label):
        """构建本次运行的异常日志文件路径（输出文件夹内）。"""
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

    def _record_workspace_exception(self, exception_log_path, workspace_path, exc):
        """记录单库异常：警告 + 异常日志，返回记录字典。"""
        workspace_name = os.path.basename(workspace_path)
        error_text = exc if isinstance(exc, UNICODE_TYPE) else UNICODE_TYPE(exc)
        traceback_text = traceback.format_exc()
        self._log_warning("####### 数据库异常：{0} #######".format(workspace_name))
        self._log_warning("当前数据库处理失败，已跳过并继续后续数据库：{0}".format(workspace_name))
        self._log_warning("错误摘要：{0}".format(error_text))
        exception_text = (
            u"####### 数据库异常 #######\n"
            u"数据库：{0}\n"
            u"错误摘要：{1}\n"
            u"详细堆栈：\n{2}\n"
        ).format(workspace_path, error_text, traceback_text)
        self._append_exception_log(exception_log_path, exception_text)
        return {"workspace_path": workspace_path, "error_text": error_text}

    def _log_run_summary(self, success_count, failed_count, exception_log_path=None):
        """输出本次批处理的成功失败汇总。"""
        self._log_message("####### 运行汇总 #######")
        self._log_message("成功数据库数：{0}".format(success_count))
        self._log_message("失败数据库数：{0}".format(failed_count))
        if failed_count > 0 and exception_log_path:
            self._log_warning("异常日志已保存至：{0}".format(exception_log_path))