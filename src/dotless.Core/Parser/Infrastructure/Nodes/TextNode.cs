using System;
using dotless.Core.Utils;

namespace dotless.Core.Parser.Infrastructure.Nodes
{
    public class TextNode : Node, IComparable
    {
        public ReadOnlyMemory<char> Value { get; set; }

        public TextNode(string contents)
        {
            Value = contents.AsMemory();
        }

        public TextNode(ReadOnlyMemory<char> contents)
        {
            Value = contents;
        }

        public static TextNode operator &(TextNode n1, TextNode n2)
        {
            return n1 != null ? n2 : null;
        }

        public static TextNode operator |(TextNode n1, TextNode n2)
        {
            return n1 ?? n2;
        }

        protected override Node CloneCore() {
            return new TextNode(Value);
        }

        public override void AppendCSS(Env env)
        {
            env.Output.Append(env.Compress ? Value.Trim() : Value);
        }

        public override string ToString()
        {
            return Value.ToString();
        }

        public virtual ReadOnlyMemory<char> ToMemory()
        {
            return Value;
        }

        public virtual int CompareTo(object obj)
        {
            if (obj == null)
            {
                return -1;
            }

            if (obj is TextNode t)
            {
                return t.ToMemory().Span.CompareTo(ToMemory().Span, StringComparison.Ordinal);
            }

            return obj.ToString().AsSpan().CompareTo(ToMemory().Span, StringComparison.Ordinal);
        }
    }
}
