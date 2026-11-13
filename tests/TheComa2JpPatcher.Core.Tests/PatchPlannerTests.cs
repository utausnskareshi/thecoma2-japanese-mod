using System.Collections.Immutable;
using TheComa2JpPatcher.Core.Patching;
using TheComa2JpPatcher.Core.Tests.Support;

namespace TheComa2JpPatcher.Core.Tests;

/// <summary>書き換え計画の作成と、バイト列への適用のテスト。</summary>
public sealed class PatchPlannerTests
{
    private static readonly byte[] Scripts = SyntheticGame.BuildScripts();

    private static SceneAnalysis Analyze(byte[] scene)
    {
        var result = SceneAnalyzer.Analyze(scene, SyntheticGame.Loader(Scripts));
        Assert.True(result.TryGetValue(out var analysis, out var error), error);
        return analysis;
    }

    private static byte[] Apply(byte[] scene)
    {
        Assert.True(PatchPlanner.PlanApply(Analyze(scene).Location).TryGetValue(out var edits, out var planError), planError);
        Assert.True(PatchPlanner.Execute(scene, edits).TryGetValue(out var patched, out var executeError), executeError);
        return patched;
    }

    private static byte[] Revert(byte[] scene)
    {
        Assert.True(PatchPlanner.PlanRevert(Analyze(scene).Location).TryGetValue(out var edits, out var planError), planError);
        Assert.True(PatchPlanner.Execute(scene, edits).TryGetValue(out var reverted, out var executeError), executeError);
        return reverted;
    }

    [Fact]
    public void 適用で変わるのは3バイトだけでファイルサイズは変わらない()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        var patched = Apply(original);

        Assert.Equal(original.Length, patched.Length);
        var changed = Enumerable.Range(0, original.Length).Count(i => original[i] != patched[i]);
        Assert.Equal(3, changed);
    }

    [Fact]
    public void 適用後は日本語が末尾に追加され他のオブジェクトは変わらない()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        var analysis = Analyze(Apply(original));

        Assert.Equal(PatchState.Applied, analysis.State);
        Assert.Equal([.. SyntheticGame.OriginalLanguages, GameLanguage.Japanese], analysis.Location.Languages.AsEnumerable());
        // 追加した要素の分だけ本体が伸び、直後の空きは使い切っている。
        Assert.Equal(Analyze(original).Location.Object.ByteSize + 4, analysis.Location.Object.ByteSize);
        Assert.Equal(0, analysis.Location.ZeroPaddingAfterObject);
    }

    [Fact]
    public void 適用してから戻すと元と完全に同じ内容になる()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        var reverted = Revert(Apply(original));

        Assert.Equal(original, reverted);
    }

    [Fact]
    public void 適用後の内容が逆適用で元に戻るかどうかを判定できる()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        var patched = Apply(original);
        var patchedLocation = Analyze(patched).Location;

        Assert.True(PatchPlanner.RevertsExactly(original, patched, patchedLocation));

        // 「元の内容」として1バイトでも違うものを渡せば、戻せるとは判定しない。
        var different = original.ToArray();
        different[^1] ^= 0xFF;
        Assert.False(PatchPlanner.RevertsExactly(different, patched, patchedLocation));

        // 適用済みではない内容（逆向きの計画を作れない）を渡した場合も、戻せるとは判定しない。
        Assert.False(PatchPlanner.RevertsExactly(original, original, Analyze(original).Location));
    }

    [Fact]
    public void 計画の適用は元のバイト列を変更しない()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        var snapshot = original.ToArray();

        _ = Apply(original);

        Assert.Equal(snapshot, original);
    }

    // 要素数が奇数だと本体のサイズが8の倍数にならず、8バイト境界への位置合わせで直後に4バイトの空きができる。
    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 11, 3, 0 })]
    [InlineData(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9 })]
    public void リストの内容が違っても往復で元に戻る(int[] languages)
    {
        var original = SyntheticGame.BuildScene(languages);

        var patched = Apply(original);

        Assert.Equal([.. languages, GameLanguage.Japanese], Analyze(patched).Location.Languages.AsEnumerable());
        Assert.Equal(original, Revert(patched));
    }

    // 要素数が偶数だと本体のサイズが8の倍数になり、直後に空きができない。その場合は書き換えずに拒否する。
    [Theory]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10 })]
    public void 位置合わせの空きが無い要素数では適用の計画を作らない(int[] languages)
    {
        var analysis = Analyze(SyntheticGame.BuildScene(languages));

        Assert.Equal(0, analysis.Location.ZeroPaddingAfterObject);
        Assert.False(PatchPlanner.PlanApply(analysis.Location).TryGetValue(out _, out var error));
        Assert.Contains("空き領域が無い", error);
    }

    [Fact]
    public void 直後に空きが無ければ適用の計画を作らない()
    {
        // 4バイト境界で詰めると、対象の直後に次のオブジェクトが隙間なく続く。
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, dataAlignment: 4);

        var result = PatchPlanner.PlanApply(Analyze(scene).Location);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Contains("空き領域が無い", error);
    }

    [Fact]
    public void 対象がファイル末尾で空きが無い場合も適用の計画を作らない()
    {
        var scene = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages, targetIsLastObject: true);

        var result = PatchPlanner.PlanApply(Analyze(scene).Location);

        Assert.False(result.TryGetValue(out _, out _));
    }

    [Fact]
    public void 適用済みには重ねて適用しない()
    {
        var patched = Apply(SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages));

        var result = PatchPlanner.PlanApply(Analyze(patched).Location);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Contains("既に日本語が選択可能", error);
    }

    [Fact]
    public void 未適用の状態からは戻す計画を作らない()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);

        var result = PatchPlanner.PlanRevert(Analyze(original).Location);

        Assert.False(result.TryGetValue(out _, out _));
    }

    [Fact]
    public void 公式対応済みの並びからは日本語を取り除かない()
    {
        var official = SyntheticGame.BuildScene([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

        var result = PatchPlanner.PlanRevert(Analyze(official).Location);

        Assert.False(result.TryGetValue(out _, out _));
    }

    [Fact]
    public void 書き換え前の内容が計画と違えば何も変えずに失敗する()
    {
        var original = SyntheticGame.BuildScene(SyntheticGame.OriginalLanguages);
        Assert.True(PatchPlanner.PlanApply(Analyze(original).Location).TryGetValue(out var edits, out _));

        // 計画を作った後でファイルが変わっていた、という状況を再現する。
        var tampered = original.ToArray();
        tampered[edits[1].Offset] = 0x7F;

        var result = PatchPlanner.Execute(tampered, edits);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Contains("想定と異なります", error);
    }

    [Fact]
    public void 書き換え位置の範囲検査は境界ちょうどを正しく扱う()
    {
        var source = new byte[8];
        ImmutableArray<ByteEdit> lastFourBytes = [new ByteEdit(4, [0, 0, 0, 0], [1, 2, 3, 4])];
        ImmutableArray<ByteEdit> onePastEnd = [new ByteEdit(5, [0, 0, 0, 0], [1, 2, 3, 4])];

        // 末尾ちょうどに収まる書き換えは受け付け、1バイトでもはみ出す書き換えは拒否する。
        Assert.True(PatchPlanner.Execute(source, lastFourBytes).TryGetValue(out var written, out var error), error);
        Assert.Equal([0, 0, 0, 0, 1, 2, 3, 4], written);
        Assert.False(PatchPlanner.Execute(source, onePastEnd).TryGetValue(out _, out _));
    }

    [Fact]
    public void 桁あふれする書き換え位置も例外ではなく失敗として返す()
    {
        // 位置と長さを足すと int の上限を超える計画。足し算があふれて負になると、範囲の検査をすり抜けてしまう。
        var source = new byte[8];
        ImmutableArray<ByteEdit> overflowing = [new ByteEdit(int.MaxValue - 1, [0, 0, 0, 0], [1, 0, 0, 0])];

        Assert.False(PatchPlanner.Execute(source, overflowing).TryGetValue(out _, out var error));
        Assert.Contains("範囲外", error);
    }

    [Fact]
    public void 範囲外や長さの合わない計画は拒否する()
    {
        var source = new byte[8];
        ImmutableArray<ByteEdit> outOfRange = [new ByteEdit(6, [0, 0, 0, 0], [1, 0, 0, 0])];
        ImmutableArray<ByteEdit> lengthMismatch = [new ByteEdit(0, [0, 0], [1, 0, 0, 0])];

        Assert.False(PatchPlanner.Execute(source, outOfRange).TryGetValue(out _, out _));
        Assert.False(PatchPlanner.Execute(source, lengthMismatch).TryGetValue(out _, out _));
    }
}
