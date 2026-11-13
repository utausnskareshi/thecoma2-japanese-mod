using System.Buffers.Binary;
using System.Collections.Immutable;

namespace TheComa2JpPatcher.Core.Patching;

/// <summary>
/// 1か所の書き換え。「元の値がこれなら、この値にする」という形で持つことで、
/// 想定外のファイルを誤って書き換えないようにする。
/// </summary>
/// <param name="Offset">書き換える位置（ファイル先頭からの絶対位置）。</param>
/// <param name="Expected">書き換え前に入っているはずのバイト列。</param>
/// <param name="Replacement">書き換え後のバイト列（Expected と同じ長さ）。</param>
public sealed record ByteEdit(int Offset, ImmutableArray<byte> Expected, ImmutableArray<byte> Replacement);

/// <summary>
/// 言語リストへ日本語を追加する／取り除くための書き換え計画を作る（副作用なし）。
/// 実際のファイル書き込みは行わず、「どこをどう変えるか」と「変えた後のバイト列」を返すだけにする。
/// </summary>
public static class PatchPlanner
{
    private const int ElementSize = sizeof(int);

    /// <summary>
    /// 日本語をリストの末尾へ追加する計画を作る。
    /// オブジェクト直後の0埋め4バイトを新しい要素として使うため、ファイルサイズも他のオブジェクトの位置も変わらない。
    /// </summary>
    public static Result<ImmutableArray<ByteEdit>> PlanApply(LanguageListLocation location)
    {
        var state = PatchStateDetector.Detect(location.Languages);
        if (state != PatchState.NotApplied)
        {
            return Result.Fail<ImmutableArray<ByteEdit>>("既に日本語が選択可能な状態のため、追加は行いません。");
        }

        if (location.ZeroPaddingAfterObject < ElementSize)
        {
            return Result.Fail<ImmutableArray<ByteEdit>>(
                "言語リストの直後に空き領域が無いため、このバージョンのゲームには対応していません。");
        }

        var count = location.Languages.Length;
        var size = location.Object.ByteSize;
        return Result.Ok<ImmutableArray<ByteEdit>>(
        [
            // 1. リストの要素数を1増やす。
            new ByteEdit(location.ListCountOffset, Int32Bytes(count), Int32Bytes(count + 1)),

            // 2. オブジェクト直後の0埋め部分に、新しい要素として日本語を書く。
            new ByteEdit(location.Object.DataEnd, Int32Bytes(0), Int32Bytes(GameLanguage.Japanese)),

            // 3. オブジェクト表のサイズ欄を、要素1つ分だけ大きくする。
            new ByteEdit(location.Object.ByteSizeFieldOffset, Int32Bytes(size), Int32Bytes(size + ElementSize)),
        ]);
    }

    /// <summary>
    /// <see cref="PlanApply"/> の逆。末尾の日本語を取り除き、要素数とサイズ欄を元に戻す。
    /// 取り除いた場所は元どおり0で埋めるので、適用前と1バイトも違わない内容に戻る。
    /// </summary>
    public static Result<ImmutableArray<ByteEdit>> PlanRevert(LanguageListLocation location)
    {
        var state = PatchStateDetector.Detect(location.Languages);
        if (state != PatchState.Applied)
        {
            return Result.Fail<ImmutableArray<ByteEdit>>("日本語化パッチが適用された状態ではないため、元に戻す処理は行いません。");
        }

        var count = location.Languages.Length;
        var size = location.Object.ByteSize;
        return Result.Ok<ImmutableArray<ByteEdit>>(
        [
            new ByteEdit(location.ListCountOffset, Int32Bytes(count), Int32Bytes(count - 1)),
            new ByteEdit(location.Object.DataEnd - ElementSize, Int32Bytes(GameLanguage.Japanese), Int32Bytes(0)),
            new ByteEdit(location.Object.ByteSizeFieldOffset, Int32Bytes(size), Int32Bytes(size - ElementSize)),
        ]);
    }

    /// <summary>
    /// 計画を元のバイト列に適用した「新しいバイト列」を返す。引数の内容は変更しない。
    /// 書き換え前の値が計画と違う場合は、何も変えずに失敗を返す。
    /// </summary>
    public static Result<byte[]> Execute(ReadOnlySpan<byte> source, ImmutableArray<ByteEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.Expected.Length != edit.Replacement.Length)
            {
                return Result.Fail<byte[]>($"書き換え計画が不正です（位置 0x{edit.Offset:X} で長さが一致しません）。");
            }

            // 位置と長さの足し算が int の上限を超えても検査をすり抜けないよう、long で比べる。
            if (edit.Offset < 0 || (long)edit.Offset + edit.Expected.Length > source.Length)
            {
                return Result.Fail<byte[]>($"書き換え位置がファイルの範囲外です（位置 0x{edit.Offset:X}）。");
            }

            if (!source.Slice(edit.Offset, edit.Expected.Length).SequenceEqual(edit.Expected.AsSpan()))
            {
                return Result.Fail<byte[]>($"書き換え前の内容が想定と異なります（位置 0x{edit.Offset:X}）。ファイルは変更していません。");
            }
        }

        // すべての検証を通ってから、コピーに対して書き込む。
        var result = source.ToArray();
        foreach (var edit in edits)
        {
            edit.Replacement.AsSpan().CopyTo(result.AsSpan(edit.Offset));
        }

        return Result.Ok(result);
    }

    /// <summary>
    /// 書き換え後の内容を逆向きに戻すと、元の内容と1バイトも違わずに一致するかを確かめる（副作用なし）。
    /// 「いつでも元に戻せる」ことを、実際に書き込む前にメモリ上で実証するために使う。
    /// </summary>
    /// <param name="original">書き換える前の内容。</param>
    /// <param name="patched">書き換えた後の内容。</param>
    /// <param name="patchedLocation"><paramref name="patched"/> を解析して得た、言語リストの位置。</param>
    public static bool RevertsExactly(ReadOnlySpan<byte> original, ReadOnlySpan<byte> patched, LanguageListLocation patchedLocation) =>
        PlanRevert(patchedLocation).TryGetValue(out var edits, out _)
        && Execute(patched, edits).TryGetValue(out var reverted, out _)
        && reverted.AsSpan().SequenceEqual(original);

    /// <summary>int32 を、ファイル内での並び（リトルエンディアンの4バイト）にする。</summary>
    private static ImmutableArray<byte> Int32Bytes(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return [.. buffer];
    }
}
