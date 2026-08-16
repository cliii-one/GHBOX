# -*- coding: utf-8 -*-
# ============================================================
# 数据库合并（原始 .pyt 参考版）
# 来源：本仓库数据库合并业务的独立参考实现（从通用工具箱框架抽取，仅保留本工具所需方法）。
# 用途：GHBOX AddIn「数据库合并」按钮的业务逻辑基准。
#       C# 实现（addin/Scripts/GDB/LayerMerge.xaml.cs）与本文件保持一致。
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
        self.label = "数据库合并工具箱"
        self.alias = "ghbox_layer_merge"
        self.tools = [WorkspaceLayerMergeTool]


class WorkspaceLayerMergeTool(object):
    """将多个 GDB/MDB 中的同名图层合并到一个输出数据库。"""

    def __init__(self):
        self.label = "数据库合并"
        self.description = "将输入文件夹下多个 GDB 或 MDB 中的同名图层合并到指定输出数据库，并记录逐库条数与合并后核对日志。"
        self.canRunInBackground = False

    def getParameterInfo(self):
        input_folder_param = arcpy.Parameter(
            displayName="需要合并的文件夹",
            name="input_workspace_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )

        output_workspace_param = arcpy.Parameter(
            displayName="输出数据库",
            name="output_workspace",
            datatype="DEWorkspace",
            parameterType="Required",
            direction="Input"
        )

        layer_names_param = arcpy.Parameter(
            displayName="需要合并的图层",
            name="layer_names_text",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )

        return [input_folder_param, output_workspace_param, layer_names_param]

    def updateMessages(self, parameters):
        if parameters[0].altered and parameters[0].valueAsText:
            input_folder = parameters[0].valueAsText
            if not os.path.isdir(input_folder):
                parameters[0].setErrorMessage("输入的需要合并的文件夹不存在，请检查路径是否正确。")

        if parameters[1].altered and parameters[1].valueAsText:
            output_workspace = parameters[1].valueAsText
            if not arcpy.Exists(output_workspace):
                parameters[1].setErrorMessage("输出数据库不存在，请先创建好 GDB 或 MDB。")
            elif not self._is_supported_workspace(output_workspace):
                parameters[1].setErrorMessage("输出数据库仅支持 .gdb 或 .mdb。")

        if parameters[2].altered and parameters[2].valueAsText:
            layer_names = self._parse_layer_names(parameters[2].valueAsText)
            if not layer_names:
                parameters[2].setErrorMessage("请输入要合并的图层名称，多个图层用英文逗号分隔，或输入 ALL。")

    def execute(self, parameters, messages):
        input_folder = parameters[0].valueAsText
        output_workspace = parameters[1].valueAsText
        layer_names_text = parameters[2].valueAsText

        workspace_list = self._list_merge_workspaces(input_folder)
        if not workspace_list:
            raise arcpy.ExecuteError("在输入文件夹下未找到任何 .gdb 或 .mdb 数据库。")

        layer_names = self._parse_layer_names(layer_names_text)
        if not layer_names:
            raise arcpy.ExecuteError("未解析到需要合并的图层名称。")

        if len(layer_names) == 1 and layer_names[0] == "ALL":
            layer_names = self._collect_all_feature_class_names(workspace_list)
            if not layer_names:
                raise arcpy.ExecuteError("输入文件夹下未找到任何可合并的图层。")

        self._log_message("####### 数据库合并开始 #######")
        self._log_message("输入数据库文件夹：{0}".format(input_folder))
        self._log_message("输出数据库：{0}".format(output_workspace))
        self._log_message("共发现 {0} 个输入数据库。".format(len(workspace_list)))
        self._log_message("本次需要合并的图层：{0}".format("，".join(layer_names)))

        failed_layer_records = []
        success_layer_count = 0
        exception_log_path = self._build_exception_log_path(os.path.dirname(output_workspace), self.label)

        for layer_name in layer_names:
            try:
                self._merge_single_layer(workspace_list, output_workspace, layer_name)
                success_layer_count += 1
            except Exception as exc:
                failed_layer_records.append(
                    self._record_layer_exception(exception_log_path, layer_name, exc)
                )
                continue

        self._log_run_summary(
            success_count=success_layer_count,
            failed_count=len(failed_layer_records),
            exception_log_path=exception_log_path
        )
        self._log_message("数据库合并执行完成。")

    # -----------------------------------------------------------------
    # 工作空间与图层名解析
    # -----------------------------------------------------------------

    def _is_supported_workspace(self, workspace_path):
        """仅支持 .gdb 和 .mdb 两种数据库。"""
        lower_path = workspace_path.lower()
        return lower_path.endswith(".gdb") or lower_path.endswith(".mdb")

    def _list_merge_workspaces(self, input_folder):
        """枚举输入文件夹（仅第一层）下全部 .gdb/.mdb，按名称排序。"""
        workspace_list = []
        for entry_name in os.listdir(input_folder):
            entry_path = os.path.join(input_folder, entry_name)
            if not os.path.isdir(entry_path):
                continue
            if self._is_supported_workspace(entry_path):
                workspace_list.append(entry_path)
        return sorted(workspace_list)

    def _parse_layer_names(self, layer_names_text):
        """解析图层名：中文逗号转英文、去空项、去重；出现 ALL 直接返回 ["ALL"]。"""
        if not layer_names_text:
            return []
        normalized_text = layer_names_text.replace("，", ",")
        layer_names = []
        for item in normalized_text.split(","):
            clean_name = item.strip()
            if not clean_name:
                continue
            upper_name = clean_name.upper()
            if upper_name == "ALL":
                return ["ALL"]
            if upper_name not in [name.upper() for name in layer_names]:
                layer_names.append(clean_name)
        return layer_names

    def _collect_all_feature_class_names(self, workspace_list):
        """收集全部输入库中出现过的要素类名（ALL 模式），去重后按名称排序。"""
        collected_names = []
        seen_names = set()
        for workspace_path in workspace_list:
            for dirpath, dirnames, filenames in arcpy.da.Walk(workspace_path, datatype="FeatureClass"):
                for filename in filenames:
                    upper_name = filename.upper()
                    if upper_name in seen_names:
                        continue
                    seen_names.add(upper_name)
                    collected_names.append(filename)
        return sorted(collected_names)

    def _find_feature_class_by_name_in_workspace(self, workspace_path, target_name):
        """在单个数据库中按名称查找要素类（大小写不敏感，含要素数据集内）。"""
        for dirpath, dirnames, filenames in arcpy.da.Walk(workspace_path, datatype="FeatureClass"):
            for filename in filenames:
                if filename.upper() == target_name.upper():
                    return os.path.join(dirpath, filename), filename
        return None, None

    def _sanitize_feature_class_name(self, feature_name):
        """清理名称中的非法字符，避免输出要素类无法创建。"""
        sanitized_name = re.sub(r'[\\/:*?"<>|\s]+', "_", feature_name)
        sanitized_name = sanitized_name.strip("_")
        if not sanitized_name:
            sanitized_name = "合并结果"
        return sanitized_name

    def _build_output_feature_class_path(self, output_workspace, layer_name):
        """构建输出要素类路径：输出库名 + 清理后的图层名。"""
        sanitized_name = self._sanitize_feature_class_name(layer_name)
        return os.path.join(output_workspace, sanitized_name)

    # -----------------------------------------------------------------
    # 单图层合并（核心）
    # -----------------------------------------------------------------

    def _merge_single_layer(self, workspace_list, output_workspace, layer_name):
        """收集各库同名图层 → 删除输出库同名图层 → Merge 合并 → 条数核对。"""
        self._log_message("####### 开始合并图层：{0} #######".format(layer_name))

        input_feature_classes = []
        input_counts = []

        for workspace_path in workspace_list:
            feature_class_path, real_name = self._find_feature_class_by_name_in_workspace(workspace_path, layer_name)
            workspace_name = os.path.basename(workspace_path)
            if not feature_class_path:
                self._log_warning("数据库 {0} 中未找到图层：{1}".format(workspace_name, layer_name))
                continue

            feature_count = int(arcpy.management.GetCount(feature_class_path)[0])
            input_feature_classes.append(feature_class_path)
            input_counts.append({
                "workspace_name": workspace_name,
                "feature_class_name": real_name,
                "count": feature_count
            })
            self._log_message("数据库 {0} 中图层 {1} 条数：{2}".format(workspace_name, real_name, feature_count))

        if not input_feature_classes:
            self._log_warning("所有输入数据库中都未找到图层：{0}".format(layer_name))
            return

        output_feature_class = self._build_output_feature_class_path(output_workspace, layer_name)
        if arcpy.Exists(output_feature_class):
            self._log_warning("输出数据库中已存在同名图层，已先删除：{0}".format(output_feature_class))
            arcpy.management.Delete(output_feature_class)

        arcpy.management.Merge(input_feature_classes, output_feature_class)

        merged_count = int(arcpy.management.GetCount(output_feature_class)[0])
        expected_count = sum(item["count"] for item in input_counts)
        count_expression = " + ".join(str(item["count"]) for item in input_counts)
        self._log_message("图层 {0} 合并后输出：{1}".format(layer_name, output_feature_class))
        self._log_message("图层 {0} 条数核对：{1} = {2}".format(layer_name, count_expression, merged_count))

        if expected_count != merged_count:
            self._log_warning("图层 {0} 条数核对不一致：预期 {1}，实际 {2}。".format(layer_name, expected_count, merged_count))
        else:
            self._log_message("图层 {0} 条数核对通过。".format(layer_name))

    # -----------------------------------------------------------------
    # 日志与异常
    # -----------------------------------------------------------------

    def _log_message(self, message):
        """输出 ArcGIS 消息。"""
        arcpy.AddMessage(message)

    def _log_warning(self, message):
        """输出 ArcGIS 警告。"""
        arcpy.AddWarning(message)

    def _build_exception_log_path(self, output_folder, tool_label):
        """构建本次运行的异常日志文件路径（输出库同级目录）。"""
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

    def _record_layer_exception(self, exception_log_path, layer_name, exc):
        """记录单图层异常：警告 + 异常日志，返回记录字典。"""
        error_text = exc if isinstance(exc, UNICODE_TYPE) else UNICODE_TYPE(exc)
        traceback_text = traceback.format_exc()
        self._log_warning("####### 图层异常：{0} #######".format(layer_name))
        self._log_warning("当前图层处理失败，已跳过并继续后续图层：{0}".format(layer_name))
        self._log_warning("错误摘要：{0}".format(error_text))
        exception_text = (
            u"####### 图层异常 #######\n"
            u"图层名称：{0}\n"
            u"错误摘要：{1}\n"
            u"详细堆栈：\n{2}\n"
        ).format(layer_name, error_text, traceback_text)
        self._append_exception_log(exception_log_path, exception_text)
        return {"layer_name": layer_name, "error_text": error_text}

    def _log_run_summary(self, success_count, failed_count, exception_log_path=None):
        """输出本次批处理的成功失败汇总。"""
        self._log_message("####### 运行汇总 #######")
        self._log_message("成功图层数：{0}".format(success_count))
        self._log_message("失败图层数：{0}".format(failed_count))
        if failed_count > 0 and exception_log_path:
            self._log_warning("异常日志已保存至：{0}".format(exception_log_path))
