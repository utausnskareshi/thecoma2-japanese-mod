using System.Collections.Immutable;

namespace TheComa2JpPatcher.Core.Unity;

/// <summary>
/// オブジェクト表の1エントリ。
/// </summary>
/// <param name="PathId">ファイル内でオブジェクトを識別するID。</param>
/// <param name="DataStart">オブジェクト本体の開始位置（ファイル先頭からの絶対位置）。</param>
/// <param name="ByteSize">オブジェクト本体のバイト数。</param>
/// <param name="ClassId">Unity のクラスID（114 = MonoBehaviour、115 = MonoScript など）。</param>
/// <param name="TableEntryOffset">このエントリ自体がファイル内のどこに書かれているか（サイズ欄を書き換える際に使う）。</param>
public sealed record SerializedObjectEntry(long PathId, int DataStart, int ByteSize, int ClassId, int TableEntryOffset)
{
    /// <summary>エントリ内でサイズ欄が始まる位置（PathId 8バイト + 開始位置 4バイトの後ろ）。</summary>
    public const int ByteSizeFieldOffsetInEntry = 12;

    /// <summary>オブジェクト本体の終了位置（この位置自体は含まない）。</summary>
    public int DataEnd => DataStart + ByteSize;

    /// <summary>サイズ欄のファイル内絶対位置。</summary>
    public int ByteSizeFieldOffset => TableEntryOffset + ByteSizeFieldOffsetInEntry;
}

/// <summary>
/// Unity の SerializedFile（level0 や *.assets）から、このツールに必要な部分だけを取り出したもの。
/// </summary>
/// <param name="FormatVersion">シリアライズ形式のバージョン。</param>
/// <param name="UnityVersion">作成した Unity のバージョン文字列（例: 2018.3.3f1）。</param>
/// <param name="Objects">オブジェクト表。</param>
/// <param name="ExternalPaths">外部参照ファイルのパス。PPtr の FileID が n のとき、n-1 番目がその参照先。</param>
public sealed record SerializedFileInfo(
    uint FormatVersion,
    string UnityVersion,
    ImmutableArray<SerializedObjectEntry> Objects,
    ImmutableArray<string> ExternalPaths);

/// <summary>
/// SerializedFile の最小限のパーサー（読み取り専用・副作用なし）。
/// 対応するのは、このゲームが使っている「形式バージョン17・型ツリーなし・リトルエンディアン」だけで、
/// それ以外は書き換えを避けるために失敗として返す。
/// </summary>
public static class SerializedFileParser
{
    /// <summary>対応している形式バージョン（Unity 2018.3 系）。</summary>
    public const uint SupportedFormatVersion = 17;

    /// <summary>MonoBehaviour のクラスID。</summary>
    public const int MonoBehaviourClassId = 114;

    /// <summary>MonoScript のクラスID。</summary>
    public const int MonoScriptClassId = 115;

    private const int HeaderSize = 20;

    // 各一覧の1件あたりの最小バイト数。件数欄の値が、残りのデータに収まり得るかの検証に使う。
    private const int MinTypeEntrySize = sizeof(int) + 1 + 2 + 16;                    // クラスID + 2つの欄 + 型のハッシュ
    private const int ObjectEntrySize = sizeof(long) + sizeof(uint) + sizeof(uint) + sizeof(int); // ID + 開始位置 + サイズ + 型番号
    private const int MinScriptTypeEntrySize = sizeof(int) + sizeof(long);            // ファイル番号 + オブジェクトID
    private const int MinExternalEntrySize = 1 + 16 + sizeof(int) + 1;                // 空文字列 + GUID + 種別 + パス（終端のみ）

    /// <summary>
    /// バイト列を SerializedFile として解析する。
    /// </summary>
    /// <param name="data">ファイル全体の内容。</param>
    /// <param name="displayName">エラーメッセージに入れるファイル名。</param>
    public static Result<SerializedFileInfo> Parse(ReadOnlySpan<byte> data, string displayName)
    {
        try
        {
            return Result.Ok(ParseCore(data));
        }
        catch (AssetFormatException ex)
        {
            // 形式の不一致は「想定される失敗」なので、理由を付けて結果型で返す。
            return Result.Fail<SerializedFileInfo>($"{displayName} を解析できませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析の本体。ヘッダー → 型の一覧 → オブジェクト表 → スクリプト型の一覧 → 外部参照の一覧 の順に読む。
    /// 形式が想定と違う場合は <see cref="AssetFormatException"/> を投げ、<see cref="Parse"/> が失敗の結果へ変換する。
    /// </summary>
    private static SerializedFileInfo ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new AssetFormatException("ファイルが小さすぎます。");
        }

        var reader = new SpanReader(data);

        // --- ヘッダー（ここだけビッグエンディアン） ---
        var metadataSize = reader.ReadUInt32BigEndian();
        var fileSize = reader.ReadUInt32BigEndian();
        var version = reader.ReadUInt32BigEndian();
        var dataOffset = reader.ReadUInt32BigEndian();
        var endianness = reader.ReadByte();
        reader.Skip(3); // 予約領域

        if (version != SupportedFormatVersion)
        {
            throw new AssetFormatException($"未対応の形式バージョンです（{version}。対応しているのは {SupportedFormatVersion}）。");
        }

        if (endianness != 0)
        {
            throw new AssetFormatException("ビッグエンディアンのデータには対応していません。");
        }

        if (fileSize != (uint)data.Length)
        {
            throw new AssetFormatException($"ヘッダー上のサイズ（{fileSize}）と実際のサイズ（{data.Length}）が一致しません。");
        }

        if (dataOffset > (uint)data.Length || metadataSize > (uint)data.Length)
        {
            throw new AssetFormatException("ヘッダーの位置情報がファイルの範囲を超えています。");
        }

        // --- メタデータ（ここからリトルエンディアン） ---
        var unityVersion = reader.ReadNullTerminatedString();
        reader.Skip(sizeof(int)); // 対象プラットフォーム
        var hasTypeTree = reader.ReadByte() != 0;
        if (hasTypeTree)
        {
            // 型ツリー付きは構造が異なる。製品版のプレイヤーでは通常含まれない。
            throw new AssetFormatException("型ツリー付きのファイルには対応していません。");
        }

        // 型の一覧。オブジェクト表は「型の番号」で型を指すので、クラスIDを控えておく。
        var typeCount = ReadCount(ref reader, "型", MinTypeEntrySize);
        var classIds = new int[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            var classId = reader.ReadInt32();
            reader.Skip(1); // isStrippedType
            reader.Skip(2); // scriptTypeIndex
            if (classId == MonoBehaviourClassId)
            {
                reader.Skip(16); // スクリプトID（MonoBehaviour だけが持つ）
            }

            reader.Skip(16); // 型のハッシュ
            classIds[i] = classId;
        }

        // オブジェクト表。
        var objectCount = ReadCount(ref reader, "オブジェクト", ObjectEntrySize);
        var objects = ImmutableArray.CreateBuilder<SerializedObjectEntry>(objectCount);
        for (var i = 0; i < objectCount; i++)
        {
            reader.Align4();
            var entryOffset = reader.Position;
            var pathId = reader.ReadInt64();
            var byteStart = reader.ReadUInt32();
            var byteSize = reader.ReadUInt32();
            var typeIndex = reader.ReadInt32();

            if (typeIndex < 0 || typeIndex >= typeCount)
            {
                throw new AssetFormatException($"オブジェクト {pathId} の型番号が不正です（{typeIndex}）。");
            }

            // 本体がファイル内に収まっているかを long で確認してから int に落とす。
            var start = (long)dataOffset + byteStart;
            var end = start + byteSize;
            if (end > data.Length)
            {
                throw new AssetFormatException($"オブジェクト {pathId} の範囲がファイルの終端を超えています。");
            }

            objects.Add(new SerializedObjectEntry(pathId, (int)start, (int)byteSize, classIds[typeIndex], entryOffset));
        }

        // スクリプト型の一覧（内容は使わないので読み飛ばす）。
        var scriptTypeCount = ReadCount(ref reader, "スクリプト型", MinScriptTypeEntrySize);
        for (var i = 0; i < scriptTypeCount; i++)
        {
            reader.Skip(sizeof(int)); // 参照先ファイルの番号
            reader.Align4();
            reader.Skip(sizeof(long)); // 参照先のオブジェクトID
        }

        // 外部参照ファイルの一覧。
        var externalCount = ReadCount(ref reader, "外部参照", MinExternalEntrySize);
        var externals = ImmutableArray.CreateBuilder<string>(externalCount);
        for (var i = 0; i < externalCount; i++)
        {
            reader.ReadNullTerminatedString(); // 常に空の文字列
            reader.Skip(16);                   // GUID
            reader.Skip(sizeof(int));          // 種別
            externals.Add(reader.ReadNullTerminatedString());
        }

        // メタデータは「ヘッダーの直後から metadataSize バイト」で、オブジェクトの本体はその後ろ（dataOffset 以降）に置かれる。
        // 読み終えた位置がメタデータの範囲を超えていたり、データ領域がメタデータと重なっていたりするファイルでは、
        // 「オブジェクトの本体」として書き換える場所がオブジェクト表そのものを指しうるので、受け付けない。
        // （この後ろにはユーザー情報の文字列が続くが、使わないので読まない。そのため読み終えた位置は終端以下になる。）
        var metadataEnd = (long)HeaderSize + metadataSize;
        if (reader.Position > metadataEnd || metadataEnd > dataOffset)
        {
            throw new AssetFormatException(
                $"メタデータとデータ領域の位置が矛盾しています（メタデータの終端 {metadataEnd}、データ開始位置 {dataOffset}、読み終えた位置 {reader.Position}）。");
        }

        return new SerializedFileInfo(version, unityVersion, objects.MoveToImmutable(), externals.MoveToImmutable());
    }

    /// <summary>
    /// 一覧の件数欄を読む。
    /// 件数は「残りのデータに収まり得る数」までしか認めない。壊れたファイルの件数欄をそのまま信じて、
    /// 実際のデータ量とかけ離れた大きさの配列を確保してしまうのを防ぐため。
    /// </summary>
    /// <param name="reader">件数欄の位置にあるリーダー。</param>
    /// <param name="what">エラーメッセージに入れる一覧の名前。</param>
    /// <param name="minimumEntrySize">その一覧の1件が最低限占めるバイト数。</param>
    private static int ReadCount(ref SpanReader reader, string what, int minimumEntrySize)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > reader.Remaining / minimumEntrySize)
        {
            throw new AssetFormatException($"{what}の件数が不正です（{count}）。");
        }

        return count;
    }
}
