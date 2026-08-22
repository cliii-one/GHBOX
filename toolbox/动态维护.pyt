# -*- coding: utf-8 -*-
import arcpy
import os
import datetime

class Toolbox(object):
    def __init__(self):
        self.label = "国土空间规划"
        self.alias = "DynamicMaintenance"
        self.tools = [DynamicMaintenanceTool]

class DynamicMaintenanceTool(object):
    def __init__(self):
        self.label = "动态维护"
        self.description = "根据备案数据库A和维护后全量数据库B，生成符合汇交要求的动态维护数据库C"
        self.canRunInBackground = False

    FIELD_ALIAS_MAP = {
        "BSM": "标识码",
        "YSDM": "要素代码",
        "XZQDM": "行政区代码",
        "XZQMC": "行政区名称",
        "WHLX": "维护类型",
        "WHLY": "维护理由",
        "JDZBLY": "机动指标来源",
        "WHBH": "维护编号",
        "KZBSHSLX": "扩展倍数核算类型",
        "BZ": "备注"
    }

    def getParameterInfo(self):
        param0 = arcpy.Parameter(
            displayName="备案数据库A（部备案版本）",
            name="db_a",
            datatype="DEWorkspace",
            parameterType="Required",
            direction="Input"
        )
        param0.filter.list = ["Local Database", "File Geodatabase"]

        param1 = arcpy.Parameter(
            displayName="维护后全量数据库B",
            name="db_b",
            datatype="DEWorkspace",
            parameterType="Required",
            direction="Input"
        )
        param1.filter.list = ["Local Database", "File Geodatabase"]

        param2 = arcpy.Parameter(
            displayName="动态维护数据库C存放目录",
            name="out_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input"
        )

        layer_choices = [
            "规划分区", "用地用海规划分区", "中心城区规划分区", "中心城区规划用地用海",
            "乡级主体功能定位", "中心城区城市蓝线", "中心城区城市绿线", "中心城区城市紫线",
            "中心城区城市黄线", "洪涝风险控制线", "历史文化保护线"
        ]
        param3 = arcpy.Parameter(
            displayName="选择需维护图层（可多选）",
            name="layers",
            datatype="GPString",
            parameterType="Required",
            direction="Input",
            multiValue=True
        )
        param3.filter.type = "ValueList"
        param3.filter.list = layer_choices

        param4 = arcpy.Parameter(
            displayName="行政区代码",
            name="xzqdm",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )
        param5 = arcpy.Parameter(
            displayName="行政区名称",
            name="xzqmc",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )
        param6 = arcpy.Parameter(
            displayName="维护年度",
            name="year",
            datatype="GPString",
            parameterType="Required",
            direction="Input"
        )

        return [param0, param1, param2, param3, param4, param5, param6]

    def isLicensed(self):
        return True

    def updateParameters(self, parameters):
        pass

    def updateMessages(self, parameters):
        pass

    def execute(self, parameters, messages):
        db_a = parameters[0].valueAsText
        db_b = parameters[1].valueAsText
        out_folder = parameters[2].valueAsText
        selected_layers = parameters[3].values
        xzqdm = parameters[4].valueAsText
        xzqmc = parameters[5].valueAsText
        year = parameters[6].valueAsText

        if not arcpy.Exists(db_a):
            raise arcpy.ExecuteError("数据库A不存在: {}".format(db_a))
        if not arcpy.Exists(db_b):
            raise arcpy.ExecuteError("数据库B不存在: {}".format(db_b))

        c_name = "{}{}{}年度县级国土空间总体规划动态维护.gdb".format(xzqdm, xzqmc, year)
        c_path = os.path.join(out_folder, c_name)
        if arcpy.Exists(c_path):
            arcpy.Delete_management(c_path)
        arcpy.CreateFileGDB_management(out_folder, c_name)
        messages.addMessage("创建动态维护数据库C: {}".format(c_path))

        target_sr = arcpy.SpatialReference(4490, 5737)
        arcpy.env.outputCoordinateSystem = target_sr
        arcpy.env.overwriteOutput = True
        arcpy.env.workspace = c_path

        layer_config = {
            "规划分区": {
                "layer_name": "GHFQ",
                "type": 2,
                "compare_field": "GHFQDM",
                "area_fields": ["MJ"],
                "ysdm": "2090020610",
                "skip": False
            },
            "用地用海规划分区": {
                "layer_name": "YDYHGHFQ",
                "type": 2,
                "compare_field": "GHFQDM",
                "area_fields": ["MJ"],
                "ysdm": "2090020610",
                "skip": False
            },
            "中心城区规划分区": {
                "layer_name": "ZXCQGHFQ",
                "type": 2,
                "compare_field": "GHFQDM",
                "area_fields": ["MJ"],
                "ysdm": "2090020610",
                "skip": False
            },
            "中心城区规划用地用海": {
                "layer_name": "ZXCQGHYDYH",
                "type": 2,
                "compare_field": "YDYHFLDM",
                "area_fields": ["TBMJ", "TBDLMJ"],
                "ysdm": "2090020620",
                "skip": False
            },
            "乡级主体功能定位": {
                "layer_name": "XZZTGNDW",
                "type": 2,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020130",
                "skip": True
            },
            "中心城区城市蓝线": {
                "layer_name": "ZXCQCSLX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020233",
                "skip": False
            },
            "中心城区城市绿线": {
                "layer_name": "ZXCQCSLVX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020232",
                "skip": False
            },
            "中心城区城市紫线": {
                "layer_name": "ZXCQCSZX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020233",
                "skip": False
            },
            "中心城区城市黄线": {
                "layer_name": "ZXCQCSHX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020234",
                "skip": False
            },
            "洪涝风险控制线": {
                "layer_name": "HLFXKZX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020229",
                "skip": False
            },
            "历史文化保护线": {
                "layer_name": "LSWHBHX",
                "type": 13,
                "compare_field": None,
                "area_fields": ["MJ"],
                "ysdm": "2090020227",
                "skip": False
            }
        }

        for alias in selected_layers:
            if alias not in layer_config:
                messages.addWarningMessage("未知图层: {}，将跳过".format(alias))
                continue
            config = layer_config[alias]
            if config["skip"]:
                continue
            layer_name = config["layer_name"]
            fc_a = os.path.join(db_a, layer_name)
            fc_b = os.path.join(db_b, layer_name)
            if not arcpy.Exists(fc_a):
                raise arcpy.ExecuteError("数据库A中缺少图层: {}".format(layer_name))
            if not arcpy.Exists(fc_b):
                raise arcpy.ExecuteError("数据库B中缺少图层: {}".format(layer_name))

        now = datetime.datetime.now()
        date_prefix = now.strftime("%Y%m%d")

        for alias in selected_layers:
            config = layer_config.get(alias)
            if not config or config["skip"]:
                continue
            layer_name = config["layer_name"]
            fc_a = os.path.join(db_a, layer_name)
            fc_b = os.path.join(db_b, layer_name)
            messages.addMessage("开始处理图层: {} ({})".format(alias, layer_name))

            cnt_a = int(arcpy.GetCount_management(fc_a)[0])
            cnt_b = int(arcpy.GetCount_management(fc_b)[0])
            messages.addMessage("  A记录数: {}, B记录数: {}".format(cnt_a, cnt_b))
            if cnt_a == 0 or cnt_b == 0:
                messages.addWarningMessage("  图层为空，跳过处理")
                continue

            if config["type"] == 2:
                self.process_type2(
                    alias, layer_name, fc_a, fc_b, c_path, xzqdm, xzqmc, date_prefix, config, target_sr, messages
                )
            elif config["type"] == 13:
                self.process_type13(
                    alias, layer_name, fc_a, fc_b, c_path, xzqdm, xzqmc, date_prefix, config, target_sr, messages
                )

        messages.addMessage("所有图层处理完成！")

    # ==================================================================
    # 处理类型 2（属性变更）
    # ==================================================================
    def process_type2(self, alias, layer_name, fc_a, fc_b, c_path, xzqdm, xzqmc, date_prefix, config, target_sr, messages):
        seq_counter = 1  # 每个图层独立从1开始
        compare_field = config["compare_field"]
        area_fields = config["area_fields"]
        ysdm_fixed = config["ysdm"]
        prefix = layer_name
        q_name = "WHQ{}".format(prefix)
        c_name_lyr = "WHC{}".format(prefix)
        h_name = "WHH{}".format(prefix)

        # ---- 1. 成对相交 A∩B，找出变化的 A 图斑 ----
        messages.addMessage("  执行成对相交 A∩B")
        intersect_ab = "in_memory/int_ab"
        arcpy.analysis.PairwiseIntersect([fc_a, fc_b], intersect_ab, "ALL", "", "INPUT")

        field_map = self._get_field_map(intersect_ab, fc_a, fc_b, compare_field)
        a_bsm = field_map["a_bsm"]
        b_bsm = field_map["b_bsm"]
        a_cmp = field_map["a_cmp"]
        b_cmp = field_map["b_cmp"]
        messages.addMessage("    识别字段: A_BSM='{}', B_BSM='{}', A_CMP='{}', B_CMP='{}'".format(a_bsm, b_bsm, a_cmp, b_cmp))

        cnt_int = int(arcpy.GetCount_management(intersect_ab)[0])
        messages.addMessage("    成对相交结果记录数: {}".format(cnt_int))

        a_bsm_set = set()
        with arcpy.da.SearchCursor(intersect_ab, [a_bsm, a_cmp, b_cmp]) as cursor:
            for row in cursor:
                if row[1] != row[2]:
                    a_bsm_set.add(row[0])
        messages.addMessage("    属性变化的 A 图斑数: {}".format(len(a_bsm_set)))
        arcpy.Delete_management(intersect_ab)

        # ---- 2. 维护前图层 WHQ ----
        messages.addMessage("  制作维护前图层: {}".format(q_name))
        q_fc = os.path.join(c_path, q_name)
        if a_bsm_set:
            where = "{} IN ({})".format("BSM", ",".join(["'{}'".format(b) for b in a_bsm_set]))
            arcpy.MakeFeatureLayer_management(fc_a, "a_lyr")
            arcpy.SelectLayerByAttribute_management("a_lyr", "NEW_SELECTION", where)
            arcpy.conversion.FeatureClassToFeatureClass("a_lyr", c_path, q_name)
            arcpy.Delete_management("a_lyr")
            self._define_sr(q_fc, target_sr)
        else:
            arcpy.CreateFeatureclass_management(c_path, q_name, "POLYGON", fc_a, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)
        cnt_q = int(arcpy.GetCount_management(q_fc)[0])
        messages.addMessage("    维护前图层记录数: {}".format(cnt_q))

        # ---- 3. 维护后图层 WHH = B ∩ WHQ ----
        messages.addMessage("  制作维护后图层: {}".format(h_name))
        intersect_b_q = "in_memory/int_b_q"
        if cnt_q > 0:
            arcpy.analysis.PairwiseIntersect([fc_b, q_fc], intersect_b_q, "ALL", "", "INPUT")
        else:
            arcpy.CreateFeatureclass_management("in_memory", "int_b_q", "POLYGON", fc_b, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)
            intersect_b_q = "in_memory/int_b_q"

        h_fc = os.path.join(c_path, h_name)
        arcpy.conversion.FeatureClassToFeatureClass(intersect_b_q, c_path, h_name)
        arcpy.management.MultipartToSinglepart(h_fc, h_fc + "_single")
        arcpy.Delete_management(h_fc)
        arcpy.Rename_management(h_fc + "_single", h_name)
        h_fc = os.path.join(c_path, h_name)
        self._define_sr(h_fc, target_sr)
        arcpy.Delete_management(intersect_b_q)

        self._ensure_fields_exist(h_fc, "BSM", "TEXT", field_length=18, field_alias="标识码")
        for afield in area_fields:
            self._ensure_fields_exist(h_fc, afield, "DOUBLE", field_alias=afield)

        max_bsm = self._get_max_bsm(fc_a)
        start_seq_h = int(max_bsm[-8:]) + 1 if max_bsm and max_bsm[-8:].isdigit() else 1
        update_fields = ["BSM", "SHAPE@"] + area_fields
        with arcpy.da.UpdateCursor(h_fc, update_fields) as cursor:
            for row in cursor:
                row[0] = self._generate_bsm(xzqdm, start_seq_h)
                start_seq_h += 1
                geom = row[1]
                if geom:
                    area = round(geom.area, 2)
                    for i in range(2, len(row)):
                        row[i] = area
                cursor.updateRow(row)
        cnt_h = int(arcpy.GetCount_management(h_fc)[0])
        messages.addMessage("    维护后图层记录数: {}".format(cnt_h))

        # ---- 4. 维护层 WHC = WHQ ∩ WHH，筛选属性变化的记录 ----
        messages.addMessage("  制作维护层图层: {}".format(c_name_lyr))
        intersect_qh = "in_memory/int_qh"
        if cnt_q > 0 and cnt_h > 0:
            arcpy.analysis.PairwiseIntersect([q_fc, h_fc], intersect_qh, "ALL", "", "INPUT")
        else:
            arcpy.CreateFeatureclass_management("in_memory", "int_qh", "POLYGON", fc_a, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)
            intersect_qh = "in_memory/int_qh"

        # 创建空维护层
        c_fc = os.path.join(c_path, c_name_lyr)
        arcpy.CreateFeatureclass_management(c_path, c_name_lyr, "POLYGON", spatial_reference=target_sr)

        field_defs = [
            ("BSM", "TEXT", 18, "标识码"),
            ("YSDM", "TEXT", 10, "要素代码"),
            ("XZQDM", "TEXT", 12, "行政区代码"),
            ("XZQMC", "TEXT", 100, "行政区名称"),
            ("WHLX", "TEXT", 2, "维护类型"),
            ("WHLY", "TEXT", 3, "维护理由"),
            ("JDZBLY", "TEXT", 10, "机动指标来源"),
            ("WHBH", "TEXT", 18, "维护编号"),
            ("KZBSHSLX", "TEXT", 3, "扩展倍数核算类型"),
            ("BZ", "TEXT", 255, "备注")
        ]
        for fname, ftype, flen, falias in field_defs:
            arcpy.AddField_management(c_fc, fname, ftype, field_length=flen, field_alias=falias)

        # 获取字段映射，用于筛选
        qh_fields = [f.name for f in arcpy.ListFields(intersect_qh)]
        # q_fc 的字段为原名，h_fc 的字段带后缀 "_1"
        q_cmp_field = compare_field
        h_cmp_field = self._find_field_with_suffix(qh_fields, compare_field, suffix="_1")
        if h_cmp_field is None:
            h_cmp_field = compare_field  # 如果找不到，就用原名（但理论上应有）

        # 插入符合条件的记录
        target_fields = ["SHAPE@", "YSDM", "XZQDM", "XZQMC", "WHLX", "WHBH", "BSM", "WHLY", "JDZBLY", "KZBSHSLX", "BZ"]
        start_seq_c = 1
        with arcpy.da.InsertCursor(c_fc, target_fields) as ins_cursor:
            with arcpy.da.SearchCursor(intersect_qh, ["SHAPE@", q_cmp_field, h_cmp_field]) as src_cursor:
                for src_row in src_cursor:
                    if src_row[1] != src_row[2]:
                        geom = src_row[0]
                        if geom is None:
                            continue
                        new_bsm = self._generate_bsm(xzqdm, start_seq_c)
                        start_seq_c += 1
                        whbh = self._generate_whbh(date_prefix, seq_counter)
                        seq_counter += 1
                        ins_row = [
                            geom,
                            ysdm_fixed,
                            xzqdm,
                            xzqmc,
                            "2",
                            whbh,
                            new_bsm,
                            "", "", "", ""
                        ]
                        ins_cursor.insertRow(ins_row)

        cnt_c = int(arcpy.GetCount_management(c_fc)[0])
        messages.addMessage("    维护层图层生成，记录数: {}".format(cnt_c))

        # 清理
        arcpy.Delete_management(intersect_qh)

    # ==================================================================
    # 处理类型 1 或 3（调入/调出）
    # ==================================================================
    def process_type13(self, alias, layer_name, fc_a, fc_b, c_path, xzqdm, xzqmc, date_prefix, config, target_sr, messages):
        seq_counter = 1  # 每个图层独立从1开始
        prefix = layer_name
        q_name = "WHQ{}".format(prefix)
        c_name_lyr = "WHC{}".format(prefix)
        h_name = "WHH{}".format(prefix)
        area_fields = config["area_fields"]
        ysdm_fixed = config["ysdm"]

        # ---------- 维护层 ----------
        messages.addMessage("  制作维护层图层: {}".format(c_name_lyr))
        erase1 = "in_memory/erase1"
        arcpy.analysis.PairwiseErase(fc_a, fc_b, erase1)
        erase2 = "in_memory/erase2"
        arcpy.analysis.PairwiseErase(fc_b, fc_a, erase2)

        arcpy.AddField_management(erase1, "WHLX", "TEXT", field_length=2, field_alias="维护类型")
        arcpy.AddField_management(erase2, "WHLX", "TEXT", field_length=2, field_alias="维护类型")
        with arcpy.da.UpdateCursor(erase1, ["WHLX"]) as cur:
            for row in cur:
                row[0] = "3"
                cur.updateRow(row)
        with arcpy.da.UpdateCursor(erase2, ["WHLX"]) as cur:
            for row in cur:
                row[0] = "1"
                cur.updateRow(row)

        merged = "in_memory/merged"
        arcpy.management.Merge([erase1, erase2], merged)

        c_fc = os.path.join(c_path, c_name_lyr)
        arcpy.CreateFeatureclass_management(c_path, c_name_lyr, "POLYGON", spatial_reference=target_sr)

        field_defs = [
            ("BSM", "TEXT", 18, "标识码"),
            ("YSDM", "TEXT", 10, "要素代码"),
            ("XZQDM", "TEXT", 12, "行政区代码"),
            ("XZQMC", "TEXT", 100, "行政区名称"),
            ("WHLX", "TEXT", 2, "维护类型"),
            ("WHLY", "TEXT", 3, "维护理由"),
            ("JDZBLY", "TEXT", 10, "机动指标来源"),
            ("WHBH", "TEXT", 18, "维护编号"),
            ("KZBSHSLX", "TEXT", 3, "扩展倍数核算类型"),
            ("BZ", "TEXT", 255, "备注")
        ]
        for fname, ftype, flen, falias in field_defs:
            arcpy.AddField_management(c_fc, fname, ftype, field_length=flen, field_alias=falias)

        target_fields = ["SHAPE@", "YSDM", "XZQDM", "XZQMC", "WHLX", "WHBH", "BSM", "WHLY", "JDZBLY", "KZBSHSLX", "BZ"]
        start_seq_c = 1
        with arcpy.da.InsertCursor(c_fc, target_fields) as ins_cursor:
            with arcpy.da.SearchCursor(merged, ["SHAPE@", "WHLX"]) as src_cursor:
                for src_row in src_cursor:
                    geom = src_row[0]
                    if geom is None:
                        continue
                    whlx = src_row[1]
                    new_bsm = self._generate_bsm(xzqdm, start_seq_c)
                    start_seq_c += 1
                    whbh = self._generate_whbh(date_prefix, seq_counter)
                    seq_counter += 1
                    ins_row = [
                        geom,
                        ysdm_fixed,
                        xzqdm,
                        xzqmc,
                        whlx,
                        whbh,
                        new_bsm,
                        "", "", "", ""
                    ]
                    ins_cursor.insertRow(ins_row)

        # 拆分多部件
        c_fc_single = c_fc + "_single"
        arcpy.management.MultipartToSinglepart(c_fc, c_fc_single)
        arcpy.Delete_management(c_fc)
        arcpy.Rename_management(c_fc_single, c_name_lyr)
        c_fc = os.path.join(c_path, c_name_lyr)
        self._define_sr(c_fc, target_sr)

        cnt_c = int(arcpy.GetCount_management(c_fc)[0])
        messages.addMessage("    维护层图层生成，记录数: {}".format(cnt_c))

        # ---------- 维护前 ----------
        # 复用结果1（erase1 = A-B），频数统计其BSM → 从A库中选对应图斑作为WHQ
        messages.addMessage("  制作维护前图层: {}".format(q_name))
        freq = "in_memory/freq"
        arcpy.analysis.Frequency(erase1, freq, "BSM")
        bsm_list = [row[0] for row in arcpy.da.SearchCursor(freq, "BSM")]
        q_fc = os.path.join(c_path, q_name)
        if bsm_list:
            where = "BSM IN ({})".format(",".join(["'{}'".format(b) for b in bsm_list]))
            arcpy.MakeFeatureLayer_management(fc_a, "a_lyr")
            arcpy.SelectLayerByAttribute_management("a_lyr", "NEW_SELECTION", where)
            arcpy.conversion.FeatureClassToFeatureClass("a_lyr", c_path, q_name)
            arcpy.Delete_management("a_lyr")
            self._define_sr(q_fc, target_sr)
        else:
            arcpy.CreateFeatureclass_management(c_path, q_name, "POLYGON", fc_a, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)
        cnt_q = int(arcpy.GetCount_management(q_fc)[0])
        messages.addMessage("    维护前图层记录数: {}".format(cnt_q))
        arcpy.Delete_management(freq)

        # ---------- 维护后 ----------
        # WHH = 结果2(B-A) + B∩(WHQ-WHC) 合并后拆单部件
        messages.addMessage("  制作维护后图层: {}".format(h_name))

        # 结果3 = WHQ - WHC（维护前去掉维护层）
        whq_erase_whc = "in_memory/whq_erase_whc"
        if cnt_q > 0 and cnt_c > 0:
            arcpy.analysis.PairwiseErase(q_fc, c_fc, whq_erase_whc)
        elif cnt_q > 0:
            arcpy.management.Copy(q_fc, whq_erase_whc)
        else:
            arcpy.CreateFeatureclass_management("in_memory", "whq_erase_whc", "POLYGON", fc_a, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)

        # 结果4 = B ∩ 结果3
        b_int_result3 = "in_memory/b_int_result3"
        cnt_result3 = int(arcpy.GetCount_management(whq_erase_whc)[0])
        if cnt_result3 > 0:
            arcpy.analysis.PairwiseIntersect([fc_b, whq_erase_whc], b_int_result3, "ALL", "", "INPUT")
        else:
            arcpy.CreateFeatureclass_management("in_memory", "b_int_result3", "POLYGON", fc_b, "SAME_AS_TEMPLATE", "SAME_AS_TEMPLATE", target_sr)

        # WHH = 结果2(erase2) + 结果4 合并
        merged_whh = "in_memory/merged_whh"
        arcpy.management.Merge([erase2, b_int_result3], merged_whh)

        h_fc = os.path.join(c_path, h_name)
        arcpy.conversion.FeatureClassToFeatureClass(merged_whh, c_path, h_name)
        arcpy.management.MultipartToSinglepart(h_fc, h_fc + "_single")
        arcpy.Delete_management(h_fc)
        arcpy.Rename_management(h_fc + "_single", h_name)
        h_fc = os.path.join(c_path, h_name)
        self._define_sr(h_fc, target_sr)
        arcpy.Delete_management(whq_erase_whc)
        arcpy.Delete_management(b_int_result3)
        arcpy.Delete_management(merged_whh)

        self._ensure_fields_exist(h_fc, "BSM", "TEXT", field_length=18, field_alias="标识码")
        for afield in area_fields:
            self._ensure_fields_exist(h_fc, afield, "DOUBLE", field_alias=afield)

        max_bsm2 = self._get_max_bsm(fc_a)
        start_seq_h = int(max_bsm2[-8:]) + 1 if max_bsm2 and max_bsm2[-8:].isdigit() else 1
        update_fields = ["BSM", "SHAPE@"] + area_fields
        with arcpy.da.UpdateCursor(h_fc, update_fields) as cursor:
            for row in cursor:
                row[0] = self._generate_bsm(xzqdm, start_seq_h)
                start_seq_h += 1
                geom = row[1]
                if geom:
                    area = round(geom.area, 2)
                    for i in range(2, len(row)):
                        row[i] = area
                cursor.updateRow(row)

        cnt_h = int(arcpy.GetCount_management(h_fc)[0])
        messages.addMessage("    维护后图层记录数: {}".format(cnt_h))

        # 清理
        arcpy.Delete_management(erase1)
        arcpy.Delete_management(erase2)
        arcpy.Delete_management(merged)

    # ==================================================================
    # 辅助函数
    # ==================================================================
    def _define_sr(self, fc, sr):
        arcpy.management.DefineProjection(fc, sr)

    def _get_field_map(self, intersect_fc, fc_first, fc_second, compare_field):
        flds = [f.name for f in arcpy.ListFields(intersect_fc)]
        bsm_candidates = [f for f in flds if "BSM" in f.upper()]
        if len(bsm_candidates) >= 2:
            a_bsm = "BSM" if "BSM" in bsm_candidates else bsm_candidates[0]
            b_bsm = [f for f in bsm_candidates if f != a_bsm][0]
        else:
            raise arcpy.ExecuteError("无法区分两个输入的BSM字段")
        cmp_candidates = [f for f in flds if compare_field in f.upper()]
        if len(cmp_candidates) >= 2:
            a_cmp = compare_field if compare_field in cmp_candidates else cmp_candidates[0]
            b_cmp = [f for f in cmp_candidates if f != a_cmp][0]
        else:
            raise arcpy.ExecuteError("无法区分两个输入的比较字段")
        return {"a_bsm": a_bsm, "b_bsm": b_bsm, "a_cmp": a_cmp, "b_cmp": b_cmp}

    def _find_field_with_suffix(self, field_list, base_name, suffix="_1"):
        if (base_name + suffix) in field_list:
            return base_name + suffix
        elif base_name in field_list:
            return base_name
        else:
            for f in field_list:
                if f.startswith(base_name):
                    return f
            return None

    def _get_max_bsm(self, fc):
        max_val = None
        if "BSM" in [f.name for f in arcpy.ListFields(fc)]:
            with arcpy.da.SearchCursor(fc, "BSM") as cursor:
                for row in cursor:
                    if row[0] and (max_val is None or row[0] > max_val):
                        max_val = row[0]
        return max_val

    def _generate_bsm(self, xzqdm, seq):
        seq_str = str(seq).zfill(8)
        return "{}{}{}".format(xzqdm, "0000", seq_str)

    def _generate_whbh(self, date_prefix, seq):
        seq_str = str(seq).zfill(6)
        return "{}{}".format(date_prefix, seq_str)

    def _ensure_fields_exist(self, fc, field_name, field_type, **kwargs):
        if field_name not in [f.name for f in arcpy.ListFields(fc)]:
            arcpy.AddField_management(fc, field_name, field_type, **kwargs)