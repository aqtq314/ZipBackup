using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZipBackup
{
    public record struct CStr(ConsoleColor? Color, string Text)
    {
        public static CStr U(string text = "") => new(null, text);
        public static CStr K(string text = "") => new(ConsoleColor.Black, text);
        public static CStr B(string text = "") => new(ConsoleColor.Blue, text);
        public static CStr C(string text = "") => new(ConsoleColor.Cyan, text);
        public static CStr S(string text = "") => new(ConsoleColor.Gray, text);
        public static CStr G(string text = "") => new(ConsoleColor.Green, text);
        public static CStr M(string text = "") => new(ConsoleColor.Magenta, text);
        public static CStr R(string text = "") => new(ConsoleColor.Red, text);
        public static CStr W(string text = "") => new(ConsoleColor.White, text);
        public static CStr Y(string text = "") => new(ConsoleColor.Yellow, text);
        public static CStr DB(string text = "") => new(ConsoleColor.DarkBlue, text);
        public static CStr DC(string text = "") => new(ConsoleColor.DarkCyan, text);
        public static CStr DS(string text = "") => new(ConsoleColor.DarkGray, text);
        public static CStr DG(string text = "") => new(ConsoleColor.DarkGreen, text);
        public static CStr DM(string text = "") => new(ConsoleColor.DarkMagenta, text);
        public static CStr DR(string text = "") => new(ConsoleColor.DarkRed, text);
        public static CStr DY(string text = "") => new(ConsoleColor.DarkYellow, text);

        public static implicit operator CStr(string text) => S(text);
    }

    public class ProgressDisplay
    {
        public long MinUpdateIntervalMs { get; init; }

        readonly Stopwatch sw;

        int lastOutputLength = 0;
        long prevElapsedMs = 0;

        public ProgressDisplay(long minUpdateIntervalMs = 500)
        {
            MinUpdateIntervalMs = minUpdateIntervalMs;
            sw = Stopwatch.StartNew();

            prevElapsedMs = -minUpdateIntervalMs;
        }

        private void Show(CStr[] texts, bool debounce = true, bool newline = false)
        {
            var elapsed = sw.ElapsedMilliseconds;
            if (debounce && elapsed - prevElapsedMs < MinUpdateIntervalMs)
                return;
            else
                prevElapsedMs = elapsed;

            var maxWidth = Console.BufferWidth;
            var printedLength = 0;
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text.Color != null)
                    Console.ForegroundColor = text.Color.Value;

                if (printedLength + text.Text.Length >= maxWidth)
                    text = text with { Text = text.Text.Substring(0, maxWidth - printedLength - 1) };

                Console.Write(text.Text);
                printedLength += text.Text.Length;
            }

            if (printedLength < lastOutputLength)
            {
                var padLength = lastOutputLength - printedLength;
                Console.Write(new string(' ', padLength) + new string('\b', padLength));
            }

            if (newline)
            {
                Console.WriteLine();
                lastOutputLength = 0;
            }
            else
            {
                Console.Write(new string('\b', printedLength) + "\r");
                lastOutputLength = printedLength;
            }
        }

        public void Tick(params CStr[] texts) => Show(texts, newline: false);
        public void TickLine(params CStr[] texts) => Show(texts, newline: true);

        public void Write(params CStr[] texts) => Show(texts, debounce: false, newline: false);
        public void WriteLine(params CStr[] texts) => Show(texts, debounce: false, newline: true);
    }
}
