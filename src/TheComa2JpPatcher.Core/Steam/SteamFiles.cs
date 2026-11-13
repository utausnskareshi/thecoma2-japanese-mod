using System.Collections.Immutable;
using System.Globalization;

namespace TheComa2JpPatcher.Core.Steam;

/// <summary>libraryfolders.vdf から Steam ライブラリの場所を取り出す（副作用なし）。</summary>
public static class SteamLibraryFolders
{
    /// <summary>
    /// ライブラリフォルダのパス一覧を返す。次の2形式の両方に対応する。
    /// 新形式: <c>"0" { "path" "D:\\SteamLibrary" ... }</c> ／ 旧形式: <c>"1" "D:\\SteamLibrary"</c>
    /// </summary>
    public static Result<ImmutableArray<string>> ExtractPaths(string vdfText)
    {
        if (!VdfParser.Parse(vdfText).TryGetValue(out var root, out var error))
        {
            return Result.Fail<ImmutableArray<string>>(error);
        }

        var folders = root.FindObject("libraryfolders");
        if (folders is null)
        {
            return Result.Fail<ImmutableArray<string>>("libraryfolders.vdf に「libraryfolders」の項目がありません。");
        }

        var paths = folders.Entries
            .Select(entry => entry.Value switch
            {
                VdfObject library => library.FindString("path"),
                // 旧形式では、数字のキーだけがライブラリを表す（他は設定値）。空のキーは数字ではないので除く。
                VdfString text when entry.Key.Length > 0 && entry.Key.All(char.IsAsciiDigit) => text.Value,
                _ => null,
            })
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return Result.Ok(paths);
    }
}

/// <summary>appmanifest_*.acf のうち、インストール先の特定に使う項目。</summary>
/// <param name="AppId">Steam のアプリID。</param>
/// <param name="Name">ゲーム名。</param>
/// <param name="InstallDir">steamapps\common 以下のフォルダ名。</param>
/// <param name="StateFlags">インストール状態（4 が「インストール済み」）。読めなければ null。</param>
public sealed record AppManifest(string AppId, string Name, string InstallDir, int? StateFlags);

/// <summary>appmanifest_*.acf を読む（副作用なし）。</summary>
public static class AppManifestParser
{
    /// <summary>ACF テキストを解析する。</summary>
    public static Result<AppManifest> Parse(string acfText)
    {
        if (!VdfParser.Parse(acfText).TryGetValue(out var root, out var error))
        {
            return Result.Fail<AppManifest>(error);
        }

        var state = root.FindObject("AppState");
        if (state is null)
        {
            return Result.Fail<AppManifest>("appmanifest に「AppState」の項目がありません。");
        }

        var installDir = state.FindString("installdir");
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return Result.Fail<AppManifest>("appmanifest に「installdir」がありません。");
        }

        int? stateFlags = int.TryParse(state.FindString("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
            ? flags
            : null;

        return Result.Ok(new AppManifest(
            state.FindString("appid") ?? string.Empty,
            state.FindString("name") ?? string.Empty,
            installDir,
            stateFlags));
    }
}
