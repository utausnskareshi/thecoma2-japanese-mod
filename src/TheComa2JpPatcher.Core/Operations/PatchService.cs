using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Steam;

namespace TheComa2JpPatcher.Core.Operations;

/// <summary>インストール済みゲームの現在の状態。</summary>
/// <param name="State">パッチの適用状態。</param>
/// <param name="Languages">ゲーム内で選択できる言語の一覧（列挙値）。</param>
/// <param name="Sha256">シーンファイルの SHA-256。</param>
/// <param name="BackupExists">このツールが作ったバックアップがあるか。</param>
public sealed record InstallStatus(PatchState State, ImmutableArray<int> Languages, string Sha256, bool BackupExists);

/// <summary>日本語化／元に戻す操作の結果。</summary>
public abstract record OperationOutcome
{
    private OperationOutcome()
    {
    }

    /// <summary>成功。</summary>
    /// <param name="Message">利用者へ見せる結果の説明。</param>
    /// <param name="Status">操作後の状態。</param>
    public sealed record Success(string Message, InstallStatus Status) : OperationOutcome;

    /// <summary>失敗。<paramref name="Message"/> に理由と対処が入る。</summary>
    public sealed record Failure(string Message) : OperationOutcome;
}

/// <summary>
/// 操作が外の世界から得る情報。副作用を関数として受け取り、テストで差し替えられるようにする。
/// </summary>
/// <param name="IsGameRunning">ゲームが起動中かどうか。</param>
/// <param name="ToolVersion">バックアップ情報に記録するツールのバージョン。</param>
/// <param name="UtcNow">現在時刻（UTC）。</param>
public sealed record PatchEnvironment(Func<bool> IsGameRunning, string ToolVersion, Func<DateTimeOffset> UtcNow);

/// <summary>バックアップと一緒に保存する記録（backup.json）。</summary>
/// <param name="ToolVersion">バックアップを作ったツールのバージョン。</param>
/// <param name="CreatedAtUtc">作成日時（UTC）。</param>
/// <param name="OriginalSha256">適用前のシーンファイルの SHA-256。</param>
/// <param name="PatchedSha256">適用後のシーンファイルの SHA-256。</param>
public sealed record BackupInfo(string ToolVersion, DateTimeOffset CreatedAtUtc, string OriginalSha256, string PatchedSha256);

/// <summary>backup.json の入出力をリフレクションなしで行うための定義。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BackupInfo))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

/// <summary>ゲームのプロセスに関する問い合わせ。</summary>
public static class GameProcess
{
    /// <summary>ゲームが起動中かどうかを返す。</summary>
    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(GameInstall.ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// ゲームのファイルに対する読み書き（副作用）をまとめた層。
/// 「何をどう書き換えるか」の判断は <see cref="SceneAnalyzer"/> と <see cref="PatchPlanner"/> に任せ、
/// ここでは バックアップ → 書き込み → 検証 の手順と、失敗時の後始末だけを受け持つ。
/// 触るのはシーンファイル（level0）とこのツール自身のバックアップだけで、セーブデータには一切触れない。
/// </summary>
public static class PatchService
{
    /// <summary>バックアップの記録ファイル名。</summary>
    public const string BackupInfoFileName = "backup.json";

    // 書き込み途中の内容を置く一時ファイルの接尾辞。
    private const string TempSuffix = ".jppatch.tmp";

    // 利用者へ見せる操作名。失敗メッセージの文頭に入る。
    private const string ApplyOperationName = "日本語化";
    private const string RestoreOperationName = "元に戻す処理";

    // ゲームが起動中のときの文面（適用と復元で共通）。
    private const string GameRunningMessage = "ゲームが起動中です。ゲームを終了してから、もう一度お試しください。";

    // 同じゲームフォルダに対する操作が、別のウィンドウなどで実行中のときの文面。
    private const string BusyMessage =
        "このゲームに対する処理が、別のウィンドウで実行中です。その処理が終わってから、もう一度お試しください。";

    // 書き換え後の内容を解析し直した結果が、想定と違っていたときの文面。
    private const string UnexpectedResultMessage =
        "書き換え後の内容が想定した状態にならなかったため、中止しました。ゲームのファイルは変更していません。";

    // 読み込んでから置き換えるまでの間に、他のプログラムがファイルを書き換えていたときの文面。
    private const string SceneChangedMessage =
        "処理の途中で、他のプログラムがゲームのファイルを変更しました。安全のため中止しました（このツールはゲームのファイルを変更していません）。" +
        "ゲームの更新などが終わるのを待ってから、もう一度お試しください。";

    /// <summary>現在の状態を調べる（ファイルは変更しない）。</summary>
    public static Result<InstallStatus> Inspect(GameInstall install)
    {
        try
        {
            var scene = File.ReadAllBytes(install.ScenePath);
            return Analyze(install, scene).TryGetValue(out var analysis, out var error)
                ? Result.Ok(ToStatus(install, analysis))
                : Result.Fail<InstallStatus>(error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<InstallStatus>($"ゲームのファイルを読み取れませんでした: {ex.Message}");
        }
    }

    /// <summary>日本語を言語選択に追加する。</summary>
    public static OperationOutcome Apply(GameInstall install, PatchEnvironment environment) =>
        RunGuarded(install, ApplyOperationName, () => ApplyCore(install, environment));

    /// <summary>日本語化を取り消し、適用前の内容へ戻す。</summary>
    public static OperationOutcome Restore(GameInstall install, PatchEnvironment environment) =>
        RunGuarded(install, RestoreOperationName, () => RestoreCore(install, environment));

    /// <summary>
    /// 適用の本体。次の順に進め、どこで失敗してもゲームのファイルが中途半端な内容にならないようにする。
    /// 1. 現在の内容を解析し、書き換え後の内容をメモリ上で作る。それが「適用済み」と読めることと、
    ///    逆向きに戻すと元の内容と完全に一致することを確かめる（ここまで副作用なし）
    /// 2. 適用前の内容をバックアップし、書けたことを確認する
    /// 3. 読み込んだ時点から内容が変わっていないことを確かめてから、シーンファイルを置き換える
    /// 4. 読み直して確認する（違っていれば元へ戻す）
    /// </summary>
    private static OperationOutcome ApplyCore(GameInstall install, PatchEnvironment environment)
    {
        if (environment.IsGameRunning())
        {
            return new OperationOutcome.Failure(GameRunningMessage);
        }

        var original = File.ReadAllBytes(install.ScenePath);
        if (!Analyze(install, original).TryGetValue(out var analysis, out var analyzeError))
        {
            return new OperationOutcome.Failure(analyzeError);
        }

        switch (analysis.State)
        {
            case PatchState.Applied:
                return new OperationOutcome.Failure("既に日本語化されています。");
            case PatchState.JapaneseAlreadySelectable:
                return new OperationOutcome.Failure("このバージョンのゲームは、パッチなしで日本語を選択できます。");
        }

        if (!PatchPlanner.PlanApply(analysis.Location).TryGetValue(out var edits, out var planError))
        {
            return new OperationOutcome.Failure(planError);
        }

        if (!PatchPlanner.Execute(original, edits).TryGetValue(out var patched, out var executeError))
        {
            return new OperationOutcome.Failure(executeError);
        }

        // 書き込む前に、書き換え後の内容が「適用済み」として正しく読めることを確かめる。
        if (!VerifyState(install, patched, PatchState.Applied).TryGetValue(out var appliedAnalysis, out var verifyError))
        {
            return new OperationOutcome.Failure(verifyError);
        }

        // 書き換え後に見つかる言語リストが、書き換えた当のオブジェクトで、中身が「元のリスト＋日本語」になっていることも確かめる。
        if (!IsSameListWith(analysis, appliedAnalysis, [.. analysis.Location.Languages, GameLanguage.Japanese]))
        {
            return new OperationOutcome.Failure(UnexpectedResultMessage);
        }

        // 同じく書き込む前に、「必ず元へ戻せること」もメモリ上で実証する。
        // 書き換え後の内容を逆向きに戻した結果が、元の内容と1バイトでも違うなら、書き込みを行わない。
        if (!PatchPlanner.RevertsExactly(original, patched, appliedAnalysis.Location))
        {
            return new OperationOutcome.Failure(
                "書き換えた内容を元どおりに戻せることを確認できなかったため、中止しました。ゲームのファイルは変更していません。");
        }

        // バックアップ先が別の場所へのリンクになっていると、バックアップの書き込みや後片付けが、
        // リンクの先にある無関係なファイル（場合によってはゲームのファイル自体）へ及んでしまう。
        if (IsLink(install.BackupDirectory))
        {
            return new OperationOutcome.Failure(
                "バックアップ先のフォルダが、別の場所へのリンク（ジャンクションやシンボリックリンク）になっているため、中止しました。" +
                "ゲームのファイルは変更していません。リンクを取り除いてから、もう一度お試しください。\n" +
                install.BackupDirectory);
        }

        // --- ここから副作用。まずバックアップを作り、内容を確認してから本体を書き換える。 ---
        var backupPath = BackupScenePath(install);
        Directory.CreateDirectory(install.BackupDirectory);
        try
        {
            WriteFileAtomically(backupPath, original);
            if (Hashing.Sha256Hex(File.ReadAllBytes(backupPath)) != analysis.Sha256)
            {
                return new OperationOutcome.Failure(WithNote(
                    "バックアップの内容を確認できなかったため、中止しました。ゲームのファイルは変更していません。",
                    TryRemoveBackup(install)));
            }

            var info = new BackupInfo(environment.ToolVersion, environment.UtcNow(), analysis.Sha256, Hashing.Sha256Hex(patched));
            WriteFileAtomically(BackupInfoPath(install), JsonSerializer.SerializeToUtf8Bytes(info, BackupJsonContext.Default.BackupInfo));

            // 読み込んでからここまでの間に、他のプログラム（ゲームの更新など）がファイルを書き換えていないことを確かめる。
            // 変わっていた場合に続行すると、その変更を、古い内容をもとにした書き換えで上書きしてしまう。
            if (!SceneStillEquals(install, original))
            {
                return new OperationOutcome.Failure(WithNote(SceneChangedMessage, TryRemoveBackup(install)));
            }

            WriteFileAtomically(install.ScenePath, patched);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 本体の書き換えは「一時ファイルへ書いてから置き換える」ため、ここへ来た時点で本体は元のまま。
            // 使われないバックアップを残さないよう片付ける。片付けにも失敗した場合は、
            // その例外で元の失敗理由を覆い隠さないよう、注記として添える。
            return DescribeIoFailure(ApplyOperationName, ex, TryRemoveBackup(install));
        }
        catch
        {
            // 想定外の例外でも、ここへ来た時点で本体は元のままなので、使われないバックアップは残さない。
            // 例外そのものは握りつぶさず、そのまま上位へ伝える（片付けの結果よりも、元の例外の情報を優先する）。
            _ = TryRemoveBackup(install);
            throw;
        }

        // ここで本体の置き換えが確定した（一時ファイルからの置き換えは、全体が反映されるか、まったく反映されないかのどちらか）。
        // 以降は確認と報告だけを行い、ゲームのファイルは書き換えない。確認で問題が見つかっても「操作の失敗」とは伝えず、
        // 書き換えは済んでいることが分かる文面にする。バックアップは、適用済みの今こそ必要なので残す。
        if (ConfirmAfterCommit(install, ApplyOperationName, patched) is { } unconfirmed)
        {
            return unconfirmed;
        }

        return ReportStatusAfterCommit(install, "日本語化しました。");
    }

    /// <summary>
    /// 復元の本体。バックアップを上書きで戻すのではなく、現在のファイルに対して適用時の変更を逆向きに行う。
    /// 逆適用の結果がバックアップと一致した場合だけバックアップを片付け、一致しなければ残して利用者へ伝える。
    /// </summary>
    private static OperationOutcome RestoreCore(GameInstall install, PatchEnvironment environment)
    {
        if (environment.IsGameRunning())
        {
            return new OperationOutcome.Failure(GameRunningMessage);
        }

        var current = File.ReadAllBytes(install.ScenePath);
        if (!Analyze(install, current).TryGetValue(out var analysis, out var analyzeError))
        {
            return new OperationOutcome.Failure(analyzeError);
        }

        switch (analysis.State)
        {
            case PatchState.NotApplied:
                return new OperationOutcome.Failure("日本語化パッチは適用されていないため、元に戻す必要はありません。");
            case PatchState.JapaneseAlreadySelectable:
                return new OperationOutcome.Failure("このツールが適用した状態ではないため、書き換えを行いません。");
        }

        // 現在のファイルから、適用時の3か所の変更だけを逆向きに戻す。
        // バックアップを丸ごと上書きするのではなく逆適用にすることで、ゲームの更新後でも正しく戻せる。
        if (!PatchPlanner.PlanRevert(analysis.Location).TryGetValue(out var edits, out var planError))
        {
            return new OperationOutcome.Failure(planError);
        }

        if (!PatchPlanner.Execute(current, edits).TryGetValue(out var reverted, out var executeError))
        {
            return new OperationOutcome.Failure(executeError);
        }

        if (!VerifyState(install, reverted, PatchState.NotApplied).TryGetValue(out var revertedAnalysis, out var verifyError))
        {
            return new OperationOutcome.Failure(verifyError);
        }

        // 書き換え後に見つかる言語リストが、書き換えた当のオブジェクトで、中身が「今のリストから末尾の日本語を除いたもの」であること。
        if (!IsSameListWith(analysis, revertedAnalysis, analysis.Location.Languages[..^1]))
        {
            return new OperationOutcome.Failure(UnexpectedResultMessage);
        }

        // バックアップ（適用前の実物）と、逆適用の結果が一致するかを確かめる。
        // バックアップは復元そのものには使わないので、読めない場合も復元は続け、照合できなかったことを最後に伝える。
        var backupCheck = CheckBackup(install, reverted);

        // 読み込んでからここまでの間に、他のプログラムがファイルを書き換えていないことを確かめる（適用のときと同じ理由）。
        if (!SceneStillEquals(install, current))
        {
            return new OperationOutcome.Failure(SceneChangedMessage);
        }

        WriteFileAtomically(install.ScenePath, reverted);

        // ここで置き換えが確定した。以降は確認・後片付け・報告だけを行う（適用のときと同じ考え方）。
        // 確認で問題が見つかった場合は、バックアップを片付けずに残す。
        if (ConfirmAfterCommit(install, RestoreOperationName, reverted) is { } unconfirmed)
        {
            return unconfirmed;
        }

        return ReportStatusAfterCommit(install, WithNote("元の状態に戻しました。", FinishBackupAfterRestore(install, backupCheck)));
    }

    /// <summary>復元の前に調べた、バックアップの状態。</summary>
    private enum BackupCheck
    {
        /// <summary>バックアップが無い。</summary>
        Missing,

        /// <summary>復元後の内容と完全に一致する（片付けてよい）。</summary>
        Matches,

        /// <summary>復元後の内容と一致しない（ゲームの更新前のものなど）。</summary>
        Differs,

        /// <summary>他のプログラムが使用中などで読めない。</summary>
        Unreadable,

        /// <summary>バックアップ先のフォルダが、別の場所へのリンクになっている（中身には触れない）。</summary>
        Link,
    }

    /// <summary>バックアップの状態を調べる。読み取れない場合も例外にはせず、その旨を返す。</summary>
    private static BackupCheck CheckBackup(GameInstall install, byte[] reverted)
    {
        // リンクの先は、ゲームフォルダの外にある無関係なファイルかもしれないので、読むことも消すこともしない。
        if (IsLink(install.BackupDirectory))
        {
            return BackupCheck.Link;
        }

        var backupPath = BackupScenePath(install);
        if (!File.Exists(backupPath))
        {
            return BackupCheck.Missing;
        }

        try
        {
            return File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(reverted) ? BackupCheck.Matches : BackupCheck.Differs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BackupCheck.Unreadable;
        }
    }

    /// <summary>
    /// 復元が確定した後のバックアップの扱いを決め、利用者へ伝える注記を返す（伝えることが無ければ null）。
    /// 適用前と完全に同じ内容へ戻ったことを確認できた場合だけ片付け、それ以外は残す。
    /// </summary>
    private static string? FinishBackupAfterRestore(GameInstall install, BackupCheck check) => check switch
    {
        // 片付けに失敗しても復元そのものは済んでいるため、失敗にはせず注記として伝える。
        BackupCheck.Matches => TryRemoveBackup(install),
        BackupCheck.Differs =>
            $"なお、保存してあったバックアップと内容が一致しなかったため、バックアップは削除せず残しています。\n{install.BackupDirectory}",
        BackupCheck.Unreadable =>
            "なお、バックアップを読み取れなかった（他のプログラムが使用中の可能性があります）ため、照合できませんでした。" +
            $"バックアップは削除せず残しています。\n{install.BackupDirectory}",
        BackupCheck.Link =>
            $"なお、バックアップ先のフォルダが別の場所へのリンクになっているため、その中のファイルには触れていません。\n{install.BackupDirectory}",
        _ => null,
    };

    /// <summary>
    /// 書き換え前後の解析結果が「同じオブジェクトの言語リスト」を指し、書き換え後の中身が期待どおりであることを確かめる（副作用なし）。
    /// 壊れた（または細工された）ファイルでは、書き換えによって別のオブジェクトが対象に見えるようになることがあるため。
    /// </summary>
    private static bool IsSameListWith(SceneAnalysis before, SceneAnalysis after, ImmutableArray<int> expectedLanguages) =>
        after.Location.Object.TableEntryOffset == before.Location.Object.TableEntryOffset
        && after.Location.Languages.SequenceEqual(expectedLanguages);

    /// <summary>
    /// 置き換えが確定した直後に、書いた内容がそのまま読めることを確かめる。問題が無ければ null を返す。
    /// 内容が違っていた場合は、置き換えの直後に他のプログラムが書き換えたと考えられる。その変更を消さないよう、
    /// ファイルを元へ書き戻すことはせず、状況を伝えるだけにする。バックアップも、この時点では片付けない。
    /// </summary>
    private static OperationOutcome.Failure? ConfirmAfterCommit(GameInstall install, string operationName, byte[] written)
    {
        try
        {
            if (SceneStillEquals(install, written))
            {
                return null;
            }

            return new OperationOutcome.Failure(WithNote(
                $"{operationName}の書き込みは行いましたが、直後に確認したところ、ゲームのファイルが書き込んだ内容と異なっていました。" +
                "他のプログラム（ゲームの更新など）が直後にファイルを変更した可能性があります。" +
                "その変更を消さないよう、ファイルはそのままにしています。画面に表示される現在の状態を確認してください。",
                BackupKeptNote(install)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OperationOutcome.Failure(WithNote(
                $"{operationName}の書き込みは完了しましたが、確認のための読み直しができませんでした（他のプログラムがファイルを使用中の可能性があります）。" +
                $"画面に表示される現在の状態を確認してください。\n\n詳細: {ex.Message}",
                BackupKeptNote(install)));
        }
    }

    /// <summary>
    /// 置き換えと確認が済んだ後に、現在の状態を添えて成功を返す。
    /// 状態を調べられなかった場合も、書き換え自体は済んでいるので、そのことが分かる文面にする。
    /// </summary>
    private static OperationOutcome ReportStatusAfterCommit(GameInstall install, string successMessage) =>
        Inspect(install).TryGetValue(out var status, out var inspectError)
            ? new OperationOutcome.Success(successMessage, status)
            : new OperationOutcome.Failure(
                $"{successMessage}\n\nただし、その後の状態の確認ができませんでした。画面に表示される現在の状態を確認してください。（{inspectError}）");

    /// <summary>バックアップを残していることを伝える注記（バックアップが無い、またはリンクの場合は null）。</summary>
    private static string? BackupKeptNote(GameInstall install) =>
        !IsLink(install.BackupDirectory) && File.Exists(BackupScenePath(install))
            ? $"バックアップ（適用前の内容）は次の場所に残しています。\n{install.BackupDirectory}"
            : null;

    /// <summary>
    /// 操作を実行する共通の入口。次の2つを受け持つ。
    /// ・同じゲームフォルダに対する操作を、同時に1つだけにする。このツールを2つ起動して同時に操作すると、
    ///   同じ名前の一時ファイルを奪い合ったり、片方の後片付けがもう片方のバックアップを消したりするため。
    /// ・読み書きの失敗を、対処の分かるメッセージ付きの結果へ変換する。
    /// </summary>
    private static OperationOutcome RunGuarded(GameInstall install, string operationName, Func<OperationOutcome> action)
    {
        try
        {
            // 名前付きのミューテックスは、別のプロセス（別のウィンドウ）との間でも排他になる。
            using var mutex = OpenOperationMutex(install);
            if (mutex is null || !TryAcquire(mutex))
            {
                return new OperationOutcome.Failure(BusyMessage);
            }

            try
            {
                return action();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DescribeIoFailure(operationName, ex, note: null);
        }
    }

    /// <summary>
    /// 排他用のミューテックスを作る（既にあれば開く）。開く権限が無い場合は null を返す。
    /// 管理者として実行された同じツールが操作中だと、そのミューテックスは通常の権限では開けないことがある。
    /// その場合も「別のウィンドウで実行中」として扱う（ミューテックスは操作の間だけ存在するため）。
    /// </summary>
    private static Mutex? OpenOperationMutex(GameInstall install)
    {
        try
        {
            return new Mutex(initiallyOwned: false, OperationMutexName(install));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 待たずに排他の取得を試みる。
    /// 前の持ち主（強制終了された別のプロセスなど）が解放しないまま終了していた場合は例外で知らされるが、
    /// 排他そのものはこのスレッドが取得できているので、取得できたものとして扱う。
    /// </summary>
    private static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// ゲームフォルダごとに決まる、排他用の名前を作る（副作用なし）。
    /// パスには名前に使えない文字が含まれるため、大文字小文字を揃えたうえでハッシュにする。
    /// </summary>
    private static string OperationMutexName(GameInstall install)
    {
        var normalizedPath = Path.GetFullPath(install.ScenePath).ToUpperInvariant();
        return $@"Local\TheComa2JpPatcher.{Hashing.Sha256Hex(Encoding.UTF8.GetBytes(normalizedPath))[..32]}";
    }

    /// <summary>読み書きの失敗を、原因に応じた対処の分かる文面にする（副作用なし）。</summary>
    /// <param name="operationName">失敗した操作の名前（文頭に入る）。</param>
    /// <param name="exception">発生した例外。アクセスを拒否されたのか、それ以外の読み書きの問題かで案内を変える。</param>
    /// <param name="note">併せて伝える注記。無ければ null。</param>
    private static OperationOutcome.Failure DescribeIoFailure(string operationName, Exception exception, string? note)
    {
        // Windows は「他のプログラムが使用中のファイルを置き換えようとした」場合にも、権限が無い場合と同じ
        // 「アクセスが拒否されました」を返す。原因を1つに決めつけず、起こりやすい順に挙げる。
        var advice = exception is UnauthorizedAccessException
            ? "ファイルへのアクセスを拒否されました。他のプログラム（ゲーム、Steam、ウイルス対策ソフトなど）がファイルを使用中か、" +
              "ファイルが読み取り専用になっているか、フォルダへ書き込む権限が無い可能性があります。" +
              "使用中のプログラムを終了してから、もう一度お試しください。"
            : "ファイルを読み書きできませんでした。ゲームや Steam がファイルを使用中でないか確認してください。";

        return new OperationOutcome.Failure(
            WithNote($"{operationName}に失敗しました。{advice}\n\n詳細: {exception.Message}", note));
    }

    /// <summary>本文の後ろに注記を1つ添える。注記が無ければ本文をそのまま返す（副作用なし）。</summary>
    private static string WithNote(string message, string? note) =>
        note is null ? message : $"{message}\n\n{note}";

    /// <summary>シーンファイルの現在の内容が、指定の内容と同じかどうかを確かめる。</summary>
    private static bool SceneStillEquals(GameInstall install, byte[] expected) =>
        File.ReadAllBytes(install.ScenePath).AsSpan().SequenceEqual(expected);

    /// <summary>
    /// 指定の場所が、別の場所へのリンク（ジャンクションやシンボリックリンクなど）かどうかを調べる。
    /// 存在しない場合はリンクではないので false を返す。
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>シーンファイルの内容を解析する。参照先のスクリプト情報は、データフォルダから読む。</summary>
    private static Result<SceneAnalysis> Analyze(GameInstall install, byte[] scene) =>
        SceneAnalyzer.Analyze(scene, fileName => LoadExternal(install, fileName));

    /// <summary>書き換え後のバイト列を解析し直し、期待した状態になっていることを確認する。</summary>
    private static Result<SceneAnalysis> VerifyState(GameInstall install, byte[] scene, PatchState expected)
    {
        if (!Analyze(install, scene).TryGetValue(out var analysis, out var error))
        {
            return Result.Fail<SceneAnalysis>($"書き換え後の内容を検証できなかったため、中止しました。ゲームのファイルは変更していません。（{error}）");
        }

        return analysis.State == expected
            ? Result.Ok(analysis)
            : Result.Fail<SceneAnalysis>(UnexpectedResultMessage);
    }

    /// <summary>シーンファイルが参照する別ファイル（スクリプト情報）を、データフォルダから読む。</summary>
    private static ReadOnlyMemory<byte>? LoadExternal(GameInstall install, string fileName)
    {
        // データフォルダの外を読まないよう、フォルダ部分は必ず落とす。
        var path = Path.Combine(install.DataDirectory, Path.GetFileName(fileName));
        if (!File.Exists(path))
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(File.ReadAllBytes(path));
    }

    private static InstallStatus ToStatus(GameInstall install, SceneAnalysis analysis) =>
        new(analysis.State, analysis.Location.Languages, analysis.Sha256, File.Exists(BackupScenePath(install)));

    private static string BackupScenePath(GameInstall install) =>
        Path.Combine(install.BackupDirectory, GameInstall.SceneFileName);

    private static string BackupInfoPath(GameInstall install) =>
        Path.Combine(install.BackupDirectory, BackupInfoFileName);

    /// <summary>
    /// 一時ファイルへ書き切ってから置き換える。途中で失敗しても、置き換え先は元の内容のまま残る。
    /// </summary>
    private static void WriteFileAtomically(string path, ReadOnlySpan<byte> content)
    {
        var tempPath = path + TempSuffix;
        try
        {
            // 前回の実行の残骸や、同じ名前で置かれたリンクがあれば先に取り除く（リンクの場合は、リンク自体だけが消える）。
            // そのうえで「新規作成のみ」で開き、既にあるファイルやリンクの先へ書き込んでしまうことを防ぐ。
            RemoveStaleTempFile(tempPath);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            // 置き換え先がリンクだった場合も、置き換わるのはリンク自体で、リンクの先のファイルは変わらない。
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            // 置き換えに成功していれば一時ファイルは既に無い。失敗時に残った分だけを消す。
            DeleteIfExists(tempPath);
        }
    }

    /// <summary>
    /// 一時ファイルの名前で残っているものを取り除く。
    /// 読み取り専用の属性が付いたまま残った残骸は、属性を外してから消す（そのままだと、以後の操作がずっと失敗するため）。
    /// フォルダだった場合は、このツールが作ったものではないので消さない。その後の新規作成が失敗し、理由が利用者へ伝わる。
    /// </summary>
    private static void RemoveStaleTempFile(string tempPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(tempPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 残っていない（通常の場合）。
            return;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return;
        }

        // リンクの属性を変えるとリンクの先へ影響しうるので、通常のファイルの場合だけ属性を外す。
        if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            File.SetAttributes(tempPath, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(tempPath);
    }

    /// <summary>
    /// バックアップの後片付けを試みる。片付けられなかった場合は、利用者へ伝える注記を返す（成功なら null）。
    /// 後片付けの失敗を、操作そのものの成否と取り違えないための入口。
    /// 呼び出すのは「バックアップがもう要らない」と分かっている場面（本体が適用前の内容である場面）に限る。
    /// </summary>
    private static string? TryRemoveBackup(GameInstall install)
    {
        try
        {
            // リンクの先にあるファイルは、このツールが作ったものとは限らないので、決して消さない。
            if (IsLink(install.BackupDirectory))
            {
                return $"なお、バックアップ先のフォルダが別の場所へのリンクになっているため、後片付けは行っていません。\n{install.BackupDirectory}";
            }

            RemoveBackup(install);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return
                $"なお、不要になったバックアップを削除できませんでした（{ex.Message}）。" +
                $"次のフォルダは手動で削除して構いません。\n{install.BackupDirectory}";
        }
    }

    /// <summary>
    /// このツールが作ったファイルだけを消し、フォルダが空になった場合に限ってフォルダも消す。
    /// 利用者が置いたファイルを巻き込まないよう、フォルダごとの一括削除はしない。
    /// </summary>
    private static void RemoveBackup(GameInstall install)
    {
        DeleteIfExists(BackupScenePath(install));
        DeleteIfExists(BackupScenePath(install) + TempSuffix);
        DeleteIfExists(BackupInfoPath(install));

        if (Directory.Exists(install.BackupDirectory)
            && !Directory.EnumerateFileSystemEntries(install.BackupDirectory).Any())
        {
            Directory.Delete(install.BackupDirectory);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
