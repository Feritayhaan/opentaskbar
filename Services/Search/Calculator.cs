using System.Globalization;

namespace OpenTaskbar.Services.Search;

/// <summary>Small recursive-descent expression evaluator for Spotlight-style inline math.</summary>
public static class Calculator
{
    private static readonly string[] Functions = ["sqrt", "sin", "cos", "tan", "log", "ln", "abs", "round", "floor", "ceil", "kok", "karekok"];

    public static bool LooksLikeExpression(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var hasDigit = query.Any(char.IsDigit) || query.Contains("pi", StringComparison.OrdinalIgnoreCase);
        if (!hasDigit) return false;
        var hasOperator = query.IndexOfAny(['+', '-', '*', '/', '^', '%', '(', 'x', '×', '÷']) >= 0
            || Functions.Any(function => query.Contains(function, StringComparison.OrdinalIgnoreCase));
        return hasOperator;
    }

    public static bool TryEvaluate(string expression, out double value)
    {
        value = 0;
        try
        {
            var parser = new Parser(Prepare(expression));
            value = parser.ParseExpression();
            if (!parser.AtEnd) return false;
            return double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static string Format(double value, CultureInfo culture)
    {
        if (Math.Abs(value) >= 1e15 || (Math.Abs(value) < 1e-6 && value != 0))
            return value.ToString("0.######E+0", culture);
        var rounded = Math.Round(value, 10);
        return rounded.ToString("#,0.##########", culture);
    }

    private static string Prepare(string expression)
    {
        var text = expression.Trim().ToLowerInvariant()
            .Replace('×', '*').Replace('÷', '/').Replace("**", "^")
            .Replace("karekök", "sqrt").Replace("karekok", "sqrt").Replace("kök", "sqrt").Replace("kok", "sqrt");
        if (text.StartsWith('=')) text = text[1..];
        // "3x4" style multiplication.
        var chars = text.ToCharArray();
        for (var i = 1; i < chars.Length - 1; i++)
        {
            if (chars[i] == 'x' && (char.IsDigit(chars[i - 1]) || chars[i - 1] is ')' or ' ')
                && (char.IsDigit(chars[i + 1]) || chars[i + 1] is '(' or ' '))
                chars[i] = '*';
        }

        text = new string(chars);
        // Accept both "1,5" and "1.5"; treat comma as decimal separator when it is not a thousands group.
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        else text = text.Replace(",", "");
        return text;
    }

    private sealed class Parser(string text)
    {
        private int _position;

        public bool AtEnd
        {
            get
            {
                SkipSpaces();
                return _position >= text.Length;
            }
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipSpaces();
                if (Match('*')) value *= ParseFactor();
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0) throw new FormatException();
                    value /= divisor;
                }
                else if (Peek() == '(') value *= ParseFactor();
                else return value;
            }
        }

        private double ParseFactor()
        {
            var value = ParseUnary();
            SkipSpaces();
            if (Match('^')) value = Math.Pow(value, ParseFactor());
            return value;
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            var value = ParsePrimary();
            SkipSpaces();
            if (Match('%')) value /= 100;
            return value;
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (Match('('))
            {
                var inner = ParseExpression();
                SkipSpaces();
                if (!Match(')')) throw new FormatException();
                return inner;
            }

            if (char.IsDigit(Peek()) || Peek() == '.')
            {
                var start = _position;
                while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] == '.')) _position++;
                if (_position < text.Length && text[_position] == 'e' && _position + 1 < text.Length
                    && (char.IsDigit(text[_position + 1]) || text[_position + 1] is '-' or '+'))
                {
                    _position += 2;
                    while (_position < text.Length && char.IsDigit(text[_position])) _position++;
                }

                return double.Parse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            if (char.IsLetter(Peek()))
            {
                var start = _position;
                while (_position < text.Length && char.IsLetter(text[_position])) _position++;
                var name = text[start.._position];
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }

                var argument = ParseUnary();
                return name switch
                {
                    "sqrt" => argument < 0 ? throw new FormatException() : Math.Sqrt(argument),
                    "sin" => Math.Sin(argument * Math.PI / 180),
                    "cos" => Math.Cos(argument * Math.PI / 180),
                    "tan" => Math.Tan(argument * Math.PI / 180),
                    "log" => Math.Log10(argument),
                    "ln" => Math.Log(argument),
                    "abs" => Math.Abs(argument),
                    "round" => Math.Round(argument),
                    "floor" => Math.Floor(argument),
                    "ceil" => Math.Ceiling(argument),
                    _ => throw new FormatException()
                };
            }

            throw new FormatException();
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private bool Match(char character)
        {
            if (Peek() != character) return false;
            _position++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }
    }
}
