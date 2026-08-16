using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.GDB
{
    /// <summary>
    /// 「唯一编码」按钮：点击打开唯一编码窗口。
    /// 窗口单例：已打开时再次点击仅激活，关闭后可重新打开。
    /// </summary>
    internal class ShowUniqueCode : Button
    {
        private UniqueCode _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }

            _window = new UniqueCode
            {
                Owner = FrameworkApplication.Current.MainWindow
            };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}
