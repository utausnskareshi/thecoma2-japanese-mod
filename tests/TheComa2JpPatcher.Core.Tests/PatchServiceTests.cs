using System.Diagnostics;
using System.Text.Json;
using TheComa2JpPatcher.Core.Operations;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Steam;
using TheComa2JpPatcher.Core.Tests.Support;

namespace TheComa2JpPatcher.Core.Tests;

/// <summary>
/// 実際のファイル操作（バックアップ → 書き込み → 検証 → 復元）のテスト。
/// 一時フォルダに合成データでゲームのフォルダ構成を作り、そこに対して実行する。
/// </summary>
public sealed class PatchServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 12, 3, 4, 5, TimeSpan.Zero);

    private readonly string _root;
    private readonly GameInstall _install;
    private readonly byte[] _originalScene;

    public PatchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TheComa2JpPatcherTests", Guid.NewGuid().ToString("N"));
        _install = new GameInstall(_root);
        _originalScene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        Directory.CreateDirectory(_install.DataDirectory);
        File.WriteAllBytes(_install.ExecutablePath, [0x4D, 0x5A]);
        File.WriteAllBytes(_install.ScenePath, _originalScene);
        File.WriteAllBytes(Path.Combine(_install.DataDirectory, SyntheticGame.ScriptsFileName), SyntheticGame.BuildScripts());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // テストで置いたリンクは、中身をたどらずにリンク自体だけを外す。
        if (IsLink(_install.BackupDirectory))
        {
            Directory.Delete(_install.BackupDirectory);
        }

        // 読み取り専用にしたファイルが残っていても片付けられるよう、属性を戻してから消す。
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static bool IsLink(string path) =>
        Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>フォルダへのリンク（ジャンクション）を作る。シンボリックリンクと違い、特別な権限は要らない。</summary>
    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && IsLink(link), "テスト用のジャンクションを作成できませんでした。");
    }

    private static PatchEnvironment Environment(bool gameRunning = false) =>
        new(() => gameRunning, "9.9.9-test", () => FixedNow);

    private string BackupScenePath => Path.Combine(_install.BackupDirectory, GameInstall.SceneFileName);

    private string BackupInfoPath => Path.Combine(_install.BackupDirectory, PatchService.BackupInfoFileName);

    private InstallStatus InspectOk()
    {
        Assert.True(PatchService.Inspect(_install).TryGetValue(out var status, out var error), error);
        return status;
    }

    /// <summary>フォルダ内に一時ファイルが残っていないこと。</summary>
    private void AssertNoTempFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));

    [Fact]
    public void 初期状態は未適用でバックアップも無い()
    {
        var status = InspectOk();

        Assert.Equal(PatchState.NotApplied, status.State);
        Assert.False(status.BackupExists);
        Assert.Equal(Hashing.Sha256Hex(_originalScene), status.Sha256);
    }

    [Fact]
    public void 適用するとシーンが書き換わり元の内容がバックアップされる()
    {
        var outcome = PatchService.Apply(_install, Environment());

        var success = Assert.IsType<OperationOutcome.Success>(outcome);
        Assert.Equal(PatchState.Applied, success.Status.State);
        Assert.True(success.Status.BackupExists);
        Assert.Equal(GameLanguage.Japanese, success.Status.Languages[^1]);

        // バックアップは適用前と同じ内容で、記録には前後のハッシュが残る。
        Assert.Equal(_originalScene, File.ReadAllBytes(BackupScenePath));
        var info = JsonDocument.Parse(File.ReadAllText(BackupInfoPath)).RootElement;
        Assert.Equal(Hashing.Sha256Hex(_originalScene), info.GetProperty("OriginalSha256").GetString());
        Assert.Equal(Hashing.Sha256Hex(File.ReadAllBytes(_install.ScenePath)), info.GetProperty("PatchedSha256").GetString());
        Assert.Equal("9.9.9-test", info.GetProperty("ToolVersion").GetString());
        AssertNoTempFiles();
    }

    [Fact]
    public void 元に戻すと適用前と完全に同じ内容になりバックアップも片付く()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));

        var outcome = PatchService.Restore(_install, Environment());

        var success = Assert.IsType<OperationOutcome.Success>(outcome);
        Assert.Equal(PatchState.NotApplied, success.Status.State);
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));
        AssertNoTempFiles();
    }

    [Fact]
    public void 適用と復元を繰り返しても毎回元に戻る()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
            Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));
            Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        }
    }

    [Fact]
    public void ゲーム起動中は適用も復元もせずファイルに触れない()
    {
        var applyOutcome = PatchService.Apply(_install, Environment(gameRunning: true));

        var applyFailure = Assert.IsType<OperationOutcome.Failure>(applyOutcome);
        Assert.Contains("ゲームが起動中", applyFailure.Message);
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));

        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        var patched = File.ReadAllBytes(_install.ScenePath);

        var restoreOutcome = PatchService.Restore(_install, Environment(gameRunning: true));

        Assert.IsType<OperationOutcome.Failure>(restoreOutcome);
        Assert.Equal(patched, File.ReadAllBytes(_install.ScenePath));
    }

    [Fact]
    public void 適用済みに重ねて適用しない()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        var patched = File.ReadAllBytes(_install.ScenePath);

        var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));

        Assert.Contains("既に日本語化されています", failure.Message);
        Assert.Equal(patched, File.ReadAllBytes(_install.ScenePath));
        // 最初のバックアップ（本当の適用前の内容）が上書きされていないこと。
        Assert.Equal(_originalScene, File.ReadAllBytes(BackupScenePath));
    }

    [Fact]
    public void 未適用の状態で元に戻そうとしても何も変えない()
    {
        var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Restore(_install, Environment()));

        Assert.Contains("適用されていない", failure.Message);
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
    }

    [Fact]
    public void バックアップが無くても元の内容へ戻せる()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        Directory.Delete(_install.BackupDirectory, recursive: true);

        Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));

        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
    }

    [Fact]
    public void バックアップと内容が合わない場合は復元したうえでバックアップを残す()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        File.WriteAllBytes(BackupScenePath, [1, 2, 3]);

        var success = Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));

        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.True(File.Exists(BackupScenePath));
        Assert.Contains("バックアップは削除せず残しています", success.Message);
    }

    [Fact]
    public void バックアップフォルダに利用者のファイルがあれば巻き込んで消さない()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        var userFile = Path.Combine(_install.BackupDirectory, "memo.txt");
        File.WriteAllText(userFile, "利用者が置いたファイル");

        Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));

        Assert.True(File.Exists(userFile));
        Assert.False(File.Exists(BackupScenePath));
    }

    [Fact]
    public void バックアップを削除できなくても復元は成功として報告する()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));
        // 読み取り専用にすると、復元後の後片付け（バックアップの削除）だけが拒否される。
        File.SetAttributes(BackupScenePath, FileAttributes.ReadOnly);
        try
        {
            var success = Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));

            // 復元そのものは済んでいるので成功として扱い、片付けられなかったことは注記で伝える。
            Assert.Equal(PatchState.NotApplied, success.Status.State);
            Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
            Assert.Contains("バックアップを削除できませんでした", success.Message);
            Assert.Contains(_install.BackupDirectory, success.Message);
            Assert.True(File.Exists(BackupScenePath));
            AssertNoTempFiles();
        }
        finally
        {
            // テスト後の一時フォルダ削除が拒否されないよう、属性を戻す。
            File.SetAttributes(BackupScenePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void バックアップ先がリンクなら適用せずリンク先のファイルにも触れない()
    {
        // バックアップ先の名前で、データフォルダへのジャンクション（フォルダへのリンク）が置かれている状況。
        // そのまま進めると、バックアップの書き込みや後片付けが、リンク越しにゲームのファイル自体へ及んでしまう。
        CreateJunction(_install.BackupDirectory, _install.DataDirectory);

        var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));

        Assert.Contains("リンク", failure.Message);
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        // リンク先（データフォルダ）に、バックアップの記録などを書き込んでいないこと。
        Assert.False(File.Exists(Path.Combine(_install.DataDirectory, PatchService.BackupInfoFileName)));
        AssertNoTempFiles();
    }

    [Fact]
    public void バックアップ先がリンクの状態で途中の処理が失敗してもゲームのファイルを消さない()
    {
        CreateJunction(_install.BackupDirectory, _install.DataDirectory);
        // バックアップを書いた後の処理で、読み書きの失敗が起きた状況を作る。
        var environment = new PatchEnvironment(() => false, "9.9.9-test", () => throw new IOException("テスト用の失敗"));

        Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, environment));

        Assert.True(File.Exists(_install.ScenePath));
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
    }

    [Fact]
    public void 復元時にバックアップ先がリンクならリンク先のファイルを消さない()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));

        // 適用後に、バックアップ先を「ゲームフォルダの外にあり、原本と同じ内容のファイルを持つフォルダ」へのリンクへ差し替える。
        var outside = _root + "-outside";
        var outsideFile = Path.Combine(outside, GameInstall.SceneFileName);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(outsideFile, _originalScene);
        Directory.Delete(_install.BackupDirectory, recursive: true);
        CreateJunction(_install.BackupDirectory, outside);
        try
        {
            var success = Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));

            // 復元は行うが、リンクの先にあるファイルには触れない。そのことは結果の文面で伝える。
            Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
            Assert.True(File.Exists(outsideFile));
            Assert.Contains("リンク", success.Message);
        }
        finally
        {
            // どこで失敗しても両方を片付けられるよう、存在するものだけを消す（リンクは中身をたどらず、リンク自体だけを外す）。
            if (IsLink(_install.BackupDirectory))
            {
                Directory.Delete(_install.BackupDirectory);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public void 処理の途中で他のプログラムがファイルを変更したら上書きせず中止する()
    {
        // バックアップを作った後・本体を置き換える前に、別のプログラム（ゲームの更新など）がファイルを書き換えた状況を作る。
        // 時刻の取得がちょうどその間に呼ばれることを利用している。
        var updatedByOther = SyntheticGame.BuildScene([0, 1, 2]);
        var environment = new PatchEnvironment(
            () => false,
            "9.9.9-test",
            () =>
            {
                File.WriteAllBytes(_install.ScenePath, updatedByOther);
                return FixedNow;
            });

        var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, environment));

        Assert.Contains("他のプログラム", failure.Message);
        // 相手の更新を、古い内容をもとにした書き換えで上書きしていないこと。
        Assert.Equal(updatedByOther, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));
        AssertNoTempFiles();
    }

    [Fact]
    public void 同じゲームフォルダへの操作が実行中なら二重には実行しない()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // 1つ目の操作を、処理の途中（ゲームが起動中かどうかの確認）で止めておく。
        var blockingEnvironment = new PatchEnvironment(
            () =>
            {
                entered.Set();
                release.Wait();
                return false;
            },
            "9.9.9-test",
            () => FixedNow);
        OperationOutcome? first = null;
        var thread = new Thread(() => first = PatchService.Apply(_install, blockingEnvironment));
        thread.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "1つ目の操作が始まりませんでした。");

            // その間に2つ目を実行すると、待たずに「実行中」と返り、ファイルには触れない。
            var second = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));
            Assert.Contains("実行中", second.Message);
            Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
            Assert.False(Directory.Exists(_install.BackupDirectory));
        }
        finally
        {
            release.Set();
            thread.Join();
        }

        // 1つ目は邪魔されずに完了し、終わった後は通常どおり操作できる。
        Assert.IsType<OperationOutcome.Success>(first);
        Assert.Equal(_originalScene, File.ReadAllBytes(BackupScenePath));
        Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
    }

    [Fact]
    public void 想定外の例外で中断してもバックアップを残さず本体も変えない()
    {
        // バックアップを書いた後の処理で、読み書き以外の想定外の例外が出た状況を作る。
        var environment = new PatchEnvironment(() => false, "9.9.9-test", () => throw new InvalidOperationException("テスト用の想定外の例外"));

        // 想定外の例外は握りつぶさず、そのまま呼び出し側へ伝わる。
        Assert.Throws<InvalidOperationException>(() => PatchService.Apply(_install, environment));

        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));
        AssertNoTempFiles();
    }

    [Fact]
    public void 読み取り専用の一時ファイルが残っていても適用できる()
    {
        // 以前の実行が途中で打ち切られ、一時ファイルが読み取り専用で残った状況。
        var stale = _install.ScenePath + ".jppatch.tmp";
        File.WriteAllBytes(stale, [9, 9, 9]);
        File.SetAttributes(stale, FileAttributes.ReadOnly);

        var success = Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));

        Assert.Equal(PatchState.Applied, success.Status.State);
        AssertNoTempFiles();
    }

    [Fact]
    public void 他のプログラムが使用中のときは権限不足と決めつけない案内を出す()
    {
        // 他のプログラムがファイルを開いている（読み取りの共有だけを許している）状況。
        // Windows はこの場合も「アクセスが拒否されました」を返すため、権限の問題と区別が付かない。
        using (new FileStream(_install.ScenePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));

            Assert.Contains("日本語化に失敗しました", failure.Message);
            Assert.Contains("使用中", failure.Message);
            Assert.DoesNotContain("管理者として実行", failure.Message);
        }

        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));
        AssertNoTempFiles();
    }

    [Fact]
    public void バックアップが他のプログラムに使用中でも元に戻せる()
    {
        Assert.IsType<OperationOutcome.Success>(PatchService.Apply(_install, Environment()));

        OperationOutcome.Success success;
        using (new FileStream(BackupScenePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            success = Assert.IsType<OperationOutcome.Success>(PatchService.Restore(_install, Environment()));
        }

        // 復元はバックアップを使わず逆向きの書き換えで行えるので、バックアップを読めなくても続ける。
        // 照合できなかったことは注記で伝え、バックアップは消さずに残す。
        Assert.Equal(PatchState.NotApplied, success.Status.State);
        Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
        Assert.Contains("バックアップを読み取れなかった", success.Message);
        Assert.True(File.Exists(BackupScenePath));
        AssertNoTempFiles();
    }

    [Fact]
    public void 未対応のファイルには適用せず理由を返す()
    {
        // 直後に空きが無い（4バイト境界で詰めた）シーンに差し替える。
        var unsupported = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, dataAlignment: 4);
        File.WriteAllBytes(_install.ScenePath, unsupported);

        var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));

        Assert.Contains("対応していません", failure.Message);
        Assert.Equal(unsupported, File.ReadAllBytes(_install.ScenePath));
        Assert.False(Directory.Exists(_install.BackupDirectory));
    }

    [Fact]
    public void シーンファイルが無ければ状態確認は失敗を返す()
    {
        File.Delete(_install.ScenePath);

        Assert.False(PatchService.Inspect(_install).TryGetValue(out _, out var error));
        Assert.Contains("読み取れませんでした", error);
    }

    [Fact]
    public void 書き込めない場合は失敗を返しバックアップを残さない()
    {
        // 読み取り専用属性を付けると、置き換え（上書き移動）が拒否される。
        File.SetAttributes(_install.ScenePath, FileAttributes.ReadOnly);
        try
        {
            var failure = Assert.IsType<OperationOutcome.Failure>(PatchService.Apply(_install, Environment()));

            Assert.Contains("日本語化に失敗しました", failure.Message);
            Assert.Equal(_originalScene, File.ReadAllBytes(_install.ScenePath));
            Assert.False(Directory.Exists(_install.BackupDirectory));
            AssertNoTempFiles();
        }
        finally
        {
            File.SetAttributes(_install.ScenePath, FileAttributes.Normal);
        }
    }
}
