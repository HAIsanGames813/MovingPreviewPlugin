
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Linq;
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
        private Task? renderTask;

        private readonly object renderLoopSync = new();
        private readonly object renderLock = new();

        private double requestedFrame = -1;
        private bool isDirty;
        private long renderGeneration;
        private int disposed;

        public BitmapSource? PreviewImage
        {
            get => previewImage;
            set => Set(ref previewImage, value);
        }

        public double Scale
        {
            get => scale;
            set => Set(ref scale, Math.Max(0.05, Math.Min(32.0, value)));
        }

        public double OffsetX
        {
            get => offsetX;
            set
            {
                if (!isLocked)
                    Set(ref offsetX, value);
            }
        }

        public double OffsetY
        {
            get => offsetY;
            set
            {
                if (!isLocked)
                    Set(ref offsetY, value);
            }
        }

        public bool IsLocked
        {
            get => isLocked;
            set
            {
                if (Set(ref isLocked, value))
                    OnPropertyChanged(nameof(LockMenuHeader));
            }
        }

        public string LockMenuHeader =>
            isLocked ? "固定を解除" : "固定";

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            var newTimeline = info.Timeline;
            var newScene = info.Scenes.AllScenes
                .FirstOrDefault(s => s.Timeline == newTimeline);

            bool timelineChanged =
                !ReferenceEquals(newTimeline, timeline);

            bool sceneChanged =
                !ReferenceEquals(newScene, scene);

            if (timelineChanged)
            {
                if (timeline is INotifyPropertyChanged oldNpc)
                    oldNpc.PropertyChanged -= OnTimelinePropertyChanged;

                timeline = newTimeline;

                if (timeline is INotifyPropertyChanged newNpc)
                    newNpc.PropertyChanged += OnTimelinePropertyChanged;
            }

            if (timelineChanged || sceneChanged)
            {
                StopRenderLoop();

                CompositionTarget.Rendering -=
                    OnCompositionTargetRendering;

                scene = newScene;

                if (scene != null && timeline != null)
                {
                    currentFps = scene.FPS > 0
                        ? scene.FPS
                        : 60;

                    StartRenderLoop(
                        scene,
                        timeline,
                        currentFps);

                    CompositionTarget.Rendering +=
                        OnCompositionTargetRendering;
                }
                else
                {
                    PreviewImage = null;
                }
            }

            RequestUpdate();
        }

        private void OnCompositionTargetRendering(
            object? sender,
            EventArgs e)
        {
            RequestUpdate();
        }

        private void OnTimelinePropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is
                    "CurrentFrame" or
                    "Items" or
                    "Length" or
                    "VideoInfo")
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

        public void ToggleLock()
        {
            IsLocked = !IsLocked;
        }

        public void ZoomAt(
            double factor,
            Point pivot,
            Size viewSize)
        {
            if (isLocked || scale <= 0)
                return;

            double newScale = Math.Max(
                0.05,
                Math.Min(32.0, scale * factor));

            double ratio = newScale / scale;

            double cx = viewSize.Width / 2.0;
            double cy = viewSize.Height / 2.0;

            OffsetX =
                pivot.X -
                ratio * (pivot.X - cx - offsetX) -
                cx;

            OffsetY =
                pivot.Y -
                ratio * (pivot.Y - cy - offsetY) -
                cy;

            Scale = newScale;
        }

        private void StartRenderLoop(
            YukkuriScene targetScene,
            YukkuriTimeline targetTimeline,
            double fps)
        {
            var newCts = new CancellationTokenSource();
            var token = newCts.Token;

            Task? previousTask;
            long generation;

            lock (renderLoopSync)
            {
                previousTask = renderTask;
                cts = newCts;
                generation = Interlocked.Increment(
                    ref renderGeneration);

                renderTask = Task.Run(async () =>
                {
                    try
                    {
                        if (previousTask != null)
                        {
                            try
                            {
                                await previousTask.ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine(
                                    $"[MovingPreviewPlugin] Previous task error: {ex}");
                            }
                        }

                        if (token.IsCancellationRequested)
                            return;

                        RenderWorkerLoop(
                            targetScene,
                            targetTimeline,
                            fps,
                            token,
                            generation);
                    }
                    catch (OperationCanceledException)
                        when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[MovingPreviewPlugin] Render task error: {ex}");
                    }
                    finally
                    {
                        lock (renderLoopSync)
                        {
                            if (ReferenceEquals(cts, newCts))
                                cts = null;
                        }

                        newCts.Dispose();
                    }
                });
            }
        }

        private void StopRenderLoop()
        {
            CancellationTokenSource? oldCts;

            lock (renderLoopSync)
            {
                Interlocked.Increment(ref renderGeneration);

                oldCts = cts;
                cts = null;

                if (oldCts != null)
                {
                    try
                    {
                        oldCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            lock (renderLock)
            {
                isDirty = false;
                requestedFrame = -1;
            }
        }

        private void RequestUpdate()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            var currentTimeline = timeline;

            if (currentTimeline == null)
                return;

            double frame;

            try
            {
                frame = currentTimeline.CurrentFrame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[MovingPreviewPlugin] Failed to get current frame: {ex}");

                return;
            }

            lock (renderLock)
            {
                requestedFrame = frame;
                isDirty = true;
            }
        }

        private void RenderWorkerLoop(
            YukkuriScene targetScene,
            YukkuriTimeline targetTimeline,
            double fps,
            CancellationToken token,
            long generation)
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
                        token.WaitHandle.WaitOne(8);
                        continue;
                    }

                    if (token.IsCancellationRequested ||
                        generation != Interlocked.Read(ref renderGeneration))
                    {
                        break;
                    }

                    if (source == null)
                    {
                        if (!targetScene.TryCreateVideoSource(out var newSource))
                        {
                            token.WaitHandle.WaitOne(200);
                            continue;
                        }

                        source = newSource;
                    }

                    if (source == null)
                        continue;

                    try
                    {
                        if (fps <= 0 ||
                            frameToRender >= targetTimeline.Length)
                        {
                            continue;
                        }

                        var time = TimeSpan.FromSeconds(
                            frameToRender / fps);

                        source.Update(
                            time,
                            TimelineSourceUsage.Paused);

                        var bmp = source.RenderBitmapSource();

                        if (bmp == null ||
                            bmp.PixelWidth <= 0 ||
                            bmp.PixelHeight <= 0)
                        {
                            continue;
                        }

                        bmp.Freeze();

                        var dispatcher = Application.Current?.Dispatcher;

                        if (dispatcher == null ||
                            dispatcher.HasShutdownStarted)
                        {
                            continue;
                        }

                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (Volatile.Read(ref disposed) != 0)
                                return;

                            if (token.IsCancellationRequested)
                                return;

                            if (generation !=
                                Interlocked.Read(ref renderGeneration))
                            {
                                return;
                            }

                            PreviewImage = bmp;
                        }));
                    }
                    catch (OperationCanceledException)
                        when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[MovingPreviewPlugin] Frame rendering error: {ex}");

                        try
                        {
                            source?.Dispose();
                        }
                        catch (Exception disposeEx)
                        {
                            Debug.WriteLine(
                                $"[MovingPreviewPlugin] Source dispose error: {disposeEx}");
                        }

                        source = null;

                        token.WaitHandle.WaitOne(100);
                    }
                }
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[MovingPreviewPlugin] Fatal render loop error: {ex}");
            }
            finally
            {
                try
                {
                    source?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[MovingPreviewPlugin] Final source dispose error: {ex}");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            CompositionTarget.Rendering -=
                OnCompositionTargetRendering;

            StopRenderLoop();

            if (timeline is INotifyPropertyChanged npc)
                npc.PropertyChanged -= OnTimelinePropertyChanged;

            timeline = null;
            scene = null;

            PreviewImage = null;

            GC.SuppressFinalize(this);
        }
    }
}
