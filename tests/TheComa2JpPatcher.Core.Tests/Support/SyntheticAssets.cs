using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace TheComa2JpPatcher.Core.Tests.Support;

/// <summary>
/// テスト用に、Unity の SerializedFile（形式バージョン17）と同じ構造のバイト列を組み立てる。
/// ゲームの実ファイルはリポジトリに入れられないため、テストはすべてこの合成データで行う。
/// </summary>
internal sealed class SerializedFileBuilder
{
    private const int HeaderSize = 20;

    private readonly List<int> _typeClassIds = [];
    private readonly List<(long PathId, int TypeIndex, byte[] Data)> _objects = [];
    private readonly List<(int FileIndex, long PathId)> _scriptTypes = [];
    private readonly List<string> _externals = [];
    private readonly Dictionary<string, int> _countFieldOffsets = [];

    /// <summary>
    /// 直前の <see cref="Build"/> で、各一覧の件数欄を書いた位置（ファイル先頭からの絶対位置）。
    /// キーは "types"・"objects"・"scriptTypes"・"externals"。件数欄を壊すテストで使う。
    /// </summary>
    public IReadOnlyDictionary<string, int> CountFieldOffsets => _countFieldOffsets;

    /// <summary>型を追加し、その番号（オブジェクトから参照する添字）を返す。</summary>
    public int AddType(int classId)
    {
        _typeClassIds.Add(classId);
        return _typeClassIds.Count - 1;
    }

    public SerializedFileBuilder AddObject(long pathId, int typeIndex, byte[] data)
    {
        _objects.Add((pathId, typeIndex, data));
        return this;
    }

    public SerializedFileBuilder AddExternal(string path)
    {
        _externals.Add(path);
        return this;
    }

    /// <summary>スクリプト型の一覧に1件追加する（参照先ファイルの番号とオブジェクトID）。</summary>
    public SerializedFileBuilder AddScriptType(int fileIndex, long pathId)
    {
        _scriptTypes.Add((fileIndex, pathId));
        return this;
    }

    /// <summary>
    /// ファイル全体のバイト列を作る。
    /// </summary>
    /// <param name="dataAlignment">オブジェクト本体を揃える境界。Unity と同じ 8 だと、サイズが8の倍数でないオブジェクトの後ろに0埋めができる。</param>
    /// <param name="formatVersion">ヘッダーに書く形式バージョン。</param>
    /// <param name="hasTypeTree">型ツリー付きとして印を付けるか（未対応形式のテスト用）。</param>
    public byte[] Build(int dataAlignment = 8, uint formatVersion = 17, bool hasTypeTree = false)
    {
        // 1. オブジェクト本体の配置（データ領域の先頭からの相対位置）を先に決める。
        var relativeStarts = new int[_objects.Count];
        var cursor = 0;
        for (var i = 0; i < _objects.Count; i++)
        {
            cursor = AlignUp(cursor, dataAlignment);
            relativeStarts[i] = cursor;
            cursor += _objects[i].Data.Length;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        // 2. ヘッダーの場所を空けてから、メタデータを書く（位置合わせはファイル先頭基準）。
        writer.Write(new byte[HeaderSize]);
        WriteNullTerminated(writer, "2018.3.3f1");
        writer.Write(19); // 対象プラットフォーム
        writer.Write(hasTypeTree);

        _countFieldOffsets.Clear();
        _countFieldOffsets["types"] = (int)stream.Position;
        writer.Write(_typeClassIds.Count);
        foreach (var classId in _typeClassIds)
        {
            writer.Write(classId);
            writer.Write((byte)0);   // isStrippedType
            writer.Write((short)-1); // scriptTypeIndex
            if (classId == 114)
            {
                writer.Write(new byte[16]); // スクリプトID
            }

            writer.Write(new byte[16]); // 型のハッシュ
        }

        _countFieldOffsets["objects"] = (int)stream.Position;
        writer.Write(_objects.Count);
        for (var i = 0; i < _objects.Count; i++)
        {
            PadTo(writer, AlignUp((int)stream.Position, 4));
            writer.Write(_objects[i].PathId);
            writer.Write((uint)relativeStarts[i]);
            writer.Write((uint)_objects[i].Data.Length);
            writer.Write(_objects[i].TypeIndex);
        }

        // スクリプト型の一覧: 参照先ファイルの番号（int32）、4バイト境界への位置合わせ、オブジェクトID（int64）。
        _countFieldOffsets["scriptTypes"] = (int)stream.Position;
        writer.Write(_scriptTypes.Count);
        foreach (var (fileIndex, pathId) in _scriptTypes)
        {
            writer.Write(fileIndex);
            PadTo(writer, AlignUp((int)stream.Position, 4));
            writer.Write(pathId);
        }

        _countFieldOffsets["externals"] = (int)stream.Position;
        writer.Write(_externals.Count);
        foreach (var external in _externals)
        {
            WriteNullTerminated(writer, string.Empty);
            writer.Write(new byte[16]); // GUID
            writer.Write(0);            // 種別
            WriteNullTerminated(writer, external);
        }

        WriteNullTerminated(writer, string.Empty); // ユーザー情報

        var metadataEnd = (int)stream.Position;
        var dataOffset = AlignUp(metadataEnd, 16);
        PadTo(writer, dataOffset);

        // 3. オブジェクト本体。
        for (var i = 0; i < _objects.Count; i++)
        {
            PadTo(writer, dataOffset + relativeStarts[i]);
            writer.Write(_objects[i].Data);
        }

        writer.Flush();
        var bytes = stream.ToArray();

        // 4. ヘッダー（ビッグエンディアン）を埋める。
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (uint)(metadataEnd - HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), formatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)dataOffset);
        bytes[16] = 0; // リトルエンディアン
        return bytes;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void PadTo(BinaryWriter writer, int position)
    {
        var padding = position - (int)writer.BaseStream.Position;
        if (padding > 0)
        {
            writer.Write(new byte[padding]);
        }
    }

    private static void WriteNullTerminated(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Write((byte)0);
    }
}

/// <summary>オブジェクト本体（MonoBehaviour・MonoScript など）の合成。</summary>
internal static class UnityBodies
{
    /// <summary>
    /// 「共通ヘッダー + 参照 n 個 + 言語リスト」で終わる MonoBehaviour を作る。
    /// 参照15個で ApplicationManager と同じ構造になる。
    /// </summary>
    /// <param name="trailingBytes">言語リストの後ろに付け足すバイト数（「リストが最後のフィールド」でない形を作るテスト用）。</param>
    public static byte[] MonoBehaviourWithLanguageList(
        int scriptFileId,
        long scriptPathId,
        IReadOnlyList<int> languages,
        int pointerCount = 15,
        string name = "",
        int trailingBytes = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        WritePointer(writer, 0, 648);   // m_GameObject
        writer.Write((byte)1);          // m_Enabled
        writer.Write(new byte[3]);      // 4バイト境界までの詰め物
        WritePointer(writer, scriptFileId, scriptPathId); // m_Script
        WriteLengthPrefixed(writer, name);                // m_Name

        for (var i = 0; i < pointerCount; i++)
        {
            WritePointer(writer, 0, 1000 + i);
        }

        writer.Write(languages.Count);
        foreach (var language in languages)
        {
            writer.Write(language);
        }

        writer.Write(Enumerable.Repeat((byte)0x01, trailingBytes).ToArray());

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>MonoScript（スクリプトのクラス情報）を作る。</summary>
    public static byte[] MonoScript(string className, string assemblyName = "Assembly-CSharp.dll", string namespaceName = "")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        WriteLengthPrefixed(writer, className); // m_Name
        writer.Write(-8000);                    // 実行順序
        writer.Write(new byte[16]);             // プロパティのハッシュ
        WriteLengthPrefixed(writer, className);
        WriteLengthPrefixed(writer, namespaceName);
        WriteLengthPrefixed(writer, assemblyName);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>0 以外で埋めた任意のデータ。隣のオブジェクトを誤って書き換えていないかの検出にも使う。</summary>
    public static byte[] Filler(int size, byte value = 0xAB) => [.. Enumerable.Repeat(value, size)];

    private static void WritePointer(BinaryWriter writer, int fileId, long pathId)
    {
        writer.Write(fileId);
        writer.Write(pathId);
    }

    private static void WriteLengthPrefixed(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        var padding = ((bytes.Length + 3) & ~3) - bytes.Length;
        writer.Write(new byte[padding]);
    }
}

/// <summary>実物と同じ構成（シーン + スクリプト情報ファイル）の合成データ一式。</summary>
internal static class SyntheticGame
{
    public const string ScriptsFileName = "globalgamemanagers.assets";

    public const long ApplicationManagerPathId = 3957;

    private const long ApplicationManagerScriptPathId = 1414;
    private const long OtherScriptPathId = 1251;

    /// <summary>Steam 版の初期状態と同じ並び（日本語の 8 だけが無い）。</summary>
    public static readonly ImmutableArray<int> OriginalLanguages = [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11];

    /// <summary>スクリプト情報ファイル（globalgamemanagers.assets 相当）。</summary>
    public static byte[] BuildScripts(string applicationManagerAssembly = "Assembly-CSharp.dll")
    {
        var builder = new SerializedFileBuilder();
        var scriptType = builder.AddType(115);
        return builder
            .AddObject(OtherScriptPathId, scriptType, UnityBodies.MonoScript("UIManager"))
            .AddObject(ApplicationManagerScriptPathId, scriptType, UnityBodies.MonoScript("ApplicationManager", applicationManagerAssembly))
            .Build();
    }

    /// <summary>
    /// シーンファイル（level0 相当）。
    /// 対象の前後に別のオブジェクトを置き、対象と同じ構造でスクリプトだけが違う「そっくりさん」も混ぜる。
    /// </summary>
    /// <param name="languages">言語リストの内容。</param>
    /// <param name="dataAlignment">8 なら対象の直後に4バイトの空きができる。4 なら空きができない。</param>
    /// <param name="targetIsLastObject">対象をファイル末尾のオブジェクトにするか。</param>
    /// <param name="duplicateTarget">ApplicationManager を2つ置くか（曖昧な場合のテスト用）。</param>
    /// <param name="scriptTypeCount">スクリプト型の一覧に入れる件数（実物には多数ある）。</param>
    /// <param name="targetTrailingBytes">対象の言語リストの後ろに付け足すバイト数。</param>
    public static byte[] BuildScene(
        IReadOnlyList<int> languages,
        int dataAlignment = 8,
        bool targetIsLastObject = false,
        bool duplicateTarget = false,
        int scriptTypeCount = 0,
        int targetTrailingBytes = 0) =>
        CreateSceneBuilder(languages, targetIsLastObject, duplicateTarget, scriptTypeCount, targetTrailingBytes).Build(dataAlignment);

    /// <summary>シーンファイルと、その各一覧の件数欄の位置を返す（件数欄を壊すテスト用）。</summary>
    public static (byte[] Scene, IReadOnlyDictionary<string, int> CountFieldOffsets) BuildSceneWithCountOffsets(IReadOnlyList<int> languages)
    {
        var builder = CreateSceneBuilder(languages, targetIsLastObject: false, duplicateTarget: false, scriptTypeCount: 2, targetTrailingBytes: 0);
        var scene = builder.Build();
        return (scene, builder.CountFieldOffsets);
    }

    private static SerializedFileBuilder CreateSceneBuilder(
        IReadOnlyList<int> languages,
        bool targetIsLastObject,
        bool duplicateTarget,
        int scriptTypeCount,
        int targetTrailingBytes)
    {
        var builder = new SerializedFileBuilder();
        var gameObjectType = builder.AddType(1);
        var behaviourType = builder.AddType(114);

        builder
            .AddExternal(ScriptsFileName)
            .AddExternal("sharedassets0.assets")
            .AddExternal("library/unity default resources")
            .AddObject(648, gameObjectType, UnityBodies.Filler(44))
            // 構造は同じだが、スクリプトが ApplicationManager ではないもの。
            .AddObject(3956, behaviourType, UnityBodies.MonoBehaviourWithLanguageList(1, OtherScriptPathId, OriginalLanguages))
            .AddObject(
                ApplicationManagerPathId,
                behaviourType,
                UnityBodies.MonoBehaviourWithLanguageList(1, ApplicationManagerScriptPathId, languages, trailingBytes: targetTrailingBytes));

        if (duplicateTarget)
        {
            builder.AddObject(4000, behaviourType, UnityBodies.MonoBehaviourWithLanguageList(1, ApplicationManagerScriptPathId, languages));
        }

        if (!targetIsLastObject)
        {
            builder.AddObject(3958, behaviourType, UnityBodies.MonoBehaviourWithLanguageList(1, OtherScriptPathId, [0, 1], pointerCount: 3));
        }

        for (var i = 0; i < scriptTypeCount; i++)
        {
            builder.AddScriptType(1, 5000 + i);
        }

        return builder;
    }

    /// <summary>スクリプト情報ファイルだけを返す読み込み関数。</summary>
    public static Func<string, ReadOnlyMemory<byte>?> Loader(byte[] scripts) =>
        fileName => fileName == ScriptsFileName ? new ReadOnlyMemory<byte>(scripts) : null;
}
