using System.Collections.Immutable;

namespace TheComa2JpPatcher.Core.Patching;

/// <summary>選択可能言語リストの状態。フラグではなくファイルの中身から判定する。</summary>
public enum PatchState
{
    /// <summary>日本語がリストに無い（パッチ未適用）。</summary>
    NotApplied,

    /// <summary>リストの末尾に日本語がある（このツールが追加した形）。</summary>
    Applied,

    /// <summary>
    /// 日本語が末尾以外に入っている。将来ゲーム側が公式に対応した場合などを想定し、
    /// このツールでは一切書き換えない。
    /// </summary>
    JapaneseAlreadySelectable,
}

/// <summary>リストの内容から <see cref="PatchState"/> を決める（副作用なし）。</summary>
public static class PatchStateDetector
{
    /// <summary>
    /// 状態を判定する。
    /// このツールは日本語を必ず末尾へ追加するので、「末尾が日本語」であることを適用済みの目印にする。
    /// </summary>
    public static PatchState Detect(ImmutableArray<int> languages)
    {
        if (!languages.Contains(GameLanguage.Japanese))
        {
            return PatchState.NotApplied;
        }

        return languages[^1] == GameLanguage.Japanese
            ? PatchState.Applied
            : PatchState.JapaneseAlreadySelectable;
    }
}
