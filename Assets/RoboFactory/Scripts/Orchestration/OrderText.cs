using System;
using System.Text;

namespace RoboFactory.Hackathon.Orchestration
{
    /// <summary>
    /// Lecture d'une commande en texte libre : "A x3, B x2", "3A + 2B", "A:4", "a4 b2".
    /// A = caisses bois, B = gros cartons, C = cartons moyens.
    /// </summary>
    public static class OrderText
    {
        /// <summary>Quantités [A,B,C] extraites du texte (0 = rien lu).</summary>
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
                    // chiffres après la lettre : "A x3", "A:3", "A3"
                    var digits = new StringBuilder();
                    for (int i = index + 1; i < part.Length; i++)
                    {
                        char c = part[i];
                        if (char.IsDigit(c)) digits.Append(c);
                        else if (digits.Length > 0) break;
                    }
                    // chiffres avant la lettre : "3A"
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

        /// <summary>"A x3, B x2" à partir des compteurs.</summary>
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
            return sb.Length > 0 ? sb.ToString() : "(vide)";
        }
    }
}
