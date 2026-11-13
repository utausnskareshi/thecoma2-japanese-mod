using System.Collections.Immutable;

namespace TheComa2JpPatcher.Core.Steam;

/// <summary>
/// ゲームのインストール先と、そこから決まる各ファイルの場所。
/// </summary>
/// <param name="RootDirectory">ゲームのルートフォルダ（exe がある場所）。</param>
public sealed record GameInstall(string RootDirectory)
{
    /// <summary>ゲーム本体の実行ファイル名。</summary>
    public const string ExecutableFileName = "TheComa2ViciousSisters.exe";

    /// <summary>実行中かどうかの判定に使うプロセス名（拡張子なし）。</summary>
    public const string ProcessName = "TheComa2ViciousSisters";

    /// <summary>Unity のデータフォルダ名。</summary>
    public const string DataDirectoryName = "TheComa2ViciousSisters_Data";

    /// <summary>言語リストを含むシーンファイル名。</summary>
    public const string SceneFileName = "level0";

    /// <summary>このツールがバックアップを置くフォルダ名。</summary>
    public const string BackupDirectoryName = "TheComa2JpPatcher_Backup";

    /// <summary>ゲーム本体の実行ファイルの場所。</summary>
    public string ExecutablePath => Path.Combine(RootDirectory, ExecutableFileName);

    /// <summary>Unity のデータフォルダの場所。</summary>
    public string DataDirectory => Path.Combine(RootDirectory, DataDirectoryName);

    /// <summary>書き換え対象のシーンファイル（level0）の場所。</summary>
    public string ScenePath => Path.Combine(DataDirectory, SceneFileName);

    /// <summary>このツールがバックアップを置くフォルダの場所（ゲームのフォルダ直下）。</summary>
    public string BackupDirectory => Path.Combine(RootDirectory, BackupDirectoryName);
}

/// <summary>インストール先の検出結果。</summary>
public abstract record GameLocateResult
{
    private GameLocateResult()
    {
    }

    /// <summary>見つかった。</summary>
    public sealed record Found(GameInstall Install) : GameLocateResult;

    /// <summary>見つからなかった。</summary>
    /// <param name="Reason">利用者へ見せる理由。</param>
    /// <param name="Details">調べた内容の記録（問い合わせ時の手がかり）。</param>
    public sealed record NotFound(string Reason, ImmutableArray<string> Details) : GameLocateResult
    {
        // 「確認した内容」として示す件数と、1行あたりの文字数の上限。
        // 設定ファイルに大量のライブラリが書かれていても、メッセージが画面に収まらなくならないようにする。
        private const int MaxDetailsShown = 10;
        private const int MaxDetailLength = 300;

        /// <summary>
        /// 起動時に表示する文面を作る（副作用なし）。
        /// 理由・次に行うこと・ツールが終了することを伝え、調べた内容があれば末尾に添える。
        /// 調べた内容には設定ファイル由来の文字列が入りうるので、件数と長さを抑え、制御文字は除いて表示する。
        /// </summary>
        public string ToUserMessage()
        {
            var message =
                $"{Reason}\n\n" +
                "Steam でゲームをインストールしてから、もう一度このツールを起動してください。\n" +
                "ツールを終了します。";

            if (Details.IsEmpty)
            {
                return message;
            }

            var lines = Details
                .Take(MaxDetailsShown)
                .Select(detail => $"・{DisplayText.Sanitize(detail, MaxDetailLength)}")
                .ToList();
            if (Details.Length > MaxDetailsShown)
            {
                lines.Add($"・ほか {Details.Length - MaxDetailsShown} 件");
            }

            return $"{message}\n\n【確認した内容】\n{string.Join("\n", lines)}";
        }
    }
}

/// <summary>Steam 本体のインストール先を探した結果。</summary>
/// <param name="Candidates">見つかった候補（レジストリに書かれていたパス）。</param>
/// <param name="Notes">
/// 調べられなかった場所とその理由。読めない場所があっても残りの場所は調べるが、理由は捨てずにここへ残し、
/// 最終的にゲームが見つからなかった場合の説明に添える。
/// </param>
public sealed record SteamRootProbe(ImmutableArray<string> Candidates, ImmutableArray<string> Notes);

/// <summary>
/// 検出処理が外の世界から得る情報。レジストリやファイルの読み取りといった副作用を関数として受け取り、
/// 検出ロジック自体は入力だけで結果が決まるようにする（テストでは偽の関数を渡す）。
/// </summary>
/// <param name="ProbeSteamRoots">Steam 本体のインストール先の候補を探す（レジストリ由来）。</param>
/// <param name="ReadTextFileOrNull">テキストファイルを読む。存在しなければ null。</param>
/// <param name="FileExists">ファイルが存在するか。</param>
public sealed record LocatorEnvironment(
    Func<SteamRootProbe> ProbeSteamRoots,
    Func<string, string?> ReadTextFileOrNull,
    Func<string, bool> FileExists);

/// <summary>Steam 版 The Coma 2: Vicious Sisters のインストール先を探す。</summary>
public static class GameLocator
{
    /// <summary>The Coma 2: Vicious Sisters の Steam アプリID。</summary>
    public const string AppId = "1045720";

    /// <summary>ゲームの表示名。</summary>
    public const string GameTitle = "The Coma 2: Vicious Sisters";

    // 採用しなかったパスを確認内容に示すときの、最大の文字数。
    private const int MaxPathDisplayLength = 300;

    /// <summary>
    /// Steam 本体 → ライブラリフォルダ → アプリのマニフェスト → ゲームのファイル、の順にたどる。
    /// </summary>
    public static GameLocateResult Locate(LocatorEnvironment environment)
    {
        var details = ImmutableArray.CreateBuilder<string>();

        // 候補を探す途中で読めなかった場所があれば、その理由を先に記録しておく。
        var probe = environment.ProbeSteamRoots();
        details.AddRange(probe.Notes);

        var steamRoots = NormalizeDirectories(probe.Candidates, details);
        if (steamRoots.IsEmpty)
        {
            return new GameLocateResult.NotFound("Steam のインストールが見つかりませんでした。", details.ToImmutable());
        }

        // Steam 本体のフォルダ自体も1つのライブラリなので、追加ライブラリと合わせて調べる。
        var libraries = NormalizeDirectories(
            steamRoots.SelectMany(root => new[] { root }.Concat(ReadLibraryPaths(environment, root, details))),
            details);

        // ネットワーク上（\\サーバー\共有）のライブラリは、到達できないと調べるだけで長く待たされることがあるので、
        // ローカルのライブラリを先に調べる。並べ替えは安定なので、それぞれの中での順序は変わらない。
        var orderedLibraries = libraries.OrderBy(library => library.StartsWith(@"\\", StringComparison.Ordinal) ? 1 : 0);

        foreach (var library in orderedLibraries)
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
            if (!TryReadText(environment, manifestPath).TryGetValue(out var manifestText, out var readError))
            {
                // 読めないファイルが1つあっても、残りのライブラリは調べる。
                details.Add(readError);
                continue;
            }

            if (manifestText is null)
            {
                details.Add($"{library}: このライブラリにはインストールされていません。");
                continue;
            }

            if (!AppManifestParser.Parse(manifestText).TryGetValue(out var manifest, out var manifestError))
            {
                details.Add($"{manifestPath}: {manifestError}");
                continue;
            }

            var commonDirectory = Path.Combine(library, "steamapps", "common");
            if (!IsSafeFolderName(manifest.InstallDir) || !IsDirectChild(commonDirectory, manifest.InstallDir))
            {
                details.Add($"{manifestPath}: インストール先のフォルダ名が不正です。");
                continue;
            }

            var install = new GameInstall(Path.Combine(commonDirectory, manifest.InstallDir));
            if (environment.FileExists(install.ExecutablePath) && environment.FileExists(install.ScenePath))
            {
                return new GameLocateResult.Found(install);
            }

            details.Add($"{install.RootDirectory}: インストール情報はありますが、ゲームのファイルが見つかりません（ダウンロード中か、削除された可能性があります）。");
        }

        return new GameLocateResult.NotFound($"{GameTitle}（Steam版）がインストールされていません。", details.ToImmutable());
    }

    /// <summary>Steam 本体のフォルダから、追加ライブラリの一覧を読む。読めない場合は空として扱い、理由を記録する。</summary>
    private static ImmutableArray<string> ReadLibraryPaths(
        LocatorEnvironment environment,
        string steamRoot,
        ImmutableArray<string>.Builder details)
    {
        // 現行は steamapps 配下。古い Steam は config 配下に置いていた。
        var candidates = new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        };

        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var candidate in candidates)
        {
            if (!TryReadText(environment, candidate).TryGetValue(out var text, out var readError))
            {
                details.Add(readError);
                continue;
            }

            if (text is null)
            {
                continue;
            }

            if (SteamLibraryFolders.ExtractPaths(text).TryGetValue(out var extracted, out var error))
            {
                paths.AddRange(extracted);
            }
            else
            {
                details.Add($"{candidate}: {error}");
            }
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// テキストファイルを読む。存在しなければ成功で null、読めなければ（使用中・アクセス拒否など）理由付きの失敗を返す。
    /// 1つのファイルが読めないだけで、検出全体を打ち切らないようにするため。
    /// </summary>
    private static Result<string?> TryReadText(LocatorEnvironment environment, string path)
    {
        try
        {
            return Result.Ok(environment.ReadTextFileOrNull(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<string?>($"{path}: 読み取れませんでした（{ex.Message}）。");
        }
    }

    /// <summary>
    /// パスの一覧を、比較と結合に使える形へ整えて重複を除く。
    /// 採用できないパスは調べず、黙って捨てる代わりに、その旨を確認内容へ記録する。
    /// </summary>
    private static ImmutableArray<string> NormalizeDirectories(IEnumerable<string> paths, ImmutableArray<string>.Builder details)
    {
        var directories = ImmutableArray.CreateBuilder<string>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = NormalizeDirectoryOrNull(path);
            if (directory is null)
            {
                details.Add($"{DisplayText.Sanitize(path, MaxPathDisplayLength)}: パスの形式が正しくないため、この場所は調べていません。");
            }
            else if (!directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(directory);
            }
        }

        return directories.ToImmutable();
    }

    /// <summary>
    /// フォルダのパスを整える。採用できないパスの場合は null を返す。
    /// ・区切り文字の向きを揃え、「..」や連続した区切りを解消し、末尾の区切りを落とす（ドライブ直下の「D:\」は残す）
    /// ・制御文字を含むパスは、壊れた設定とみなして採用しない
    /// ・相対パスは、このツールを起動した場所によって指す先が変わってしまうので採用しない
    /// </summary>
    private static string? NormalizeDirectoryOrNull(string path)
    {
        var candidate = path.Trim().Replace('/', '\\');

        // 「D:」のようにドライブ名だけが書かれている場合は、そのドライブの直下として扱う
        // （そのままだと「D ドライブの現在のフォルダ」を指す相対パスになる）。
        if (candidate.Length == 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':')
        {
            candidate += '\\';
        }

        if (candidate.Any(char.IsControl) || !Path.IsPathFullyQualified(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return HasUsableRoot(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // パスとして解釈できない。呼び出し側が「調べていない」ことを記録する。
            return null;
        }
    }

    /// <summary>
    /// パスの先頭が、Steam のライブラリとしてあり得る形かどうかを確かめる。
    /// 受け付けるのは「D:\…」（ドライブ名から始まる形）と「\\サーバー\共有\…」（サーバー名と共有名がそろったネットワークパス）だけ。
    /// 「\\」だけの不完全なネットワークパスや、「\\?\」「\\.\」で始まる特殊な表記は、壊れた設定とみなして受け付けない。
    /// </summary>
    private static bool HasUsableRoot(string fullPath)
    {
        if (fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\')
        {
            return true;
        }

        if (!fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = fullPath[2..].Split('\\');
        return parts.Length >= 2
            && parts[0].Length > 0
            && parts[1].Length > 0
            && parts[0] is not ("?" or ".");
    }

    /// <summary>
    /// 「親フォルダ＋名前」を絶対パスに直したとき、本当に親フォルダの直下を指すかを確かめる。
    /// 名前の文字の検査をすり抜ける書き方（古い Windows での「NUL」などの装置名を含む）があっても、
    /// 別の場所を指すインストール先を採用しないための二重の確認。
    /// </summary>
    private static bool IsDirectChild(string parent, string name)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(parent, name));
            return string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// フォルダ名が、親フォルダへ抜けたり別の場所を指したりしない単純な名前であることを確認する。
    /// 区切り文字や「..」のほか、制御文字、前後の空白、末尾のピリオドも認めない
    /// （Windows は前後の空白や末尾のピリオドを黙って取り除くため、書かれている名前とは別のフォルダを指しうる）。
    /// </summary>
    private static bool IsSafeFolderName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "."
        && name != ".."
        && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !name.Any(char.IsControl)
        && name == name.Trim()
        && !name.EndsWith('.');
}
