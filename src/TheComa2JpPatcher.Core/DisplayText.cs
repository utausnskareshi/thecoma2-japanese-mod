namespace TheComa2JpPatcher.Core;

/// <summary>ファイルなどから読んだ文字列を、利用者へ見せるメッセージに埋め込める形へ整える（副作用なし）。</summary>
internal static class DisplayText
{
    /// <summary>
    /// 制御文字（改行やタブを含む）を「?」へ置き換え、長すぎる場合は途中で切って「…」を付ける。
    /// 壊れた（または細工された）ファイル由来の文字列で、表示が崩れたり、メッセージが巨大になったりするのを防ぐ。
    /// </summary>
    /// <param name="text">元の文字列。</param>
    /// <param name="maxLength">残す最大の文字数（これを超える部分は省略する）。</param>
    public static string Sanitize(string text, int maxLength)
    {
        var shortened = text.Length > maxLength ? $"{text[..maxLength]}…" : text;
        return string.Concat(shortened.Select(character => char.IsControl(character) ? '?' : character));
    }
}
