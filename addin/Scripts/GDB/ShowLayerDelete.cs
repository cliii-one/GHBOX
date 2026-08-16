using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 「删除图层」按钮：点击打开删除图层窗口。
    /// 窗口单例：已打开时再次点击仅激活，关闭后可重新打开。
    /// </summary>
    internal class ShowLayerDelete : Button
    {
        private LayerDelete _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }

            _window = new LayerDelete
            {
                Owner = FrameworkApplication.Current.MainWindow
            };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}
