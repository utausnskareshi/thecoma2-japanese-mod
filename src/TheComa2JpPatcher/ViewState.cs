using TheComa2JpPatcher.Core;
using TheComa2JpPatcher.Core.Operations;
using TheComa2JpPatcher.Core.Patching;

namespace TheComa2JpPatcher;

/// <summary>
/// 画面に表示する内容。ゲームの状態から一意に決まる値だけを持ち、画面部品には依存しない。
/// </summary>
/// <param name="State">現在の状態。確認できなかった場合は null。</param>
/// <param name="StatusText">「現在の状態」欄の文言。</param>
/// <param name="StatusColor">状態の文字色。</param>
/// <param name="ButtonText">主ボタンの文言（日本語化する／元に戻す）。</param>
/// <param name="ButtonEnabled">主ボタンを押せるか。</param>
/// <param name="Guidance">次に行うことの案内。</param>
/// <param name="Details">技術的な詳細（問い合わせ時の手がかり）。</param>
internal sealed record ViewState(
    PatchState? State,
    string StatusText,
    Color StatusColor,
    string ButtonText,
    bool ButtonEnabled,
    string Guidance,
    string Details);

/// <summary>状態の確認結果から、表示内容を組み立てる（副作用なし）。</summary>
internal static class ViewStateFactory
{
    /// <summary>ゲーム内で日本語を選ぶ手順。メニュー名はゲームの英語表示に合わせている。</summary>
    public const string HowToSelectJapanese =
        "ゲームを起動し、メニューの OPTIONS → LANGUAGE で「日本語」を選択してください。";

    /// <summary>元に戻す前の注意。セーブデータには触れない設計のため、言語の変更は利用者に行ってもらう。</summary>
    public const string RestoreCaution =
        "日本語のまま元に戻すと、ゲーム内で言語を切り替えられなくなります。" +
        "先にゲーム内の言語を日本語以外（English など）へ変更してください。";

    private static readonly Color AppliedColor = Color.FromArgb(0x1B, 0x7F, 0x3B);
    private static readonly Color ErrorColor = Color.FromArgb(0xB0, 0x2A, 0x2A);

    public static ViewState From(Result<InstallStatus> inspection)
    {
        if (!inspection.TryGetValue(out var status, out var error))
        {
            return new ViewState(
                State: null,
                StatusText: "確認できませんでした",
                StatusColor: ErrorColor,
                ButtonText: "日本語化する",
                ButtonEnabled: false,
                Guidance:
                    $"{error}\n\n" +
                    "ゲームが更新された場合や、ファイルが壊れている場合は、Steam の「インストール済みファイルの整合性を確認」を" +
                    "実行してから、もう一度このツールを起動してください。",
                Details: $"ツールのバージョン: {AppInfo.Version}");
        }

        var details =
            $"選択できる言語: {string.Join(", ", status.Languages.Select(GameLanguage.NameOf))}\r\n" +
            $"level0 の SHA-256: {status.Sha256}\r\n" +
            $"バックアップ: {(status.BackupExists ? "あり" : "なし")}\r\n" +
            $"ツールのバージョン: {AppInfo.Version}";

        return status.State switch
        {
            PatchState.NotApplied => new ViewState(
                status.State,
                "未適用（ゲーム内で日本語を選択できません）",
                SystemColors.ControlText,
                "日本語化する",
                ButtonEnabled: true,
                "「日本語化する」を押すと、ゲームに同梱されている公式の日本語を、ゲーム内の言語選択に追加します。\n" +
                "このツール自体には、ゲームのテキストや画像などのデータは含まれていません。",
                details),

            PatchState.Applied => new ViewState(
                status.State,
                "日本語化済み（ゲーム内で日本語を選択できます）",
                AppliedColor,
                "元に戻す",
                ButtonEnabled: true,
                $"{HowToSelectJapanese}\n\n【元に戻すとき】{RestoreCaution}",
                details),

            PatchState.JapaneseAlreadySelectable => new ViewState(
                status.State,
                "対応不要（パッチなしで日本語を選択できます）",
                AppliedColor,
                "日本語化は不要です",
                ButtonEnabled: false,
                "このバージョンのゲームは、パッチなしで日本語を選択できます。このツールでの書き換えは行いません。",
                details),

            _ => throw new InvalidOperationException($"想定外の状態です: {status.State}"),
        };
    }
}
