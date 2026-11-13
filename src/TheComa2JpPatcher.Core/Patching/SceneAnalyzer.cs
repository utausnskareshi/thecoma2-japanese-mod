using System.Buffers.Binary;
using System.Collections.Immutable;
using TheComa2JpPatcher.Core.Unity;

namespace TheComa2JpPatcher.Core.Patching;

/// <summary>
/// 選択可能言語リスト（ApplicationManager._playableLanguageList）がファイルのどこにあるか。
/// 位置はすべてファイル先頭からの絶対位置。
/// </summary>
/// <param name="Object">リストを持つ ApplicationManager のオブジェクト表エントリ。</param>
/// <param name="ListCountOffset">リストの要素数（int32）が書かれている位置。要素本体はその直後に並ぶ。</param>
/// <param name="Languages">現在のリストの内容。</param>
/// <param name="ZeroPaddingAfterObject">オブジェクト本体の直後に続く、次のオブジェクトまでの0埋めバイト数。</param>
public sealed record LanguageListLocation(
    SerializedObjectEntry Object,
    int ListCountOffset,
    ImmutableArray<int> Languages,
    int ZeroPaddingAfterObject);

/// <summary>シーンファイル（level0）の解析結果。</summary>
/// <param name="Location">言語リストの位置と内容。</param>
/// <param name="State">パッチの適用状態。</param>
/// <param name="Sha256">解析したファイル内容の SHA-256。</param>
public sealed record SceneAnalysis(LanguageListLocation Location, PatchState State, string Sha256);

/// <summary>
/// level0 から言語リストを探し出す（読み取り専用・副作用なし）。
/// 固定オフセットには頼らず、次の2段階で特定する。
/// 1. 構造の一致: 「共通ヘッダー + 参照15個 + int のリスト」で終わる MonoBehaviour である
/// 2. クラスの一致: そのスクリプトが Assembly-CSharp の ApplicationManager である
/// どちらかが確認できなければ、書き換え対象なしとして失敗を返す。
/// </summary>
public static class SceneAnalyzer
{
    /// <summary>対象コンポーネントのクラス名。</summary>
    public const string TargetClassName = "ApplicationManager";

    /// <summary>ゲーム本体のスクリプトが入っているアセンブリ名。</summary>
    public const string TargetAssemblyName = "Assembly-CSharp.dll";

    // ApplicationManager は、言語リストの前に各種マネージャーへの参照を15個持つ。
    private const int PointerFieldCountBeforeList = 15;

    private const int ListElementSize = sizeof(int);

    // 「見つからなかった理由」として示す件数と、1件あたりの文字数の上限（メッセージが巨大にならないように）。
    private const int MaxNotesShown = 3;
    private const int MaxNoteLength = 200;

    /// <summary>
    /// シーンファイルを解析する。
    /// </summary>
    /// <param name="sceneData">level0 の内容。</param>
    /// <param name="loadExternal">
    /// 外部参照ファイル（例: globalgamemanagers.assets）の内容を返す関数。存在しなければ null。
    /// ファイル読み取りという副作用を呼び出し側へ追い出すための引数。
    /// </param>
    public static Result<SceneAnalysis> Analyze(ReadOnlyMemory<byte> sceneData, Func<string, ReadOnlyMemory<byte>?> loadExternal)
    {
        var parsed = SerializedFileParser.Parse(sceneData.Span, "level0");
        if (!parsed.TryGetValue(out var scene, out var parseError))
        {
            return Result.Fail<SceneAnalysis>(parseError);
        }

        // 1. 構造が一致する候補を集める。
        var candidates = scene.Objects
            .Where(entry => entry.ClassId == SerializedFileParser.MonoBehaviourClassId)
            .Select(entry => TryReadCandidate(sceneData.Span, entry))
            .OfType<Candidate>()
            .ToImmutableArray();

        // 2. スクリプトのクラスを確認する。同じ外部ファイルを何度も読み込まないよう、また同じスクリプトを何度も
        //    確かめないよう（細工されたファイルで処理時間が膨らまないよう）、それぞれの結果を覚えておく。
        var externalCache = new Dictionary<string, Result<ExternalFile>>(StringComparer.OrdinalIgnoreCase);
        var scriptCache = new Dictionary<ObjectPointer, Result<bool>>();
        var confirmed = ImmutableArray.CreateBuilder<Candidate>();
        var notes = new List<string>();                          // 確認できなかった理由（重複なし、出てきた順）
        var seenNotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!scriptCache.TryGetValue(candidate.Script, out var check))
            {
                check = IsTargetScript(candidate.Script, scene.ExternalPaths, loadExternal, externalCache);
                scriptCache[candidate.Script] = check;
            }

            if (!check.TryGetValue(out var isTarget, out var checkError))
            {
                // 同じ理由は1回だけ示す。ファイル由来の文字列が入りうるので、長さを抑え制御文字を除いてから覚える。
                var note = DisplayText.Sanitize(checkError, MaxNoteLength);
                if (seenNotes.Add(note))
                {
                    notes.Add(note);
                }

                continue;
            }

            if (isTarget)
            {
                confirmed.Add(candidate);
            }
        }

        if (confirmed.Count == 0)
        {
            return Result.Fail<SceneAnalysis>(
                $"言語リストが見つかりませんでした。このバージョンのゲームには対応していない可能性があります{FormatNotes(notes)}。");
        }

        if (confirmed.Count > 1)
        {
            return Result.Fail<SceneAnalysis>(
                $"言語リストの候補が {confirmed.Count} 個見つかり、1つに特定できませんでした。安全のため書き換えを行いません。");
        }

        var target = confirmed[0];
        var location = new LanguageListLocation(
            target.Entry,
            target.ListCountOffset,
            target.Languages,
            CountZeroPaddingAfter(sceneData.Span, scene.Objects, target.Entry));

        return Result.Ok(new SceneAnalysis(location, PatchStateDetector.Detect(target.Languages), Hashing.Sha256Hex(sceneData.Span)));
    }

    /// <summary>構造が一致した候補。</summary>
    private sealed record Candidate(SerializedObjectEntry Entry, ObjectPointer Script, int ListCountOffset, ImmutableArray<int> Languages);

    /// <summary>読み込んで解析済みの外部参照ファイル。</summary>
    /// <param name="Data">ファイルの内容。</param>
    /// <param name="Scripts">
    /// MonoScript のオブジェクトを PathId で引けるようにした索引（同じ PathId が複数あれば、表で先に現れたもの）。
    /// 候補ごとに表全体をなめると、細工されたファイルで処理時間が膨らむため。
    /// </param>
    private sealed record ExternalFile(ReadOnlyMemory<byte> Data, IReadOnlyDictionary<long, SerializedObjectEntry> Scripts);

    /// <summary>確認できなかった理由を、件数を絞って括弧書きにする（理由が無ければ空文字列）。</summary>
    private static string FormatNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            return string.Empty;
        }

        var rest = notes.Count > MaxNotesShown ? $" / ほか {notes.Count - MaxNotesShown} 件" : string.Empty;
        return $"（{string.Join(" / ", notes.Take(MaxNotesShown))}{rest}）";
    }

    /// <summary>
    /// MonoBehaviour が「言語リストで終わる」構造かどうかを調べ、一致すれば候補として返す。
    /// 一致しないものは対象外なので null を返す（エラーではない）。
    /// </summary>
    private static Candidate? TryReadCandidate(ReadOnlySpan<byte> sceneData, SerializedObjectEntry entry)
    {
        var body = sceneData.Slice(entry.DataStart, entry.ByteSize);
        if (!UnityObjectReader.ReadMonoBehaviourHeader(body).TryGetValue(out var header, out _))
        {
            return null;
        }

        // リストの要素数が書かれている位置（オブジェクト本体内）。巨大な本体でも桁あふれしないよう long で計算する。
        var listOffsetInBody = (long)header.FieldsOffset + (PointerFieldCountBeforeList * ObjectPointer.SerializedSize);
        if (entry.ByteSize < listOffsetInBody + sizeof(int))
        {
            return null;
        }

        var listOffset = (int)listOffsetInBody; // 本体の大きさ（int）以下であることを上で確かめたので int に収まる
        var count = BinaryPrimitives.ReadInt32LittleEndian(body[listOffset..]);
        if (count < 1 || count > GameLanguage.Count)
        {
            return null;
        }

        // リストが最後のフィールドであること（本体がリストの終わりでちょうど終わること）を要求する。
        if (entry.ByteSize != listOffsetInBody + sizeof(int) + ((long)count * ListElementSize))
        {
            return null;
        }

        var languages = ImmutableArray.CreateBuilder<int>(count);
        for (var i = 0; i < count; i++)
        {
            languages.Add(BinaryPrimitives.ReadInt32LittleEndian(body[(listOffset + sizeof(int) + (i * ListElementSize))..]));
        }

        // 値がすべて定義済みの言語で、重複が無いこと。
        var values = languages.MoveToImmutable();
        if (!values.All(GameLanguage.IsValid) || values.Distinct().Count() != values.Length)
        {
            return null;
        }

        return new Candidate(entry, header.Script, entry.DataStart + listOffset, values);
    }

    /// <summary>候補のスクリプトが、ゲーム本体の ApplicationManager かどうかを確認する。</summary>
    private static Result<bool> IsTargetScript(
        ObjectPointer script,
        ImmutableArray<string> externalPaths,
        Func<string, ReadOnlyMemory<byte>?> loadExternal,
        Dictionary<string, Result<ExternalFile>> cache)
    {
        // ゲームのスクリプト情報は別ファイルにあるはずなので、同一ファイル内参照や範囲外は対象外とする。
        if (script.FileId < 1 || script.FileId > externalPaths.Length)
        {
            return Result.Ok(false);
        }

        // 外部参照は「library/unity default resources」のようなパス付きの場合があるため、ファイル名だけを使う。
        var fileName = Path.GetFileName(externalPaths[script.FileId - 1]);
        if (!cache.TryGetValue(fileName, out var loaded))
        {
            loaded = LoadExternalFile(fileName, loadExternal);
            cache[fileName] = loaded;
        }

        if (!loaded.TryGetValue(out var external, out var loadError))
        {
            return Result.Fail<bool>(loadError);
        }

        if (!external.Scripts.TryGetValue(script.PathId, out var scriptEntry))
        {
            return Result.Ok(false);
        }

        var scriptBody = external.Data.Span.Slice(scriptEntry.DataStart, scriptEntry.ByteSize);
        if (!UnityObjectReader.ReadMonoScript(scriptBody).TryGetValue(out var info, out var scriptError))
        {
            return Result.Fail<bool>($"{fileName}: {scriptError}");
        }

        return Result.Ok(
            string.Equals(info.ClassName, TargetClassName, StringComparison.Ordinal)
            && string.Equals(info.AssemblyName, TargetAssemblyName, StringComparison.Ordinal));
    }

    /// <summary>外部参照ファイルを読み込んで解析する。ファイルが無い場合と解析できない場合は、理由付きの失敗を返す。</summary>
    private static Result<ExternalFile> LoadExternalFile(string fileName, Func<string, ReadOnlyMemory<byte>?> loadExternal)
    {
        var data = loadExternal(fileName);
        if (data is null)
        {
            return Result.Fail<ExternalFile>($"{fileName} が見つかりません");
        }

        if (!SerializedFileParser.Parse(data.Value.Span, fileName).TryGetValue(out var info, out var error))
        {
            return Result.Fail<ExternalFile>(error);
        }

        // MonoScript だけを PathId で引けるようにしておく（同じ PathId が複数あれば、表で先に現れたものを使う）。
        var scripts = new Dictionary<long, SerializedObjectEntry>();
        foreach (var entry in info.Objects.Where(entry => entry.ClassId == SerializedFileParser.MonoScriptClassId))
        {
            scripts.TryAdd(entry.PathId, entry);
        }

        return Result.Ok(new ExternalFile(data.Value, scripts));
    }

    /// <summary>
    /// 対象オブジェクトの直後に、どのオブジェクトにも属さない 0 が何バイト連続しているかを数える。
    /// Unity はオブジェクトを8バイト境界に揃えるため、ここに空きがあればファイルサイズを変えずに要素を1つ足せる。
    /// 表の上で他のオブジェクトが対象と重なっている場合（対象の本体や直後の領域を、別のオブジェクトも自分の範囲だと
    /// 主張している場合）は、書き換えるとそのオブジェクトの内容まで変えてしまうので、空きは無いものとして扱う。
    /// 正常なファイルではオブジェクト同士は重ならないため、この判定が効くのは壊れたファイルや細工されたファイルだけ。
    /// </summary>
    private static int CountZeroPaddingAfter(
        ReadOnlySpan<byte> sceneData,
        ImmutableArray<SerializedObjectEntry> objects,
        SerializedObjectEntry target)
    {
        // 表のエントリの位置は1件ごとに異なるので、対象自身を除く目印に使う。
        var others = objects.Where(entry => entry.TableEntryOffset != target.TableEntryOffset).ToImmutableArray();

        // 対象の本体と重なる（対象の終わりをまたぐものを含む）オブジェクトがあれば、安全に使える領域は無い。
        if (others.Any(entry => entry.DataStart < target.DataEnd && entry.DataEnd > target.DataStart))
        {
            return 0;
        }

        // 対象の終わり以降で最も近いオブジェクトの開始位置（無ければファイル終端）までが、空きの候補になる。
        var nextStart = others
            .Where(entry => entry.DataStart >= target.DataEnd)
            .Select(entry => entry.DataStart)
            .DefaultIfEmpty(sceneData.Length)
            .Min();

        var zeroCount = 0;
        while (target.DataEnd + zeroCount < nextStart && sceneData[target.DataEnd + zeroCount] == 0)
        {
            zeroCount++;
        }

        return zeroCount;
    }
}
