using System.Security.Cryptography;
using System.Text;

namespace SqlDrop.Core;

/// <summary>Derives the channel id and the AES-256-GCM key from the userId and encrypts/decrypts payloads.</summary>
public sealed class ChannelCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public byte[] ChannelId { get; }

    public ChannelCrypto(string userId)
    {
        var id = userId.Trim();
        ChannelId = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(id + "|key"));
    }

    /// <summary>Random 128-bit id, base64url (22 chars).</summary>
    public static string GenerateUserId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Returns nonce(12) + ciphertext + tag(16).</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var result = new byte[NonceSize + plain.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, result.AsSpan(NonceSize, plain.Length), result.AsSpan(NonceSize + plain.Length, TagSize));
        return result;
    }

    /// <exception cref="CryptographicException">Wrong key or corrupted data.</exception>
    public byte[] Decrypt(byte[] data)
    {
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted data is too short.");

        var cipherLength = data.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            data.AsSpan(0, NonceSize),
            data.AsSpan(NonceSize, cipherLength),
            data.AsSpan(NonceSize + cipherLength, TagSize),
            plain);
        return plain;
    }

    public byte[] EncryptMeta(string fileName, long size)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(size);
            w.Write(fileName);
        }
        return Encrypt(ms.ToArray());
    }

    public (string FileName, long Size) DecryptMeta(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(Decrypt(data)), Encoding.UTF8);
        var size = r.ReadInt64();
        var name = r.ReadString();
        return (name, size);
    }
}
