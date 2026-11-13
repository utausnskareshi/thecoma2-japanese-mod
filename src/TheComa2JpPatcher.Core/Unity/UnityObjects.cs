namespace TheComa2JpPatcher.Core.Unity;

/// <summary>
/// 他のオブジェクトへの参照（Unity の PPtr）。
/// </summary>
/// <param name="FileId">0 なら同じファイル内、1以上なら「外部参照一覧の FileId-1 番目」のファイル。</param>
/// <param name="PathId">参照先ファイル内でのオブジェクトID。</param>
public readonly record struct ObjectPointer(int FileId, long PathId)
{
    /// <summary>PPtr 1個のシリアライズ後のバイト数（int32 + int64）。</summary>
    public const int SerializedSize = sizeof(int) + sizeof(long);
}

/// <summary>
/// すべての MonoBehaviour に共通する先頭部分。
/// </summary>
/// <param name="GameObject">このコンポーネントが付いている GameObject への参照。</param>
/// <param name="Enabled">コンポーネントが有効かどうか。</param>
/// <param name="Script">このコンポーネントのクラスを表す MonoScript への参照。</param>
/// <param name="FieldsOffset">クラス固有のフィールドが始まる位置（オブジェクト本体の先頭からのバイト数）。</param>
/// <remarks>
/// 名前（m_Name）は使わないので、解読せずに読み飛ばしている。
/// すべての MonoBehaviour について名前を解読すると、細工されたファイルで処理時間が大きく膨らむため。
/// </remarks>
public sealed record MonoBehaviourHeader(ObjectPointer GameObject, bool Enabled, ObjectPointer Script, int FieldsOffset);

/// <summary>MonoScript（スクリプトのクラス情報）のうち、クラスの特定に使う部分。</summary>
/// <param name="Name">オブジェクトの名前（通常はクラス名と同じ）。</param>
/// <param name="ClassName">クラス名。</param>
/// <param name="Namespace">名前空間（無ければ空文字列）。</param>
/// <param name="AssemblyName">クラスが入っているアセンブリのファイル名（例: Assembly-CSharp.dll）。</param>
public sealed record MonoScriptInfo(string Name, string ClassName, string Namespace, string AssemblyName);

/// <summary>オブジェクト本体のバイト列から、決まった形式の部分を読み取る（副作用なし）。</summary>
public static class UnityObjectReader
{
    // MonoScript の各文字列（クラス名・名前空間・アセンブリ名）として認める最大のバイト数。
    private const int MaxScriptStringLength = 4096;

    /// <summary>MonoBehaviour の共通ヘッダーを読む。</summary>
    public static Result<MonoBehaviourHeader> ReadMonoBehaviourHeader(ReadOnlySpan<byte> objectData)
    {
        try
        {
            var reader = new SpanReader(objectData);
            var gameObject = ReadPointer(ref reader);
            var enabled = reader.ReadByte() != 0;
            reader.Align4(); // bool の後ろは4バイト境界まで詰められている
            var script = ReadPointer(ref reader);
            reader.SkipLengthPrefixedString(); // m_Name（使わない）
            return Result.Ok(new MonoBehaviourHeader(gameObject, enabled, script, reader.Position));
        }
        catch (AssetFormatException ex)
        {
            return Result.Fail<MonoBehaviourHeader>($"MonoBehaviour のヘッダーを読めませんでした: {ex.Message}");
        }
    }

    /// <summary>MonoScript のクラス名などを読む（Unity 2018.3 のレイアウト）。</summary>
    public static Result<MonoScriptInfo> ReadMonoScript(ReadOnlySpan<byte> objectData)
    {
        try
        {
            // クラス名などは実際には数十文字程度。壊れたファイルで巨大な文字列を作らないよう、長さに上限を設ける。
            var reader = new SpanReader(objectData);
            var name = reader.ReadLengthPrefixedString(MaxScriptStringLength);
            reader.Skip(sizeof(int)); // 実行順序
            reader.Skip(16);          // プロパティのハッシュ
            var className = reader.ReadLengthPrefixedString(MaxScriptStringLength);
            var namespaceName = reader.ReadLengthPrefixedString(MaxScriptStringLength);
            var assemblyName = reader.ReadLengthPrefixedString(MaxScriptStringLength);
            return Result.Ok(new MonoScriptInfo(name, className, namespaceName, assemblyName));
        }
        catch (AssetFormatException ex)
        {
            return Result.Fail<MonoScriptInfo>($"MonoScript を読めませんでした: {ex.Message}");
        }
    }

    /// <summary>参照（PPtr）を1つ読む。FileID（int32）、PathID（int64）の順に並んでいる。</summary>
    private static ObjectPointer ReadPointer(ref SpanReader reader)
    {
        var fileId = reader.ReadInt32();
        var pathId = reader.ReadInt64();
        return new ObjectPointer(fileId, pathId);
    }
}
