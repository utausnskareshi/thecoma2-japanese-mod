using System.Collections.Immutable;
using System.Text;

namespace TheComa2JpPatcher.Core.Steam;

/// <summary>VDF（Valve の KeyValues 形式）の値。文字列か、入れ子のオブジェクトのどちらか。</summary>
public abstract record VdfNode;

/// <summary>文字列の値。</summary>
public sealed record VdfString(string Value) : VdfNode;

/// <summary>
/// 入れ子のオブジェクト。キーの重複があり得る形式なので、辞書ではなく出現順の並びで持つ。
/// </summary>
public sealed record VdfObject(ImmutableArray<KeyValuePair<string, VdfNode>> Entries) : VdfNode
{
    /// <summary>キーに一致する最初の値を返す。Steam のファイルは大文字小文字が揺れるため区別しない。</summary>
    public VdfNode? Find(string key) =>
        Entries
            .Where(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .FirstOrDefault();

    /// <summary>キーに一致する値が文字列ならそれを返す。</summary>
    public string? FindString(string key) => Find(key) is VdfString text ? text.Value : null;

    /// <summary>キーに一致する値がオブジェクトならそれを返す。</summary>
    public VdfObject? FindObject(string key) => Find(key) as VdfObject;
}

/// <summary>
/// Steam の設定ファイル（libraryfolders.vdf、appmanifest_*.acf）を読むための VDF テキストパーサー（副作用なし）。
/// </summary>
public static class VdfParser
{
    // 壊れたファイルによる無限の入れ子を防ぐための上限。Steam のファイルは数段しかない。
    private const int MaxDepth = 64;

    /// <summary>VDF テキスト全体を解析し、最上位のキーと値を持つオブジェクトを返す。</summary>
    public static Result<VdfObject> Parse(string text)
    {
        try
        {
            var tokens = Tokenize(text);
            var index = 0;
            var root = ParseObject(tokens, ref index, isRoot: true, depth: 0);
            return Result.Ok(root);
        }
        catch (VdfFormatException ex)
        {
            return Result.Fail<VdfObject>($"VDF を解析できませんでした: {ex.Message}");
        }
    }

    private enum TokenKind
    {
        Text,
        OpenBrace,
        CloseBrace,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>形式の誤りを表す内部例外。<see cref="Parse"/> で結果型の失敗へ変換する。</summary>
    private sealed class VdfFormatException(string message) : Exception(message);

    /// <summary>
    /// テキストを「文字列」「{」「}」の並びに分解する。空白と行コメント（// から行末まで）は読み飛ばす。
    /// 引用符で囲まれた文字列と、囲まれていない文字列（空白か記号まで）の両方を1つの文字列トークンとして扱う。
    /// </summary>
    private static ImmutableArray<Token> Tokenize(string text)
    {
        var tokens = ImmutableArray.CreateBuilder<Token>();
        var position = 0;
        while (position < text.Length)
        {
            var current = text[position];
            if (char.IsWhiteSpace(current))
            {
                position++;
            }
            else if (current == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                // 行コメントは行末まで読み飛ばす。
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else if (current == '{')
            {
                tokens.Add(new Token(TokenKind.OpenBrace, "{"));
                position++;
            }
            else if (current == '}')
            {
                tokens.Add(new Token(TokenKind.CloseBrace, "}"));
                position++;
            }
            else if (current == '"')
            {
                tokens.Add(new Token(TokenKind.Text, ReadQuoted(text, ref position)));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Text, ReadBare(text, ref position)));
            }
        }

        return tokens.ToImmutable();
    }

    /// <summary>引用符で囲まれた文字列を読む。Windows のパスは「\\」とエスケープされている。</summary>
    private static string ReadQuoted(string text, ref int position)
    {
        var builder = new StringBuilder();
        position++; // 開き引用符
        while (position < text.Length)
        {
            var current = text[position];
            if (current == '"')
            {
                position++; // 閉じ引用符
                return builder.ToString();
            }

            if (current == '\\' && position + 1 < text.Length)
            {
                var escaped = text[position + 1];
                builder.Append(escaped switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    '\\' => "\\",
                    '"' => "\"",
                    // 未知のエスケープは情報を落とさないよう、そのまま残す。
                    _ => $"\\{escaped}",
                });
                position += 2;
                continue;
            }

            builder.Append(current);
            position++;
        }

        throw new VdfFormatException("引用符が閉じられていません。");
    }

    /// <summary>引用符なしのトークンを、空白か記号の手前まで読む。</summary>
    private static string ReadBare(string text, ref int position)
    {
        var start = position;
        while (position < text.Length
               && !char.IsWhiteSpace(text[position])
               && text[position] != '{'
               && text[position] != '}'
               && text[position] != '"')
        {
            position++;
        }

        return text[start..position];
    }

    /// <summary>
    /// 「キー 値」の並びを、対応する「}」（最上位ではトークンの終わり）まで読んで1つのオブジェクトにする。
    /// 値が「{」で始まる場合は、入れ子のオブジェクトとして自分自身を呼び出す。
    /// </summary>
    /// <param name="tokens">トークンの並び。</param>
    /// <param name="index">次に読むトークンの位置。読んだ分だけ進める。</param>
    /// <param name="isRoot">
    /// 最上位かどうか。最上位は「{」で始まっていないので、トークンが尽きたところで正常終了とし、「}」が現れたら誤りとする。
    /// 入れ子ではその逆で、「}」で正常終了とし、「}」が無いままトークンが尽きたら誤りとする。
    /// </param>
    /// <param name="depth">現在の入れ子の深さ（上限を超えたら誤りとする）。</param>
    private static VdfObject ParseObject(ImmutableArray<Token> tokens, ref int index, bool isRoot, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new VdfFormatException("入れ子が深すぎます。");
        }

        var entries = ImmutableArray.CreateBuilder<KeyValuePair<string, VdfNode>>();
        while (true)
        {
            if (index >= tokens.Length)
            {
                if (isRoot)
                {
                    return new VdfObject(entries.ToImmutable());
                }

                throw new VdfFormatException("「}」が不足しています。");
            }

            var keyToken = tokens[index];
            if (keyToken.Kind == TokenKind.CloseBrace)
            {
                if (isRoot)
                {
                    throw new VdfFormatException("対応する「{」の無い「}」があります。");
                }

                index++;
                return new VdfObject(entries.ToImmutable());
            }

            if (keyToken.Kind == TokenKind.OpenBrace)
            {
                throw new VdfFormatException("キーの無い「{」があります。");
            }

            // ここまで来たらキーは文字列。続くトークンがその値になる。
            index++;
            if (index >= tokens.Length)
            {
                throw new VdfFormatException($"キー「{keyToken.Text}」に値がありません。");
            }

            var valueToken = tokens[index];
            index++;
            switch (valueToken.Kind)
            {
                case TokenKind.OpenBrace:
                    entries.Add(new KeyValuePair<string, VdfNode>(
                        keyToken.Text,
                        ParseObject(tokens, ref index, isRoot: false, depth + 1)));
                    break;
                case TokenKind.Text:
                    entries.Add(new KeyValuePair<string, VdfNode>(keyToken.Text, new VdfString(valueToken.Text)));
                    break;
                default:
                    throw new VdfFormatException($"キー「{keyToken.Text}」の値が不正です。");
            }
        }
    }
}
