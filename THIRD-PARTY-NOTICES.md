# 第三者ソフトウェアについて

## 配布物に同梱しているもの

配布用Zipの `TheComa2JpPatcher.exe` は、追加のインストールなしで動くように、Microsoft の .NET ランタイムを内蔵しています（自己完結・単一ファイル形式）。

| 名称 | 提供元 | ライセンス |
|---|---|---|
| .NET Runtime（Microsoft.NETCore.App） | Microsoft Corporation | Windows 向け配布物は .NET Library License、その他は MIT License |
| .NET Windows Desktop Runtime（Microsoft.WindowsDesktop.App） | Microsoft Corporation | 同上 |

- これらは本ツールの MIT License の対象ではなく、Microsoft のライセンス条件に従います。
- ランタイムのパッケージに含まれるライセンス文と第三者通知は、配布用Zipの `licenses\dotnet` フォルダに収録しています。
- ライセンスの内訳は、Microsoft が公開している次の資料を参照してください。
  - https://github.com/dotnet/core/blob/main/license-information.md
  - https://github.com/dotnet/core/blob/main/license-information-windows.md
  - https://dotnet.microsoft.com/dotnet_library_license.htm

## 開発時のみ使用しているもの（配布物には含まれません）

- xUnit、Microsoft.NET.Test.Sdk、coverlet.collector（単体テスト用）

## 含まれていないもの

本ツールおよび本リポジトリには、『The Coma 2: Vicious Sisters』のデータ（テキスト・画像・音声・フォント・プログラム）や、それらを加工したものは含まれていません。
