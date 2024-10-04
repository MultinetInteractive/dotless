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

        public Quoted(string value, char? quote)
            : base(value)
        {
            Quote = quote;
        }

        public Quoted(string value, char? quote, bool escaped)
            : base(value)
        {
            Escaped = escaped;
            Quote = quote;
        }

        public Quoted(string value, string contents, bool escaped)
            : base(contents)
        {
            Escaped = escaped;
            Quote = value[0];
        }

        public Quoted(string value, bool escaped)
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

        public MemList RenderString()
        {
            if (Escaped)
            {
                return new MemList() { UnescapeContents().AsMemory() };
            }

            var list = new MemList();

            if (Quote.HasValue)
                list.Add(new[] { Quote.Value });
            list.Add(Value.AsMemory());

            if (Quote.HasValue)
                list.Add(new[] { Quote.Value });

            return list;
        }

        public override string ToString()
        {
            return RenderString().ToString();
        }

        public override Node Evaluate(Env env)
        {
            var value = Regex.Replace(Value, @"@\{([\w-]+)\}",
                          m =>
                          {
                              var v = new Variable('@' + m.Groups[1].Value) 
                                    { Location = new NodeLocation(Location.Index + m.Index, Location.Source, Location.FileName) }
                                    .Evaluate(env);
                              return v is TextNode ? (v as TextNode).Value : v.ToCSS(env);
                          });

            return new Quoted(value, Quote, Escaped).ReducedFrom<Quoted>(this);
        }

        private readonly Regex _unescape = new Regex(@"(^|[^\\])\\(['""])");

        public string UnescapeContents()
        {
            return _unescape.Replace(Value, @"$1$2");
        }
    }
}
