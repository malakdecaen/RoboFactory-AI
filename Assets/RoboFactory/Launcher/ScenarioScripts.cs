using System.Collections.Generic;

namespace RoboFactory.Launcher
{
    /// <summary>One scripted input: a normal order or a spoken urgent order (as text).</summary>
    public sealed class ScriptInput
    {
        public double T;                 // seconds since the scenario start
        public bool Urgent;
        public int A, B, C;              // normal order: quantities per type
        public string Transcript = "";   // urgent order: what the operator says
    }

    public sealed class ScenarioScript
    {
        public string Id, Title, Summary;
        public List<ScriptInput> Inputs = new List<ScriptInput>();
    }

    /// <summary>
    /// The three demo scenarios. Only the INPUTS (the operator's orders) are scripted here: the Meta
    /// decisions always come from real Meta calls, recorded in LIVE AI and replayed in RECORDED DEMO.
    /// A recording stores the inputs that were really sent, so editing a script never changes a recording.
    /// </summary>
    public static class ScenarioScripts
    {
        public static readonly ScenarioScript[] All =
        {
            new ScenarioScript
            {
                Id = "normal",
                Title = "NORMAL ORDERS",
                Summary = "Three ordinary orders: Muse picks the pallets, bench and truck of each one.",
                Inputs =
                {
                    new ScriptInput { T = 1,  A = 2, B = 1 },
                    new ScriptInput { T = 12, C = 2 },
                    new ScriptInput { T = 25, A = 1, B = 2 }
                }
            },
            new ScenarioScript
            {
                Id = "urgent",
                Title = "URGENT INTERRUPTION",
                Summary = "A spoken urgent order suspends the running orders, runs first, then the others resume.",
                Inputs =
                {
                    new ScriptInput { T = 1,  A = 2, B = 1 },
                    new ScriptInput { T = 8,  C = 2 },
                    new ScriptInput { T = 15, A = 1, B = 2 },
                    new ScriptInput { T = 40, Urgent = true, Transcript = "Urgent order! Two B boxes and one A box, right now." }
                }
            },
            new ScenarioScript
            {
                Id = "saturation",
                Title = "FULL SATURATION",
                Summary = "5 orders fill the 3 benches and 3 trucks, then 1 urgent order: Muse frees a bay (FINISH TO FREE).",
                Inputs =
                {
                    new ScriptInput { T = 1,  A = 1, B = 1 },
                    new ScriptInput { T = 4,  B = 2 },
                    new ScriptInput { T = 7,  C = 2 },
                    new ScriptInput { T = 10, A = 2 },
                    new ScriptInput { T = 13, B = 1, C = 1 },
                    new ScriptInput { T = 45, Urgent = true, Transcript = "This is urgent! One A box and two C boxes, as fast as possible." }
                }
            }
        };

        public static ScenarioScript Find(string id)
        {
            foreach (ScenarioScript s in All) if (s.Id == id) return s;
            return null;
        }
    }
}
