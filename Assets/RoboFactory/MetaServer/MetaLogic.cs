using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Faithful translation of the pure functions of meta_proxy.py: reading the model's
    /// answer, shape validations, deterministic mock modes. No Unity dependency.
    /// </summary>
    internal static class MetaLogic
    {
        // ------------------------------------------------------------------ model output

        /// <summary>extract_output_text: text of the SSE events, otherwise every output_text of the JSON.</summary>
        public static string ExtractOutputText(string raw)
        {
            raw = raw ?? "";
            var parts = new List<string>();
            foreach (string rawLine in SplitLines(raw))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("data:")) continue;
                string chunk = line.Substring(5).Trim();
                if (chunk.Length == 0 || chunk == "[DONE]") continue;
                JToken ev;
                try { ev = Py.Parse(chunk); }
                catch (Exception) { continue; }
                if (ev is JObject eo && Py.Get(eo, "delta") is JToken delta && delta.Type == JTokenType.String &&
                    Py.Str(Py.Get(eo, "type") ?? "").Contains("output_text"))
                    parts.Add(delta.Value<string>());
            }
            if (parts.Count > 0) return string.Concat(parts);
            JToken payload;
            try { payload = Py.Parse(raw); }
            catch (Exception) { return raw; }
            var stack = new List<JToken> { payload };
            while (stack.Count > 0)
            {
                JToken node = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (node is JObject o)
                {
                    JToken type = Py.Get(o, "type");
                    JToken text = Py.Get(o, "text");
                    if (type != null && type.Type == JTokenType.String && type.Value<string>() == "output_text" &&
                        text != null && text.Type == JTokenType.String)
                        parts.Add(text.Value<string>());
                    foreach (JProperty p in o.Properties()) stack.Add(p.Value);
                }
                else if (node is JArray a)
                    foreach (JToken item in a) stack.Add(item);
            }
            return string.Concat(parts);
        }

        /// <summary>Python's str.splitlines().</summary>
        private static IEnumerable<string> SplitLines(string s)
        {
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool br = c == '\n' || c == '\r' || c == '\v' || c == '\f' || c == '\x1c' || c == '\x1d' ||
                          c == '\x1e' || c == '\x85' || c == '\u2028' || c == '\u2029';
                if (!br) continue;
                yield return s.Substring(start, i - start);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (start < s.Length) yield return s.Substring(start);
        }

        /// <summary>first_json_object: from the first « { » to the last « } ».</summary>
        public static JObject FirstJsonObject(string text)
        {
            text = text ?? "";
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return Py.Parse(text.Substring(start, end - start + 1)) as JObject; }
            catch (Exception) { return null; }
        }

        /// <summary>Python's « if parsed »: None or empty {} = no JSON.</summary>
        public static bool HasJson(JObject parsed) => parsed != null && parsed.Count > 0;

        // ------------------------------------------------------------------ validations

        public static JArray ValidateDetections(JObject payload, out string error)
        {
            error = null;
            JToken dets = Py.Get(payload, "detections");
            if (payload == null || !(dets is JArray arr)) { error = "missing detections array"; return null; }
            var clean = new JArray();
            foreach (JToken t in arr)
            {
                if (!(t is JObject item)) continue;
                string label = Py.Strip(Py.GetStr(item, "label", "unknown")).ToLowerInvariant();
                if (!MetaPrompts.SUITS.Contains(label)) label = "unknown";
                if (!(Py.Get(item, "box") is JArray box) || box.Count != 4) continue;
                var vals = new double[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                {
                    if (!Py.TryFloat(box[i], out double v)) { ok = false; break; }
                    vals[i] = Math.Min(1.0, Math.Max(0.0, v));
                }
                if (!ok) continue;
                if (vals[2] <= vals[0] || vals[3] <= vals[1]) continue;
                JToken conf = Py.Get(item, "confidence");
                JToken confidence = Py.TryFloat(conf, out double c)
                    ? new JValue(Math.Min(1.0, Math.Max(0.0, c)))
                    : JValue.CreateNull();
                JToken track = Py.Get(item, "track_id");
                clean.Add(new JObject
                {
                    ["label"] = label,
                    ["box"] = new JArray(vals.Select(v => (object)v).ToArray()),
                    ["confidence"] = confidence,
                    ["track_id"] = Py.IsNone(track) ? JValue.CreateNull() : new JValue(Py.Str(track))
                });
            }
            return clean;
        }

        public static JObject ValidateOrder(JObject payload, out string error)
        {
            error = null;
            if (payload == null || payload.ContainsKey("error"))
            {
                error = payload != null ? Py.GetStr(payload, "error", "invalid order") : "invalid order";
                return null;
            }
            var counts = new JObject();
            long total = 0;
            foreach (string suit in MetaPrompts.SUITS)
            {
                JToken raw = Py.Get(payload, suit) ?? new JValue(0);
                if (!Py.TryInt(raw, out long value)) { error = "non integer count for " + suit; return null; }
                if (value < 0) { error = "negative count for " + suit; return null; }
                counts[suit] = value;
                total += value;
            }
            if (total < 1 || total > 6) { error = "total must be 1..6, got " + total; return null; }
            return counts;
        }

        public static JObject ValidateSupervision(JObject payload, out string error)
        {
            error = null;
            if (payload == null) { error = "decision must be a json object"; return null; }
            JToken action = Py.Get(payload, "action");
            if (action == null || action.Type != JTokenType.String || Py.Strip(action.Value<string>()).Length == 0)
            {
                error = "action is required";
                return null;
            }
            if (!Py.TryIntOr0(Py.Get(payload, "order_id"), out long orderId)) orderId = 0;
            if (!Py.TryIntOr0(Py.Get(payload, "priority"), out long priority)) priority = 0;
            JToken obs = Py.Get(payload, "observation_id") ?? new JValue("");
            if (obs.Type == JTokenType.Null) obs = new JValue("");
            JToken reason = Py.Get(payload, "reason") ?? new JValue("");
            if (reason.Type == JTokenType.Null) reason = new JValue("");
            return new JObject
            {
                ["action"] = Py.Strip(action.Value<string>()),
                ["order_id"] = orderId,
                ["priority"] = priority,
                ["observation_id"] = Py.Strip(Py.Str(obs)),
                ["reason"] = Py.Cut(Py.Strip(Py.Str(reason)), 400)
            };
        }

        public static JObject ValidateWarehousePlan(JObject payload, out string error)
        {
            error = null;
            if (payload == null) { error = "plan must be a json object"; return null; }
            JToken actionTok = Py.Get(payload, "action");
            if (actionTok == null || actionTok.Type != JTokenType.String ||
                !MetaPrompts.WAREHOUSE_ACTIONS.Contains(Py.Strip(actionTok.Value<string>())))
            {
                error = "action must be one of " + string.Join(",", MetaPrompts.WAREHOUSE_ACTIONS);
                return null;
            }
            string action = Py.Strip(actionTok.Value<string>());
            if (!Py.TryIntOr0(Py.Get(payload, "order_id"), out long orderId)) orderId = 0;
            var picks = new JArray();
            JToken rawPicks = Py.Get(payload, "picks");
            if (Py.IsNone(rawPicks)) rawPicks = new JArray();
            if (!(rawPicks is JArray pickArr)) { error = "picks must be a list"; return null; }
            for (int i = 0; i < pickArr.Count; i++)
            {
                if (!(pickArr[i] is JObject leg)) { error = "picks[" + i + "] must be an object"; return null; }
                string palletId = Py.Strip(Py.StrOr(Py.Get(leg, "pallet_id")));
                string boxType = Py.Strip(Py.StrOr(Py.Get(leg, "type"))).ToUpperInvariant();
                if (!Py.TryIntOr0(Py.Get(leg, "count"), out long count)) { error = "picks[" + i + "].count is not an integer"; return null; }
                if (palletId.Length == 0) { error = "picks[" + i + "].pallet_id is required"; return null; }
                if (!MetaPrompts.WAREHOUSE_TYPES.Contains(boxType)) { error = "picks[" + i + "].type must be A, B or C"; return null; }
                if (count <= 0) { error = "picks[" + i + "].count must be > 0"; return null; }
                picks.Add(new JObject { ["pallet_id"] = palletId, ["type"] = boxType, ["count"] = count });
            }
            JToken fb = Py.Get(payload, "fallback_pallets");
            if (Py.IsNone(fb)) fb = new JArray();
            if (!(fb is JArray fbArr)) { error = "fallback_pallets must be a list"; return null; }
            var fallback = new JArray();
            foreach (JToken p in fbArr)
                if (Py.Strip(Py.StrOr(p)).Length > 0) fallback.Add(Py.Strip(Py.Str(p)));
            if (action == "execute_order")
            {
                if (orderId <= 0) { error = "execute_order requires a valid order_id"; return null; }
                if (picks.Count == 0) { error = "execute_order requires at least one pick"; return null; }
                foreach (string field in new[] { "bench_id", "truck_id" })
                    if (Py.Strip(Py.StrOr(Py.Get(payload, field))).Length == 0)
                    {
                        error = "execute_order requires " + field;
                        return null;
                    }
            }
            if (!Py.TryFloatOr0(Py.Get(payload, "start_delay_s"), out double startDelay)) startDelay = 0.0;
            JToken startImm = Py.Get(payload, "start_immediately");
            return new JObject
            {
                ["action"] = action,
                ["order_id"] = orderId,
                ["picks"] = picks,
                ["fallback_pallets"] = fallback,
                ["bench_id"] = Py.Strip(Py.StrOr(Py.Get(payload, "bench_id"))),
                ["truck_id"] = Py.Strip(Py.StrOr(Py.Get(payload, "truck_id"))),
                ["robot_pick"] = Py.Strip(Py.StrOr(Py.Get(payload, "robot_pick") ?? new JValue("R1"), "R1")),
                ["robot_load"] = Py.Strip(Py.StrOr(Py.Get(payload, "robot_load") ?? new JValue("R2"), "R2")),
                ["start_immediately"] = startImm == null || Py.Truthy(startImm),
                ["start_delay_s"] = startDelay,
                ["reason"] = Py.Cut(Py.StrOr(Py.Get(payload, "reason")), 400)
            };
        }

        public static JObject ValidateCapacity(JObject payload, JObject ctx, out string error)
        {
            error = null;
            if (payload == null) { error = "capacity answer is not a json object"; return null; }
            string action = Py.Strip(Py.StrOr(Py.Get(payload, "action"))).ToLowerInvariant();
            string reason = Py.Cut(Py.StrOr(Py.Get(payload, "reason")), 300);
            if (action == "wait")
                return new JObject
                {
                    ["action"] = "wait", ["order_id"] = 0, ["bench_id"] = "", ["truck_id"] = "",
                    ["expected_free_s"] = 0.0, ["reason"] = reason
                };
            if (action != "finish_order") { error = "unknown capacity action: " + action; return null; }
            if (!Py.TryIntOr0(Py.Get(payload, "order_id"), out long orderId)) { error = "order_id is not an integer"; return null; }
            var candidates = new Dictionary<long, JObject>();
            if (Py.Truthy(Py.Get(ctx, "candidates")) && Py.Get(ctx, "candidates") is JArray cands)
                foreach (JToken c in cands)
                    if (c is JObject co && Py.TryInt(Py.Get(co, "order_id") ?? new JValue(-1), out long cid))
                        candidates[cid] = co;
            if (!candidates.TryGetValue(orderId, out JObject cand))
            {
                error = "order #" + orderId + " is not a FINISH TO FREE candidate";
                return null;
            }
            if (!Py.Truthy(Py.Get(cand, "can_finish"))) { error = "order #" + orderId + " cannot finish (stock)"; return null; }
            JToken expected = Py.Get(payload, "expected_free_s") ?? Py.Get(cand, "eta_free_s") ?? new JValue(0);
            if (!Py.TryFloatOr0(expected, out double eta))
                Py.TryFloatOr0(Py.Get(cand, "eta_free_s"), out eta);
            JToken pb = Py.Get(payload, "bench_id");
            JToken pt = Py.Get(payload, "truck_id");
            return new JObject
            {
                ["action"] = "finish_order",
                ["order_id"] = orderId,
                ["bench_id"] = Py.Truthy(pb) ? Py.Str(pb) : Py.GetStr(cand, "bench_id"),
                ["truck_id"] = Py.Truthy(pt) ? Py.Str(pt) : Py.GetStr(cand, "truck_id"),
                ["expected_free_s"] = eta,
                ["reason"] = reason
            };
        }

        public static JObject ValidateUrgent(JObject payload, long urgentOrderId, out string error)
        {
            error = null;
            if (payload == null) { error = "response must be a json object"; return null; }
            string action = Py.Strip(Py.StrOr(Py.Get(payload, "action")));
            if (!MetaPrompts.URGENT_ACTIONS.Contains(action))
            {
                error = "action must be one of " + string.Join(",", MetaPrompts.URGENT_ACTIONS);
                return null;
            }
            JToken urgentTok = Py.Get(payload, "urgent");
            bool urgent = urgentTok == null ? action == "execute_order" : Py.Truthy(urgentTok);
            double confidence = Py.TryFloatOr0(Py.Get(payload, "confidence"), out double cf)
                ? Math.Min(1.0, Math.Max(0.0, cf))
                : 0.0;
            JObject rawOrder = Py.Get(payload, "order") as JObject ?? new JObject();
            var counts = new JObject();
            long sum = 0;
            foreach (string t in MetaPrompts.WAREHOUSE_TYPES)
            {
                if (!Py.TryIntOr0(Py.Get(rawOrder, t), out long v)) { error = "order." + t + " is not an integer"; return null; }
                if (v < 0) { error = "order." + t + " is negative"; return null; }
                counts[t] = v;
                sum += v;
            }
            var result = new JObject
            {
                ["action"] = action,
                ["urgent"] = urgent,
                ["order"] = counts,
                ["confidence"] = confidence,
                ["clarification"] = Py.Cut(Py.StrOr(Py.Get(payload, "clarification")), 400),
                ["reason"] = Py.Cut(Py.StrOr(Py.Get(payload, "reason")), 400),
                ["plan"] = JValue.CreateNull()
            };
            if (action == "execute_order")
            {
                if (!urgent) { error = "execute_order requires urgent=true"; return null; }
                if (sum <= 0) { error = "execute_order requires a non-empty order"; return null; }
                var planPayload = (JObject)payload.DeepClone();
                planPayload["order_id"] = urgentOrderId;
                planPayload["action"] = "execute_order";
                JObject plan = ValidateWarehousePlan(planPayload, out string planError);
                if (planError != null) { error = "plan: " + planError; return null; }
                result["plan"] = plan;
            }
            return result;
        }

        // ------------------------------------------------------------------ mock modes (offline tests)

        private static JArray ListOf(JObject state, string key) => Py.Get(state, key) as JArray ?? new JArray();

        private static long StockOf(JObject pallet, string boxType)
        {
            JObject stock = Py.Get(pallet, "stock") as JObject ?? new JObject();
            JToken v = Py.Get(stock, boxType.ToLowerInvariant()) ?? Py.Get(stock, boxType) ?? new JValue(0);
            return Py.TryIntOr0(v, out long have) ? have : 0;
        }

        private static long IntField(JObject o, string key)
        {
            return Py.TryIntOr0(Py.Get(o, key), out long v) ? v : 0;
        }

        private static JObject StateFrom(JToken stateJson)
        {
            if (stateJson == null || stateJson.Type != JTokenType.String) return new JObject();
            return Py.ParseObject(stateJson.Value<string>()) ?? new JObject();
        }

        public static JObject MockWarehousePlan(JToken stateJson, JObject body)
        {
            string forced = Py.StrOr(Py.Get(body, "mock_plan_error"));
            JObject state = StateFrom(stateJson);
            JArray orders = ListOf(state, "orders"), pallets = ListOf(state, "pallets"),
                   benches = ListOf(state, "benches"), trucks = ListOf(state, "trucks");
            JObject pending = orders.OfType<JObject>().FirstOrDefault(o =>
            {
                JToken st = Py.Get(o, "status");
                return st != null && st.Type == JTokenType.String && (st.Value<string>() == "pending" || st.Value<string>() == "waiting");
            });
            if (pending == null)
                return new JObject
                {
                    ["action"] = "no_plan", ["order_id"] = 0, ["picks"] = new JArray(), ["fallback_pallets"] = new JArray(),
                    ["bench_id"] = "", ["truck_id"] = "", ["robot_pick"] = "R1", ["robot_load"] = "R2",
                    ["start_immediately"] = true, ["start_delay_s"] = 0, ["reason"] = "mock: no pending order"
                };
            var needed = new Dictionary<string, long>
            {
                ["A"] = IntField(pending, "a") - IntField(pending, "staged_a") - IntField(pending, "loaded_a"),
                ["B"] = IntField(pending, "b") - IntField(pending, "staged_b") - IntField(pending, "loaded_b"),
                ["C"] = IntField(pending, "c") - IntField(pending, "staged_c") - IntField(pending, "loaded_c")
            };
            var picks = new JArray();
            var fallback = new JArray();
            var used = new HashSet<string>();
            foreach (string boxType in MetaPrompts.WAREHOUSE_TYPES)
            {
                long left = needed[boxType];
                if (left <= 0) continue;
                foreach (JObject pallet in pallets.OfType<JObject>())
                {
                    if (left <= 0) break;
                    long have = StockOf(pallet, boxType);
                    string pid = Py.GetStr(pallet, "id");
                    if (have <= 0 || used.Contains(pid) || Py.Truthy(Py.Get(pallet, "reserved_by"))) continue;
                    long take = Math.Min(have, left);
                    picks.Add(new JObject { ["pallet_id"] = pid, ["type"] = boxType, ["count"] = take });
                    used.Add(pid);
                    left -= take;
                }
            }
            foreach (JObject pallet in pallets.OfType<JObject>())
            {
                string pid = Py.GetStr(pallet, "id");
                if (pid.Length > 0 && !used.Contains(pid) && !Py.Truthy(Py.Get(pallet, "reserved_by"))) fallback.Add(pid);
            }
            JObject bench = benches.OfType<JObject>().FirstOrDefault(b => !Py.Truthy(Py.Get(b, "reserved_by")) && IntField(b, "free_levels") > 0);
            long total = Math.Max(0, needed.Values.Sum());
            JObject truck = trucks.OfType<JObject>().FirstOrDefault(t => !Py.Truthy(Py.Get(t, "reserved_by")) && IntField(t, "free") >= total) ??
                            trucks.OfType<JObject>().FirstOrDefault(t => !Py.Truthy(Py.Get(t, "reserved_by")) && IntField(t, "free") > 0);
            long orderId = IntField(pending, "id");
            if (forced == "bad_pallet" && picks.Count > 0) picks[0]["pallet_id"] = "P99";
            else if (forced == "bad_bench") bench = null;
            else if (forced == "bad_truck") truck = null;
            else if (forced == "too_many" && picks.Count > 0) picks[0]["count"] = picks[0]["count"].Value<long>() + 50;
            else if (forced == "bad_order") orderId = 999;
            string planBench = forced == "bad_bench" ? "B99" : bench == null ? "" : Py.GetStr(bench, "id");
            string planTruck = forced == "bad_truck" ? "T99" : truck == null ? "" : Py.GetStr(truck, "id");
            return new JObject
            {
                ["action"] = "execute_order", ["order_id"] = orderId,
                ["picks"] = picks, ["fallback_pallets"] = fallback,
                ["bench_id"] = planBench, ["truck_id"] = planTruck,
                ["robot_pick"] = "R1", ["robot_load"] = "R2",
                ["start_immediately"] = true, ["start_delay_s"] = 0,
                ["reason"] = "mock: deterministic plan"
            };
        }

        public static JObject MockSupervision(JToken stateJson, string trigger, JObject body)
        {
            JToken forced = Py.Get(body, "mock_action");
            JObject state = StateFrom(stateJson);
            JArray orders = ListOf(state, "orders"), unknowns = ListOf(state, "unknowns");
            trigger = (trigger ?? "").ToLowerInvariant();
            if (Py.Truthy(forced))
            {
                if (!Py.TryIntOr0(Py.Get(body, "mock_order_id"), out long oid)) oid = 0;
                if (!Py.TryIntOr0(Py.Get(body, "mock_priority"), out long pr)) pr = 0;
                return new JObject
                {
                    ["action"] = Py.Str(forced), ["order_id"] = oid, ["priority"] = pr,
                    ["observation_id"] = Py.StrOr(Py.Get(body, "mock_observation_id")), ["reason"] = "mock forced action"
                };
            }
            bool StatusIs(JObject o, string s)
            {
                JToken st = Py.Get(o, "status");
                return st != null && st.Type == JTokenType.String && st.Value<string>() == s;
            }
            long IdOf(JObject o) => Py.TryInt(Py.Get(o, "id") ?? new JValue(0), out long v) ? v : 0;
            if (trigger.Contains("urgent"))
            {
                JObject urgent = orders.OfType<JObject>().FirstOrDefault(o => Py.Truthy(Py.Get(o, "urgent_requested")) && StatusIs(o, "queued")) ??
                                 orders.OfType<JObject>().FirstOrDefault(o =>
                                 {
                                     JToken p = Py.Get(o, "priority");
                                     return p != null && (p.Type == JTokenType.Integer || p.Type == JTokenType.Float || p.Type == JTokenType.Boolean) &&
                                            Py.TryFloat(p, out double pv) && pv == 1.0 && StatusIs(o, "queued");
                                 });
                if (urgent != null)
                    return new JObject
                    {
                        ["action"] = "set_priority", ["order_id"] = IdOf(urgent), ["priority"] = 1, ["observation_id"] = "",
                        ["reason"] = "mock: urgent order prioritised without touching the active carton"
                    };
            }
            if (trigger.Contains("stock") || trigger.Contains("shortage"))
            {
                JObject target = orders.OfType<JObject>().FirstOrDefault(o => StatusIs(o, "waiting")) ??
                                 orders.OfType<JObject>().FirstOrDefault(o => StatusIs(o, "queued"));
                if (target != null)
                    return new JObject
                    {
                        ["action"] = "defer_order", ["order_id"] = IdOf(target), ["priority"] = 0, ["observation_id"] = "",
                        ["reason"] = "mock: requested suit has no stock, order deferred"
                    };
            }
            if (unknowns.Count > 0)
                return new JObject
                {
                    ["action"] = "request_inspection", ["order_id"] = 0, ["priority"] = 0,
                    ["observation_id"] = unknowns[0] is JObject u ? Py.GetStr(u, "id") : "",
                    ["reason"] = "mock: unknown object needs SAM 3.1 inspection"
                };
            return new JObject
            {
                ["action"] = "no_action", ["order_id"] = 0, ["priority"] = 0, ["observation_id"] = "",
                ["reason"] = "mock: factory state nominal"
            };
        }

        public static JObject MockCapacity(JObject ctx)
        {
            bool needBench = !Py.Truthy(Py.Get(ctx, "free_benches"));
            bool needTruck = !Py.Truthy(Py.Get(ctx, "empty_trucks"));
            long total = IntField(ctx, "urgent_total");
            JObject best = null;
            double Eta(JObject c)
            {
                JToken e = Py.Get(c, "eta_free_s") ?? new JValue(1e9);
                return Py.TryFloat(e, out double v) ? v : 1e9;
            }
            if (Py.Get(ctx, "candidates") is JArray cands && cands.Count > 0)
                foreach (JObject c in cands.OfType<JObject>())
                {
                    if (!Py.Truthy(Py.Get(c, "can_finish"))) continue;
                    if (needBench && !Py.Truthy(Py.Get(c, "frees_bench"))) continue;
                    if (needTruck && (!Py.Truthy(Py.Get(c, "frees_truck")) || IntField(c, "truck_capacity") < total)) continue;
                    if (best == null || Eta(c) < Eta(best)) best = c;
                }
            if (best == null)
                return new JObject
                {
                    ["action"] = "wait", ["order_id"] = 0, ["bench_id"] = "", ["truck_id"] = "", ["expected_free_s"] = 0.0,
                    ["reason"] = "mock: no candidate can free a bench and an empty truck"
                };
            JToken bestBench = Py.Get(best, "bench_id") ?? new JValue("");
            JToken bestTruck = Py.Get(best, "truck_id") ?? new JValue("");
            Py.TryFloat(Py.Get(best, "eta_free_s") ?? new JValue(0), out double eta);
            return new JObject
            {
                ["action"] = "finish_order",
                ["order_id"] = Py.TryInt(Py.Get(best, "order_id"), out long oid) ? oid : 0,
                ["bench_id"] = bestBench.DeepClone(),
                ["truck_id"] = bestTruck.DeepClone(),
                ["expected_free_s"] = eta,
                ["reason"] = "mock: shortest estimated time to free bench " + Py.Str(bestBench) + " and dock " + Py.Str(bestTruck)
            };
        }

        // ---- urgent order: transcription reader of the mock mode (French and English keywords)

        private static int WordValue(string w)
        {
            int i = Array.IndexOf(MetaPrompts.WordNumberKeys, w);
            return i >= 0 ? MetaPrompts.WordNumberValues[i] : 0;
        }

        private static long ToInt(string digits)
        {
            return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
        }

        public static Dictionary<string, long> ParseTranscriptCounts(string text)
        {
            string lowered = " " + (text ?? "").ToLowerInvariant() + " ";
            var counts = new Dictionary<string, long> { ["A"] = 0, ["B"] = 0, ["C"] = 0 };
            foreach (Match m in Regex.Matches(lowered, @"([abc])\s*(?:x|:)?\s*(\d+)"))
                counts[m.Groups[1].Value.ToUpperInvariant()] = ToInt(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(lowered, @"(\d+)\s*(?:x)?\s*([abc])\b"))
            {
                string k = m.Groups[2].Value.ToUpperInvariant();
                counts[k] = Math.Max(counts[k], ToInt(m.Groups[1].Value));
            }
            string words = string.Join("|", MetaPrompts.WordNumberKeys);
            foreach (Match m in Regex.Matches(lowered, @"(\d+|" + words + @")\s+(?:cartons?|caisses?|colis|box(?:es)?)" +
                                                       @"\s*(?:de\s+|of\s+|type\s+)?([abc])\b"))
            {
                string token = m.Groups[1].Value;
                long value = token.All(char.IsDigit) ? ToInt(token) : WordValue(token);
                string k = m.Groups[2].Value.ToUpperInvariant();
                counts[k] = Math.Max(counts[k], value);
            }
            var typed = new[] { ("a", "(?:caisse|crate)"), ("b", "(?:gros|big)"), ("c", "(?:moyen|medium)") };
            foreach ((string key, string word) in typed)
            {
                string k = key.ToUpperInvariant();
                Match m = Regex.Match(lowered, @"(\d+)\s+" + word);
                if (m.Success)
                {
                    counts[k] = Math.Max(counts[k], ToInt(m.Groups[1].Value));
                    continue;
                }
                m = Regex.Match(lowered, "(" + words + @")\s+" + word);
                if (m.Success) counts[k] = Math.Max(counts[k], WordValue(m.Groups[1].Value));
            }
            return counts;
        }

        private static JObject Counts(Dictionary<string, long> c) => new JObject { ["A"] = c["A"], ["B"] = c["B"], ["C"] = c["C"] };

        public static JObject MockUrgent(JObject body)
        {
            string transcript = Py.StrOr(Py.Get(body, "transcript"));
            if (!Py.TryIntOr0(Py.Get(body, "urgent_order_id"), out long orderId)) orderId = 0;
            string lowered = transcript.ToLowerInvariant();
            string[] urgentWords = { "urgent", "urgence", "priorit", "tout de suite", "immediat", "vite" };
            if (!urgentWords.Any(w => lowered.Contains(w)))
                return new JObject
                {
                    ["action"] = "no_plan", ["urgent"] = false, ["order"] = new JObject { ["A"] = 0, ["B"] = 0, ["C"] = 0 },
                    ["confidence"] = 0.9, ["clarification"] = "", ["plan"] = JValue.CreateNull(),
                    ["reason"] = "mock: not an urgent order"
                };
            Dictionary<string, long> counts = ParseTranscriptCounts(transcript);
            if (counts.Values.Sum() <= 0)
                return new JObject
                {
                    ["action"] = "clarify", ["urgent"] = true, ["order"] = new JObject { ["A"] = 0, ["B"] = 0, ["C"] = 0 },
                    ["confidence"] = 0.4,
                    ["clarification"] = "How many boxes of each type (A, B, C) for the urgent order?",
                    ["plan"] = JValue.CreateNull(), ["reason"] = "mock: quantities not found in the transcription"
                };
            JToken sj = Py.Get(body, "state_json");
            JObject state = Py.ParseObject(Py.Truthy(sj) && sj.Type == JTokenType.String ? sj.Value<string>() : "{}") ?? new JObject();
            JArray pallets = ListOf(state, "pallets"), benches = ListOf(state, "benches"), trucks = ListOf(state, "trucks");
            var picks = new JArray();
            var fallback = new JArray();
            var used = new HashSet<string>();
            foreach (string boxType in MetaPrompts.WAREHOUSE_TYPES)
            {
                long left = counts[boxType];
                if (left <= 0) continue;
                foreach (JObject pallet in pallets.OfType<JObject>())
                {
                    if (left <= 0) break;
                    long have = StockOf(pallet, boxType);
                    string pid = Py.GetStr(pallet, "id");
                    if (have <= 0 || used.Contains(pid) || Py.Truthy(Py.Get(pallet, "reserved_by"))) continue;
                    long take = Math.Min(have, left);
                    picks.Add(new JObject { ["pallet_id"] = pid, ["type"] = boxType, ["count"] = take });
                    used.Add(pid);
                    left -= take;
                }
                if (left > 0)
                    return new JObject
                    {
                        ["action"] = "no_plan", ["urgent"] = true, ["order"] = Counts(counts), ["confidence"] = 0.95,
                        ["clarification"] = "", ["plan"] = JValue.CreateNull(),
                        ["reason"] = "mock: insufficient stock for the urgent order"
                    };
            }
            foreach (JObject pallet in pallets.OfType<JObject>())
            {
                string pid = Py.GetStr(pallet, "id");
                if (pid.Length > 0 && !used.Contains(pid) && !Py.Truthy(Py.Get(pallet, "reserved_by"))) fallback.Add(pid);
            }
            JObject bench = benches.OfType<JObject>().FirstOrDefault(b => !Py.Truthy(Py.Get(b, "reserved_by")) && IntField(b, "free_levels") > 0);
            long total = counts.Values.Sum();
            JObject truck = trucks.OfType<JObject>().FirstOrDefault(t => !Py.Truthy(Py.Get(t, "reserved_by")) && IntField(t, "free") >= total);
            if (bench == null || truck == null)
                return new JObject
                {
                    ["action"] = "wait", ["urgent"] = true, ["order"] = Counts(counts), ["confidence"] = 0.95,
                    ["clarification"] = "", ["plan"] = JValue.CreateNull(),
                    ["reason"] = "mock: no free bench or truck for the urgent order"
                };
            var plan = new JObject
            {
                ["action"] = "execute_order", ["order_id"] = orderId, ["picks"] = picks,
                ["fallback_pallets"] = fallback, ["bench_id"] = Py.GetStr(bench, "id"),
                ["truck_id"] = Py.GetStr(truck, "id"), ["robot_pick"] = "R1", ["robot_load"] = "R2",
                ["start_immediately"] = true, ["start_delay_s"] = 0, ["reason"] = "mock: deterministic urgent plan"
            };
            return new JObject
            {
                ["action"] = "execute_order", ["urgent"] = true, ["order"] = Counts(counts), ["confidence"] = 0.95,
                ["clarification"] = "", ["plan"] = plan, ["reason"] = "mock: deterministic urgent order"
            };
        }

        /// <summary>re.findall(r"-?\d+(?:\.\d+)?", output) — number of values (/sam route).</summary>
        /// <summary>Quality inspection answer: kind and defect from closed lists, confidence 0..1.</summary>
        public static JObject ValidateInspection(JObject payload, out string error)
        {
            error = "";
            string kind = Py.StrOr(Py.Get(payload, "kind")).Trim().ToLowerInvariant().Replace("_", " ");
            string defect = Py.StrOr(Py.Get(payload, "defect")).Trim().ToLowerInvariant();
            if (Array.IndexOf(MetaPrompts.INSPECT_KINDS, kind) < 0) { error = "unknown kind '" + kind + "'"; return null; }
            if (Array.IndexOf(MetaPrompts.INSPECT_DEFECTS, defect) < 0) { error = "unknown defect '" + defect + "'"; return null; }
            double confidence = 0;
            JToken c = Py.Get(payload, "confidence");
            if (c != null && (c.Type == JTokenType.Float || c.Type == JTokenType.Integer)) confidence = Math.Max(0, Math.Min(1, c.Value<double>()));
            return new JObject
            {
                ["kind"] = kind, ["defect"] = defect, ["confidence"] = Math.Round(confidence, 3),
                ["reason"] = Py.Cut(Py.StrOr(Py.Get(payload, "reason")), 240)
            };
        }

        public static int CountNumbers(string output) => Regex.Matches(output ?? "", @"-?\d+(?:\.\d+)?").Count;
    }
}
