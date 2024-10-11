using System;

namespace dotless.Core.Parser.Tree
{
    using System.Text.RegularExpressions;
    using Infrastructure;
    using Infrastructure.Nodes;
using System.Text;
    using System.Collections.Generic;
    using dotless.Core.Utils;

    public class Quoted : TextNode
    {
        public char? Quote { get; set; }
        public bool Escaped { get; set; }

        public Quoted(ReadOnlyMemory<char> value, char? quote)
            : base(value)
        {
            Quote = quote;
        }

        public Quoted(ReadOnlyMemory<char> value, char? quote, bool escaped)
            : base(value)
        {
            Escaped = escaped;
            Quote = quote;
        }

        public Quoted(ReadOnlyMemory<char> value, ReadOnlyMemory<char> contents, bool escaped)
            : base(contents)
        {
            Escaped = escaped;
            Quote = value.Span[0];
        }

        public Quoted(ReadOnlyMemory<char> value, bool escaped)
            : base(value)
        {
            Escaped = escaped;
            Quote = null;
        }

        protected override Node CloneCore()
        {
            return new Quoted(Value, Quote, Escaped);
        }

        public override void AppendCSS(Env env)
        {
            env.Output
                .Append(RenderString());
        }

        private ReadOnlyMemory<char> quotedString;

        public ReadOnlyMemory<char> RenderString()
        {
            if (Escaped)
            {
                return unescapeContentsMem();
            }

            var list = new MemList();

            if (!Quote.HasValue)
                return Value;

            if(quotedString.Length == (Value.Length + 2) && quotedString.Slice(1, Value.Length).Span.Equals(Value.Span, StringComparison.Ordinal))
            {
                return quotedString;
            }

            if (Quote.HasValue)
                list.Add(new[] { Quote.Value });
            list.Add(Value);

            if (Quote.HasValue)
                list.Add(new[] { Quote.Value });

            quotedString = list.ToMemory();
            return quotedString;
        }

        public override string ToString()
        {
            return RenderString().ToString();
        }

        public override ReadOnlyMemory<char> ToMemory()
        {
            return RenderString();
        }

        public override Node Evaluate(Env env)
        {
            var value = Regex.Replace(Value.ToString(), @"@\{([\w-]+)\}",
                          m =>
                          {
                              var v = new Variable('@' + m.Groups[1].Value) 
                                    { Location = new NodeLocation(Location.Index + m.Index, Location.Source, Location.FileName) }
                                    .Evaluate(env);
                              return v is TextNode ? (v as TextNode).Value.ToString() : v.ToCSS(env).ToString();
                          });

            return new Quoted(value.AsMemory(), Quote, Escaped).ReducedFrom<Quoted>(this);
        }

        private readonly Regex _unescape = new Regex(@"(^|[^\\])\\(['""])");

        public string UnescapeContents()
        {
            return _unescape.Replace(Value.ToString(), @"$1$2");
        }

        public ReadOnlyMemory<char> unescapeContentsMem()
        {
            var outBuffer = new Memory<char>(new char[Value.Length]);
            int outLength = 0;

            bool GetChar(int i, out char c)
            {
                if (i >= 0 && i < Value.Length)
                {
                    c = Value.Span[i];
                    return true;
                }
                c = ' ';
                return false;
            }

            for (int i = 0; i < Value.Length; i++)
            {
                if (GetChar(i, out var current) && current == '\\' && (!GetChar(i - 1, out var prev) || prev != '\\') && GetChar(i + 1, out var next) && (next == '\'' || next == '"'))
                {
                    continue;
                }
                outBuffer.Span[outLength++] = Value.Span[i];
            }

            return outBuffer.Slice(0, outLength);
        }


    }
}
