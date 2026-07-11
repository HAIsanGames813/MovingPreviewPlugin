using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
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
        // -----------------------------------------------------------------------
        // バッキングフィールド
        // -----------------------------------------------------------------------
        private YukkuriTimeline? timeline;
        private YukkuriScene? scene;
        private double currentFps = 60;

        private BitmapSource? previewImage;
        private double scale = 1.0;
        private double offsetX = 0.0;
        private double offsetY = 0.0;
        private bool isLocked = false;

        private CancellationTokenSource? cts;
        private readonly object lockObj = new object();
        private double requestedFrame = -1;
        private bool isDirty = false;

        // アイテム変更監視 — リフレクションで発見したコレクションと購読済みアイテムを管理する
        // コレクション購読: (コレクション本体, CollectionChanged ハンドラ) のペア
        private readonly System.Collections.Generic.List<(INotifyCollectionChanged col, NotifyCollectionChangedEventHandler handler)>
            _collectionSubscriptions = new();

        // アイテム購読: (アイテム, PropertyChanged ハンドラ) のペア
        private readonly System.Collections.Generic.List<(INotifyPropertyChanged item, PropertyChangedEventHandler handler)>
            _itemSubscriptions = new();

        // -----------------------------------------------------------------------
        // 公開プロパティ
        // -----------------------------------------------------------------------

        public BitmapSource? PreviewImage { get => previewImage; set => Set(ref previewImage, value); }
        public double Scale { get => scale; set => Set(ref scale, Math.Max(0.05, Math.Min(32.0, value))); }
        public double OffsetX { get => offsetX; set { if (!isLocked) Set(ref offsetX, value); } }
        public double OffsetY { get => offsetY; set { if (!isLocked) Set(ref offsetY, value); } }

        /// <summary>
        /// 固定モード。ON のときはドラッグ・ホイールによる移動・拡大縮小を無効にする。
        /// </summary>
        public bool IsLocked
        {
            get => isLocked;
            set
            {
                if (Set(ref isLocked, value))
                    OnPropertyChanged(nameof(LockMenuHeader));
            }
        }

        /// <summary>右クリックメニューの「固定」項目のラベル。</summary>
        public string LockMenuHeader => isLocked ? "固定を解除" : "固定";

        // -----------------------------------------------------------------------
        // ITimelineToolViewModel
        // -----------------------------------------------------------------------

        public void SetTimelineToolInfo(TimelineToolInfo info)
        {
            Dispose();
            timeline = info.Timeline;
            scene = info.Scenes.AllScenes.FirstOrDefault(s => s.Timeline == timeline);

            if (timeline is INotifyPropertyChanged npc)
                npc.PropertyChanged += Timeline_PropertyChanged;

            if (scene != null)
            {
                currentFps = scene.FPS > 0 ? scene.FPS : 60;
                SubscribeToTimelineItems(timeline);
                cts = new CancellationTokenSource();
                Task.Run(() => RenderWorkerLoop(scene, cts.Token));
                RequestUpdate();
            }
        }

        // -----------------------------------------------------------------------
        // 操作
        // -----------------------------------------------------------------------

        /// <summary>表示位置と拡大率を初期値に戻す。</summary>
        public void ResetView()
        {
            Scale = 1.0;
            OffsetX = 0.0;
            OffsetY = 0.0;
        }

        /// <summary>固定モードを切り替える。</summary>
        public void ToggleLock() => IsLocked = !IsLocked;

        /// <summary>ホイール操作による拡大縮小。pivot はコントロール座標系でのマウス位置。</summary>
        public void ZoomAt(double factor, Point pivot, Size viewSize)
        {
            if (isLocked) return;

            double newScale = Math.Max(0.05, Math.Min(32.0, scale * factor));
            double ratio = newScale / scale;

            // pivot を中心にズームするようにオフセットを補正する
            double cx = viewSize.Width / 2.0;
            double cy = viewSize.Height / 2.0;

            OffsetX = pivot.X - ratio * (pivot.X - cx - offsetX) - cx;
            OffsetY = pivot.Y - ratio * (pivot.Y - cy - offsetY) - cy;
            Scale = newScale;
        }

        // -----------------------------------------------------------------------
        // アイテム変更監視（リフレクション）
        // -----------------------------------------------------------------------

        /// <summary>
        /// Timeline オブジェクトのプロパティをリフレクションで走査し、
        /// INotifyCollectionChanged を実装するコレクションをすべて購読する。
        /// 各コレクションに含まれるアイテムが INotifyPropertyChanged を実装していれば
        /// そのプロパティ変更も購読する。
        ///
        /// これにより、アイテムの追加・削除・移動・プロパティ変更のいずれでも
        /// RequestUpdate() が呼ばれてプレビューが更新される。
        /// </summary>
        private void SubscribeToTimelineItems(object? target)
        {
            if (target == null) return;
            try
            {
                // Timeline の公開プロパティを全列挙
                var props = target.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

                foreach (var prop in props)
                {
                    object? value;
                    try { value = prop.GetValue(target); }
                    catch { continue; }

                    if (value is INotifyCollectionChanged col)
                        SubscribeCollection(col);
                }
            }
            catch { }
        }

        /// <summary>コレクション自体の変更イベントを購読し、中のアイテムも個別に購読する。</summary>
        private void SubscribeCollection(INotifyCollectionChanged col)
        {
            NotifyCollectionChangedEventHandler handler = (s, e) => OnCollectionChanged(s, e);
            col.CollectionChanged += handler;
            _collectionSubscriptions.Add((col, handler));

            // 既存アイテムへの購読
            if (col is IEnumerable items)
            {
                foreach (var item in items)
                    TrySubscribeItem(item);
            }
        }

        /// <summary>コレクションの追加・削除イベントで個別アイテムの購読を増減する。</summary>
        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 新たに追加されたアイテムを購読する
            if (e.NewItems != null)
                foreach (var item in e.NewItems) TrySubscribeItem(item);

            // 削除されたアイテムの購読を解除する
            if (e.OldItems != null)
                foreach (var item in e.OldItems) TryUnsubscribeItem(item);

            RequestUpdate();
        }

        private void TrySubscribeItem(object? item)
        {
            if (item is not INotifyPropertyChanged npc) return;
            // 二重登録を防ぐ
            if (_itemSubscriptions.Any(x => ReferenceEquals(x.item, npc))) return;

            PropertyChangedEventHandler handler = (s, e) => RequestUpdate();
            npc.PropertyChanged += handler;
            _itemSubscriptions.Add((npc, handler));
        }

        private void TryUnsubscribeItem(object? item)
        {
            if (item is not INotifyPropertyChanged npc) return;
            int idx = _itemSubscriptions.FindIndex(x => ReferenceEquals(x.item, npc));
            if (idx < 0) return;

            var (i, h) = _itemSubscriptions[idx];
            i.PropertyChanged -= h;
            _itemSubscriptions.RemoveAt(idx);
        }

        /// <summary>すべてのコレクション・アイテム購読を解除する。</summary>
        private void UnsubscribeAll()
        {
            foreach (var (col, handler) in _collectionSubscriptions)
            {
                try { col.CollectionChanged -= handler; } catch { }
            }
            _collectionSubscriptions.Clear();

            foreach (var (item, handler) in _itemSubscriptions)
            {
                try { item.PropertyChanged -= handler; } catch { }
            }
            _itemSubscriptions.Clear();
        }

        // -----------------------------------------------------------------------
        // バックグラウンドレンダラー
        // -----------------------------------------------------------------------

        public void RequestUpdate()
        {
            if (timeline == null) return;
            lock (lockObj)
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
                    lock (lockObj)
                    {
                        frameToRender = requestedFrame;
                        shouldRender = isDirty;
                        isDirty = false;
                    }

                    if (!shouldRender || frameToRender < 0)
                    {
                        Thread.Sleep(15);
                        continue;
                    }

                    if (source == null)
                    {
                        if (!targetScene.TryCreateVideoSource(out source))
                        {
                            Thread.Sleep(200);
                            continue;
                        }
                    }

                    try
                    {
                        if (currentFps <= 0) continue;
                        if (timeline == null || frameToRender >= timeline.Length) continue;

                        source.Update(TimeSpan.FromSeconds(frameToRender / currentFps), TimelineSourceUsage.Paused);
                        var bmp = source.RenderBitmapSource();
                        if (bmp == null || bmp.PixelWidth <= 0) continue;

                        bmp.Freeze();
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!token.IsCancellationRequested) PreviewImage = bmp;
                        }));
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { source?.Dispose(); }
        }

        // -----------------------------------------------------------------------
        // タイムラインイベント（フレーム移動・長さ変更など）
        // -----------------------------------------------------------------------

        private void Timeline_PropertyChanged(object? sender, PropertyChangedEventArgs e)
            => RequestUpdate();

        // -----------------------------------------------------------------------
        // IDisposable
        // -----------------------------------------------------------------------

        public void Dispose()
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;

            if (timeline is INotifyPropertyChanged npc)
                npc.PropertyChanged -= Timeline_PropertyChanged;

            UnsubscribeAll();

            timeline = null;
            scene = null;
        }
    }
}