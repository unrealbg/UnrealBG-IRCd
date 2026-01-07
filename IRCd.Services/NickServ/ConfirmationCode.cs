namespace IRCd.Services.NickServ
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    internal static class ConfirmationCode
    {
        public static string Generate(int byteLength = 10)
        {
            var bytes = RandomNumberGenerator.GetBytes(byteLength);
            return Convert.ToHexString(bytes);
        }

        public static string Hash(string code)
        {
            if (code is null)
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(code.Trim());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        public static bool EqualsHash(string? expectedHexHash, string providedCode)
        {
            if (string.IsNullOrWhiteSpace(expectedHexHash))
                return false;

            var providedHash = Hash(providedCode);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHexHash),
                Convert.FromHexString(providedHash));
        }
    }
}
