#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace NuclearReplay.Playback
{
    // Selection rules of the Replays list, without Unity (the tests link this file). Rows are keyed by recording
    // path, so checks survive refreshes and deletes; indexes are positions in the list as shown (newest first).
    //
    //   Current: the highlighted row (Watch, double-click, single Delete). A plain click moves it.
    //   Checked: the rows ticked for a bulk delete.
    //   Anchor:  where a Shift range starts: the last row clicked or ticked.
    //
    //   Row click            current = row, anchor = row (checks stay as they are)
    //   Ctrl+row click       tick or untick the row, current = row, anchor = row
    //   Shift+row click      tick every row from the anchor to this one (adds to the ticks), current = row
    //   Checkbox click       tick or untick the row, anchor = row
    //   Shift+checkbox       give every row from the anchor to this one the row's new state (tick or untick a range)
    internal sealed class ListSelection
    {
        public enum Ticks { None, Some, All }

        private readonly List<string> keys = new List<string>();
        private readonly Dictionary<string, int> positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Current { get; private set; }
        public string Anchor { get; private set; }
        public int Count => keys.Count;
        public int CheckedCount => ticked.Count;
        // Bumped on every change, so the list redraws only when something changed.
        public int Version { get; private set; }

        public Ticks State => ticked.Count == 0 ? Ticks.None : ticked.Count >= keys.Count ? Ticks.All : Ticks.Some;

        // New list contents (a refresh or a delete). Ticks, the current row and the anchor of recordings that are
        // gone are dropped; the rest keep their state.
        public void SetKeys(IEnumerable<string> list)
        {
            keys.Clear(); positions.Clear();
            foreach (var k in list)
            {
                if (k == null || positions.ContainsKey(k)) continue;
                positions[k] = keys.Count;
                keys.Add(k);
            }
            ticked.RemoveWhere(k => !positions.ContainsKey(k));
            if (Current != null && !positions.ContainsKey(Current)) Current = null;
            if (Anchor != null && !positions.ContainsKey(Anchor)) Anchor = null;
            Version++;
        }

        public int IndexOf(string key) => key != null && positions.TryGetValue(key, out var i) ? i : -1;
        public string KeyAt(int index) => index >= 0 && index < keys.Count ? keys[index] : null;
        public bool IsChecked(int index) => index >= 0 && index < keys.Count && ticked.Contains(keys[index]);
        public bool IsChecked(string key) => key != null && ticked.Contains(key);
        public int CurrentIndex => IndexOf(Current);

        // The ticked keys in list order.
        public List<string> CheckedKeys() => keys.Where(k => ticked.Contains(k)).ToList();

        public void SetCurrent(int index)
        {
            if (index < 0 || index >= keys.Count) { Current = null; Version++; return; }
            Current = keys[index];
            Anchor = Current;
            Version++;
        }

        // A click on a row (not on its checkbox).
        public void Click(int index, bool shift, bool ctrl)
        {
            if (index < 0 || index >= keys.Count) return;
            if (shift)
            {
                int from = IndexOf(Anchor);
                if (from < 0) from = CurrentIndex >= 0 ? CurrentIndex : index;
                SetRange(from, index, true);
                Current = keys[index];
                if (Anchor == null) Anchor = keys[from];
            }
            else
            {
                if (ctrl) Toggle(keys[index]);
                Current = keys[index];
                Anchor = Current;
            }
            Version++;
        }

        // A click on a row's checkbox.
        public void ToggleBox(int index, bool shift)
        {
            if (index < 0 || index >= keys.Count) return;
            string key = keys[index];
            bool on = !ticked.Contains(key);
            int from = IndexOf(Anchor);
            if (shift && from >= 0) SetRange(from, index, on);
            else Set(key, on);
            Anchor = key;
            Version++;
        }

        public void SelectAll()
        {
            foreach (var k in keys) ticked.Add(k);
            Version++;
        }

        public void Clear()
        {
            ticked.Clear();
            Version++;
        }

        // The header checkbox: ticks every row unless all are ticked, then clears.
        public void ToggleAll()
        {
            if (State == Ticks.All) Clear();
            else SelectAll();
        }

        private void SetRange(int a, int b, bool on)
        {
            int lo = Math.Max(0, Math.Min(a, b)), hi = Math.Min(keys.Count - 1, Math.Max(a, b));
            for (int i = lo; i <= hi; i++) Set(keys[i], on);
        }

        private void Set(string key, bool on)
        {
            if (on) ticked.Add(key);
            else ticked.Remove(key);
        }

        private void Toggle(string key) => Set(key, !ticked.Contains(key));
    }
}
