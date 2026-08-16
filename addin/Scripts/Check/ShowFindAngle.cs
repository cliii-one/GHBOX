using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.Check
{
    /// <summary>「查找尖锐角」按钮：单例打开检查窗口。</summary>
    internal class ShowFindAngle : Button
    {
        private FindAngle _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }
            _window = new FindAngle { Owner = FrameworkApplication.Current.MainWindow };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}
