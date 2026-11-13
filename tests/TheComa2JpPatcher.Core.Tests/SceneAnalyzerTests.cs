using System.Buffers.Binary;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Tests.Support;
using TheComa2JpPatcher.Core.Unity;

namespace TheComa2JpPatcher.Core.Tests;

/// <summary>シーンファイルから言語リストを特定する処理のテスト。</summary>
public sealed class SceneAnalyzerTests
{
    private static readonly byte[] Scripts = SyntheticGame.BuildScripts();

    private static SceneAnalysis AnalyzeOk(byte[] scene)
    {
        var result = SceneAnalyzer.Analyze(scene, SyntheticGame.Loader(Scripts));
        Assert.True(result.TryGetValue(out var analysis, out var error), error);
        return analysis;
    }

    private static SerializedFileInfo ParseOk(byte[] data)
    {
        Assert.True(SerializedFileParser.Parse(data, "test").TryGetValue(out var info, out var error), error);
        return info;
    }

    private static string AnalyzeError(byte[] scene, Func<string, ReadOnlyMemory<byte>?> loader)
    {
        var result = SceneAnalyzer.Analyze(scene, loader);
        Assert.False(result.TryGetValue(out _, out var error));
        return error!;
    }

    [Fact]
    public void 初期状態のシーンから言語リストを特定できる()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        var analysis = AnalyzeOk(scene);

        Assert.Equal(PatchState.NotApplied, analysis.State);
        Assert.Equal(SyntheticGame.ApplicationManagerPathId, analysis.Location.Object.PathId);
        Assert.Equal(SyntheticGame.OriginalLanguages.AsEnumerable(), analysis.Location.Languages.AsEnumerable());
        // 要素数の位置には、実際に要素数（11）が書かれている。
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(scene.AsSpan(analysis.Location.ListCountOffset)));
        // 8バイト境界への位置合わせにより、直後に4バイトの空きがある。
        Assert.Equal(4, analysis.Location.ZeroPaddingAfterObject);
        Assert.Equal(Hashing.Sha256Hex(scene), analysis.Sha256);
    }

    [Fact]
    public void 構造が同じでもスクリプトが違うオブジェクトは選ばない()
    {
        // 合成シーンには、同じ構造で UIManager を指す MonoBehaviour（PathId 3956）が含まれている。
        var analysis = AnalyzeOk(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages));

        Assert.NotEqual(3956, analysis.Location.Object.PathId);
    }

    [Fact]
    public void 末尾に日本語があれば適用済みと判定する()
    {
        var analysis = AnalyzeOk(SyntheticGame.BuildScene([.. SyntheticGame.OriginalLanguages, GameLanguage.Japanese]));

        Assert.Equal(PatchState.Applied, analysis.State);
    }

    [Fact]
    public void 日本語が末尾以外にあれば公式対応済みとして扱う()
    {
        var analysis = AnalyzeOk(SyntheticGame.BuildScene([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]));

        Assert.Equal(PatchState.JapaneseAlreadySelectable, analysis.State);
    }

    [Fact]
    public void 直後の空きが他のオブジェクトの領域に入っていれば空きとして数えない()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        var target = AnalyzeOk(scene).Location.Object;

        // 対象より手前にあるオブジェクト（PathId 3956）のサイズ欄を偽り、対象の本体とその直後の4バイトまでを覆わせる。
        // 直後の4バイトは0だが、表の上では別のオブジェクトの一部なので、空きとして使ってはいけない。
        var earlier = ParseOk(scene).Objects.Single(entry => entry.PathId == 3956);
        BinaryPrimitives.WriteUInt32LittleEndian(scene.AsSpan(earlier.ByteSizeFieldOffset), (uint)(target.DataEnd + 4 - earlier.DataStart));

        var analysis = AnalyzeOk(scene);

        Assert.Equal(0, analysis.Location.ZeroPaddingAfterObject);
        Assert.False(PatchPlanner.PlanApply(analysis.Location).TryGetValue(out _, out var error));
        Assert.Contains("空き領域が無い", error);
    }

    [Fact]
    public void 対象の本体を別のエントリも指している場合は空きとして数えない()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        var target = AnalyzeOk(scene).Location.Object;

        // 先頭のオブジェクト（PathId 648、別の型）の位置とサイズを書き換え、対象とまったく同じ範囲を指させる。
        // 対象の中身を書き換えると、このエントリから見た内容も変わってしまう。
        var first = ParseOk(scene).Objects.Single(entry => entry.PathId == 648);
        var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(scene.AsSpan(12));
        const int byteStartFieldOffsetInEntry = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(scene.AsSpan(first.TableEntryOffset + byteStartFieldOffsetInEntry), (uint)(target.DataStart - dataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(scene.AsSpan(first.ByteSizeFieldOffset), (uint)target.ByteSize);

        var analysis = AnalyzeOk(scene);

        Assert.Equal(0, analysis.Location.ZeroPaddingAfterObject);
        Assert.False(PatchPlanner.PlanApply(analysis.Location).TryGetValue(out _, out _));
    }

    [Fact]
    public void 対象がファイル末尾でその後ろに0が続いていれば空きとして数える()
    {
        // 対象が最後のオブジェクトで、ファイルの終わりまで0が8バイト続いている形。
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, targetIsLastObject: true);
        var extended = new byte[scene.Length + 8];
        scene.CopyTo(extended, 0);
        BinaryPrimitives.WriteUInt32BigEndian(extended.AsSpan(4), (uint)extended.Length); // ヘッダー上のサイズも合わせる

        var analysis = AnalyzeOk(extended);

        Assert.Equal(8, analysis.Location.ZeroPaddingAfterObject);
        Assert.True(PatchPlanner.PlanApply(analysis.Location).TryGetValue(out _, out var error), error);
    }

    [Fact]
    public void データ領域がメタデータと重なるファイルは解析に失敗する()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        // データ領域の開始位置を、メタデータ（型の一覧やオブジェクト表）の途中へずらす。
        // この形だと「オブジェクトの本体」として書き換える場所が、オブジェクト表そのものを指しうる。
        BinaryPrimitives.WriteUInt32BigEndian(scene.AsSpan(12), 40);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("メタデータとデータ領域の位置が矛盾しています", error);
    }

    [Fact]
    public void ヘッダーのメタデータの大きさが実際より小さいファイルは解析に失敗する()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        BinaryPrimitives.WriteUInt32BigEndian(scene.AsSpan(0), 10);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("メタデータとデータ領域の位置が矛盾しています", error);
    }

    [Theory]
    [InlineData(40)]   // 型の件数を読んだ直後（件数の検査で止まる）
    [InlineData(91)]   // 2つ目の型の途中（読み取り位置の境界検査で止まる）
    [InlineData(120)]  // オブジェクト表の手前（件数の検査で止まる）
    [InlineData(300)]  // 3つ目の外部参照の途中（読み取り位置の境界検査で止まる）
    public void ヘッダーを合わせて途中で切ったファイルも例外ではなく失敗として返す(int length)
    {
        // 途中で切ったうえで、ヘッダーのファイルサイズ・メタデータの大きさ・データ開始位置を切った長さに合わせる。
        // ヘッダーの検査では弾かれず、各欄を読み進める途中でデータが尽きる形にして、その時点の検査を確かめる。
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages).AsSpan(0, length).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(scene.AsSpan(0), (uint)(length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(scene.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(scene.AsSpan(12), (uint)length);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("解析できませんでした", error);
    }

    [Fact]
    public void メタデータの文字列が長すぎるファイルは解析に失敗する()
    {
        // Unity のバージョン名の位置に、終端の無い長い文字列を置く（実物では十数文字）。
        // 文字列として読み込もうとせず、壊れたファイルとして扱うこと。
        var data = new byte[20 + 10_000 + 64];
        data.AsSpan(20, 10_000).Fill((byte)'A');
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 17);

        var error = AnalyzeError(data, SyntheticGame.Loader(Scripts));

        Assert.Contains("文字列が長すぎます", error);
    }

    [Fact]
    public void ビッグエンディアンのファイルは解析に失敗する()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        scene[16] = 1; // ヘッダーのエンディアンの欄

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("ビッグエンディアン", error);
    }

    [Fact]
    public void オブジェクトの範囲がファイルの終端を超えていれば解析に失敗する()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        var last = ParseOk(scene).Objects[^1];
        BinaryPrimitives.WriteUInt32LittleEndian(scene.AsSpan(last.ByteSizeFieldOffset), (uint)(scene.Length - last.DataStart + 1));

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("終端を超えています", error);
    }

    [Theory]
    [InlineData("objects", "オブジェクトの件数が不正です")]
    [InlineData("scriptTypes", "スクリプト型の件数が不正です")]
    [InlineData("externals", "外部参照の件数が不正です")]
    public void 件数欄がデータに収まらない一覧はどれも拒否する(string countField, string expected)
    {
        var (scene, offsets) = SyntheticGame.BuildSceneWithCountOffsets(SyntheticGame.OriginalLanguages);
        BinaryPrimitives.WriteInt32LittleEndian(scene.AsSpan(offsets[countField]), 5_000_000);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains(expected, error);
    }

    [Fact]
    public void スクリプト型の一覧があっても外部参照を正しく読める()
    {
        // 実物のシーンにはスクリプト型の一覧が多数ある。読み飛ばす量を誤ると、後ろの外部参照の一覧を読み違える。
        var analysis = AnalyzeOk(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, scriptTypeCount: 3));

        Assert.Equal(PatchState.NotApplied, analysis.State);
        Assert.Equal(SyntheticGame.ApplicationManagerPathId, analysis.Location.Object.PathId);
    }

    [Fact]
    public void 言語リストに重複があれば対象にしない()
    {
        var error = AnalyzeError(SyntheticGame.BuildScene([0, 1, 1]), SyntheticGame.Loader(Scripts));

        Assert.Contains("見つかりませんでした", error);
    }

    [Fact]
    public void 言語リストの後ろに続きがあるオブジェクトは対象にしない()
    {
        // 言語リストが最後のフィールドでない（後ろに別のデータが続く）なら、想定した構造ではない。
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, targetTrailingBytes: 4);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("見つかりませんでした", error);
    }

    [Fact]
    public void 外部ファイルが見つからない理由は1回だけ示す()
    {
        // 合成シーンには、同じ外部ファイルを参照する候補が2つある（対象と、構造だけが同じ別のオブジェクト）。
        var error = AnalyzeError(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages), _ => null);

        var occurrences = error.Split($"{SyntheticGame.ScriptsFileName} が見つかりません").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void スクリプト情報のファイルが無ければ失敗し理由にファイル名が入る()
    {
        var error = AnalyzeError(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages), _ => null);

        Assert.Contains(SyntheticGame.ScriptsFileName, error);
    }

    [Fact]
    public void 別アセンブリの同名クラスは対象にしない()
    {
        var otherScripts = SyntheticGame.BuildScripts(applicationManagerAssembly: "SomePlugin.dll");

        var error = AnalyzeError(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages), SyntheticGame.Loader(otherScripts));

        Assert.Contains("見つかりませんでした", error);
    }

    [Fact]
    public void 候補が複数あれば曖昧として失敗する()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, duplicateTarget: true);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("1つに特定できません", error);
    }

    [Fact]
    public void 言語リストに未定義の値があれば対象にしない()
    {
        var error = AnalyzeError(SyntheticGame.BuildScene([0, 1, 99]), SyntheticGame.Loader(Scripts));

        Assert.Contains("見つかりませんでした", error);
    }

    [Fact]
    public void 未対応の形式バージョンは解析に失敗する()
    {
        var builder = new SerializedFileBuilder();
        builder.AddType(1);
        var scene = builder.Build(formatVersion: 22);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("未対応の形式バージョン", error);
    }

    [Fact]
    public void 型ツリー付きのファイルは解析に失敗する()
    {
        var builder = new SerializedFileBuilder();
        builder.AddType(1);
        var scene = builder.Build(hasTypeTree: true);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("型ツリー", error);
    }

    [Theory]
    [InlineData(5_000_000)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void 件数がデータに収まらないファイルは件数の時点で拒否する(int typeCount)
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        // 型の件数欄は、ヘッダー(20) + バージョン文字列 "2018.3.3f1" と終端(11) + プラットフォーム(4) + 型ツリー有無(1) の直後にある。
        const int typeCountOffset = 20 + 11 + 4 + 1;
        BinaryPrimitives.WriteInt32LittleEndian(scene.AsSpan(typeCountOffset), typeCount);

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        // 件数ぶんの配列を確保して読み進める前に、件数そのものが不正だと判定されること。
        Assert.Contains("型の件数が不正です", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(200)]
    public void 途中で切れたファイルは例外ではなく失敗として返す(int length)
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages).AsSpan(0, length).ToArray();

        var error = AnalyzeError(scene, SyntheticGame.Loader(Scripts));

        Assert.Contains("解析できませんでした", error);
    }
}
