using System.Security.Cryptography;

namespace TheComa2JpPatcher.Core;

/// <summary>ファイル内容の同一性確認に使うハッシュ計算。</summary>
public static class Hashing
{
    /// <summary>SHA-256 を小文字16進文字列で返す（副作用なし）。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));
}
