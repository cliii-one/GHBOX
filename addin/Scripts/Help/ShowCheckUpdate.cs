using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.Help
{
    /// <summary>「检查更新」按钮：单例打开检查更新窗口。</summary>
    internal class ShowCheckUpdate : Button
    {
        private CheckUpdate _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }
            _window = new CheckUpdate { Owner = FrameworkApplication.Current.MainWindow };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}