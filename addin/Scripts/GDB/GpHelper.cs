using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// GP 工具执行与地理数据库访问的公共辅助（数据库合并 / 删除图层共用）。
    /// </summary>
    internal static class GpHelper
    {
        /// <summary>执行 GP 工具，失败抛出带消息的异常。
        /// 可选传入 targetSR 自动设置 outputCoordinateSystem 环境变量。
        /// 可选传入 scratchPath 设置 workspace/scratchWorkspace（避免中间数据写入只读输入库）。</summary>
        public static async Task<IGPResult> RunToolAsync(string tool, IEnumerable<string> args, CancellationToken ct,
            SpatialReference targetSR = null, string scratchPath = null)
        {
            ct.ThrowIfCancellationRequested();

            // 构建环境数组
            IGPResult result;
            if (targetSR != null && !string.IsNullOrEmpty(scratchPath))
            {
                var env = Geoprocessing.MakeEnvironmentArray(
                    overwriteoutput: true,
                    outputCoordinateSystem: targetSR,
                    workspace: scratchPath,
                    scratchWorkspace: scratchPath);
                result = await Geoprocessing.ExecuteToolAsync(
                    tool, args, env, null, null, GPExecuteToolFlags.None);
            }
            else if (targetSR != null)
            {
                var env = Geoprocessing.MakeEnvironmentArray(
                    overwriteoutput: true,
                    outputCoordinateSystem: targetSR);
                result = await Geoprocessing.ExecuteToolAsync(
                    tool, args, env, null, null, GPExecuteToolFlags.None);
            }
            else
            {
                result = await Geoprocessing.ExecuteToolAsync(
                    tool, args,
                    Geoprocessing.MakeEnvironmentArray(overwriteoutput: true),
                    null, null, GPExecuteToolFlags.None);
            }

            ct.ThrowIfCancellationRequested();

            if (result?.IsFailed != false)
            {
                string msg = result?.Messages != null && result.Messages.Any()
                    ? string.Join("\n", result.Messages.Select(m => m.Text))
                    : $"{tool} 执行失败。";
                throw new InvalidOperationException(msg);
            }
            return result;
        }

        /// <summary>GP Exists 判断数据集是否存在</summary>
        public static async Task<bool> ExistsDatasetAsync(string path)
        {
            IGPResult result = await Geoprocessing.ExecuteToolAsync(
                "management.Exists", Geoprocessing.MakeValueArray(path));
            return result?.IsFailed == false &&
                   result.Values != null &&
                   result.Values.Count > 0 &&
                   string.Equals(result.Values[0]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>GetCount 取数据条数</summary>
        public static async Task<long> GetCountAsync(string path, CancellationToken ct)
        {
            IGPResult result = await RunToolAsync("management.GetCount",
                Geoprocessing.MakeValueArray(path), ct);
            return long.TryParse(result.Values?[0]?.ToString(), out long n) ? n : 0;
        }

        /// <summary>打开文件地理数据库；非 .gdb（如 .mdb）或打开失败返回 null</summary>
        public static Geodatabase OpenGeodatabase(string path)
        {
            try
            {
                if (path.ToLowerInvariant().EndsWith(".gdb"))
                    return new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(path)));
            }
            catch { /* 打不开按不存在处理 */ }
            return null;
        }
    }
}
