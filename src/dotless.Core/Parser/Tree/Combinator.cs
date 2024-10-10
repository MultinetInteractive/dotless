namespace dotless.Core.Parser.Tree
{
    using System;
    using System.Collections.Generic;
    using Infrastructure;
    using Infrastructure.Nodes;
    using dotless.Core.Utils;

    public class Combinator : Node
    {
        public ReadOnlyMemory<char> Value { get; set; }

        public Combinator(ReadOnlyMemory<char> value)
        {
            if (value.Length == 1 && value.Span[0] == ' ')
                Value = value;
            else
                Value = value.Trim();
        }

        protected override Node CloneCore() {
            return new Combinator(Value);
        }

        public override void AppendCSS(Env env)
        {
            env.Output.Append(GetValue(env));
        }

        private ReadOnlyMemory<char> GetValue(Env env) {
            if(Value.Length == 1)
            {
                switch(Value.Span[0])
                {
                    case '+':
                        return env.Compress ? Value : " + ".AsMemory();
                    case '~':
                        return env.Compress ? Value : " ~ ".AsMemory();
                    case '>':
                        return env.Compress ? Value : " > ".AsMemory();
                }
            }

            return Value;
        }
    }
}
