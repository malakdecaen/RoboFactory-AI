using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RoboFactory.MetaServer
{
    /// <summary>
    /// Small functions reproducing the Python semantics of the original proxy
    /// (truthiness, int()/float()/str(), json.dumps) so that the C# server
    /// produces exactly the same answers and the same prompts.
    /// </summary>
    internal static class Py
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>json.loads: dates never converted, object expected.</summary>
        public static JToken Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text))
                   {
                       DateParseHandling = DateParseHandling.None,
                       FloatParseHandling = FloatParseHandling.Double
                   })
            {
                JToken token = JToken.ReadFrom(reader);
                // nothing but whitespace after the value (like json.loads)
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("extra data");
                return token;
            }
        }

        public static JObject ParseObject(string text)
        {
            try { return Parse(text) as JObject; }
            catch (Exception) { return null; }
        }

        /// <summary>dict.get(key): null if the key is missing (≠ JSON null).</summary>
        public static JToken Get(JObject o, string key)
        {
            if (o == null) return null;
            return o.TryGetValue(key, out JToken v) ? v : null;
        }

        public static bool IsNone(JToken t) => t == null || t.Type == JTokenType.Null;

        /// <summary>Python's bool(x).</summary>
        public static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: return false;
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.Integer: return t.ToString() != "0";
                case JTokenType.Float: return t.Value<double>() != 0.0;
                case JTokenType.String: return t.Value<string>().Length > 0;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                case JTokenType.Object: return ((JObject)t).Count > 0;
                default: return true;
            }
        }

        /// <summary>Python's str(x).</summary>
        public static string Str(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "None";
            switch (t.Type)
            {
                case JTokenType.String: return t.Value<string>();
                case JTokenType.Boolean: return t.Value<bool>() ? "True" : "False";
                case JTokenType.Integer: return ((JValue)t).Value is System.Numerics.BigInteger bi ? bi.ToString(Inv) : t.Value<long>().ToString(Inv);
                case JTokenType.Float: return Repr(t.Value<double>());
                default: return Dumps(t);
            }
        }

        /// <summary>str(x or "") / str(x or default).</summary>
        public static string StrOr(JToken t, string fallback = "") => Truthy(t) ? Str(t) : fallback;

        /// <summary>str(o.get(key, default)): default only if the key is missing.</summary>
        public static string GetStr(JObject o, string key, string fallback = "")
        {
            JToken v = Get(o, key);
            return v == null ? fallback : Str(v);
        }

        public static string Strip(string s) => s == null ? "" : s.Trim();

        public static string Cut(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max);

        /// <summary>Python's int(x); false = ValueError / TypeError.</summary>
        public static bool TryInt(JToken t, out long value)
        {
            value = 0;
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Boolean: value = t.Value<bool>() ? 1 : 0; return true;
                case JTokenType.Integer:
                    try { value = t.Value<long>(); return true; }
                    catch (Exception) { return false; }
                case JTokenType.Float:
                    double d = t.Value<double>();
                    if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                    value = (long)Math.Truncate(d);
                    return true;
                case JTokenType.String:
                    string s = t.Value<string>().Trim().Replace("_", "");
                    return long.TryParse(s, NumberStyles.AllowLeadingSign, Inv, out value);
                default: return false;
            }
        }

        /// <summary>int(x or 0).</summary>
        public static bool TryIntOr0(JToken t, out long value)
        {
            if (!Truthy(t)) { value = 0; return true; }
            return TryInt(t, out value);
        }

        /// <summary>Python's float(x).</summary>
        public static bool TryFloat(JToken t, out double value)
        {
            value = 0;
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Boolean: value = t.Value<bool>() ? 1 : 0; return true;
                case JTokenType.Integer:
                case JTokenType.Float: value = t.Value<double>(); return true;
                case JTokenType.String:
                    string s = t.Value<string>().Trim().Replace("_", "");
                    string low = s.ToLowerInvariant();
                    if (low == "nan" || low == "+nan" || low == "-nan") { value = double.NaN; return true; }
                    if (low == "inf" || low == "+inf" || low == "infinity" || low == "+infinity") { value = double.PositiveInfinity; return true; }
                    if (low == "-inf" || low == "-infinity") { value = double.NegativeInfinity; return true; }
                    return double.TryParse(s, NumberStyles.Float, Inv, out value);
                default: return false;
            }
        }

        /// <summary>float(x or 0).</summary>
        public static bool TryFloatOr0(JToken t, out double value)
        {
            if (!Truthy(t)) { value = 0; return true; }
            return TryFloat(t, out value);
        }

        /// <summary>Python's repr(float): shortest round trip, ".0" for integers, 2-digit exponent.</summary>
        public static string Repr(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            string s = d.ToString("R", Inv);
            bool neg = s.StartsWith("-");
            if (neg) s = s.Substring(1);
            int exp = 0;
            int ei = s.IndexOfAny(new[] { 'E', 'e' });
            if (ei >= 0)
            {
                exp = int.Parse(s.Substring(ei + 1), NumberStyles.AllowLeadingSign, Inv);
                s = s.Substring(0, ei);
            }
            int dot = s.IndexOf('.');
            string ip = dot >= 0 ? s.Substring(0, dot) : s;
            string fp = dot >= 0 ? s.Substring(dot + 1) : "";
            string all = ip + fp;
            int pointPos = ip.Length + exp;
            int lz = 0;
            while (lz < all.Length && all[lz] == '0') lz++;
            string digits = all.Substring(lz).TrimEnd('0');
            string sign = neg ? "-" : "";
            if (digits.Length == 0) return sign + "0.0";
            int exp10 = pointPos - lz - 1;
            if (exp10 < -4 || exp10 >= 16)
            {
                string mant = digits.Length > 1 ? digits[0] + "." + digits.Substring(1) : digits;
                return sign + mant + "e" + (exp10 < 0 ? "-" : "+") + Math.Abs(exp10).ToString("00", Inv);
            }
            if (exp10 >= 0)
            {
                string intDigits = digits.Length > exp10 + 1 ? digits.Substring(0, exp10 + 1) : digits.PadRight(exp10 + 1, '0');
                string frac = digits.Length > exp10 + 1 ? digits.Substring(exp10 + 1) : "0";
                return sign + intDigits + "." + frac;
            }
            return sign + "0." + new string('0', -exp10 - 1) + digits;
        }

        /// <summary>Python's json.dumps(x) (separators ", " / ": ", ensure_ascii=True).</summary>
        public static string Dumps(JToken t)
        {
            var sb = new StringBuilder();
            DumpsInto(sb, t);
            return sb.ToString();
        }

        private static void DumpsInto(StringBuilder sb, JToken t)
        {
            if (t == null) { sb.Append("null"); return; }
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: sb.Append("null"); break;
                case JTokenType.Boolean: sb.Append(t.Value<bool>() ? "true" : "false"); break;
                case JTokenType.Integer: sb.Append(Str(t)); break;
                case JTokenType.Float:
                    double d = t.Value<double>();
                    sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Repr(d));
                    break;
                case JTokenType.String: AppendAsciiString(sb, t.Value<string>()); break;
                case JTokenType.Array:
                    sb.Append('[');
                    bool firstA = true;
                    foreach (JToken item in (JArray)t)
                    {
                        if (!firstA) sb.Append(", ");
                        firstA = false;
                        DumpsInto(sb, item);
                    }
                    sb.Append(']');
                    break;
                case JTokenType.Object:
                    sb.Append('{');
                    bool firstO = true;
                    foreach (JProperty p in ((JObject)t).Properties())
                    {
                        if (!firstO) sb.Append(", ");
                        firstO = false;
                        AppendAsciiString(sb, p.Name);
                        sb.Append(": ");
                        DumpsInto(sb, p.Value);
                    }
                    sb.Append('}');
                    break;
                default: AppendAsciiString(sb, t.ToString()); break;
            }
        }

        private static void AppendAsciiString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Compact serialisation of an answer (the exact format does not matter to Unity).</summary>
        public static string Compact(JToken t) => t.ToString(Formatting.None);
    }
}
