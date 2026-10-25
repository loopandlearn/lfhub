using System.Security.Cryptography;
using System.Text;

namespace LFHub.Db;

/// <summary>
/// AES-256-GCM for secrets at rest (Nightscout tokens, APNs keys). The stored
/// form is base64 of nonce(12) || tag(16) || ciphertext.
/// </summary>
public sealed class SecretBox
{
    private readonly byte[] _key;

    public SecretBox(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new InvalidOperationException("LFHUB_MASTER_KEY must be 32 bytes, base64-encoded");
    }

    public string Seal(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Open(string sealedText)
    {
        var all = Convert.FromBase64String(sealedText);
        var nonce = all.AsSpan(0, 12);
        var tag = all.AsSpan(12, 16);
        var cipher = all.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
