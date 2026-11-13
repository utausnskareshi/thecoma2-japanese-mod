using System.Collections.Immutable;
using System.Reflection;
using System.Security;
using Microsoft.Win32;
using TheComa2JpPatcher.Core;
using TheComa2JpPatcher.Core.Operations;
using TheComa2JpPatcher.Core.Steam;

namespace TheComa2JpPatcher;

/// <summary>アプリ自身の情報（表示名・バージョン）。</summary>
internal static class AppInfo
{
    /// <summary>ウィンドウやメッセージの題名。非公式のツールであることを常に明示する。</summary>
    public const string Title = "The Coma 2: Vicious Sisters 日本語化パッチ（非公式）";

    /// <summary>ツールのバージョン。ビルド情報（+以降）は取り除く。</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}

/// <summary>
/// コアライブラリへ渡す「外の世界」の実装（レジストリ・ファイル・プロセス・時計）。
/// 副作用のある処理をここへ集め、コア側のロジックを入力だけで決まる形に保つ。
/// </summary>
internal static class SystemEnvironment
{
    /// <summary>Steam の検出に使う環境。</summary>
    public static LocatorEnvironment CreateLocatorEnvironment() =>
        new(ProbeSteamRoots, ReadTextFileOrNull, File.Exists);

    /// <summary>日本語化／元に戻す操作に使う環境。</summary>
    public static PatchEnvironment CreatePatchEnvironment() =>
        new(GameProcess.IsRunning, AppInfo.Version, () => DateTimeOffset.UtcNow);

    /// <summary>
    /// レジストリから Steam 本体のインストール先の候補を集める。
    /// 利用者ごとの設定（HKCU）と、インストーラーが書くマシン全体の設定（HKLM の32bit/64bit）の両方を見る。
    /// 読めなかった場所は、理由を <see cref="SteamRootProbe.Notes"/> に残す。
    /// </summary>
    private static SteamRootProbe ProbeSteamRoots()
    {
        (RegistryHive Hive, RegistryView View, string KeyPath, string ValueName)[] sources =
        [
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        ];

        var results = sources
            .Select(source => ReadRegistryString(source.Hive, source.View, source.KeyPath, source.ValueName))
            .ToImmutableArray();

        return new SteamRootProbe(
            Candidates:
            [
                .. results
                    .OfType<Result<string?>.Ok>()
                    .Select(ok => ok.Value)
                    .OfType<string>()
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
            ],
            // HKLM は32bit/64bitの両方を見るため、同じ理由が2回出ないようにまとめる。
            Notes: [.. results.OfType<Result<string?>.Fail>().Select(fail => fail.Message).Distinct()]);
    }

    /// <summary>
    /// レジストリの文字列値を読む。
    /// キーや値が無い場合は「成功で null」（そこには Steam の情報が無いだけ）、
    /// 読み取り自体を拒否された場合などは理由付きの失敗を返す。
    /// </summary>
    private static Result<string?> ReadRegistryString(RegistryHive hive, RegistryView view, string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            return Result.Ok(key?.GetValue(valueName) as string);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // 読めない場所が1つあっても、残りの場所で Steam が見つかることがあるので処理は続ける。
            // ただし理由は捨てずに返し、最終的にゲームが見つからなかった場合の説明へ添える。
            return Result.Fail<string?>($"レジストリの {HiveName(hive)}\\{keyPath} を読み取れませんでした（{ex.Message}）。");
        }
    }

    /// <summary>レジストリのルートキーを、利用者がレジストリエディターで見る表記にする。</summary>
    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        _ => hive.ToString(),
    };

    /// <summary>
    /// テキストファイルを読む。存在しなければ null。
    /// 存在するのに読めない場合は例外をそのまま上へ伝え、起動時の処理で理由を表示する。
    /// </summary>
    private static string? ReadTextFileOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
