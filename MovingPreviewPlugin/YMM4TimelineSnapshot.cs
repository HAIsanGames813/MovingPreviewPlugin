using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MovingPreviewPlugin
{
    // =========================================================================
    // YMM4 タイムライン情報リーダー
    // =========================================================================

    // -------------------------------------------------------------------------
    // タイムライン全体のスナップショット
    // -------------------------------------------------------------------------
    public class YMM4TimelineSnapshot
    {
        public int FPS { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Length { get; init; }
        public int CurrentFrame { get; init; }

        public IReadOnlyList<YMM4ItemSnapshot> AllItems { get; init; } = Array.Empty<YMM4ItemSnapshot>();
        public IReadOnlyList<YMM4ItemSnapshot> SelectedItems { get; init; } = Array.Empty<YMM4ItemSnapshot>();

        public IReadOnlyList<YMM4ItemSnapshot> ActiveItems =>
            AllItems.Where(x => x.Frame <= CurrentFrame && CurrentFrame < x.Frame + x.Length).ToArray();

        public IReadOnlyList<YMM4ItemSnapshot> ActiveVisibleItems =>
            ActiveItems.Where(x => !x.IsHidden).ToArray();
    }

    // -------------------------------------------------------------------------
    // アイテム1つのスナップショット
    // -------------------------------------------------------------------------
    public class YMM4ItemSnapshot
    {
        public object OriginalItem { get; init; } = null!;
        public string ItemTypeName { get; init; } = "";

        public int Frame { get; init; }
        public int Length { get; init; }
        public int Layer { get; init; }
        public bool IsHidden { get; init; }
        public bool IsLocked { get; init; }
        public bool IsSelected { get; set; }

        /// <summary>includeAllProperties=true のときだけ中身が入る</summary>
        public IReadOnlyDictionary<string, object?> AllProperties { get; init; }
            = new Dictionary<string, object?>();

        public override string ToString()
            => $"[{ItemTypeName}] Frame={Frame} Len={Length} Layer={Layer} " +
               $"Hidden={IsHidden} Locked={IsLocked} Selected={IsSelected}";
    }

    // -------------------------------------------------------------------------
    // リフレクションキャッシュ（型ごとに1回だけ解決）
    // -------------------------------------------------------------------------
    internal sealed class YMM4ReflectionCache
    {
        public static readonly YMM4ReflectionCache Instance = new();

        private readonly object _lock = new();

        // タイムライン側
        private PropertyInfo? _tlItemsProp;
        private PropertyInfo? _tlSelectedItemsProp;
        private PropertyInfo? _tlCurrentFrameProp;
        private PropertyInfo? _tlLengthProp;
        private PropertyInfo? _tlFpsProp;
        private PropertyInfo? _tlWidthProp;
        private PropertyInfo? _tlHeightProp;
        private bool _tlResolved;

        // アイテム側（診断確定）
        private PropertyInfo? _itemFrameProp;
        private PropertyInfo? _itemLengthProp;   // ← アイテムの Length
        private PropertyInfo? _itemLayerProp;
        private FieldInfo? _itemIsHiddenField;
        private PropertyInfo? _itemIsHiddenProp;
        private FieldInfo? _itemIsLockedField;
        private PropertyInfo? _itemIsLockedProp;
        private MethodInfo? _onPropertyChangedMethod;
        private bool _itemResolved;

        // -----------------------------------------------------------------------
        // タイムライン解決
        // -----------------------------------------------------------------------
        public bool ResolveTimeline(object tl)
        {
            lock (_lock)
            {
                if (_tlResolved) return _tlItemsProp != null;

                var t = tl.GetType();
                _tlItemsProp = FindProp(t, "Items");
                _tlSelectedItemsProp = FindProp(t, "SelectedItems");
                _tlCurrentFrameProp = FindProp(t, "CurrentFrame");
                _tlLengthProp = FindProp(t, "Length");

                // VideoInfo.FPS / Width / Height
                var viProp = FindProp(t, "VideoInfo");
                if (viProp != null)
                {
                    var vi = viProp.GetValue(tl);
                    if (vi != null)
                    {
                        var vit = vi.GetType();
                        _tlFpsProp = FindProp(vit, "FPS") ?? FindProp(vit, "Fps");
                        _tlWidthProp = FindProp(vit, "Width");
                        _tlHeightProp = FindProp(vit, "Height");
                    }
                }
                _tlFpsProp ??= FindProp(t, "FPS") ?? FindProp(t, "Fps");
                _tlWidthProp ??= FindProp(t, "Width");
                _tlHeightProp ??= FindProp(t, "Height");

                _tlResolved = true;
                return _tlItemsProp != null;
            }
        }

        // -----------------------------------------------------------------------
        // アイテム解決
        // -----------------------------------------------------------------------
        public bool ResolveItem(object item)
        {
            lock (_lock)
            {
                if (_itemResolved) return _itemFrameProp != null;

                var itemType = item.GetType();
                _itemFrameProp = FindProp(itemType, "Frame");
                _itemLengthProp = FindProp(itemType, "Length");
                _itemLayerProp = FindProp(itemType, "Layer");

                // BaseItem に isHidden / isLocked が確認済み
                for (var bt = itemType; bt != null; bt = bt.BaseType)
                {
                    if (bt.Name != "BaseItem") continue;
                    _itemIsHiddenField = bt.GetField("isHidden",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _itemIsHiddenProp = bt.GetProperty("IsHidden",
                        BindingFlags.Public | BindingFlags.Instance);
                    _itemIsLockedField = bt.GetField("isLocked",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _itemIsLockedProp = bt.GetProperty("IsLocked",
                        BindingFlags.Public | BindingFlags.Instance);
                    break;
                }

                // OnPropertyChanged(string) — Bindable 基底クラスに存在
                for (var bt = itemType; bt != null; bt = bt.BaseType)
                {
                    _onPropertyChangedMethod = bt.GetMethod("OnPropertyChanged",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, new[] { typeof(string) }, null);
                    if (_onPropertyChangedMethod != null) break;
                }

                _itemResolved = true;
                return _itemFrameProp != null;
            }
        }

        // -----------------------------------------------------------------------
        // タイムライン値の読み取り（名前を明示して GetLength の衝突を回避）
        // -----------------------------------------------------------------------
        public int GetTimelineCurrentFrame(object tl) => GetInt(tl, _tlCurrentFrameProp);
        public int GetTimelineLength(object tl) => GetInt(tl, _tlLengthProp);

        public (int fps, int w, int h) GetVideoInfo(object tl)
        {
            var viProp = FindProp(tl.GetType(), "VideoInfo");
            var vi = viProp?.GetValue(tl);
            return vi != null
                ? (GetInt(vi, _tlFpsProp), GetInt(vi, _tlWidthProp), GetInt(vi, _tlHeightProp))
                : (GetInt(tl, _tlFpsProp), GetInt(tl, _tlWidthProp), GetInt(tl, _tlHeightProp));
        }

        public IEnumerable<object>? GetAllItems(object tl)
            => (_tlItemsProp?.GetValue(tl) as IEnumerable)?.Cast<object>();

        public HashSet<object> GetSelectedSet(object tl)
        {
            var sel = (_tlSelectedItemsProp?.GetValue(tl) as IEnumerable)?.Cast<object>();
            return sel != null
                ? new HashSet<object>(sel, ReferenceEqualityComparer.Instance)
                : new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        // -----------------------------------------------------------------------
        // アイテム値の読み取り（こちらは GetItemXxx で区別）
        // -----------------------------------------------------------------------
        public int GetItemFrame(object item) => GetInt(item, _itemFrameProp);
        public int GetItemLength(object item) => GetInt(item, _itemLengthProp);
        public int GetItemLayer(object item) => GetInt(item, _itemLayerProp);
        public bool GetItemIsHidden(object item) => GetBoolField(item, _itemIsHiddenField) ?? GetBoolProp(item, _itemIsHiddenProp) ?? false;
        public bool GetItemIsLocked(object item) => GetBoolField(item, _itemIsLockedField) ?? GetBoolProp(item, _itemIsLockedProp) ?? false;

        // -----------------------------------------------------------------------
        // IsHidden 書き換え（フィールド直書き + PropertyChanged 発火）
        // Bindable.Set を経由しないため Undo/Redo に記録されない
        // -----------------------------------------------------------------------
        public void SetItemIsHidden(object item, bool value)
        {
            if (GetItemIsHidden(item) == value) return;

            if (_itemIsHiddenField != null)
                _itemIsHiddenField.SetValue(item, value);
            else
                _itemIsHiddenProp?.SetValue(item, value);

            _onPropertyChangedMethod?.Invoke(item, new object[] { "IsHidden" });
        }

        // -----------------------------------------------------------------------
        // 全プロパティスナップショット（デバッグ用）
        // -----------------------------------------------------------------------
        public Dictionary<string, object?> SnapshotAllProperties(object item)
        {
            var result = new Dictionary<string, object?>();
            for (var t = item.GetType(); t != null; t = t.BaseType)
            {
                foreach (var p in t.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (result.ContainsKey(p.Name)) continue;
                    try { result[p.Name] = p.GetValue(item); }
                    catch { result[p.Name] = "[error]"; }
                }
            }
            return result;
        }

        // -----------------------------------------------------------------------
        // ヘルパー
        // -----------------------------------------------------------------------
        internal static PropertyInfo? FindProp(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        private static int GetInt(object obj, PropertyInfo? prop) => prop?.GetValue(obj) is int v ? v : 0;
        private static bool? GetBoolField(object o, FieldInfo? f) => f?.GetValue(o) as bool?;
        private static bool? GetBoolProp(object o, PropertyInfo? p) => p?.GetValue(o) as bool?;
    }

    // =========================================================================
    // メイン API
    // =========================================================================
    public static class YMM4TimelineReader
    {
        private static readonly YMM4ReflectionCache Cache = YMM4ReflectionCache.Instance;

        // -----------------------------------------------------------------------
        // スナップショット取得
        // -----------------------------------------------------------------------

        /// <summary>
        /// タイムライン全体のスナップショットを取得する。UIスレッドから呼ぶこと。
        /// </summary>
        public static YMM4TimelineSnapshot? TakeSnapshot(object? timeline,
            bool includeAllProperties = false)
        {
            if (timeline == null) return null;
            if (!Cache.ResolveTimeline(timeline)) return null;

            var (fps, w, h) = Cache.GetVideoInfo(timeline);
            int length = Cache.GetTimelineLength(timeline);
            int currentFrame = Cache.GetTimelineCurrentFrame(timeline);

            var allRaw = Cache.GetAllItems(timeline)?.Where(x => x != null).ToList();

            if (allRaw == null || allRaw.Count == 0)
            {
                return new YMM4TimelineSnapshot
                {
                    FPS = fps,
                    Width = w,
                    Height = h,
                    Length = length,
                    CurrentFrame = currentFrame,
                };
            }

            Cache.ResolveItem(allRaw[0]);
            var selectedSet = Cache.GetSelectedSet(timeline);

            var allItems = allRaw
                .Select(item => BuildSnapshot(item, selectedSet, includeAllProperties))
                .ToList();

            return new YMM4TimelineSnapshot
            {
                FPS = fps,
                Width = w,
                Height = h,
                Length = length,
                CurrentFrame = currentFrame,
                AllItems = allItems,
                SelectedItems = allItems.Where(x => x.IsSelected).ToList(),
            };
        }

        private static YMM4ItemSnapshot BuildSnapshot(object item,
            HashSet<object> selectedSet, bool includeAllProperties)
        {
            return new YMM4ItemSnapshot
            {
                OriginalItem = item,
                ItemTypeName = item.GetType().Name,
                Frame = Cache.GetItemFrame(item),
                Length = Cache.GetItemLength(item),
                Layer = Cache.GetItemLayer(item),
                IsHidden = Cache.GetItemIsHidden(item),
                IsLocked = Cache.GetItemIsLocked(item),
                IsSelected = selectedSet.Contains(item),
                AllProperties = includeAllProperties
                    ? Cache.SnapshotAllProperties(item)
                    : new Dictionary<string, object?>(),
            };
        }

        // -----------------------------------------------------------------------
        // IsHidden 操作
        // -----------------------------------------------------------------------

        /// <summary>
        /// 非選択アイテムを非表示にして変更リストを返す。UIスレッドから呼ぶこと。
        /// </summary>
        public static List<object> HideNonSelectedItems(object? timeline)
        {
            var hidden = new List<object>();
            if (timeline == null) return hidden;
            if (!Cache.ResolveTimeline(timeline)) return hidden;

            var allRaw = Cache.GetAllItems(timeline)?.Where(x => x != null).ToList();
            if (allRaw == null || allRaw.Count == 0) return hidden;

            Cache.ResolveItem(allRaw[0]);
            var selectedSet = Cache.GetSelectedSet(timeline);

            foreach (var item in allRaw)
            {
                if (selectedSet.Contains(item)) continue;
                if (Cache.GetItemIsHidden(item)) continue;

                Cache.SetItemIsHidden(item, true);
                hidden.Add(item);
            }

            return hidden;
        }

        /// <summary>
        /// HideNonSelectedItems で隠したアイテムを元に戻す。UIスレッドから呼ぶこと。
        /// </summary>
        public static void RestoreHiddenItems(List<object> items)
        {
            foreach (var item in items)
            {
                try { Cache.SetItemIsHidden(item, false); }
                catch { }
            }
        }

        // -----------------------------------------------------------------------
        // デバッグテキスト
        // -----------------------------------------------------------------------

        public static string ToDebugString(YMM4TimelineSnapshot snap)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== YMM4 Timeline Snapshot ===");
            sb.AppendLine($"FPS={snap.FPS}  Size={snap.Width}x{snap.Height}");
            sb.AppendLine($"Length={snap.Length}  CurrentFrame={snap.CurrentFrame}");
            sb.AppendLine($"Items={snap.AllItems.Count}  Selected={snap.SelectedItems.Count}  " +
                          $"Active={snap.ActiveItems.Count}  ActiveVisible={snap.ActiveVisibleItems.Count}");
            sb.AppendLine();
            sb.AppendLine("--- All Items ---");
            foreach (var item in snap.AllItems)
            {
                string sel = item.IsSelected ? "[SEL]" : "     ";
                string hid = item.IsHidden ? "[HID]" : "     ";
                string lck = item.IsLocked ? "[LCK]" : "     ";
                sb.AppendLine($"{sel}{hid}{lck} {item}");
            }
            return sb.ToString();
        }

        /// <summary>アイテムの全プロパティを辞書で返す（デバッグ用）。</summary>
        public static Dictionary<string, object?> GetItemAllProperties(object item)
            => Cache.SnapshotAllProperties(item);
    }
}