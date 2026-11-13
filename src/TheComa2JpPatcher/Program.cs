using TheComa2JpPatcher.Core.Steam;

namespace TheComa2JpPatcher;

/// <summary>
/// 起動処理。ゲームのインストールを確認し、見つからなければその旨を表示して終了する。
/// 見つかった場合だけメイン画面を開く。
/// </summary>
internal static class Program
{
    /// <summary>終了コード: ゲームが見つからなかった。</summary>
    private const int ExitGameNotFound = 1;

    /// <summary>終了コード: 想定外のエラーで起動できなかった。</summary>
    private const int ExitStartupError = 2;

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        // 画面操作中の想定外の例外は、黙って落とさずに内容を表示する。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpectedError(e.Exception);

        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            // 画面を出す前（ゲームの検出や画面の組み立て）の想定外の失敗も、
            // 何も表示せずに異常終了させず、内容を表示してから失敗の終了コードで終わる。
            ShowUnexpectedError(ex);
            return ExitStartupError;
        }
    }

    /// <summary>ゲームを探し、見つかればメイン画面を開く。終了コードを返す。</summary>
    private static int Run()
    {
        GameLocateResult located;
        try
        {
            located = GameLocator.Locate(SystemEnvironment.CreateLocatorEnvironment());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 検出処理は読めないファイルを個別に記録して先へ進むので、通常ここへは来ない（念のための安全網）。
            ShowMessage($"Steam の設定ファイルを読み取れませんでした。\n\n詳細: {ex.Message}", MessageBoxIcon.Error);
            return ExitStartupError;
        }

        switch (located)
        {
            case GameLocateResult.Found found:
                Application.Run(new MainForm(found.Install, SystemEnvironment.CreatePatchEnvironment()));
                return 0;

            case GameLocateResult.NotFound notFound:
                // 文面の組み立てはコア側（テスト対象）に任せ、ここでは表示して終了するだけにする。
                ShowMessage(notFound.ToUserMessage(), MessageBoxIcon.Information);
                return ExitGameNotFound;

            default:
                throw new InvalidOperationException("検出結果の種類が想定外です。");
        }
    }

    private static void ShowUnexpectedError(Exception exception) =>
        ShowMessage($"想定外のエラーが発生しました。\n\n{exception.GetType().Name}: {exception.Message}", MessageBoxIcon.Error);

    /// <summary>
    /// メッセージを表示する。
    /// 対話できない環境（サービスやスケジュールされたタスクなど）ではメッセージボックスを出せず例外になるため、
    /// その場合は標準エラーへ書き出す。表示の失敗で、終了コードが返らなくなることを防ぐ。
    /// </summary>
    private static void ShowMessage(string text, MessageBoxIcon icon)
    {
        try
        {
            MessageBox.Show(text, AppInfo.Title, MessageBoxButtons.OK, icon);
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"{AppInfo.Title}: {text}");
        }
    }
}
