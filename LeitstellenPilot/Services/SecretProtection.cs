using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LeitstellenPilot.Services;

public interface ISecretProtection
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class SecretProtection : ISecretProtection
{
    private readonly string _directory;
    private readonly bool _windows;
    public SecretProtection(string directory, bool? windows = null) { _directory = Path.Combine(directory, ".secrets"); _windows = windows ?? OperatingSystem.IsWindows(); }
    public string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        if (_windows)
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException("Windows-Schutz ist hier nicht verfügbar.");
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        var key = ReadKey(create: true);
        try
        {
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, "LeitstellenPilot|secrets-v1"u8);
            var envelope = new byte[nonce.Length + tag.Length + cipher.Length];
            nonce.CopyTo(envelope, 0); tag.CopyTo(envelope, nonce.Length); cipher.CopyTo(envelope, 28);
            return "aesgcm-v1:" + Convert.ToBase64String(envelope);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public string Unprotect(string value)
    {
        if (value.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!_windows || !OperatingSystem.IsWindows()) throw new CryptographicException("Windows-Zugangsdaten auf diesem Gerät erneut eingeben.");
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser));
        }
        if (!value.StartsWith("aesgcm-v1:", StringComparison.Ordinal)) throw new CryptographicException("Unbekanntes Speicherformat.");
        var data = Convert.FromBase64String(value[10..]);
        if (data.Length < 28 || data.Length > 1048576) throw new CryptographicException("Ungültige geschützte Daten.");
        var plain = new byte[data.Length - 28]; var key = ReadKey(create: false);
        try
        {
            using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, "LeitstellenPilot|secrets-v1"u8);
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    private byte[] ReadKey(bool create)
    {
        var path = Path.Combine(_directory, "storage.key");
        if (create)
        {
            if (Directory.Exists(_directory) && File.GetAttributes(_directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Schlüsselspeicher darf kein Link sein.");
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!File.Exists(path))
            {
                var generated = RandomNumberGenerator.GetBytes(32);
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using var output = new FileStream(path, options);
                    output.Write(generated); output.Flush(flushToDisk: true);
                }
                catch (IOException) when (File.Exists(path)) { }
                finally { CryptographicOperations.ZeroMemory(generated); }
            }
        }
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new CryptographicException("Lokaler Speicherschlüssel fehlt oder ist ungültig.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            throw new CryptographicException("Speicherschlüssel muss nur für den Benutzer lesbar sein (chmod 600).");
        var key = File.ReadAllBytes(path); if (key.Length != 32) throw new CryptographicException("Lokaler Speicherschlüssel ist ungültig.");
        return key;
    }
    public static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
