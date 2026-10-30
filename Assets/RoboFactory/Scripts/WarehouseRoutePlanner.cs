using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoboFactory.Hackathon
{
    public sealed class WarehouseRoutePlanner
    {
        public float CellSize = 0.25f;
        public Vector2 Min = new Vector2(1f, -5f);
        public Vector2 Max = new Vector2(9f, 5f);

        private static readonly Vector2Int[] Steps = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

        public List<Vector3> Plan(Vector3 from, Vector3 to, Func<Vector3, bool> isFree)
        {
            var route = new List<Vector3>();
            Vector2Int start = ToCell(from);
            Vector2Int goal = ToCell(to);
            if (start == goal)
            {
                route.Add(new Vector3(to.x, from.y, to.z));
                return route;
            }
            if (!isFree(ToWorld(goal, from.y))) return route;

            var previous = new Dictionary<Vector2Int, Vector2Int> { [start] = start };
            var open = new Queue<Vector2Int>();
            open.Enqueue(start);
            while (open.Count > 0 && !previous.ContainsKey(goal))
            {
                Vector2Int node = open.Dequeue();
                foreach (Vector2Int step in Steps)
                {
                    Vector2Int next = node + step;
                    if (previous.ContainsKey(next) || !Inside(next, from.y)) continue;
                    Vector3 a = ToWorld(node, from.y);
                    Vector3 b = ToWorld(next, from.y);
                    if (!isFree(b) || !isFree((a + b) * .5f)) continue;
                    previous[next] = node;
                    open.Enqueue(next);
                }
            }
            if (!previous.ContainsKey(goal)) return route;

            for (Vector2Int cell = goal; cell != start; cell = previous[cell]) route.Add(ToWorld(cell, from.y));
            route.Reverse();
            route[route.Count - 1] = new Vector3(to.x, from.y, to.z);
            return route;
        }

        private Vector2Int ToCell(Vector3 position)
        {
            return new Vector2Int(Mathf.RoundToInt(position.x / CellSize), Mathf.RoundToInt(position.z / CellSize));
        }

        private Vector3 ToWorld(Vector2Int cell, float y)
        {
            return new Vector3(cell.x * CellSize, y, cell.y * CellSize);
        }

        private bool Inside(Vector2Int cell, float y)
        {
            Vector3 world = ToWorld(cell, y);
            return world.x >= Min.x - 1e-4f && world.x <= Max.x + 1e-4f && world.z >= Min.y - 1e-4f && world.z <= Max.y + 1e-4f;
        }
    }
}
