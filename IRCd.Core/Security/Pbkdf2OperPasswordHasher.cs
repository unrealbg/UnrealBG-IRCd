namespace IRCd.Core.Security
{
    using System;
    using System.Globalization;
    using System.Security.Cryptography;

    public static class Pbkdf2OperPasswordHasher
    {
        private const string Prefix = "$pbkdf2$";
        private const string Algo = "sha256";
        private const int DefaultSaltSize = 16;
        private const int DefaultKeySize = 32;
        private const int DefaultIterations = 100_000;

        public static string Hash(string password, int? iterations = null)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password must not be empty", nameof(password));
            }

            var iter = iterations is > 0 ? iterations.Value : DefaultIterations;

            Span<byte> salt = stackalloc byte[DefaultSaltSize];
            RandomNumberGenerator.Fill(salt);

            var key = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iter,
                HashAlgorithmName.SHA256,
                DefaultKeySize);

            return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Algo}:{iter}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}");
        }

        public static bool TryVerify(string password, string stored, out OperPasswordVerifyFailure failure)
        {
            failure = OperPasswordVerifyFailure.Incorrect;

            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
            {
                return false;
            }

            if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            var parts = stored.Split('$', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            if (!string.Equals(parts[0], "pbkdf2", StringComparison.Ordinal))
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            var algoAndIter = parts[1];
            var colon = algoAndIter.IndexOf(':');
            if (colon <= 0 || colon + 1 >= algoAndIter.Length)
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            var algo = algoAndIter[..colon];
            var iterPart = algoAndIter[(colon + 1)..];

            if (!string.Equals(algo, Algo, StringComparison.OrdinalIgnoreCase))
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            if (!int.TryParse(iterPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iter) || iter <= 0)
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch
            {
                failure = OperPasswordVerifyFailure.InvalidHashFormat;
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iter,
                HashAlgorithmName.SHA256,
                expected.Length);

            var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
            failure = ok ? OperPasswordVerifyFailure.None : OperPasswordVerifyFailure.Incorrect;
            return ok;
        }
    }
}
