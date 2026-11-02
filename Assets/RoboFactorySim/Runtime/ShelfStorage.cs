using System;
using UnityEngine;

namespace RoboFactory.Sim
{
    /// <summary>
    /// Storage locations (pallet, bench, truck floor): columns of levels,
    /// each level being a point where a box is placed. Simplified public version.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShelfStorage : MonoBehaviour
    {
        [Serializable]
        public class StorageColumn
        {
            public Transform[] levels = new Transform[0];
        }

        public StorageColumn[] columns = new StorageColumn[0];

        private CargoBox[][] slots;

        private void Awake() { Rescan(); }

        /// <summary>Re-reads the boxes already placed under each level point.</summary>
        public void Rescan()
        {
            InitializeStorage();
            AutoFindExistingBoxes();
        }

        // The two steps below keep these exact names: the RoboFactory scripts (TruckBay when it generates
        // the truck levels, the autopilot when it starts) call them to rebuild the table after changing
        // the columns, as they do on the full project's storage.

        /// <summary>Empty table sized on the current columns.</summary>
        private void InitializeStorage()
        {
            int count = columns != null ? columns.Length : 0;
            slots = new CargoBox[count][];
            for (int c = 0; c < count; c++)
            {
                Transform[] levels = columns[c] != null && columns[c].levels != null ? columns[c].levels : new Transform[0];
                slots[c] = new CargoBox[levels.Length];
            }
        }

        /// <summary>Fills the table with the (active) boxes already placed under each level point.</summary>
        private void AutoFindExistingBoxes()
        {
            if (slots == null) InitializeStorage();
            for (int c = 0; c < slots.Length; c++)
            {
                Transform[] levels = columns[c] != null && columns[c].levels != null ? columns[c].levels : new Transform[0];
                for (int l = 0; l < levels.Length && l < slots[c].Length; l++)
                {
                    if (levels[l] == null) continue;
                    CargoBox found = levels[l].GetComponentInChildren<CargoBox>();
                    if (found == null) continue;
                    found.isCarried = false;
                    slots[c][l] = found;
                }
            }
        }

        private bool Valid(int column, int level)
        {
            if (slots == null) Rescan();
            return column >= 0 && column < slots.Length && level >= 0 && level < slots[column].Length &&
                   columns != null && column < columns.Length && columns[column] != null &&
                   columns[column].levels != null && level < columns[column].levels.Length;
        }

        public Transform GetPoint(int column, int level)
        {
            return Valid(column, level) ? columns[column].levels[level] : null;
        }

        public CargoBox GetBox(int column, int level)
        {
            return Valid(column, level) ? slots[column][level] : null;
        }

        /// <summary>The highest box of the column (the one that can be taken).</summary>
        public bool HasBoxToTake(int column, out int level, out CargoBox box)
        {
            level = -1;
            box = null;
            if (!Valid(column, 0)) return false;
            for (int l = slots[column].Length - 1; l >= 0; l--)
            {
                if (slots[column][l] == null) continue;
                level = l;
                box = slots[column][l];
                return true;
            }
            return false;
        }

        /// <summary>The lowest free level of the column.</summary>
        public bool HasFreePlaceToPut(int column, out int level)
        {
            level = -1;
            if (!Valid(column, 0)) return false;
            for (int l = 0; l < slots[column].Length; l++)
            {
                if (slots[column][l] != null) continue;
                level = l;
                return true;
            }
            return false;
        }

        public void RemoveBox(int column, int level)
        {
            if (Valid(column, level)) slots[column][level] = null;
        }

        public void PutBox(int column, int level, CargoBox box)
        {
            if (box != null) PutBox(column, level, box, box.transform.rotation);
        }

        public void PutBox(int column, int level, CargoBox box, Quaternion worldRotation)
        {
            if (box == null || !Valid(column, level)) return;
            Transform point = columns[column].levels[level];
            if (point == null) return;
            slots[column][level] = box;
            box.transform.SetParent(point, true);
            box.transform.SetPositionAndRotation(point.position, worldRotation);
            box.isCarried = false;
        }
    }
}
