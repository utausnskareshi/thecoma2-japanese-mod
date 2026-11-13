using System.Collections.Immutable;
using TheComa2JpPatcher.Core.Steam;

namespace TheComa2JpPatcher.Core.Tests;

/// <summary>Steam の設定ファイルの解析と、インストール先の検出のテスト。</summary>
public sealed class SteamTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";
    private const string Library = @"D:\SteamLibrary";

    // 実際の Steam が書き出す形式を模した内容（値はテスト用の作り物）。
    private const string LibraryFoldersVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"277286956"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"1045720"		"0"
        		}
        	}
        }
        """;

    private const string AppManifestAcf = """
        "AppState"
        {
        	"appid"		"1045720"
        	"name"		"The Coma 2: Vicious Sisters"
        	"StateFlags"		"4"
        	"installdir"		"The Coma 2 Vicious Sisters"
        }
        """;

    private static readonly string GameRoot = Path.Combine(Library, "steamapps", "common", "The Coma 2 Vicious Sisters");

    /// <summary>辞書に入れたテキストだけが「存在するファイル」になる偽の環境を作る。</summary>
    private static LocatorEnvironment FakeEnvironment(
        IEnumerable<string> steamRoots,
        Dictionary<string, string> textFiles,
        IEnumerable<string> existingFiles)
    {
        var files = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(textFiles, StringComparer.OrdinalIgnoreCase);
        return new LocatorEnvironment(
            () => new SteamRootProbe([.. steamRoots], []),
            path => texts.GetValueOrDefault(path),
            files.Contains);
    }

    private static Dictionary<string, string> StandardTextFiles() => new()
    {
        [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] = LibraryFoldersVdf,
        [Path.Combine(Library, "steamapps", "appmanifest_1045720.acf")] = AppManifestAcf,
    };

    private static string[] GameFiles() =>
    [
        Path.Combine(GameRoot, "TheComa2ViciousSisters.exe"),
        Path.Combine(GameRoot, "TheComa2ViciousSisters_Data", "level0"),
    ];

    [Fact]
    public void VDFの入れ子とエスケープとコメントを解析できる()
    {
        const string text = """
            // コメント行
            "root"
            {
                "path"   "C:\\Games\\Steam"
                bare     value
                "quote"  "say \"hi\""
                "child"  { "k" "v" }
            }
            """;

        Assert.True(VdfParser.Parse(text).TryGetValue(out var parsed, out var error), error);
        var root = parsed.FindObject("ROOT"); // キーは大文字小文字を区別しない

        Assert.NotNull(root);
        Assert.Equal(@"C:\Games\Steam", root.FindString("path"));
        Assert.Equal("value", root.FindString("bare"));
        Assert.Equal("say \"hi\"", root.FindString("quote"));
        Assert.Equal("v", root.FindObject("child")?.FindString("k"));
        Assert.Null(root.FindString("missing"));
    }

    [Theory]
    [InlineData("\"a\" {")]            // 閉じ括弧が無い
    [InlineData("\"a\" \"b\" }")]      // 余分な閉じ括弧
    [InlineData("\"a\"")]              // 値が無い
    [InlineData("\"a\" \"unterminated")] // 引用符が閉じていない
    [InlineData("{ \"a\" \"b\" }")]    // キーの無い括弧
    public void 壊れたVDFは例外ではなく失敗として返す(string text)
    {
        Assert.False(VdfParser.Parse(text).TryGetValue(out _, out var error));
        Assert.Contains("VDF を解析できませんでした", error);
    }

    [Fact]
    public void ライブラリ一覧を新形式から取り出せる()
    {
        Assert.True(SteamLibraryFolders.ExtractPaths(LibraryFoldersVdf).TryGetValue(out var paths, out var error), error);

        Assert.Equal([SteamRoot, Library], paths.AsEnumerable());
    }

    [Fact]
    public void ライブラリ一覧を旧形式から取り出せる()
    {
        const string oldFormat = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "1234567890"
                "ContentStatsID"       "-1"
                "1"                    "D:\\SteamLibrary"
                "2"                    "E:\\Games"
            }
            """;

        Assert.True(SteamLibraryFolders.ExtractPaths(oldFormat).TryGetValue(out var paths, out var error), error);

        // 数字のキーだけがライブラリで、それ以外の設定値は含めない。
        Assert.Equal([@"D:\SteamLibrary", @"E:\Games"], paths.AsEnumerable());
    }

    [Fact]
    public void マニフェストからインストール先のフォルダ名を読める()
    {
        Assert.True(AppManifestParser.Parse(AppManifestAcf).TryGetValue(out var manifest, out var error), error);

        Assert.Equal("1045720", manifest.AppId);
        Assert.Equal("The Coma 2 Vicious Sisters", manifest.InstallDir);
        Assert.Equal(4, manifest.StateFlags);
    }

    [Fact]
    public void マニフェストにインストール先が無ければ失敗する()
    {
        Assert.False(AppManifestParser.Parse("\"AppState\" { \"appid\" \"1045720\" }").TryGetValue(out _, out var error));
        Assert.Contains("installdir", error);
    }

    [Fact]
    public void 追加ライブラリにあるゲームを見つけられる()
    {
        var environment = FakeEnvironment([SteamRoot], StandardTextFiles(), GameFiles());

        var result = GameLocator.Locate(environment);

        var found = Assert.IsType<GameLocateResult.Found>(result);
        Assert.Equal(GameRoot, found.Install.RootDirectory);
        Assert.Equal(Path.Combine(GameRoot, "TheComa2ViciousSisters_Data", "level0"), found.Install.ScenePath);
    }

    [Fact]
    public void レジストリの表記揺れがあっても同じSteamとして扱う()
    {
        // HKCU は小文字とスラッシュ、HKLM は通常の表記で返ってくる。
        var environment = FakeEnvironment(["c:/program files (x86)/steam", SteamRoot + "\\"], StandardTextFiles(), GameFiles());

        Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));
    }

    [Fact]
    public void Steamが無ければ見つからない()
    {
        var environment = FakeEnvironment([], [], []);

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));
        Assert.Contains("Steam のインストールが見つかりません", notFound.Reason);
    }

    [Fact]
    public void マニフェストが無ければ未インストールと判定する()
    {
        var texts = StandardTextFiles();
        texts.Remove(Path.Combine(Library, "steamapps", "appmanifest_1045720.acf"));
        var environment = FakeEnvironment([SteamRoot], texts, GameFiles());

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));
        Assert.Contains("インストールされていません", notFound.Reason);
    }

    [Fact]
    public void マニフェストがあってもゲームのファイルが無ければ未インストールと判定する()
    {
        var environment = FakeEnvironment([SteamRoot], StandardTextFiles(), []);

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));
        Assert.Contains(notFound.Details, detail => detail.Contains("ゲームのファイルが見つかりません"));
    }

    [Fact]
    public void 未インストール時の文面は理由と対処と終了する旨を伝える()
    {
        var texts = StandardTextFiles();
        texts.Remove(Path.Combine(Library, "steamapps", "appmanifest_1045720.acf"));
        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(FakeEnvironment([SteamRoot], texts, [])));

        var message = notFound.ToUserMessage();

        Assert.StartsWith("The Coma 2: Vicious Sisters（Steam版）がインストールされていません。", message);
        Assert.Contains("Steam でゲームをインストールしてから", message);
        Assert.Contains("ツールを終了します。", message);
        // 調べたライブラリごとの結果が、箇条書きで添えられる。
        Assert.Contains("【確認した内容】", message);
        Assert.Contains($"・{Library}: このライブラリにはインストールされていません。", message);
    }

    [Fact]
    public void 調べた内容が無ければ文面に確認内容の欄を付けない()
    {
        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(FakeEnvironment([], [], [])));

        Assert.DoesNotContain("【確認した内容】", notFound.ToUserMessage());
    }

    [Theory]
    [InlineData("SteamLibrary")]         // 相対パス（このツールを起動した場所によって指す先が変わる）
    [InlineData("D:SteamLibrary")]       // ドライブだけを指定した相対パス
    [InlineData(@"\SteamLibrary")]       // 現在のドライブの直下からのパス
    [InlineData("D:\\Steam\tLibrary")]   // 制御文字（タブ）を含む
    [InlineData("D:\\Steam\0Library")]   // 制御文字（NUL）を含む
    [InlineData(@"\\")]                  // サーバー名も共有名も無い、不完全なネットワークパス
    [InlineData(@"\\server")]            // 共有名の無いネットワークパス
    [InlineData(@"\\?\D:\SteamLibrary")] // 特殊な表記（Steam がこの形で書くことは無い）
    public void 絶対パスでないライブラリや制御文字を含むライブラリは調べない(string libraryPath)
    {
        var libraryFoldersPath = Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf");
        var libraryFolders = $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{libraryPath.Replace(@"\", @"\\")}\" }} }}";

        // Steam 本体のライブラリにはゲームが無く、それ以外の場所ならどこでもゲームが「ある」と答える環境。
        // 一覧に書かれたおかしなパスを調べてしまうと、ゲームが見つかったことになってしまう。
        var environment = new LocatorEnvironment(
            () => new SteamRootProbe([SteamRoot], []),
            path => string.Equals(path, libraryFoldersPath, StringComparison.OrdinalIgnoreCase)
                ? libraryFolders
                : path.StartsWith(SteamRoot, StringComparison.OrdinalIgnoreCase) ? null : AppManifestAcf,
            _ => true);

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));

        // 調べなかったことは黙って捨てず、確認内容に残す。
        Assert.Contains(notFound.Details, detail => detail.Contains("パスの形式が正しくない"));
    }

    [Fact]
    public void 読めないマニフェストがあっても他のライブラリを調べる()
    {
        // Steam 本体のライブラリのマニフェストは存在するが、読み取りを拒否される（Steam が書き込み中など）。
        // ゲームは別のライブラリにあるので、そこまで調べれば見つかるはず。
        var texts = StandardTextFiles();
        var unreadable = Path.Combine(SteamRoot, "steamapps", "appmanifest_1045720.acf");
        var files = new HashSet<string>(GameFiles(), StringComparer.OrdinalIgnoreCase);
        var environment = new LocatorEnvironment(
            () => new SteamRootProbe([SteamRoot], []),
            path => string.Equals(path, unreadable, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("テスト用: 読み取りを拒否")
                : texts.GetValueOrDefault(path),
            files.Contains);

        var found = Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));

        Assert.Equal(GameRoot, found.Install.RootDirectory);
    }

    [Fact]
    public void 読めなかったファイルは確認内容に理由を残す()
    {
        var environment = new LocatorEnvironment(
            () => new SteamRootProbe([SteamRoot], []),
            _ => throw new IOException("テスト用: 使用中"),
            _ => false);

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));

        Assert.Contains(notFound.Details, detail => detail.Contains("読み取れませんでした") && detail.Contains("テスト用: 使用中"));
    }

    [Fact]
    public void ネットワーク上のライブラリはローカルのライブラリより後に調べる()
    {
        // 一覧の順ではネットワーク上のライブラリが先にある。到達できないネットワーク上の場所は、
        // 調べるだけで長く待たされることがあるので、ローカルで見つかるならネットワーク上の場所には触れない。
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] =
                "\"libraryfolders\" { \"1\" { \"path\" \"\\\\\\\\nas\\\\games\" } \"2\" { \"path\" \"D:\\\\SteamLibrary\" } }",
            [Path.Combine(Library, "steamapps", "appmanifest_1045720.acf")] = AppManifestAcf,
        };
        var files = new HashSet<string>(GameFiles(), StringComparer.OrdinalIgnoreCase);
        var environment = new LocatorEnvironment(
            () => new SteamRootProbe([SteamRoot], []),
            path => path.StartsWith(@"\\", StringComparison.Ordinal)
                ? throw new InvalidOperationException("ローカルで見つかる場合は、ネットワーク上の場所を調べないこと")
                : texts.GetValueOrDefault(path),
            files.Contains);

        Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));
    }

    [Fact]
    public void 旧形式で空のキーはライブラリとして扱わない()
    {
        const string oldFormat = """
            "LibraryFolders"
            {
                ""     "X:\\Unexpected"
                "1"    "D:\\SteamLibrary"
            }
            """;

        Assert.True(SteamLibraryFolders.ExtractPaths(oldFormat).TryGetValue(out var paths, out var error), error);

        Assert.Equal([@"D:\SteamLibrary"], paths.AsEnumerable());
    }

    [Fact]
    public void 確認内容が多いときは件数を絞り制御文字を除いて表示する()
    {
        ImmutableArray<string> details =
        [
            "壊れた\u0007行\r\n続き",
            .. Enumerable.Range(1, 100).Select(i => $@"D:\Lib{i}: このライブラリにはインストールされていません。"),
        ];

        var message = new GameLocateResult.NotFound("テスト用の理由", details).ToUserMessage();

        Assert.Contains(@"・D:\Lib1:", message);
        Assert.DoesNotContain(@"・D:\Lib100:", message);
        Assert.Contains("ほか", message);
        Assert.DoesNotContain('\u0007', message);
        Assert.DoesNotContain('\r', message);
    }

    [Fact]
    public void ネットワーク上のライブラリも調べられる()
    {
        // サーバー名と共有名のそろったネットワークパスは、正しいライブラリの場所として扱う。
        const string networkLibrary = @"\\nas\games\SteamLibrary";
        var networkGameRoot = Path.Combine(networkLibrary, "steamapps", "common", "The Coma 2 Vicious Sisters");
        var texts = new Dictionary<string, string>
        {
            [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] =
                "\"libraryfolders\" { \"1\" { \"path\" \"\\\\\\\\nas\\\\games\\\\SteamLibrary\" } }",
            [Path.Combine(networkLibrary, "steamapps", "appmanifest_1045720.acf")] = AppManifestAcf,
        };
        var environment = FakeEnvironment(
            [SteamRoot],
            texts,
            [
                Path.Combine(networkGameRoot, "TheComa2ViciousSisters.exe"),
                Path.Combine(networkGameRoot, "TheComa2ViciousSisters_Data", "level0"),
            ]);

        var found = Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));

        Assert.Equal(networkGameRoot, found.Install.RootDirectory);
    }

    [Fact]
    public void ライブラリのパスに含まれる上位参照や重複した区切りは解消して扱う()
    {
        var texts = StandardTextFiles();
        // 「D:\Games\..\\SteamLibrary\」と書かれている場合。指しているのは D:\SteamLibrary。
        texts[Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] =
            "\"libraryfolders\" { \"1\" { \"path\" \"D:\\\\Games\\\\..\\\\\\\\SteamLibrary\\\\\" } }";

        var found = Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(FakeEnvironment([SteamRoot], texts, GameFiles())));

        Assert.Equal(GameRoot, found.Install.RootDirectory);
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    [InlineData("..")]
    [InlineData("Game\0Name")]   // 制御文字（NUL）を含む
    [InlineData("Ga\tme")]       // 制御文字（タブ）を含む
    [InlineData("Game ")]        // 末尾の空白（Windows が黙って取り除き、別のフォルダを指しうる）
    [InlineData(" Game")]        // 先頭の空白
    [InlineData("Game.")]        // 末尾のピリオド（同上）
    public void インストール先のフォルダ名が不正なら採用しない(string installDir)
    {
        var texts = StandardTextFiles();
        texts[Path.Combine(Library, "steamapps", "appmanifest_1045720.acf")] =
            $"\"AppState\" {{ \"appid\" \"1045720\" \"installdir\" \"{installDir.Replace(@"\", @"\\")}\" }}";
        // どのパスでも「存在する」と答える環境でも、不正な名前は採用されないこと。
        var environment = new LocatorEnvironment(() => new SteamRootProbe([SteamRoot], []), texts.GetValueOrDefault, _ => true);

        Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));
    }

    [Fact]
    public void ドライブ直下のライブラリでもゲームを見つけられる()
    {
        // libraryfolders.vdf には、ドライブ直下をライブラリにした「E:\\」という書き方がある。
        var texts = new Dictionary<string, string>
        {
            [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] = """
                "libraryfolders"
                {
                	"0"
                	{
                		"path"		"E:\\"
                	}
                }
                """,
            [@"E:\steamapps\appmanifest_1045720.acf"] = AppManifestAcf,
        };
        const string gameRoot = @"E:\steamapps\common\The Coma 2 Vicious Sisters";
        var environment = FakeEnvironment(
            [SteamRoot],
            texts,
            [
                Path.Combine(gameRoot, "TheComa2ViciousSisters.exe"),
                Path.Combine(gameRoot, "TheComa2ViciousSisters_Data", "level0"),
            ]);

        var found = Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));

        // 「E:steamapps」のような、カレントフォルダ相対の表記になっていないこと。
        Assert.Equal(gameRoot, found.Install.RootDirectory);
    }

    [Fact]
    public void ドライブ直下のライブラリは確認内容でも区切り付きで表示する()
    {
        var texts = new Dictionary<string, string>
        {
            [Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf")] =
                "\"libraryfolders\" { \"0\" { \"path\" \"E:\\\\\" } }",
        };

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(FakeEnvironment([SteamRoot], texts, [])));

        Assert.Contains(@"E:\: このライブラリにはインストールされていません。", notFound.Details);
    }

    [Fact]
    public void Steamの場所を調べられなかった理由は文面に添えられる()
    {
        // レジストリを読めなかった、という状況。候補は1つも得られていない。
        const string note = @"レジストリの HKEY_CURRENT_USER\Software\Valve\Steam を読み取れませんでした（テスト用の理由）。";
        var environment = new LocatorEnvironment(() => new SteamRootProbe([], [note]), _ => null, _ => false);

        var notFound = Assert.IsType<GameLocateResult.NotFound>(GameLocator.Locate(environment));

        Assert.Contains("Steam のインストールが見つかりません", notFound.Reason);
        Assert.Contains($"・{note}", notFound.ToUserMessage());
    }

    [Fact]
    public void 一部の場所を調べられなくても残りの候補からゲームを見つける()
    {
        var environment = new LocatorEnvironment(
            () => new SteamRootProbe([SteamRoot], ["レジストリの一部を読み取れませんでした（テスト用の理由）。"]),
            StandardTextFiles().GetValueOrDefault,
            new HashSet<string>(GameFiles(), StringComparer.OrdinalIgnoreCase).Contains);

        Assert.IsType<GameLocateResult.Found>(GameLocator.Locate(environment));
    }
}
