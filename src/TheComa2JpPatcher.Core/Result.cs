using System.Diagnostics.CodeAnalysis;

namespace TheComa2JpPatcher.Core;

/// <summary>
/// 成功か失敗かを型で表す結果型。
/// 「想定される失敗」（ファイル形式が違う、対象が見つからない等）は例外ではなくこの型で返し、
/// 呼び出し側が成功・失敗の両方を必ず扱うようにする。
/// </summary>
/// <typeparam name="T">成功時に返す値の型。</typeparam>
public abstract record Result<T>
{
    // 派生型を Ok / Fail の2つに限定するため、コンストラクタは非公開にする。
    private Result()
    {
    }

    /// <summary>成功。<paramref name="Value"/> に結果が入る。</summary>
    public sealed record Ok(T Value) : Result<T>;

    /// <summary>失敗。<paramref name="Message"/> に利用者へ見せられる日本語の理由が入る。</summary>
    public sealed record Fail(string Message) : Result<T>;

    /// <summary>
    /// 成功なら値を、失敗なら理由を取り出す。
    /// if 文で早期リターンを書きやすくするための補助。
    /// </summary>
    /// <param name="value">成功時の値。</param>
    /// <param name="error">失敗時の理由。</param>
    /// <returns>成功なら true。</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value, [MaybeNullWhen(true)] out string error)
    {
        switch (this)
        {
            case Ok ok:
                value = ok.Value;
                error = null;
                return true;
            case Fail fail:
                value = default;
                error = fail.Message;
                return false;
            default:
                // 派生型は Ok / Fail のみなので、ここには到達しない。
                throw new InvalidOperationException("Result の派生型が想定外です。");
        }
    }
}

/// <summary><see cref="Result{T}"/> を型引数の推論付きで作るための補助。</summary>
public static class Result
{
    /// <summary>成功の結果を作る。</summary>
    public static Result<T> Ok<T>(T value) => new Result<T>.Ok(value);

    /// <summary>失敗の結果を作る。</summary>
    public static Result<T> Fail<T>(string message) => new Result<T>.Fail(message);
}
