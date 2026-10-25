using System.Security.Cryptography;
using System.Text;

namespace LFHub.Engine;

/// <summary>
/// Delivery receipts for alarm pushes. Every alarm push carries a fresh token
/// (<c>lfhubReceipt</c>), which the phone posts back to <c>POST /v1/receipt/{token}</c>
/// when the push arrives. The token is a bearer capability: 128 random bits as 32
/// lower-case hex characters, good for one use within <see cref="Lifetime"/>, and kept
/// by the hub only as its SHA-256, with the device and alarm it was sent for.
/// </summary>
public static class AlarmReceipts
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// The form <see cref="NewToken"/> gives; anything else cannot be a receipt.
    public static bool IsWellFormed(string token) =>
        token.Length == 32 && token.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
