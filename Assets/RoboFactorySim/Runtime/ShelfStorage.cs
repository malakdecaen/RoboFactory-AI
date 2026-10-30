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
            int count = columns != null ? columns.Length : 0;
            slots = new CargoBox[count][];
            for (int c = 0; c < count; c++)
            {
                Transform[] levels = columns[c] != null && columns[c].levels != null ? columns[c].levels : new Transform[0];
                slots[c] = new CargoBox[levels.Length];
                for (int l = 0; l < levels.Length; l++)
                {
                    if (levels[l] == null) continue;
                    CargoBox found = levels[l].GetComponentInChildren<CargoBox>(true);
                    if (found == null) continue;
                    found.isCarried = false;
                    slots[c][l] = found;
                }
            }
        }

        private bool Valid(int column, int level)
        {
            if (slots == null) Rescan();
            return column >= 0 && column < slots.Length && level >= 0 && level < slots[column].Length;
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
