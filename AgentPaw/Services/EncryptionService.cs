using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace AgentPaw.Services;

/// <summary>
/// AES-256-GCM 암호화. 기존 Node.js 버전과 동일한 키 파생 및 포맷(iv:tag:ciphertext) 사용.
/// Node.js crypto는 IV 16바이트를 허용하지만 .NET AesGcm은 12바이트만 허용한다.
/// BouncyCastle을 사용하여 양쪽 모두 호환한다.
///
/// 듀얼 키 전략:
///   _key      — 랜덤 키 파일(.encryption_key)에서 로드. 신규 암호화에 사용.
///   _legacyKey — hostname+username 기반 결정론적 키. 이전 버전으로 암호화된 데이터 복호화에 사용.
/// Decrypt는 _key로 먼저 시도하고 실패 시 _legacyKey로 폴백한다.
/// </summary>
public class EncryptionService
{
    private readonly byte[] _key;
    private readonly byte[] _legacyKey;
    private const int IvLength = 16;   // Node.js 호환 (16바이트)
    private const int TagBits = 128;   // 16바이트 = 128비트

    public EncryptionService()
    {
        // 레거시 키: 이전 버전의 hostname+username 기반 결정론적 키 재현 (폴백용)
        var seed = $"agent-paw-{Environment.MachineName}-{Environment.UserName}-encryption-key-v1";
        _legacyKey = SHA256.HashData(Encoding.UTF8.GetBytes(seed));

        // 현재 키: 파일에서 로드하거나 최초 1회 랜덤 생성한다.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentPaw", "data");
        Directory.CreateDirectory(dataDir);
        var keyPath = Path.Combine(dataDir, ".encryption_key");

        if (File.Exists(keyPath))
        {
            _key = Convert.FromHexString(File.ReadAllText(keyPath).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyPath, Convert.ToHexString(_key).ToLower());
        }
    }

    public string Encrypt(string plaintext)
    {
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(_key), TagBits, iv));

        var output = new byte[cipher.GetOutputSize(plaintextBytes.Length)];
        var len = cipher.ProcessBytes(plaintextBytes, 0, plaintextBytes.Length, output, 0);
        cipher.DoFinal(output, len);

        // output = ciphertext + tag (마지막 16바이트가 tag)
        var tagLength = TagBits / 8;
        var ciphertext = output[..^tagLength];
        var tag = output[^tagLength..];

        return $"{Convert.ToHexString(iv).ToLower()}:{Convert.ToHexString(tag).ToLower()}:{Convert.ToHexString(ciphertext).ToLower()}";
    }

    public string Decrypt(string encrypted)
    {
        var parts = encrypted.Split(':');
        if (parts.Length != 3)
            throw new FormatException("Invalid encrypted format. Expected iv:tag:ciphertext");

        var iv = Convert.FromHexString(parts[0]);
        var tag = Convert.FromHexString(parts[1]);
        var ciphertext = Convert.FromHexString(parts[2]);

        // 현재 키로 먼저 시도, 실패 시 레거시 키로 폴백 (이전 버전 데이터 호환)
        try
        {
            return DecryptWith(_key, iv, tag, ciphertext);
        }
        catch
        {
            return DecryptWith(_legacyKey, iv, tag, ciphertext);
        }
    }

    private static string DecryptWith(byte[] key, byte[] iv, byte[] tag, byte[] ciphertext)
    {
        // BouncyCastle expects ciphertext + tag concatenated
        var input = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, input, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, input, ciphertext.Length, tag.Length);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, iv));

        var output = new byte[cipher.GetOutputSize(input.Length)];
        var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        cipher.DoFinal(output, len);

        return Encoding.UTF8.GetString(output);
    }
}
