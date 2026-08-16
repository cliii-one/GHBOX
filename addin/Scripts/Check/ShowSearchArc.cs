using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace GHBoxAddIn.Scripts.Check
{
    /// <summary>「查找弧线段」按钮：单例打开检查窗口。</summary>
    internal class ShowSearchArc : Button
    {
        private SearchArc _window;

        protected override void OnClick()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }
            _window = new SearchArc { Owner = FrameworkApplication.Current.MainWindow };
            _window.Closed += (o, e) => _window = null;
            _window.Show();
        }
    }
}
