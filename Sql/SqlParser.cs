namespace LsmWriteDb.Sql;

internal sealed class SqlParser
{
    private readonly IReadOnlyList<SqlToken> _tokens;
    private int _position;

    private SqlParser(IReadOnlyList<SqlToken> tokens)
    {
        _tokens = tokens;
    }

    public static SqlStatement Parse(string sql)
    {
        var parser = new SqlParser(SqlTokenizer.Tokenize(sql));
        var statement = parser.ParseStatement();
        parser.MatchSymbol(";");
        parser.ExpectEnd();
        return statement;
    }

    private SqlStatement ParseStatement()
    {
        if (MatchKeyword("BEGIN"))
        {
            MatchKeyword("TRANSACTION");
            return new SqlBeginStatement();
        }

        if (MatchKeyword("COMMIT"))
        {
            MatchKeyword("TRANSACTION");
            return new SqlCommitStatement();
        }

        if (MatchKeyword("ROLLBACK"))
        {
            MatchKeyword("TRANSACTION");
            return new SqlRollbackStatement();
        }

        if (MatchKeyword("CREATE"))
        {
            return ParseCreate();
        }

        if (MatchKeyword("INSERT"))
        {
            return ParseInsert();
        }

        if (MatchKeyword("SELECT"))
        {
            return ParseSelect();
        }

        if (MatchKeyword("UPDATE"))
        {
            return ParseUpdate();
        }

        if (MatchKeyword("DELETE"))
        {
            return ParseDelete();
        }

        throw Error("Expected BEGIN, COMMIT, ROLLBACK, CREATE, INSERT, SELECT, UPDATE, or DELETE.");
    }

    private SqlCreateTableStatement ParseCreate()
    {
        ExpectKeyword("TABLE");
        if (MatchKeyword("IF"))
        {
            ExpectKeyword("NOT");
            ExpectKeyword("EXISTS");
        }

        return new SqlCreateTableStatement(ExpectTableName());
    }

    private SqlInsertStatement ParseInsert()
    {
        ExpectKeyword("INTO");
        var table = ExpectTableName();

        var columns = new List<string>();
        if (MatchSymbol("("))
        {
            do
            {
                columns.Add(ExpectColumnName());
            }
            while (MatchSymbol(","));

            ExpectSymbol(")");
        }
        else
        {
            columns.Add("key");
            columns.Add("value");
        }

        ExpectKeyword("VALUES");
        ExpectSymbol("(");

        var values = new List<string>();
        do
        {
            values.Add(ExpectStringLiteral());
        }
        while (MatchSymbol(","));

        ExpectSymbol(")");

        if (columns.Count != values.Count)
        {
            throw Error("INSERT column count must match value count.");
        }

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            row[columns[i]] = values[i];
        }

        if (!row.TryGetValue("key", out var key) || !row.TryGetValue("value", out var value))
        {
            throw Error("INSERT requires key and value columns.");
        }

        return new SqlInsertStatement(table, key, value);
    }

    private SqlSelectStatement ParseSelect()
    {
        var columns = ParseSelectColumns();
        ExpectKeyword("FROM");
        var table = ExpectTableName();

        string? key = null;
        string? start = null;
        string? end = null;

        if (MatchKeyword("WHERE"))
        {
            (key, start, end) = ParseWhereClause();
        }

        var limit = 100;
        if (MatchKeyword("LIMIT"))
        {
            limit = ExpectPositiveNumber();
        }

        return new SqlSelectStatement(table, columns, key, start, end, limit);
    }

    private IReadOnlyList<string> ParseSelectColumns()
    {
        if (MatchSymbol("*"))
        {
            return ["key", "value"];
        }

        var columns = new List<string>();
        do
        {
            columns.Add(ExpectColumnName());
        }
        while (MatchSymbol(","));

        return columns;
    }

    private (string? Key, string? Start, string? End) ParseWhereClause()
    {
        ExpectIdentifier("key");

        if (MatchKeyword("BETWEEN"))
        {
            var start = ExpectStringLiteral();
            ExpectKeyword("AND");
            var end = ExpectStringLiteral();
            return (null, start, end);
        }

        if (MatchSymbol("="))
        {
            return (ExpectStringLiteral(), null, null);
        }

        string? startBound = null;
        string? endBound = null;

        if (MatchSymbol(">="))
        {
            startBound = ExpectStringLiteral();
        }
        else if (MatchSymbol("<="))
        {
            endBound = ExpectStringLiteral();
        }
        else
        {
            throw Error("WHERE supports key = 'value', key BETWEEN 'start' AND 'end', key >= 'start', and key <= 'end'.");
        }

        if (MatchKeyword("AND"))
        {
            ExpectIdentifier("key");
            if (MatchSymbol(">="))
            {
                if (startBound is not null)
                {
                    throw Error("Duplicate lower key bound in WHERE clause.");
                }

                startBound = ExpectStringLiteral();
            }
            else if (MatchSymbol("<="))
            {
                if (endBound is not null)
                {
                    throw Error("Duplicate upper key bound in WHERE clause.");
                }

                endBound = ExpectStringLiteral();
            }
            else
            {
                throw Error("Range WHERE clauses support only key >= 'start' and key <= 'end'.");
            }
        }

        return (null, startBound, endBound);
    }

    private SqlUpdateStatement ParseUpdate()
    {
        var table = ExpectTableName();
        ExpectKeyword("SET");
        ExpectIdentifier("value");
        ExpectSymbol("=");
        var value = ExpectStringLiteral();
        ExpectKeyword("WHERE");
        ExpectIdentifier("key");
        ExpectSymbol("=");
        var key = ExpectStringLiteral();

        return new SqlUpdateStatement(table, key, value);
    }

    private SqlDeleteStatement ParseDelete()
    {
        ExpectKeyword("FROM");
        var table = ExpectTableName();
        ExpectKeyword("WHERE");
        ExpectIdentifier("key");
        ExpectSymbol("=");
        var key = ExpectStringLiteral();

        return new SqlDeleteStatement(table, key);
    }

    private string ExpectTableName()
    {
        return ExpectIdentifier().ToLowerInvariant();
    }

    private string ExpectColumnName()
    {
        var column = ExpectIdentifier();
        if (!string.Equals(column, "key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(column, "value", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("Only key and value columns are supported.");
        }

        return column.ToLowerInvariant();
    }

    private void ExpectIdentifier(string expected)
    {
        var value = ExpectIdentifier();
        if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Error($"Expected {expected}.");
        }
    }

    private string ExpectIdentifier()
    {
        var token = Current;
        if (token.Kind != SqlTokenKind.Identifier)
        {
            throw Error("Expected identifier.");
        }

        _position++;
        return token.Value;
    }

    private string ExpectStringLiteral()
    {
        var token = Current;
        if (token.Kind != SqlTokenKind.String)
        {
            throw Error("Expected string literal.");
        }

        _position++;
        return token.Value;
    }

    private int ExpectPositiveNumber()
    {
        var token = Current;
        if (token.Kind != SqlTokenKind.Number || !int.TryParse(token.Value, out var value) || value <= 0)
        {
            throw Error("Expected positive number.");
        }

        _position++;
        return value;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!MatchKeyword(keyword))
        {
            throw Error($"Expected {keyword}.");
        }
    }

    private bool MatchKeyword(string keyword)
    {
        if (Current.Kind == SqlTokenKind.Identifier
            && string.Equals(Current.Value, keyword, StringComparison.OrdinalIgnoreCase))
        {
            _position++;
            return true;
        }

        return false;
    }

    private void ExpectSymbol(string symbol)
    {
        if (!MatchSymbol(symbol))
        {
            throw Error($"Expected {symbol}.");
        }
    }

    private bool MatchSymbol(string symbol)
    {
        if (Current.Kind == SqlTokenKind.Symbol
            && string.Equals(Current.Value, symbol, StringComparison.Ordinal))
        {
            _position++;
            return true;
        }

        return false;
    }

    private void ExpectEnd()
    {
        if (Current.Kind != SqlTokenKind.End)
        {
            throw Error("Unexpected token after end of statement.");
        }
    }

    private SqlParseException Error(string message)
    {
        return new SqlParseException($"{message} Near '{Current.Value}'.");
    }

    private SqlToken Current => _tokens[_position];
}

internal enum SqlTokenKind
{
    Identifier,
    String,
    Number,
    Symbol,
    End
}

internal sealed record SqlToken(SqlTokenKind Kind, string Value);

internal static class SqlTokenizer
{
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var index = 0;

        while (index < sql.Length)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Identifier, sql[start..index]));
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index;
                index++;
                while (index < sql.Length && char.IsDigit(sql[index]))
                {
                    index++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..index]));
                continue;
            }

            if (current == '\'')
            {
                tokens.Add(new SqlToken(SqlTokenKind.String, ReadString(sql, ref index)));
                continue;
            }

            if ((current == '>' || current == '<') && index + 1 < sql.Length && sql[index + 1] == '=')
            {
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, sql[index..(index + 2)]));
                index += 2;
                continue;
            }

            if (current is '(' or ')' or ',' or ';' or '*' or '=')
            {
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, current.ToString()));
                index++;
                continue;
            }

            throw new SqlParseException($"Unexpected character '{current}'.");
        }

        tokens.Add(new SqlToken(SqlTokenKind.End, "<end>"));
        return tokens;
    }

    private static string ReadString(string sql, ref int index)
    {
        index++;
        var value = new System.Text.StringBuilder();

        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '\'')
            {
                if (index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    value.Append('\'');
                    index += 2;
                    continue;
                }

                index++;
                return value.ToString();
            }

            value.Append(current);
            index++;
        }

        throw new SqlParseException("Unterminated string literal.");
    }
}
