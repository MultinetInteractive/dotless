using System;

namespace dotless.Core.Parser.Infrastructure.Nodes
{
    public class CharMatchResult : TextNode
    {
        public char Char { get; set; }

        public CharMatchResult(ReadOnlyMemory<char> value) : base(value)
        {
            if (value.Length != 1)
                throw new ArgumentException("Value length cannot differ from 1");

            Char = value.Span[0];
        }
    }
}
