# The Coma 2: Vicious Sisters 日本語化パッチ（非公式）

Steam版『The Coma 2: Vicious Sisters』で、ゲームに同梱されている日本語を、ゲーム内の言語設定から選べるようにするツールです。

- 非公式のツールです。ゲームの開発元・販売元とは関係ありません。
- このツールには、ゲームのテキスト・画像・フォント・プログラムなどのデータは一切含まれていません。
- 書き換えるのは、お手元のゲームフォルダにあるファイル1つ（3バイト）だけです。いつでも元に戻せます。

## 動作環境

- Windows 10 / 11（64bit）
- Steam版 The Coma 2: Vicious Sisters がインストールされていること
- 追加のランタイムのインストールは不要です（必要なものは exe に同梱しています）

## 使い方

1. 次のページ（Releases）から配布Zipをダウンロードして展開します。
   https://github.com/utausnskareshi/thecoma2-japanese-mod/releases
2. ゲームを終了した状態で `TheComa2JpPatcher.exe` を起動します。
   - ゲームがインストールされていない場合は、その旨を表示して終了します。
3. 「日本語化する」を押します。
4. ゲームを起動し、メニューの OPTIONS → LANGUAGE で「日本語」を選びます。

## 元に戻す

1. ゲーム内の言語を、日本語以外（English など）に変更してからゲームを終了します。
2. ツールを起動し、「元に戻す」を押します。

ファイルは、適用前と1バイトも違わない内容に戻ります。

## 注意事項

- 日本語のまま元に戻すと、ゲーム内で言語を切り替えられなくなります。その場合は、もう一度「日本語化する」→ ゲーム内で言語を変更 →「元に戻す」の順で直せます。
- ゲームの更新や、Steam の「インストール済みファイルの整合性を確認」を行うと、日本語化が外れることがあります。その場合は、もう一度「日本語化する」を押してください。
- 表示される日本語は、ゲームに同梱されているデータそのままです。一部に、英語のまま表示される文があります。
- ゲームの版が想定と異なる場合、ツールは何も書き換えずにその旨を表示します。
- 初回起動時に、Windows の SmartScreen が警告を表示することがあります（署名のない実行ファイルのため）。「詳細情報」→「実行」で起動できます。

## 仕組み

- ゲームフォルダの `TheComa2ViciousSisters_Data\level0` にある「選択できる言語の一覧」の末尾に、日本語を追加します。
- 適用前のファイルは、ゲームフォルダ内の `TheComa2JpPatcher_Backup` に保存します。元に戻すと、このフォルダは削除されます。ただし、保存した内容と一致しない場合（ゲームの更新後など）や、他のプログラムが使用中で確認・削除できない場合は、削除せずに残し、その旨を表示します。
- セーブデータや設定には一切触れません。そのため、言語の切り替えはゲーム内で行っていただく必要があります。

## 免責事項・権利について

- 本ツールは無保証です。利用はご自身の責任でお願いします。
- 『The Coma 2: Vicious Sisters』およびその日本語訳の著作権は、それぞれの権利者に帰属します。
- Steam は Valve Corporation の商標または登録商標です。その他、記載されている会社名・製品名は各社の商標または登録商標です。
- 権利者の方から要請があった場合は、速やかに公開を停止します。本リポジトリの Issues（<https://github.com/utausnskareshi/thecoma2-japanese-mod/issues>）からご連絡ください。

### For rights holders

This is an unofficial, free, fan-made tool. It does not contain or redistribute any game assets, text, fonts, or code.
It only edits the list of selectable languages in the user's own installed copy, and the change can be fully reverted.
If you are a rights holder and have any concerns, please open an issue at <https://github.com/utausnskareshi/thecoma2-japanese-mod/issues> and it will be taken down promptly.

## ビルド方法

.NET 10 SDK が必要です。

```
dotnet test
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

`dist` フォルダに配布用Zipが作られます。テストは合成したデータで行うため、ゲーム本体は不要です。

## ライセンス

本ツールのソースコードは [MIT License](LICENSE) です。
配布物に同梱している .NET ランタイムについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
（配布用Zipでは、それぞれ `LICENSE.txt`、`THIRD-PARTY-NOTICES.txt` という名前で同梱しています。）
