using System.Collections.Generic;

namespace NuclearReplay.Playback
{
    // A recorded toggle after the last replacement must survive deferred cleanup at the end of the burst.
    // A replacement without a later toggle keeps the game's own resulting state.
    internal sealed class JumpRadarState<TUnit> where TUnit : class
    {
        private readonly Dictionary<TUnit, bool> toggles = new Dictionary<TUnit, bool>();

        public void Replaced(TUnit unit) => toggles.Remove(unit);
        public void RecordToggle(TUnit unit, bool active) => toggles[unit] = active;
        public bool Restore(TUnit unit, bool current) => toggles.TryGetValue(unit, out bool active) ? active : current;
        public void Clear() => toggles.Clear();
    }
}
