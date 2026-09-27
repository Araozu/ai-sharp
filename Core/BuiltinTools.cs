using System.Globalization;
using System.Text.Json;

namespace AiSharp.Core;

/// <summary>Safe arithmetic evaluator: + - * / % ^, parentheses, unary minus, decimals. No code execution.</summary>
public sealed class CalcTool : ITool
{
    public string Name => "calc";
    public string Description => "Evaluate a simple arithmetic expression (numbers, + - * / % ^, parentheses). Returns the numeric result.";
    public string Approval => "auto";
    public string ParametersSchemaJson => """{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"],"additionalProperties":false}""";

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var expr = args.GetProperty("expression").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(expr)) throw new ArgumentException("expression is empty.");
        if (expr.Length > 500) throw new ArgumentException("expression is too long (max 500 chars).");
        var parser = new Parser(expr);
        var value = parser.Parse(ct);
        return Task.FromResult(value.ToString("G", CultureInfo.InvariantCulture));
    }

    private sealed class Parser(string text)
    {
        private int _pos;
        public double Parse(CancellationToken ct)
        {
            var v = AddSub(ct);
            SkipWs();
            if (_pos != text.Length) throw new ArgumentException($"Unexpected '{text[_pos]}' at position {_pos}.");
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("Result is not finite.");
            return v;
        }
        private double AddSub(CancellationToken ct)
        {
            var v = MulDiv(ct);
            while (true)
            {
                SkipWs();
                if (!More()) return v;
                var c = text[_pos];
                if (c is not ('+' or '-')) return v;
                _pos++;
                var r = MulDiv(ct);
                v = c == '+' ? v + r : v - r;
            }
        }
        private double MulDiv(CancellationToken ct)
        {
            var v = Power(ct);
            while (true)
            {
                SkipWs();
                if (!More()) return v;
                var c = text[_pos];
                if (c is not ('*' or '/' or '%')) return v;
                _pos++;
                var r = Power(ct);
                v = c switch
                {
                    '*' => v * r,
                    '/' => r == 0 ? throw new ArgumentException("Division by zero.") : v / r,
                    _ => r == 0 ? throw new ArgumentException("Modulo by zero.") : v % r,
                };
            }
        }
        private double Power(CancellationToken ct)
        {
            var v = Unary(ct);
            SkipWs();
            if (More() && text[_pos] == '^')
            {
                _pos++;
                var r = Power(ct); // right associative
                v = Math.Pow(v, r);
            }
            return v;
        }
        private double Unary(CancellationToken ct)
        {
            SkipWs();
            if (More() && text[_pos] == '-') { _pos++; return -Unary(ct); }
            if (More() && text[_pos] == '+') { _pos++; return Unary(ct); }
            return Primary(ct);
        }
        private double Primary(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SkipWs();
            if (!More()) throw new ArgumentException("Unexpected end of expression.");
            if (text[_pos] == '(')
            {
                _pos++;
                var v = AddSub(ct);
                SkipWs();
                if (!More() || text[_pos] != ')') throw new ArgumentException("Missing closing parenthesis.");
                _pos++;
                return v;
            }
            var start = _pos;
            while (More() && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
            if (start == _pos) throw new ArgumentException($"Expected a number at position {start}.");
            var token = text[start.._pos];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                throw new ArgumentException($"'{token}' is not a valid number.");
            return num;
        }
        private void SkipWs() { while (More() && char.IsWhiteSpace(text[_pos])) _pos++; }
        private bool More() => _pos < text.Length;
    }
}

/// <summary>Returns the current UTC time. No input, no side effects.</summary>
public sealed class TimeTool : ITool
{
    public string Name => "time_now";
    public string Description => "Return the current UTC date and time (ISO 8601). Takes no arguments.";
    public string Approval => "auto";
    public string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) =>
        Task.FromResult(DateTimeOffset.UtcNow.ToString("o"));
}
