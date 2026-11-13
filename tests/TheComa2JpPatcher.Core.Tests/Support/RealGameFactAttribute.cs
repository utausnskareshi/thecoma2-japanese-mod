namespace TheComa2JpPatcher.Core.Tests.Support;

/// <summary>
/// 実物のゲームファイルを使うテストの印。
/// 環境変数でインストール先が指定されていない場合は、合格ではなく「スキップ」として表示させる
/// （何も確かめていないのに合格と表示され、確認したつもりになるのを防ぐため）。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealGameFactAttribute : FactAttribute
{
    public RealGameFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RealGameCopyTests.GameDirectoryVariable)))
        {
            Skip = $"環境変数 {RealGameCopyTests.GameDirectoryVariable} にゲームのインストール先が設定されていないため、実物のファイルを使う確認は行いません。";
        }
    }
}
