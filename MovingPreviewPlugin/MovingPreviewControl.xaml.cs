using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YukkuriMovieMaker.Plugin;

namespace MovingPreviewPlugin
{
    public partial class MovingPreviewControl : UserControl, ITimelineToolViewModel
    {
        // 左ドラッグ用の状態
        private bool isPanning = false;
        private Point lastPanPoint;

        public MovingPreviewControl()
        {
            InitializeComponent();
        }

        // -----------------------------------------------------------------------
        // ITimelineToolViewModel
        // -----------------------------------------------------------------------

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            var vm = new MovingPreviewViewModel();
            vm.SetTimelineToolInfo(info);
            DataContext = vm;
        }

        // -----------------------------------------------------------------------
        // ホイール：拡大縮小（マウス位置を中心にズーム）
        // -----------------------------------------------------------------------

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is not MovingPreviewViewModel vm) return;

            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            Point pivot = e.GetPosition(RootGrid);
            Size viewSize = new Size(RootGrid.ActualWidth, RootGrid.ActualHeight);

            vm.ZoomAt(factor, pivot, viewSize);
            e.Handled = true;
        }

        // -----------------------------------------------------------------------
        // 左ドラッグ：移動（パン）
        // -----------------------------------------------------------------------

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MovingPreviewViewModel vm && !vm.IsLocked)
            {
                isPanning = true;
                lastPanPoint = e.GetPosition(this);
                RootGrid.CaptureMouse();
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isPanning)
            {
                isPanning = false;
                RootGrid.ReleaseMouseCapture();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning && DataContext is MovingPreviewViewModel vm)
            {
                Point current = e.GetPosition(this);
                vm.OffsetX += current.X - lastPanPoint.X;
                vm.OffsetY += current.Y - lastPanPoint.Y;
                lastPanPoint = current;
            }
        }

        // -----------------------------------------------------------------------
        // コンテキストメニュー
        // -----------------------------------------------------------------------

        private void OnResetPosition(object sender, RoutedEventArgs e)
        {
            if (DataContext is MovingPreviewViewModel vm)
                vm.ResetView();
        }

        private void OnToggleLock(object sender, RoutedEventArgs e)
        {
            if (DataContext is MovingPreviewViewModel vm)
                vm.ToggleLock();
        }
    }
}