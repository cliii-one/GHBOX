# -*- coding: utf-8 -*-
import arcpy
import os

class Toolbox(object):
    def __init__(self):
        self.label = "批量删除图层工具箱"
        self.alias = "BatchDeleteLayers"
        self.tools = [BatchDeleteLayersTool]

class BatchDeleteLayersTool(object):
    def __init__(self):
        self.label = "批量删除图层工具"
        self.description = "遍历所有地理数据库，批量删除或保留指定图层，可选择是否删除空数据集。遇错跳过，最终报告问题数据库。"
        self.canRunInBackground = False

    def getParameterInfo(self):
        # 参数0：根文件夹
        p0 = arcpy.Parameter(
            displayName="根文件夹",
            name="root_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )
        p0.value = r"C:\Your\Root\Folder"

        # 参数1：图层名称列表（多值）
        p1 = arcpy.Parameter(
            displayName="图层名称列表",
            name="layer_names",
            datatype="GPString",
            parameterType="Required",
            direction="Input",
            multiValue=True
        )
        p1.value = "图层1;图层2"

        # 参数2：操作类型
        p2 = arcpy.Parameter(
            displayName="操作类型",
            name="operation_type",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )
        p2.filter.type = "ValueList"
        p2.filter.list = ["保留模式（删除其他所有图层）", "删除模式（仅删除所选图层）"]
        p2.value = "删除模式（仅删除所选图层）"

        # 参数3：是否删除空数据集
        p3 = arcpy.Parameter(
            displayName="删除空的数据集",
            name="delete_empty_datasets",
            datatype="GPBoolean",
            parameterType="Optional",
            direction="Input"
        )
        p3.value = False  # 默认不删除
        p3.category = "高级选项"

        return [p0, p1, p2, p3]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        pass

    def updateMessages(self, parameters):
        pass

    # -----------------------------------------------------------------
    # 辅助函数：递归删除空数据集（要素数据集、栅格数据集等）
    # -----------------------------------------------------------------
    def _delete_empty_dataset(self, dataset_path, is_retain_mode, root_gdb):
        """
        检查 dataset_path 是否为空数据集（即该数据集下没有任何用户数据）。
        若为空则删除，并递归检查其父级数据集。
        dataset_path: 数据集的完整路径
        is_retain_mode: 当前是否为保留模式（仅用于日志输出风格）
        root_gdb: 当前数据库路径，用于判断是否仍在数据库内
        """
        # 防止删除数据库本身或系统表
        if not dataset_path.lower().startswith(root_gdb.lower()):
            return
        if not arcpy.Exists(dataset_path):
            return

        # 获取数据集的类型
        try:
            desc = arcpy.Describe(dataset_path)
            ds_type = desc.datasetType  # 可能是 "FeatureDataset", "RasterDataset", "MosaicDataset" 等
        except:
            return

        # 只处理容器类型：要素数据集、栅格数据集、镶嵌数据集
        container_types = ["FeatureDataset", "RasterDataset", "MosaicDataset"]
        if ds_type not in container_types:
            return

        # 检查该数据集下是否包含任何用户数据（图层、表、子数据集）
        children = []
        try:
            # 使用 arcpy.List 或 da.Walk 列出所有子元素
            # 注意：对于要素数据集，用 arcpy.ListFeatureClasses(feature_dataset=dataset_path)
            if ds_type == "FeatureDataset":
                children = arcpy.ListFeatureClasses(feature_dataset=dataset_path)
            elif ds_type == "RasterDataset":
                # 栅格数据集下通常没有子数据，但可能有子栅格（如波段），忽略
                children = []
            elif ds_type == "MosaicDataset":
                # 镶嵌数据集下无直接子图层，但可能有新增的栅格，但一般我们视为叶子节点
                children = []
            # 也可以统一用 arcpy.da.Walk 指定该路径
            if not children and ds_type == "FeatureDataset":
                # 再检查是否有表、栅格等（要素数据集下可能还有独立表？极少，但安全起见）
                walk = arcpy.da.Walk(dataset_path, datatype=["Table", "RasterDataset"])
                for dirpath, dirnames, datanames in walk:
                    children.extend(datanames)
        except:
            pass

        # 如果没有子数据，则删除该空数据集
        if len(children) == 0:
            try:
                arcpy.AddMessage(f"  发现空数据集，正在删除：{os.path.basename(dataset_path)}")
                arcpy.Delete_management(dataset_path)
                # 递归处理父级数据集
                parent_path = os.path.dirname(dataset_path)
                self._delete_empty_dataset(parent_path, is_retain_mode, root_gdb)
            except Exception as e:
                arcpy.AddWarning(f"  删除空数据集失败：{dataset_path}，原因：{str(e)}")
        return

    # -----------------------------------------------------------------
    # 主执行函数
    # -----------------------------------------------------------------
    def execute(self, parameters, messages):
        # 获取参数
        root_folder = parameters[0].valueAsText
        layer_names_multi = parameters[1].values
        operation = parameters[2].valueAsText
        delete_empty_ds = parameters[3].value if len(parameters) > 3 else False

        # 验证输入
        if not root_folder or not os.path.exists(root_folder):
            arcpy.AddError("错误：根文件夹不存在。")
            return
        if not layer_names_multi:
            arcpy.AddError("错误：未提供任何图层名称。")
            return

        raw_names = [n.strip() for n in layer_names_multi if n.strip()]
        if not raw_names:
            arcpy.AddError("错误：图层名称列表为空。")
            return
        layer_names_lower = [n.lower() for n in raw_names]

        is_retain_mode = "保留" in operation

        arcpy.AddMessage("=" * 60)
        arcpy.AddMessage(f"操作模式：{'保留模式（删除指定图层以外的所有图层）' if is_retain_mode else '删除模式（仅删除指定图层）'}")
        arcpy.AddMessage(f"指定图层：{', '.join(raw_names)}")
        arcpy.AddMessage(f"删除空数据集：{'是' if delete_empty_ds else '否'}")
        arcpy.AddMessage("=" * 60)

        # 查找所有 .gdb 和 .mdb
        gdb_list = []
        for root, dirs, files in os.walk(root_folder):
            for d in dirs:
                lower_d = d.lower()
                if lower_d.endswith('.gdb') or lower_d.endswith('.mdb'):
                    gdb_list.append(os.path.join(root, d))

        if not gdb_list:
            arcpy.AddWarning("未找到任何 .gdb 或 .mdb 数据库。")
            return

        arcpy.AddMessage(f"共发现 {len(gdb_list)} 个数据库，开始处理...\n")

        total_datasets = 0
        total_deleted = 0
        total_kept = 0
        problem_dbs = []  # (路径, 错误信息)

        # 遍历每个数据库
        for idx, gdb_path in enumerate(gdb_list, 1):
            arcpy.AddMessage(f"\n>>> [{idx}/{len(gdb_list)}] 正在处理数据库：{gdb_path}")

            datasets = []  # (完整路径, 数据名称)
            try:
                walk = arcpy.da.Walk(gdb_path,
                                     datatype=["FeatureClass", "Table", "RasterDataset"],
                                     topdown=False)  # 自底向上，便于后续删除数据集
                for dirpath, dirnames, datanames in walk:
                    for name in datanames:
                        if name.upper().startswith("GDB_"):
                            continue
                        datasets.append((os.path.join(dirpath, name), name))
            except Exception as e:
                err_msg = f"遍历数据库失败：{str(e)}"
                arcpy.AddError(f"  错误：{err_msg}")
                problem_dbs.append((gdb_path, err_msg))
                continue

            gdb_total = len(datasets)
            total_datasets += gdb_total
            if gdb_total == 0:
                arcpy.AddMessage("  该数据库无用户数据，跳过。")
                continue

            # 确定需要删除的图层
            to_delete = []
            for full_path, data_name in datasets:
                if is_retain_mode:
                    if data_name.lower() not in layer_names_lower:
                        to_delete.append((full_path, data_name))
                else:
                    if data_name.lower() in layer_names_lower:
                        to_delete.append((full_path, data_name))

            deleted = 0
            for full_path, data_name in to_delete:
                try:
                    arcpy.AddMessage(f"  正在删除：{os.path.basename(gdb_path)} 库 -> 图层：{data_name}")
                    arcpy.Delete_management(full_path)
                    deleted += 1

                    # 如果启用删除空数据集，则尝试删除父级空数据集
                    if delete_empty_ds:
                        parent_dir = os.path.dirname(full_path)
                        # 检查父目录是否是一个数据集（需要判断是否为要素数据集容器）
                        # 简单判断：如果父路径与 gdb_path 不同，且父路径不是数据库根目录，则尝试清理
                        if parent_dir.lower() != gdb_path.lower():
                            self._delete_empty_dataset(parent_dir, is_retain_mode, gdb_path)
                except Exception as e:
                    arcpy.AddWarning(f"  删除失败：{data_name}，原因：{str(e)}")

            kept = gdb_total - deleted
            total_deleted += deleted
            total_kept += kept
            arcpy.AddMessage(f"  数据库汇总：总共 {gdb_total} 图层，已删除 {deleted} 图层，保留 {kept} 图层")

        # 最终统计与问题报告
        arcpy.AddMessage("\n" + "=" * 60)
        arcpy.AddMessage("【操作完成】最终统计")
        arcpy.AddMessage(f"  成功处理的数据库数量：{len(gdb_list) - len(problem_dbs)} / {len(gdb_list)}")
        arcpy.AddMessage(f"  所有数据库图层总数（成功处理库）：{total_datasets}")
        arcpy.AddMessage(f"  成功删除图层数：{total_deleted}")
        arcpy.AddMessage(f"  保留图层数：{total_kept}")
        if total_deleted + total_kept != total_datasets:
            arcpy.AddWarning("  注意：删除数+保留数 ≠ 总数，请检查上方的删除失败警告。")

        if problem_dbs:
            arcpy.AddWarning("\n【存在问题数据库列表】以下数据库因致命错误未处理：")
            for db_path, err_info in problem_dbs:
                arcpy.AddWarning(f"  - {db_path}\n    错误：{err_info}")
        else:
            arcpy.AddMessage("\n所有数据库均成功处理，未发现致命错误。")

        arcpy.AddMessage("=" * 60)
        return