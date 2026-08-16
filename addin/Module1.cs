using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn
{
    /// <summary>
    /// AddIn 模块入口（单例），由 Config.daml 中的 GHBoxAddIn_Module 自动加载。
    /// </summary>
    internal class Module1 : Module
    {
        private static Module1 _this;

        /// <summary>获取模块单例</summary>
        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("GHBoxAddIn_Module");

        /// <summary>ArcGIS Pro 关闭时允许卸载本模块</summary>
        protected override bool CanUnload() => true;
    }
}
