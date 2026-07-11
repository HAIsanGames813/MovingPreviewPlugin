using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriTimeline = YukkuriMovieMaker.Project.Timeline;
using YukkuriScene = YukkuriMovieMaker.Project.Scene;

namespace MovingPreviewPlugin
{
    public class MovingPreviewViewModel : Bindable, ITimelineToolViewModel, IDisposable
    {
        private YukkuriTimeline? timeline;
        private YukkuriScene? scene;
        private double currentFps = 60;

        private BitmapSource? previewImage;
        private double scale = 1.0;
        private double offsetX = 0.0;
        private double offsetY = 0.0;
        private bool isLocked = false;

        private CancellationTokenSource? cts;
        private readonly Lock renderLock = new();
        private double requestedFrame = -1;
        private bool isDirty = false;

        public BitmapSource? PreviewImage { get => previewImage; set => Set(ref previewImage, value); }
        public double Scale { get => scale; set => Set(ref scale, Math.Max(0.05, Math.Min(32.0, value))); }
        public double OffsetX { get => offsetX; set { if (!isLocked) Set(ref offsetX, value); } }
        public double OffsetY { get => offsetY; set { if (!isLocked) Set(ref offsetY, value); } }

        public bool IsLocked
        {
            get => isLocked;
            set
            {
                if (Set(ref isLocked, value))
                    OnPropertyChanged(nameof(LockMenuHeader));
            }
        }

        public string LockMenuHeader => isLocked ? "固定を解除" : "固定";

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            var newTimeline = info.Timeline;
            var newScene = info.Scenes.AllScenes.FirstOrDefault(s => s.Timeline == newTimeline);

            if (!ReferenceEquals(newTimeline, timeline))
            {
                if (timeline is INotifyPropertyChanged oldNpc)
                    oldNpc.PropertyChanged -= OnTimelinePropertyChanged;

                timeline = newTimeline;

                if (timeline is INotifyPropertyChanged newNpc)
                    newNpc.PropertyChanged += OnTimelinePropertyChanged;
            }

            if (!ReferenceEquals(newScene, scene))
            {
                StopRenderLoop();
                scene = newScene;

                if (scene != null)
                {
                    currentFps = scene.FPS > 0 ? scene.FPS : 60;
                    StartRenderLoop(scene);

                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                    CompositionTarget.Rendering += OnCompositionTargetRendering;
                }
            }

            RequestUpdate();
        }

        private void OnCompositionTargetRendering(object? sender, EventArgs e)
            => RequestUpdate();

        private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is "CurrentFrame" or "Items" or "Length" or "VideoInfo")
            {
                RequestUpdate();
            }
        }

        public void ResetView()
        {
            Scale = 1.0;
            OffsetX = 0.0;
            OffsetY = 0.0;
        }

        public void ToggleLock() => IsLocked = !IsLocked;

        public void ZoomAt(double factor, Point pivot, Size viewSize)
        {
            if (isLocked) return;

            double newScale = Math.Max(0.05, Math.Min(32.0, scale * factor));
            double ratio = newScale / scale;
            double cx = viewSize.Width / 2.0;
            double cy = viewSize.Height / 2.0;

            OffsetX = pivot.X - ratio * (pivot.X - cx - offsetX) - cx;
            OffsetY = pivot.Y - ratio * (pivot.Y - cy - offsetY) - cy;
            Scale = newScale;
        }

        private void StartRenderLoop(YukkuriScene targetScene)
        {
            cts = new();
            Task.Run(() => RenderWorkerLoop(targetScene, cts.Token));
        }

        private void StopRenderLoop()
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        }

        private void RequestUpdate()
        {
            if (timeline == null) return;
            lock (renderLock)
            {
                requestedFrame = timeline.CurrentFrame;
                isDirty = true;
            }
        }

        private void RenderWorkerLoop(YukkuriScene targetScene, CancellationToken token)
        {
            ITimelineSourceAndDevices? source = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    double frameToRender;
                    bool shouldRender;
                    lock (renderLock)
                    {
                        frameToRender = requestedFrame;
                        shouldRender = isDirty;
                        isDirty = false;
                    }

                    if (!shouldRender || frameToRender < 0)
                    {
                        Thread.Sleep(8);
                        continue;
                    }

                    source ??= targetScene.TryCreateVideoSource(out var s) ? s : null;
                    if (source == null)
                    {
                        Thread.Sleep(200);
                        continue;
                    }

                    try
                    {
                        if (currentFps <= 0 || timeline == null || frameToRender >= timeline.Length)
                            continue;

                        source.Update(
                            TimeSpan.FromSeconds(frameToRender / currentFps),
                            TimelineSourceUsage.Paused);

                        var bmp = source.RenderBitmapSource();
                        if (bmp == null || bmp.PixelWidth <= 0) continue;

                        bmp.Freeze();
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            if (!token.IsCancellationRequested)
                                PreviewImage = bmp;
                        });
                    }
                    catch
                    {
                        source?.Dispose();
                        source = null;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { source?.Dispose(); }
        }

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            StopRenderLoop();

            if (timeline is INotifyPropertyChanged npc)
                npc.PropertyChanged -= OnTimelinePropertyChanged;

            timeline = null;
            scene = null;

            GC.SuppressFinalize(this);
        }
    }
}