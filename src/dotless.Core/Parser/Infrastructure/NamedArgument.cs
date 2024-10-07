namespace dotless.Core.Parser.Infrastructure
{
    using System;
    using Tree;

    public class NamedArgument
    {
        public ReadOnlyMemory<char> Name { get; set; }
        public Expression Value { get; set; }
    }
}
