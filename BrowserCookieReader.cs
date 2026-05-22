#if WINDOWS
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeCap;

static class BrowserCookieReader
{
    private record Profile(string Label, string CookieDb, string LocalState);

    public static string? TryGetSessionKey()
    {
        foreach (var p in Profiles())
        {
            try
            {
                var key = ReadFrom(p);
                if (key != null)
                {
                    Logger.Log($"BrowserCookieReader: found sessionKey in {p.Label}");
                    return key;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"BrowserCookieReader: {p.Label}: {ex.Message}");
            }
        }
        return null;
    }

    static IEnumerable<Profile> Profiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        (string Name, string UserData)[] roots =
        [
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge",   Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave",  Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        ];

        foreach (var (name, ud) in roots)
        {
            if (!Directory.Exists(ud)) continue;
            var ls = Path.Combine(ud, "Local State");
            if (!File.Exists(ls)) continue;

            var profileDirs = new[] { "Default" }.Concat(
                Directory.EnumerateDirectories(ud, "Profile *")
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => n != null));

            foreach (var pd in profileDirs)
            {
                var db = Path.Combine(ud, pd, "Network", "Cookies");
                if (!File.Exists(db)) db = Path.Combine(ud, pd, "Cookies");
                if (!File.Exists(db)) continue;
                yield return new Profile($"{name}/{pd}", db, ls);
            }
        }
    }

    static string? ReadFrom(Profile p)
    {
        var aesKey = GetAesKey(p.LocalState);
        if (aesKey == null) return null;

        var tmp = Path.GetTempFileName();
        try
        {
            File.Copy(p.CookieDb, tmp, overwrite: true);
            return QueryAndDecrypt(tmp, aesKey);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static byte[]? GetAesKey(string localStatePath)
    {
        var json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
        if (!osCrypt.TryGetProperty("encrypted_key", out var el)) return null;
        var b64 = el.GetString();
        if (b64 == null) return null;
        var enc = Convert.FromBase64String(b64);
        if (enc.Length < 5) return null;
        return ProtectedData.Unprotect(enc[5..], null, DataProtectionScope.CurrentUser);
    }

    static string? QueryAndDecrypt(string dbPath, byte[] aesKey)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT encrypted_value FROM cookies " +
            "WHERE host_key LIKE '%claude.ai%' AND name='sessionKey' " +
            "ORDER BY last_access_utc DESC LIMIT 1";
        return cmd.ExecuteScalar() is byte[] enc ? Decrypt(enc, aesKey) : null;
    }

    static string? Decrypt(byte[] enc, byte[] aesKey)
    {
        // Format: "v10"/"v11" (3 bytes) + nonce (12 bytes) + ciphertext + tag (16 bytes)
        if (enc.Length < 31) return null;
        var prefix = Encoding.ASCII.GetString(enc, 0, 3);
        if (prefix != "v10" && prefix != "v11") return null;

        var nonce = enc[3..15];
        var ct    = enc[15..^16];
        var tag   = enc[^16..];

        using var aes = new AesGcm(aesKey, 16);
        var plain = new byte[ct.Length];
        aes.Decrypt(nonce, ct, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
#else
namespace ClaudeCap;

static class BrowserCookieReader
{
    public static string? TryGetSessionKey() => null;
}
#endif
