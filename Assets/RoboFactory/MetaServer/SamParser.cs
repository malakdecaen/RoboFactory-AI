using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Reads the SAM 3.1 segmentation text returned by the Meta Responses API.
    ///
    /// Wire grammar (dev.meta.ai/docs/sam/reading-segmentation): one line per frame, "&lt;Nf&gt;" then
    /// records "id&lt;|box;x1=..;y1=..;x2=..;y2=..;w=..;h=..[;c=..]|&gt;&lt;|mask;...|&gt;" separated by commas.
    /// Boxes are in source pixels (x1,y1 top-left, x2,y2 bottom-right, inclusive), w/h = source size,
    /// c = confidence 0..1 when metadata.include_confidence is "true". A single image is frame 0 and
    /// zero matches give an empty text.
    ///
    /// Only the boxes and the confidence are kept: masks are counted, not decoded.
    /// Tokens are matched by their own delimiters, never by splitting on commas (mask payloads may contain any
    /// printable character). Thread-safe, no Unity.
    /// </summary>
    public static class SamParser
    {
        public sealed class SamObject
        {
            public int Id;
            public int Frame;
            public float X1, Y1, X2, Y2;
            public float Confidence = -1f;   // -1 = not requested / not returned
            public bool HasMask;
        }

        public sealed class SamResult
        {
            public int Width, Height;
            public readonly List<SamObject> Objects = new List<SamObject>();
            public int Frames;
            public string Error = "";
        }

        private static readonly Regex Frame = new Regex(@"<(\d+)f(?:;[^>]*)?>", RegexOptions.Compiled);
        private static readonly Regex Token = new Regex(@"<\|([A-Za-z][A-Za-z0-9_.-]*)(?:;([^|]*))?\|>", RegexOptions.Compiled);

        public static SamResult Parse(string text)
        {
            var result = new SamResult();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var seenFrames = new HashSet<int>();
            foreach (string rawLine in text.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                Match f = Frame.Match(line);
                if (!f.Success) continue;
                int frame = int.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                seenFrames.Add(frame);
                SamObject current = null;
                foreach (Match t in Token.Matches(line, f.Index + f.Length))
                {
                    string name = t.Groups[1].Value;
                    Dictionary<string, string> fields = Fields(t.Groups[2].Value);
                    if (name == "box")
                    {
                        int id = IdBefore(line, t.Index);
                        if (!TryF(fields, "x1", out float x1) || !TryF(fields, "y1", out float y1) ||
                            !TryF(fields, "x2", out float x2) || !TryF(fields, "y2", out float y2))
                        {
                            result.Error = "box token without coordinates";
                            current = null;
                            continue;
                        }
                        current = new SamObject { Id = id, Frame = frame, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
                        if (TryF(fields, "c", out float c)) current.Confidence = c;
                        if (TryF(fields, "w", out float w) && TryF(fields, "h", out float h))
                        {
                            result.Width = (int)w;
                            result.Height = (int)h;
                        }
                        result.Objects.Add(current);
                    }
                    else if (name == "mask" && current != null)
                    {
                        current.HasMask = true;
                        if (current.Confidence < 0f && TryF(fields, "c", out float c)) current.Confidence = c;
                    }
                }
            }
            result.Frames = seenFrames.Count;
            return result;
        }

        /// <summary>Compact JSON for the game (no mask payload).</summary>
        public static JArray ToJson(SamResult r, float minConfidence)
        {
            var array = new JArray();
            foreach (SamObject o in r.Objects)
            {
                if (o.Frame != 0) continue;   // still image: frame 0 only
                if (o.Confidence >= 0f && o.Confidence < minConfidence) continue;
                array.Add(new JObject
                {
                    ["id"] = o.Id,
                    ["x1"] = o.X1, ["y1"] = o.Y1, ["x2"] = o.X2, ["y2"] = o.Y2,
                    ["c"] = o.Confidence < 0f ? -1f : Math.Round(o.Confidence, 3),
                    ["mask"] = o.HasMask
                });
            }
            return array;
        }

        // the object id is the run of digits written just before its box token ("…,12<|box;…")
        private static int IdBefore(string line, int tokenStart)
        {
            int i = tokenStart - 1;
            while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
            int end = i;
            while (i >= 0 && line[i] >= '0' && line[i] <= '9') i--;
            if (end <= i) return -1;
            return int.TryParse(line.Substring(i + 1, end - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;
        }

        private static Dictionary<string, string> Fields(string s)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (string part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim();
                if (!d.ContainsKey(k)) d[k] = part.Substring(eq + 1).Trim();
            }
            return d;
        }

        private static bool TryF(Dictionary<string, string> d, string key, out float value)
        {
            value = 0f;
            return d.TryGetValue(key, out string s) &&
                   float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
