using System.Buffers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Talknado.Client.Models;

public interface ICryptoSessionManager
{
    void SharedSecretExchange(NetworkStream stream, CancellationToken token);
    public void ReceiveAndSetSessionKey(NetworkStream stream, CancellationToken token);

    /// <summary>Пишет IV и шифротекст в <paramref name="destination"/>, возвращает длину.</summary>
    int Encrypt(ReadOnlySpan<byte> message, Span<byte> destination);

    /// <summary>
    /// <paramref name="destination"/> не короче <paramref name="encrypted"/> без IV;
    /// возвращает длину открытого текста.
    /// </summary>
    int Decrypt(ReadOnlySpan<byte> encrypted, Span<byte> destination);

    byte[] EncryptMessage(byte[] message);
    byte[] EncryptPassword(byte[] message);
    byte[] DecryptMessage(byte[] encryptedMessage);
}

/// <summary>
/// Пакет — IV 16 байт и шифротекст AES-256-CBC с PKCS7. У каждого потока свой
/// экземпляр Aes с готовым ключом: экземпляр не потокобезопасен, а создавать
/// его на каждый пакет дорого.
/// </summary>
public class CryptoSessionManager(INetworkUtils networkUtils) : ICryptoSessionManager
{
    public const int IvSize = 16;

    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ECDiffieHellman _clientECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _sessionKey = new byte[32];
    private readonly ThreadLocal<SessionCipher> _cipher = new(() => new SessionCipher());
    private int _keyVersion;

    private byte[] _sharedSecret = null!;

    public static int EncryptedLength(int messageLength) => IvSize + (messageLength / 16 + 1) * 16;

    public void SharedSecretExchange(NetworkStream stream, CancellationToken token)
    {
        var buffer = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        SetSharedSecret(buffer);
        _networkUtils.WritePacketAsync(stream, _clientECDH.PublicKey.ExportSubjectPublicKeyInfo(), token).GetAwaiter().GetResult();
    }

    public void ReceiveAndSetSessionKey(NetworkStream stream, CancellationToken token)
    {
        var encryptedSessionKey = _networkUtils.ReadPacketAsync(stream, token).GetAwaiter().GetResult();
        for (int i = 0; i < 32; i++)
        {
            _sessionKey[i] = (byte)(encryptedSessionKey[i] ^ _sharedSecret[i]);
        }
        Interlocked.Increment(ref _keyVersion);
    }

    private void SetSharedSecret(byte[] serverPublicKey)
    {
        using ECDiffieHellman serverECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        serverECDH.ImportSubjectPublicKeyInfo(serverPublicKey, out _);

        _sharedSecret = _clientECDH.DeriveKeyMaterial(serverECDH.PublicKey);
    }

    private Aes SessionAes()
    {
        var cipher = _cipher.Value!;
        int version = Volatile.Read(ref _keyVersion);
        if (cipher.Version != version)
        {
            cipher.Aes.Key = _sessionKey;
            cipher.Version = version;
        }
        return cipher.Aes;
    }

    public int Encrypt(ReadOnlySpan<byte> message, Span<byte> destination) =>
        Encrypt(SessionAes(), message, destination);

    public int Decrypt(ReadOnlySpan<byte> encrypted, Span<byte> destination) =>
        SessionAes().DecryptCbc(encrypted[IvSize..], encrypted[..IvSize], destination, PaddingMode.PKCS7);

    private static int Encrypt(Aes aes, ReadOnlySpan<byte> message, Span<byte> destination)
    {
        var iv = destination[..IvSize];
        RandomNumberGenerator.Fill(iv);
        return IvSize + aes.EncryptCbc(message, iv, destination[IvSize..], PaddingMode.PKCS7);
    }

    public byte[] EncryptMessage(byte[] message)
    {
        var result = new byte[EncryptedLength(message.Length)];
        Encrypt(message, result);
        return result;
    }

    public byte[] EncryptPassword(byte[] message)
    {
        using var aes = Aes.Create();
        aes.Key = _sharedSecret;

        var result = new byte[EncryptedLength(message.Length)];
        Encrypt(aes, message, result);
        return result;
    }

    public byte[] DecryptMessage(byte[] encryptedMessage)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(encryptedMessage.Length);
        try
        {
            int length = Decrypt(encryptedMessage, buffer);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class SessionCipher
    {
        public Aes Aes { get; } = Aes.Create();
        public int Version { get; set; } = -1;
    }
}
