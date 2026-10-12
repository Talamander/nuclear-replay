using System.Collections.Generic;

namespace NuclearReplay
{
    // This ledger uses plugin IDs, never whichever target is currently selected.
    public sealed class Correlation
    {
        public sealed class Launch
        {
            public string ProjectileId { get; }
            public string EventId { get; }
            public string OwnerId { get; }
            public string AssignedTargetId { get; }
            public Launch(string projectile, string eventId, string owner, string target)
            { ProjectileId = projectile; EventId = eventId; OwnerId = owner; AssignedTargetId = target; }
        }
        private readonly Dictionary<string, Launch> launches = new Dictionary<string, Launch>();
        private readonly Dictionary<string, string> observations = new Dictionary<string, string>();
        private readonly Dictionary<uint, HashSet<string>> activeGameIds = new Dictionary<uint, HashSet<string>>();
        private readonly Dictionary<uint, string> firstGameIds = new Dictionary<uint, string>();
        private readonly HashSet<uint> ambiguousGameIds = new HashSet<uint>();
        // Observation is independent of launch proof. Reused or ambiguous raw IDs must not guess a projectile.
        public bool Observe(string projectile, string eventId, uint gameId)
        {
            if (observations.ContainsKey(projectile)) return false;
            observations.Add(projectile, eventId);
            if (gameId != 0)
            {
                if (firstGameIds.TryGetValue(gameId, out var first) && first != projectile) ambiguousGameIds.Add(gameId);
                else firstGameIds[gameId] = projectile;
                if (!activeGameIds.TryGetValue(gameId, out var ids)) activeGameIds.Add(gameId, ids = new HashSet<string>());
                ids.Add(projectile);
            }
            return true;
        }
        public string Observation(string projectile) => projectile != null && observations.TryGetValue(projectile, out var id) ? id : null;
        public string Resolve(uint gameId)
        {
            if (ambiguousGameIds.Contains(gameId) || !activeGameIds.TryGetValue(gameId, out var ids) || ids.Count != 1) return null;
            foreach (var id in ids) return id;
            return null;
        }
        public void Retire(string projectile, uint gameId)
        {
            if (!activeGameIds.TryGetValue(gameId, out var ids)) return;
            ids.Remove(projectile);
            if (ids.Count == 0) activeGameIds.Remove(gameId);
        }
        public bool Add(Launch launch)
        {
            if (launches.ContainsKey(launch.ProjectileId)) return false;
            launches.Add(launch.ProjectileId, launch); return true;
        }
        public Launch Find(string projectile) => projectile != null && launches.TryGetValue(projectile, out var launch) ? launch : null;
        public void Clear() { launches.Clear(); observations.Clear(); activeGameIds.Clear(); firstGameIds.Clear(); ambiguousGameIds.Clear(); }
    }
}
