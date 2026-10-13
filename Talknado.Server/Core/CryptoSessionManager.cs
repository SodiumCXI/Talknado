using System.Buffers;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace Talknado.Server.Core;

public interface ICryptoSessionManager
{
    byte[] GetEncryptedSessionKey(ushort userId);
    byte[] GetServerPublicKey();
    void SetSharedSecret(ushort userId, byte[] userPublicKey);
    void TryRemoveSharedSecret(ushort userId);

    /// <summary>Пишет IV и шифротекст в <paramref name="destination"/>, возвращает длину.</summary>
    int Encrypt(ReadOnlySpan<byte> message, Span<byte> destination);

    /// <summary>
    /// <paramref name="destination"/> не короче <paramref name="encrypted"/> без IV;
    /// возвращает длину открытого текста.
    /// </summary>
    int Decrypt(ReadOnlySpan<byte> encrypted, Span<byte> destination);

    byte[] EncryptMessage(byte[] message);
    byte[] DecryptMessage(byte[] encryptedMessage);
    byte[] DecryptPassword(ushort userId, byte[] encryptedMessage);
}

/// <summary>
/// Пакет — IV 16 байт и шифротекст AES-256-CBC с PKCS7. У каждого потока свой
/// экземпляр Aes с ключом сессии: экземпляр не потокобезопасен, а создавать
/// его на каждый пакет дорого.
/// </summary>
public class CryptoSessionManager : ICryptoSessionManager
{
    public const int IvSize = 16;

    private readonly ECDiffieHellman _serverECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly ConcurrentDictionary<ushort, byte[]> _sharedSecrets = [];
    private readonly byte[] _sessionKey;
    private readonly ThreadLocal<Aes> _sessionAes;

    public CryptoSessionManager()
    {
        using (var aes = Aes.Create())
        {
            aes.KeySize = 256;
            aes.GenerateKey();
            _sessionKey = aes.Key;
        }

        _sessionAes = new(() =>
        {
            var aes = Aes.Create();
            aes.Key = _sessionKey;
            return aes;
        });
    }

    public static int EncryptedLength(int messageLength) => IvSize + (messageLength / 16 + 1) * 16;

    public byte[] GetEncryptedSessionKey(ushort userId)
    {
        byte[] encryptedSessionKey = new byte[32];
        byte[] sharedSecret = _sharedSecrets[userId];

        for (int i = 0; i < 32; i++)
        {
            encryptedSessionKey[i] = (byte)(_sessionKey[i] ^ sharedSecret[i]);
        }
        return encryptedSessionKey;
    }

    public byte[] GetServerPublicKey()
    {
        return _serverECDH.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public void SetSharedSecret(ushort userId, byte[] userPublicKey)
    {
        using ECDiffieHellman clientECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        clientECDH.ImportSubjectPublicKeyInfo(userPublicKey, out _);
        byte[] sharedSecret = _serverECDH.DeriveKeyMaterial(clientECDH.PublicKey);
        _sharedSecrets[userId] = sharedSecret;
    }

    public void TryRemoveSharedSecret(ushort userId)
    {
        _sharedSecrets.TryRemove(userId, out _);
    }

    public int Encrypt(ReadOnlySpan<byte> message, Span<byte> destination)
    {
        var iv = destination[..IvSize];
        RandomNumberGenerator.Fill(iv);
        return IvSize + _sessionAes.Value!.EncryptCbc(message, iv, destination[IvSize..], PaddingMode.PKCS7);
    }

    public int Decrypt(ReadOnlySpan<byte> encrypted, Span<byte> destination) =>
        Decrypt(_sessionAes.Value!, encrypted, destination);

    private static int Decrypt(Aes aes, ReadOnlySpan<byte> encrypted, Span<byte> destination) =>
        aes.DecryptCbc(encrypted[IvSize..], encrypted[..IvSize], destination, PaddingMode.PKCS7);

    public byte[] EncryptMessage(byte[] message)
    {
        var result = new byte[EncryptedLength(message.Length)];
        Encrypt(message, result);
        return result;
    }

    public byte[] DecryptMessage(byte[] encryptedMessage) =>
        DecryptToArray(_sessionAes.Value!, encryptedMessage);

    public byte[] DecryptPassword(ushort userId, byte[] encryptedMessage)
    {
        using var aes = Aes.Create();
        aes.Key = _sharedSecrets[userId];
        return DecryptToArray(aes, encryptedMessage);
    }

    private static byte[] DecryptToArray(Aes aes, byte[] encryptedMessage)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(encryptedMessage.Length);
        try
        {
            int length = Decrypt(aes, encryptedMessage, buffer);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
