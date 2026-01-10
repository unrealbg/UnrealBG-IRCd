namespace IRCd.Core.Security
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    public sealed class OperPasswordVerifier : IOperPasswordVerifier
    {
        public OperPasswordVerifyResult Verify(string providedPassword, string storedPassword, bool requireHashed)
        {
            if (string.IsNullOrEmpty(storedPassword))
            {
                return OperPasswordVerifyResult.Fail(OperPasswordVerifyFailure.Incorrect);
            }

            if (storedPassword.StartsWith("$pbkdf2$", StringComparison.Ordinal))
            {
                return Pbkdf2OperPasswordHasher.TryVerify(providedPassword, storedPassword, out var failure)
                    ? OperPasswordVerifyResult.Ok()
                    : OperPasswordVerifyResult.Fail(failure);
            }

            if (requireHashed)
            {
                return OperPasswordVerifyResult.Fail(OperPasswordVerifyFailure.PlaintextDisallowed);
            }

            var ok = FixedTimeEqualsUtf8(providedPassword ?? string.Empty, storedPassword);
            return ok ? OperPasswordVerifyResult.Ok() : OperPasswordVerifyResult.Fail(OperPasswordVerifyFailure.Incorrect);
        }

        private static bool FixedTimeEqualsUtf8(string a, string b)
        {
            var aBytes = Encoding.UTF8.GetBytes(a);
            var bBytes = Encoding.UTF8.GetBytes(b);

            var max = Math.Max(aBytes.Length, bBytes.Length);
            var diff = aBytes.Length ^ bBytes.Length;

            for (var i = 0; i < max; i++)
            {
                var x = i < aBytes.Length ? aBytes[i] : (byte)0;
                var y = i < bBytes.Length ? bBytes[i] : (byte)0;
                diff |= x ^ y;
            }

            var diffBytes = BitConverter.GetBytes(diff);
            Span<byte> zero = stackalloc byte[diffBytes.Length];
            return CryptographicOperations.FixedTimeEquals(diffBytes, zero);
        }
    }
}
