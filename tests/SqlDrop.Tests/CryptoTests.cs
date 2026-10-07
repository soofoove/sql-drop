using System.Security.Cryptography;
using SqlDrop.Core;

namespace SqlDrop.Tests;

public class CryptoTests
{
    [Fact]
    public void Encrypt_then_decrypt_round_trips()
    {
        var crypto = new ChannelCrypto("user-1");
        var plain = RandomNumberGenerator.GetBytes(10_000);

        var decrypted = crypto.Decrypt(crypto.Encrypt(plain));

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var crypto = new ChannelCrypto("user-1");
        var plain = new byte[100];

        Assert.NotEqual(crypto.Encrypt(plain), crypto.Encrypt(plain));
    }

    [Fact]
    public void Tampered_data_is_rejected()
    {
        var crypto = new ChannelCrypto("user-1");
        var data = crypto.Encrypt(new byte[64]);
        data[20] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(data));
    }

    [Fact]
    public void Wrong_user_id_cannot_decrypt()
    {
        var data = new ChannelCrypto("user-1").Encrypt(new byte[64]);

        Assert.ThrowsAny<CryptographicException>(() => new ChannelCrypto("user-2").Decrypt(data));
    }

    [Fact]
    public void Channel_id_is_stable_and_differs_per_user()
    {
        Assert.Equal(new ChannelCrypto("a").ChannelId, new ChannelCrypto(" a ").ChannelId);
        Assert.NotEqual(new ChannelCrypto("a").ChannelId, new ChannelCrypto("b").ChannelId);
    }

    [Fact]
    public void Metadata_round_trips()
    {
        var crypto = new ChannelCrypto("user-1");

        var (name, size) = crypto.DecryptMeta(crypto.EncryptMeta("тест file.txt", 123456789012));

        Assert.Equal("тест file.txt", name);
        Assert.Equal(123456789012, size);
    }

    [Fact]
    public void Generated_user_id_is_22_url_safe_chars()
    {
        var id = ChannelCrypto.GenerateUserId();

        Assert.Equal(22, id.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", id);
        Assert.NotEqual(id, ChannelCrypto.GenerateUserId());
    }
}
