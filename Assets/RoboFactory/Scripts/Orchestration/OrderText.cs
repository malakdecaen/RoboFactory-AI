using System;
using System.Text;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Reads a free-text order: "A x3, B x2", "3A + 2B", "A:4", "a4 b2".
    /// A = wooden crates, B = big cardboard boxes, C = medium cardboard boxes.
    /// </summary>
    public static class OrderText
    {
        /// <summary>Quantities [A,B,C] extracted from the text (0 = nothing read).</summary>
        public static int[] Parse(string text)
        {
            var counts = new int[3];
            if (string.IsNullOrWhiteSpace(text)) return counts;
            string[] parts = text.ToUpperInvariant().Split(new[] { ',', ';', '+', '&' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                for (char letter = 'A'; letter <= 'C'; letter++)
                {
                    int index = part.IndexOf(letter);
                    if (index < 0) continue;
                    // digits after the letter: "A x3", "A:3", "A3"
                    var digits = new StringBuilder();
                    for (int i = index + 1; i < part.Length; i++)
                    {
                        char c = part[i];
                        if (char.IsDigit(c)) digits.Append(c);
                        else if (digits.Length > 0) break;
                    }
                    // digits before the letter: "3A"
                    if (digits.Length == 0)
                        for (int i = index - 1; i >= 0; i--)
                        {
                            char c = part[i];
                            if (char.IsDigit(c)) digits.Insert(0, c);
                            else break;
                        }
                    if (digits.Length == 0) continue;
                    if (int.TryParse(digits.ToString(), out int count) && count > 0)
                        counts[letter - 'A'] = count;
                }
            }
            return counts;
        }

        public static int Total(int[] counts)
        {
            int total = 0;
            if (counts != null)
                for (int i = 0; i < counts.Length; i++) total += counts[i];
            return total;
        }

        /// <summary>"A x3, B x2" from the counters.</summary>
        public static string Format(int[] counts)
        {
            var sb = new StringBuilder();
            if (counts != null)
                for (int i = 0; i < 3 && i < counts.Length; i++)
                {
                    if (counts[i] <= 0) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append((char)('A' + i)).Append(" x").Append(counts[i]);
                }
            return sb.Length > 0 ? sb.ToString() : "(empty)";
        }
    }
}
