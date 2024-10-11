namespace dotless.Core.Parser.Infrastructure.Nodes
{
    using System;
    using System.Text.RegularExpressions;

    public class RegexMatchResult : TextNode
    {
        public Match Match { get; set; }

        public RegexMatchResult(Match match) : base(match.Value)
        {
            Match = match;
        }

        public RegexMatchResult(ReadOnlyMemory<char> keyword, NodeLocation location) : base(keyword)
        {
            Match = null;
            Location = location;
        }

        public string this[int index]
        {
            get
            {
                var value = Match.Groups[index].Value;
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
    }
}
