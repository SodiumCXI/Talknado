using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Talknado.Client.Models;

public interface ICryptoSessionManager
{
    void SharedSecretExchange(NetworkStream stream, CancellationToken token);
    public void ReceiveAndSetSessionKey(NetworkStream stream, CancellationToken token);
    byte[] EncryptMessage(byte[] message);
    byte[] EncryptPassword(byte[] message);
    byte[] DecryptMessage(byte[] encryptedMessage);
}

public class CryptoSessionManager(INetworkUtils networkUtils) : ICryptoSessionManager
{
    private readonly INetworkUtils _networkUtils = networkUtils;
    private readonly ECDiffieHellman _clientECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _sessionKey = new byte[32];

    private byte[] _sharedSecret = null!;

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
    }

    private void SetSharedSecret(byte[] serverPublicKey)
    {
        using ECDiffieHellman serverECDH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        serverECDH.ImportSubjectPublicKeyInfo(serverPublicKey, out _);

        _sharedSecret = _clientECDH.DeriveKeyMaterial(serverECDH.PublicKey);
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

    public byte[] EncryptPassword(byte[] message)
    {
        using var aes = Aes.Create();

        aes.Key = _sharedSecret;
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
}