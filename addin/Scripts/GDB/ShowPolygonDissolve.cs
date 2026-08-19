using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// “按属性合并”按钮：点击后打开按属性合并窗口。
    /// 窗口单例：已打开时再次点击仅激活，关闭后可重新打开。
    /// </summary>
    internal class ShowPolygonDissolve : Button
    {
        private PolygonDissolve _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }

            _window = new PolygonDissolve
            {
                Owner = FrameworkApplication.Current.MainWindow
            };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}