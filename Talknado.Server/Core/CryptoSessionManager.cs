using System.Security.Cryptography;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace Talknado.Server.Core;

public interface ICryptoSessionManager
{
    byte[] GetEncryptedSessionKey(ushort userId);
    byte[] GetServerPublicKey();
    void SetSharedSecret(ushort userId, byte[] userPublicKey);
    void TryRemoveSharedSecret(ushort userId);
    byte[] EncryptMessage(byte[] message);
    byte[] DecryptMessage(byte[] encryptedMessage);
    byte[] DecryptPassword(ushort userId, byte[] encryptedMessage);
}

public class CryptoSessionManager : ICryptoSessionManager
{
    private readonly ECDiffieHellman _serverECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly ConcurrentDictionary<ushort, byte[]> _sharedSecrets =[];

    private byte[] _sessionKey = new byte[32];

    public CryptoSessionManager()
    {
        GenerateSessionKey();
    }

    private void GenerateSessionKey()
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();
        _sessionKey = aes.Key;
    }

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

    public byte[] EncryptMessage(byte[] message)
    {
        using var aes = Aes.Create();

        aes.Key = _sessionKey;
        aes.Padding = PaddingMode.PKCS7;

        aes.GenerateIV();
        byte[] iv = aes.IV;

        using var encryptor = aes.CreateEncryptor();
        using var memoryStream = new MemoryStream();

        memoryStream.Write(iv, 0, iv.Length);

        using var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write);

        cryptoStream.Write(message, 0, message.Length);
        cryptoStream.FlushFinalBlock();

        return memoryStream.ToArray();
    }

    public byte[] DecryptMessage(byte[] encryptedMessage)
    {
        using var aes = Aes.Create();

        aes.Key = _sessionKey;
        aes.Padding = PaddingMode.PKCS7;

        byte[] iv = new byte[16];
        Buffer.BlockCopy(encryptedMessage, 0, iv, 0, iv.Length);
        aes.IV = iv;

        int encryptedDataLength = encryptedMessage.Length - iv.Length;
        byte[] encryptedData = new byte[encryptedDataLength];
        Buffer.BlockCopy(encryptedMessage, iv.Length, encryptedData, 0, encryptedDataLength);

        using var decryptor = aes.CreateDecryptor();
        using var memoryStream = new MemoryStream(encryptedData);
        using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
        using var resultStream = new MemoryStream();

        cryptoStream.CopyTo(resultStream);
        return resultStream.ToArray();
    }

    public byte[] DecryptPassword(ushort userId, byte[] encryptedMessage)
    {
        using var aes = Aes.Create();

        aes.Key = _sharedSecrets[userId];
        aes.Padding = PaddingMode.PKCS7;

        byte[] iv = new byte[16];
        Buffer.BlockCopy(encryptedMessage, 0, iv, 0, iv.Length);
        aes.IV = iv;

        int encryptedDataLength = encryptedMessage.Length - iv.Length;
        byte[] encryptedData = new byte[encryptedDataLength];
        Buffer.BlockCopy(encryptedMessage, iv.Length, encryptedData, 0, encryptedDataLength);

        using var decryptor = aes.CreateDecryptor();
        using var memoryStream = new MemoryStream(encryptedData);
        using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
        using var resultStream = new MemoryStream();

        cryptoStream.CopyTo(resultStream);
        return resultStream.ToArray();
    }
}