using System.ComponentModel;
using System.Diagnostics;
using TheComa2JpPatcher.Core.Operations;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Steam;

namespace TheComa2JpPatcher;

/// <summary>
/// メイン画面。現在の状態を表示し、ボタン1つで「日本語化する」と「元に戻す」を切り替える。
/// 表示内容は <see cref="ViewStateFactory"/> が決め、この画面は表示と操作の受け付けだけを行う。
/// </summary>
internal sealed class MainForm : Form
{
    // 本文の幅（96dpi基準の論理ピクセル）。フォームの大きさは中身から自動で決まる。
    private const int ContentWidth = 560;

    // 詳細欄の本来の高さ（96dpi基準の論理ピクセル）。標準の文字サイズでは、この高さに4行が収まる。
    // Windows の「テキストのサイズ」を大きくした場合は、中身に合わせてこれより高くする（FitDetailsHeight）。
    private const int DetailsBaseHeight = 76;

    // 詳細欄を中身に合わせて伸ばすときに、行の高さの合計へ足す余白（96dpi基準の論理ピクセル）。
    private const int DetailsVerticalPadding = 4;

    // 詳細欄を伸ばしても、ウィンドウの下端と画面の作業領域（タスクバーを除く領域）の下端の間に残す余白（96dpi基準の論理ピクセル）。
    private const int ScreenBottomMargin = 24;

    // Windows の終了などで処理中に閉じられる場合に、実行中の書き換えの完了を待つ上限の時間。
    // 書き換えは通常1秒もかからないので、十分に長い値にしている。
    private static readonly TimeSpan OperationWaitOnClose = TimeSpan.FromSeconds(10);

    private readonly GameInstall _install;
    private readonly PatchEnvironment _environment;

    private readonly Font _titleFont;
    private readonly Font _boldFont;

    private readonly Label _statusValue;
    private readonly Button _primaryButton;
    private readonly Label _guidance;
    private readonly TextBox _details;
    private readonly Button _launchButton;
    private readonly Button _closeButton;

    // 現在の表示内容。操作のたびにファイルの中身から作り直す。
    private ViewState _view;

    // 書き換えの実行中かどうか。実行中はボタンを無効にし、ウィンドウも閉じさせない。
    private bool _busy;

    // 実行中の書き換え（別スレッドで動いている処理）。実行中でなければ null。
    private Task? _operation;

    // DPI に合わせた自動拡大が済んだ後の、詳細欄の本来の高さ（実寸のピクセル）。中身に合わせて伸ばすときの下限に使う。
    // ウィンドウを表示する直前（OnLoad）に記録し、DPI の異なる画面へ移ったときは換算し直す。0 の間は高さ合わせを行わない。
    private int _detailsBaseHeight;

    // 最小化中のため、詳細欄の高さ合わせを見送ったかどうか。
    // 最小化中はウィンドウの大きさが仮の値（タイトルバーだけの大きさ）になり、画面に収める計算ができないので、
    // 元の大きさに戻ったとき（OnResize）に改めて行う。
    private bool _detailsFitPending;

    /// <summary>
    /// 画面を組み立て、現在の状態を読み取って表示する。
    /// 部品はデザイナーを使わずコードで配置し、大きさは中身から自動で決まるようにしている。
    /// </summary>
    /// <param name="install">検出済みのゲームのインストール先。</param>
    /// <param name="environment">日本語化／元に戻す操作に渡す環境（起動中の判定・時計など）。</param>
    public MainForm(GameInstall install, PatchEnvironment environment)
    {
        _install = install;
        _environment = environment;

        SuspendLayout();

        // 96dpi 基準で組み、実際の拡大率に合わせて自動で拡大させる。
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        _titleFont = new Font(Font.FontFamily, Font.Size + 4F, FontStyle.Bold);
        _boldFont = new Font(Font, FontStyle.Bold);

        Text = AppInfo.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var title = new Label
        {
            Text = "The Coma 2: Vicious Sisters 日本語化パッチ",
            Font = _titleFont,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        };

        var subtitle = new Label
        {
            Text = "非公式のツールです。ゲームの開発元・販売元とは関係ありません。",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14),
        };

        _statusValue = new Label
        {
            Name = "statusValue",
            Font = _boldFont,
            // 実行時に決まる文字列を表示するラベルでは、「&」を特別扱い（次の文字への下線）させない。
            UseMnemonic = false,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 110, 0),
            Margin = new Padding(0, 0, 0, 6),
        };

        var info = BuildInfoTable(install.RootDirectory, _statusValue);

        _primaryButton = new Button
        {
            Name = "primaryButton",
            Font = _titleFont,
            Size = new Size(ContentWidth, 56),
            Margin = new Padding(0, 10, 0, 12),
        };
        _primaryButton.Click += OnPrimaryButtonClick;

        _guidance = new Label
        {
            Name = "guidance",
            // エラーの説明にはファイルのパスが入ることがあるので、「&」をそのまま表示する。
            UseMnemonic = false,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Margin = new Padding(0, 0, 0, 12),
        };

        _details = new TextBox
        {
            Name = "details",
            // 読み上げソフト向けの名前。指定しないと、直前に並ぶ案内文のラベルが、この欄の名前として読まれてしまう。
            AccessibleName = "詳細情報",
            AccessibleDescription = "問い合わせの際の手がかりになる技術的な情報（選択できる言語、level0 の SHA-256、バックアップの有無、ツールのバージョン）",
            Multiline = true,
            ReadOnly = true,
            TabStop = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.GrayText,
            Size = new Size(ContentWidth, DetailsBaseHeight),
            Margin = new Padding(0, 0, 0, 12),
        };

        _launchButton = new Button
        {
            Name = "launchButton",
            Text = "ゲームを起動",
            AutoSize = true,
            MinimumSize = new Size(120, 32),
            Margin = new Padding(8, 0, 0, 0),
        };
        _launchButton.Click += OnLaunchButtonClick;

        _closeButton = new Button
        {
            Name = "closeButton",
            Text = "閉じる",
            AutoSize = true,
            MinimumSize = new Size(120, 32),
            Margin = new Padding(8, 0, 0, 0),
        };
        _closeButton.Click += (_, _) => Close();
        CancelButton = _closeButton;

        // 右寄せにするため、右から左へ並べる（追加した順に右から置かれる）。
        var bottomButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        bottomButtons.Controls.Add(_closeButton);
        bottomButtons.Controls.Add(_launchButton);

        // 右から左へ並べているため、追加した順のままだと Tab キーで「閉じる」→「ゲームを起動」と、見た目と逆に移る。
        // 見た目の並び（左から「ゲームを起動」「閉じる」）と揃える。
        _launchButton.TabIndex = 0;
        _closeButton.TabIndex = 1;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20),
            Dock = DockStyle.Fill,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));
        foreach (var control in new Control[] { title, subtitle, info, _primaryButton, _guidance, _details, bottomButtons })
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(control);
        }

        Controls.Add(root);

        _view = ViewStateFactory.From(PatchService.Inspect(_install));
        Render();

        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    /// <summary>
    /// ウィンドウを表示する直前の処理。部品のウィンドウができ、DPI に合わせた拡大も済んだこの時点で、
    /// 詳細欄の高さを中身に合わせる（コンストラクターの中では、まだ折り返しの行数を測れないため）。
    /// 高さが変わった場合は、大きくなったウィンドウが画面の中央に来るよう置き直す。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 自動拡大の後の実寸を、伸ばすときの下限として記録する。標準の文字サイズでは、この高さで全行が収まるので何も変わらない。
        // （論理値の 76 を LogicalToDeviceUnits で換算した値は、WinForms の拡大の丸め方と1〜数ピクセル食い違うため使わない。
        //   使うと、標準の文字サイズでも欄が伸びて、ウィンドウの置き直しまで起きてしまう。）
        _detailsBaseHeight = _details.Height;
        if (FitDetailsHeight() && StartPosition == FormStartPosition.CenterScreen)
        {
            CenterToScreen();
        }
    }

    /// <summary>
    /// ウィンドウの大きさが変わったときの処理。最小化中に見送った詳細欄の高さ合わせを、元の大きさに戻ったところで行う
    /// （最小化した状態で起動した場合や、処理中に最小化した場合）。
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (_detailsFitPending && WindowState != FormWindowState.Minimized)
        {
            _detailsFitPending = false;
            FitDetailsHeightLater();
        }
    }

    /// <summary>
    /// DPI の異なる画面へ移ったときの処理。WinForms は部品の大きさを DPI の比で拡大するだけなので、文字サイズを大きくしていると、
    /// 行の高さの丸めや折り返し位置の違いで詳細欄の末尾が欠けたり、移った先の画面に収まらなくなったりする。
    /// 拡大が済んだ後で、下限の高さを新しい DPI に合わせて換算し、詳細欄の高さを合わせ直す。
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // 拡大の前に、詳細欄が本来の高さのまま（伸ばしていない状態）かを覚えておく。
        var atBaseHeight = _detailsBaseHeight > 0 && _details.Height == _detailsBaseHeight;

        base.OnDpiChanged(e);

        if (_detailsBaseHeight > 0 && e.DeviceDpiOld > 0 && e.DeviceDpiNew != e.DeviceDpiOld)
        {
            // 本来の高さのままだった場合は、WinForms が拡大した実寸をそのまま下限にする（標準の文字サイズでは見た目を変えないため）。
            // 伸ばしていた場合は、本来の高さを DPI の比で換算する（多少の丸めの違いは、下限としては問題にならない）。
            _detailsBaseHeight = atBaseHeight
                ? _details.Height
                : (int)Math.Round(_detailsBaseHeight * (double)e.DeviceDpiNew / e.DeviceDpiOld);
        }

        FitDetailsHeightLater();
    }

    /// <summary>
    /// 詳細欄の高さ合わせを、いま進行中のウィンドウの処理（元の大きさへの復元や、DPI の変更に伴う配置）が終わった後で行う。
    /// それらの処理の途中で詳細欄の高さを変えると、ウィンドウの自動調整（AutoSize）による縮小が反映されず、
    /// ウィンドウが画面からはみ出したままになることがある（最小化中に処理が終わり、その後で元に戻した場合に確認）。
    /// </summary>
    private void FitDetailsHeightLater() =>
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && FitDetailsHeight())
            {
                KeepBottomOnScreen();
            }
        }));

    /// <summary>この画面が作ったフォントを解放する（コントロール自体は基底クラスが解放する）。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _boldFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// ファイルの書き換え中に閉じられそうになったときの扱い。
    /// ・利用者の操作（×ボタンや Alt+F4）による場合は、閉じさせない（処理が終われば結果を表示する）。
    /// ・Windows の終了や、他のプログラム（タスクマネージャーなど）からの終了要求の場合は、閉じること自体は妨げず、
    ///   実行中の書き換えが終わるのを上限付きで待ってから閉じる。
    /// 書き換えは別スレッドで動いており、待たずに閉じるとプロセスごと終了して、書き込み後の確認や
    /// 一時ファイルの片付けが行われないまま打ち切られてしまうため。
    /// 書き換えの処理は画面のスレッドを使わないので、ここで待っても互いに待ち合って止まることはない。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
            }
            else if (_operation is { } operation)
            {
                // 失敗して終わった処理を待っても例外にならないよう、WaitAny で完了だけを待つ。
                Task.WaitAny(new[] { operation }, OperationWaitOnClose);
            }
        }

        base.OnFormClosing(e);
    }

    /// <summary>「インストール先」「現在の状態」の2行を並べた表を作る。</summary>
    private static TableLayoutPanel BuildInfoTable(string installDirectory, Label statusValue)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth - 110));

        var installValue = new Label
        {
            Name = "installValue",
            Text = installDirectory,
            // フォルダ名に「&」が含まれていても、消えたり下線になったりせず、そのまま表示されるようにする。
            UseMnemonic = false,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 110, 0),
            Margin = new Padding(0, 0, 0, 6),
        };

        table.Controls.Add(CaptionLabel("インストール先"), 0, 0);
        table.Controls.Add(installValue, 1, 0);
        table.Controls.Add(CaptionLabel("現在の状態"), 0, 1);
        table.Controls.Add(statusValue, 1, 1);
        return table;
    }

    /// <summary>表の左列に置く、灰色の見出しラベルを作る。</summary>
    private static Label CaptionLabel(string text) => new()
    {
        Text = text,
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>現在の <see cref="_view"/> と処理中フラグを画面へ反映する。</summary>
    private void Render()
    {
        _statusValue.Text = _view.StatusText;
        _statusValue.ForeColor = _view.StatusColor;
        _primaryButton.Text = _busy ? "処理中…" : _view.ButtonText;
        _primaryButton.Enabled = _view.ButtonEnabled && !_busy;
        _guidance.Text = _view.Guidance;
        _details.Text = _view.Details;
        _launchButton.Enabled = !_busy;
        _closeButton.Enabled = !_busy;
        UseWaitCursor = _busy;

        // 操作の後で詳細欄の行数が変わることがある（例: 言語の一覧に JPN が加わって折り返す）ので、高さを合わせ直す。
        // 伸びたウィンドウの下端が画面の外へ出た場合は、画面の内側へ戻す。
        if (FitDetailsHeight())
        {
            KeepBottomOnScreen();
        }
    }

    /// <summary>
    /// 詳細欄の高さを、表示する文字がすべて見える高さへ合わせる。高さを変えた場合は true を返す。
    /// Windows の「テキストのサイズ」を大きくすると、文字は大きくなる一方で、欄の高さは DPI に合わせた拡大しかされないため、
    /// 固定の高さのままでは末尾の行（SHA-256・バックアップ・バージョン）が欠けてしまう。
    /// ・行数は詳細欄自身が実際に折り返した結果から数え、行の高さは今のフォントで測る（どちらも拡大後の実寸）
    /// ・本来の高さ（_detailsBaseHeight）より低くはしない（標準の文字サイズでは見た目を変えない）
    /// ・ウィンドウが画面の作業領域に収まらなくなる高さにはせず、その場合だけ縦スクロールバーを付けて残りを読めるようにする
    /// 本来の高さを記録する前（表示の直前より前）は何もしない。最小化中は見送り、元の大きさに戻ったときに行う。
    /// </summary>
    private bool FitDetailsHeight()
    {
        if (!_details.IsHandleCreated || _detailsBaseHeight <= 0)
        {
            return false;
        }

        // 最小化中は、ウィンドウの大きさが仮の値（タイトルバーだけの大きさ）なので、画面に収める上限を正しく計算できない。
        if (WindowState == FormWindowState.Minimized)
        {
            _detailsFitPending = true;
            return false;
        }

        // 折り返しを含めた表示行数と、1行の高さ（エディットボックスと同じ測り方）。
        var lineCount = _details.GetLineFromCharIndex(_details.TextLength) + 1;
        var lineHeight = TextRenderer.MeasureText(
            "あ",
            _details.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding).Height;

        // 枠の太さ（外形と内側の差）と余白を足して、全行が見える外形の高さを求める。
        var frame = _details.Height - _details.ClientSize.Height;
        var needed = frame + (lineCount * lineHeight) + _details.LogicalToDeviceUnits(DetailsVerticalPadding);

        var minimum = _detailsBaseHeight;
        var maximum = Math.Max(minimum, MaximumDetailsHeight());
        var height = Math.Clamp(needed, minimum, maximum);

        // 上限で止めた場合だけ、隠れた行をスクロールで読めるようにする（標準の文字サイズでは付けない）。
        // スクロールバーの有無を変えると欄の作り直しが起きるので、変わるときだけ設定する。
        var scrollBars = needed > height ? ScrollBars.Vertical : ScrollBars.None;
        if (_details.ScrollBars != scrollBars)
        {
            _details.ScrollBars = scrollBars;
        }

        if (_details.Height == height)
        {
            return false;
        }

        _details.Height = height;
        return true;
    }

    /// <summary>
    /// 詳細欄に使える高さの上限を求める（副作用なし）。
    /// ウィンドウのうち詳細欄以外の部分（タイトルバーを含む）を除いた残りを、画面の作業領域の高さから割り出す。
    /// 中身が画面より高いと、ウィンドウの高さは Windows によって画面の大きさで頭打ちになり、今の高さからでは
    /// 詳細欄以外の部分を小さく見積もってしまう。そのため、中身から求めた本来の高さ（推奨サイズ）も使い、大きい方で計算する。
    /// </summary>
    private int MaximumDetailsHeight()
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        var formHeight = Math.Max(Height, GetPreferredSize(Size.Empty).Height);
        var otherParts = formHeight - _details.Height;
        return workingArea.Height - otherParts - LogicalToDeviceUnits(ScreenBottomMargin);
    }

    /// <summary>ウィンドウの下端が画面の作業領域の外へ出ていたら、収まる位置まで上へ動かす（上端は作業領域の上端より上へは出さない）。</summary>
    private void KeepBottomOnScreen()
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        if (Bottom > workingArea.Bottom)
        {
            Top = Math.Max(workingArea.Top, workingArea.Bottom - Height);
        }
    }

    /// <summary>
    /// 主ボタンの処理。現在の状態に応じて「日本語化する」か「元に戻す」のどちらかを実行する。
    /// 押された時点の表示が古くなっていても、実際に書き換えるかどうかは操作側がファイルを読み直して判断する。
    /// </summary>
    private async void OnPrimaryButtonClick(object? sender, EventArgs e)
    {
        switch (_view.State)
        {
            case PatchState.NotApplied:
                await RunOperationAsync(
                    () => PatchService.Apply(_install, _environment),
                    successNote: $"\n\n{ViewStateFactory.HowToSelectJapanese}");
                break;

            case PatchState.Applied:
                if (ConfirmRestore())
                {
                    await RunOperationAsync(() => PatchService.Restore(_install, _environment), successNote: string.Empty);
                }

                break;

            default:
                // ボタンが無効な状態（確認不能・対応不要）では何もしない。
                break;
        }
    }

    /// <summary>
    /// 元に戻す前の確認。セーブデータを読まない設計のため現在の言語は分からず、毎回注意を表示する。
    /// </summary>
    private bool ConfirmRestore() =>
        MessageBox.Show(
            this,
            $"{ViewStateFactory.RestoreCaution}\n\n" +
            "（日本語のまま戻してしまった場合は、もう一度「日本語化する」→ ゲーム内で言語を変更 →「元に戻す」の順で直せます。）\n\n" +
            "元に戻しますか？",
            AppInfo.Title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>
    /// ファイル操作を画面とは別のスレッドで実行し、終わったら状態を読み直して結果を表示する。
    /// 表示する状態は操作の戻り値ではなく、常にファイルの中身から判定し直す。
    /// </summary>
    private async Task RunOperationAsync(Func<OperationOutcome> operation, string successNote)
    {
        _busy = true;
        Render();

        OperationOutcome outcome;
        try
        {
            var running = Task.Run(operation);
            _operation = running;
            outcome = await running;
        }
        finally
        {
            // 成功・失敗・想定外の例外のどれで終わっても、ボタンを押せる状態へ戻し、
            // 表示をファイルの中身から判定し直す（途中まで進んだ処理があっても、実際の状態を見せるため）。
            _busy = false;
            _operation = null;
            if (!IsDisposed)
            {
                try
                {
                    _view = ViewStateFactory.From(PatchService.Inspect(_install));
                }
                finally
                {
                    // 状態の読み直しで想定外の例外が出ても、「処理中…」のまま固まらないよう、表示は必ず更新する。
                    Render();

                    // 処理中は主ボタンを無効にしていたため、キーボードのフォーカスが外れている。操作したボタンへ戻しておく。
                    // 結果のメッセージボックスは、表示した時点でフォーカスのある部品を覚えて、閉じたときにそこへ戻すので、
                    // ここで戻しておかないと、閉じた後のフォーカスが「閉じる」など別のボタンへ移ってしまう。
                    // 押せない状態（確認できなかった場合など）のときは、フォーカスを当てられないので何もしない。
                    if (_primaryButton.CanFocus)
                    {
                        ActiveControl = _primaryButton;
                    }
                }
            }
        }

        if (IsDisposed)
        {
            // 処理中に（Windows の終了などで）画面が閉じられた後は、結果を表示する先が無い。
            return;
        }

        switch (outcome)
        {
            case OperationOutcome.Success success:
                MessageBox.Show(this, success.Message + successNote, AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case OperationOutcome.Failure failure:
                MessageBox.Show(this, failure.Message, AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            default:
                throw new InvalidOperationException("操作結果の種類が想定外です。");
        }
    }

    /// <summary>Steam 経由でゲームを起動する。</summary>
    private void OnLaunchButtonClick(object? sender, EventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo($"steam://rungameid/{GameLocator.AppId}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"ゲームを起動できませんでした。Steam から起動してください。\n\n詳細: {ex.Message}",
                AppInfo.Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
