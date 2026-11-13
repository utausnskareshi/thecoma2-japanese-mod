using TheComa2JpPatcher.Core.Operations;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Steam;
using TheComa2JpPatcher.Core.Tests.Support;

namespace TheComa2JpPatcher.Core.Tests;

/// <summary>
/// 実物のゲームファイルに対する確認。ゲームのファイルはリポジトリに含められないため、
/// 環境変数 THECOMA2_GAME_DIR にインストール先が設定されている場合だけ実行する。
/// インストール先そのものには書き込まず、必要な2ファイルを一時フォルダへコピーして試す。
/// </summary>
public sealed class RealGameCopyTests
{
    /// <summary>実物のインストール先を指定する環境変数の名前。</summary>
    public const string GameDirectoryVariable = "THECOMA2_GAME_DIR";

    // Steam build 20285137 の level0 の SHA-256（適用前／適用後）。内容そのものではなく指紋だけを持つ。
    private const string KnownOriginalSha256 = "5ee08ab7f48739d4dfbec7848ecb74c1a85adc44695623b33fdf81ab30bb2e7c";
    private const string KnownPatchedSha256 = "3f73a030ca8a88bbd7ba107ae394cf1b30c291de2cfab76b6fccbd82e7208f3e";

    [RealGameFact]
    public void 実物のコピーに適用して戻すと元と同じ内容になる()
    {
        var gameDirectory = System.Environment.GetEnvironmentVariable(GameDirectoryVariable);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            // 環境変数が無い環境（CI など）では何もしない。
            return;
        }

        var source = new GameInstall(gameDirectory);
        var workRoot = Path.Combine(Path.GetTempPath(), "TheComa2JpPatcherTests", Guid.NewGuid().ToString("N"));
        var work = new GameInstall(workRoot);
        try
        {
            Directory.CreateDirectory(work.DataDirectory);
            File.Copy(source.ScenePath, work.ScenePath);
            File.Copy(
                Path.Combine(source.DataDirectory, "globalgamemanagers.assets"),
                Path.Combine(work.DataDirectory, "globalgamemanagers.assets"));

            var original = File.ReadAllBytes(work.ScenePath);
            var environment = new PatchEnvironment(() => false, "test", () => DateTimeOffset.UnixEpoch);

            // 適用前: 日本語を除く11言語。
            Assert.True(PatchService.Inspect(work).TryGetValue(out var before, out var beforeError), beforeError);
            Assert.Equal(PatchState.NotApplied, before.State);
            Assert.Equal(["ENG", "KOR", "UKR", "RUS", "SPA", "FRN", "GER", "CHN", "THA", "BR", "PTG"], before.Languages.Select(GameLanguage.NameOf));

            // 適用: 変わるのは3バイトだけ。
            var applied = Assert.IsType<OperationOutcome.Success>(PatchService.Apply(work, environment));
            var patched = File.ReadAllBytes(work.ScenePath);
            Assert.Equal(PatchState.Applied, applied.Status.State);
            Assert.Equal("JPN", GameLanguage.NameOf(applied.Status.Languages[^1]));
            Assert.Equal(original.Length, patched.Length);
            Assert.Equal(3, Enumerable.Range(0, original.Length).Count(i => original[i] != patched[i]));

            // 調査時に確認したビルドであれば、ハッシュも既知の値と一致する。
            if (before.Sha256 == KnownOriginalSha256)
            {
                Assert.Equal(KnownPatchedSha256, applied.Status.Sha256);
            }

            // 復元: 1バイトも違わない。
            Assert.IsType<OperationOutcome.Success>(PatchService.Restore(work, environment));
            Assert.Equal(original, File.ReadAllBytes(work.ScenePath));
            Assert.False(Directory.Exists(work.BackupDirectory));
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }
}
