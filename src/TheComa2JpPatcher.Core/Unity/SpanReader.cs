using System.Buffers.Binary;
using System.Text;

namespace TheComa2JpPatcher.Core.Unity;

/// <summary>
/// アセットの形式が想定と違うことを表す内部例外。
/// 範囲外の読み取りなどを1か所で検出するために使い、公開APIの境界で必ず <see cref="Result{T}"/> の失敗へ変換する
/// （握りつぶさず、理由を付けて呼び出し側へ返す）。
/// </summary>
internal sealed class AssetFormatException(string message) : Exception(message);

/// <summary>
/// バイト列を先頭から順に読むための読み取り専用リーダー。
/// Unity のシリアライズ形式は「ヘッダーだけビッグエンディアン、それ以外はリトルエンディアン」なので両方を用意する。
/// すべての読み取りで残りバイト数を確認し、足りなければ <see cref="AssetFormatException"/> を投げる。
/// </summary>
internal ref struct SpanReader
{
    // NUL 終端の文字列として認める最大のバイト数（終端の NUL を除く）。
    private const int MaxNullTerminatedStringLength = 4096;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public SpanReader(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        _position = position;
    }

    /// <summary>現在の読み取り位置（先頭からのバイト数）。</summary>
    public readonly int Position => _position;

    /// <summary>未読のバイト数。</summary>
    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte()
    {
        Require(1);
        return _data[_position++];
    }

    public int ReadInt32()
    {
        Require(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
        _position += sizeof(int);
        return value;
    }

    public uint ReadUInt32()
    {
        Require(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += sizeof(uint);
        return value;
    }

    public long ReadInt64()
    {
        Require(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]);
        _position += sizeof(long);
        return value;
    }

    /// <summary>ヘッダー専用のビッグエンディアン読み取り。</summary>
    public uint ReadUInt32BigEndian()
    {
        Require(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data[_position..]);
        _position += sizeof(uint);
        return value;
    }

    /// <summary>指定バイト数を読み飛ばす。</summary>
    public void Skip(int count)
    {
        if (count < 0)
        {
            throw new AssetFormatException($"読み飛ばすバイト数が負です（{count}）。");
        }

        Require(count);
        _position += count;
    }

    /// <summary>
    /// 読み取り位置を4バイト境界へ進める。
    /// データ末尾ちょうどで終わる文字列の後など、境界が末尾を超える場合は末尾で止める。
    /// </summary>
    public void Align4()
    {
        var aligned = (_position + 3) & ~3;
        _position = Math.Min(aligned, _data.Length);
    }

    /// <summary>NUL 終端の UTF-8 文字列を読む（メタデータ部で使われる形式）。</summary>
    /// <remarks>
    /// メタデータ部の文字列（Unity のバージョンや参照先ファイルのパス）は短い。上限の範囲に終端が無い場合は壊れたファイルとみなし、
    /// 巨大な文字列を作ろうとしてメモリ不足の例外で落ちるのを防ぐ。
    /// </remarks>
    public string ReadNullTerminatedString()
    {
        var rest = _data[_position..];
        var length = rest[..Math.Min(rest.Length, MaxNullTerminatedStringLength + 1)].IndexOf((byte)0);
        if (length < 0)
        {
            throw new AssetFormatException($"文字列の終端が見つからないか、文字列が長すぎます（位置 {_position}）。");
        }

        var text = Encoding.UTF8.GetString(rest[..length]);
        _position += length + 1;
        return text;
    }

    /// <summary>「長さ(int32) + UTF-8 本体 + 4バイト境界までの詰め物」形式の文字列を読む（オブジェクト本体で使われる形式）。</summary>
    /// <param name="maxLength">
    /// 認める最大のバイト数。壊れたファイルの長さ欄をそのまま信じて、巨大な文字列を作ってしまうのを防ぐ。
    /// </param>
    public string ReadLengthPrefixedString(int maxLength = int.MaxValue)
    {
        var length = ReadStringLength(maxLength);
        var text = Encoding.UTF8.GetString(_data.Slice(_position, length));
        _position += length;
        Align4();
        return text;
    }

    /// <summary>
    /// 「長さ(int32) + 本体 + 詰め物」形式の文字列を、中身を解読せずに読み飛ばす。
    /// 使わない文字列を毎回解読すると、細工されたファイルで処理時間が大きく膨らむため。
    /// </summary>
    public void SkipLengthPrefixedString()
    {
        var length = ReadStringLength(int.MaxValue);
        _position += length;
        Align4();
    }

    /// <summary>文字列の長さ欄を読み、残りのデータと上限の範囲に収まることを確かめる。</summary>
    private int ReadStringLength(int maxLength)
    {
        var length = ReadInt32();
        if (length < 0 || length > Remaining || length > maxLength)
        {
            throw new AssetFormatException($"文字列の長さが不正です（{length}、位置 {_position}）。");
        }

        return length;
    }

    private readonly void Require(int count)
    {
        if (count > _data.Length - _position)
        {
            throw new AssetFormatException($"データが途中で終わっています（位置 {_position}、必要 {count} バイト）。");
        }
    }
}
