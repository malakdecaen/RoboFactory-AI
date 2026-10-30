using System.Collections.Generic;
using System.Text;
using UnityEngine;
using RoboFactory.Sim;

namespace RoboFactory.Logistics
{
    /// <summary>
    /// Read-only cargo conservation audit of a scenario (no robot or order is touched).
    /// Every box present at the start is followed: it must always be on a pallet, a bench, carried by
    /// a robot or in a truck. A box may disappear only after it was in a truck (shipping). A box that
    /// vanishes from anywhere else, stays on the floor, or any new box that appears, is reported.
    /// </summary>
    public sealed class CargoAudit
    {
        private sealed class Track
        {
            public string Name;
            public CargoStatus Last = CargoStatus.Unknown;
            public bool Gone, Shipped, FlaggedLost;
            public float LooseSince = -1f;
        }

        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private readonly HashSet<int> duplicates = new HashSet<int>();
        private readonly List<string> issues = new List<string>();
        private float nextCheck;

        public int Initial { get; private set; }
        public int Shipped { get; private set; }
        public int Lost { get; private set; }
        public int Duplicated => duplicates.Count;
        public bool Started { get; private set; }

        private const float LooseLimit = 8f;     // seconds on the floor before a box counts as lost
        private const float FloorY = -0.6f;      // below this height: fell out of the world

        public void Begin()
        {
            tracks.Clear(); duplicates.Clear(); issues.Clear();
            Shipped = Lost = 0;
            foreach (CargoBox box in Object.FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null) continue;
                tracks[box.GetInstanceID()] = new Track { Name = box.name, Last = CargoRegistry.Classify(box) };
            }
            Initial = tracks.Count;
            Started = true;
            nextCheck = 0f;
        }

        public void Tick()
        {
            if (!Started || Time.time < nextCheck) return;
            nextCheck = Time.time + .2f;
            var present = new HashSet<int>();
            foreach (CargoBox box in Object.FindObjectsByType<CargoBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (box == null) continue;
                int id = box.GetInstanceID();
                present.Add(id);
                if (!tracks.TryGetValue(id, out Track t))
                {
                    if (duplicates.Add(id)) issues.Add("duplicated box: " + box.name);
                    continue;
                }
                CargoStatus now = CargoRegistry.Classify(box);
                t.Last = now;
                bool onFloor = now == CargoStatus.Loose || box.transform.position.y < FloorY;
                if (onFloor)
                {
                    if (t.LooseSince < 0f) t.LooseSince = Time.time;
                    if (!t.FlaggedLost && (Time.time - t.LooseSince > LooseLimit || box.transform.position.y < FloorY))
                    {
                        t.FlaggedLost = true; Lost++;
                        issues.Add("lost box (on the floor): " + t.Name);
                    }
                }
                else t.LooseSince = -1f;
            }
            foreach (KeyValuePair<int, Track> kv in tracks)
            {
                Track t = kv.Value;
                if (t.Gone || present.Contains(kv.Key)) continue;
                t.Gone = true;
                if (t.Last == CargoStatus.InTruck) { t.Shipped = true; Shipped++; }
                else if (!t.FlaggedLost) { t.FlaggedLost = true; Lost++; issues.Add("lost box (disappeared from " + CargoStatusNames.Of(t.Last) + "): " + t.Name); }
            }
        }

        /// <summary>Boxes still in the scene and accounted for (pallet, bench, robot or truck).</summary>
        public int Accounted
        {
            get
            {
                int n = 0;
                foreach (Track t in tracks.Values) if (!t.Gone && !t.FlaggedLost) n++;
                return n;
            }
        }

        /// <summary>Boxes delivered: already shipped (destroyed after loading) + still sitting in a truck.</summary>
        public int Delivered
        {
            get
            {
                int n = Shipped;
                foreach (Track t in tracks.Values) if (!t.Gone && t.Last == CargoStatus.InTruck) n++;
                return n;
            }
        }

        public bool Passed => Started && Lost == 0 && Duplicated == 0;

        public string Summary(int expectedShipped)
        {
            var sb = new StringBuilder();
            sb.Append(Initial).Append(" boxes tracked  ·  ").Append(Delivered).Append(" delivered");
            if (expectedShipped >= 0) sb.Append(" (").Append(expectedShipped).Append(" ordered)");
            sb.Append("  ·  ").Append(Lost).Append(" lost  ·  ").Append(Duplicated).Append(" duplicated");
            if (expectedShipped >= 0 && Delivered != expectedShipped) sb.Append("  ·  DELIVERED COUNT DIFFERS FROM ORDERED");
            return sb.ToString();
        }

        public IReadOnlyList<string> Issues => issues;
    }
}
