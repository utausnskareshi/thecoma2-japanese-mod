using System.Collections.Immutable;

namespace TheComa2JpPatcher.Core.Patching;

/// <summary>
/// ゲーム内の言語を表す列挙値（ApplicationManager.ELanguage）の対応表。
/// 値はゲーム側の定義順に従う。
/// </summary>
public static class GameLanguage
{
    /// <summary>日本語（JPN）の値。</summary>
    public const int Japanese = 8;

    // 添字が列挙値に対応する。
    private static readonly ImmutableArray<string> Names =
        ["ENG", "KOR", "UKR", "RUS", "SPA", "FRN", "GER", "CHN", "JPN", "THA", "BR", "PTG"];

    /// <summary>定義されている言語の数。</summary>
    public static int Count => Names.Length;

    /// <summary>値がゲームの定義する言語かどうか。</summary>
    public static bool IsValid(int value) => value >= 0 && value < Names.Length;

    /// <summary>表示用の言語コードを返す。未定義の値は「?数値」とする。</summary>
    public static string NameOf(int value) => IsValid(value) ? Names[value] : $"?{value}";
}
