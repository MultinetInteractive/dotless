namespace dotless.Core.Parser.Tree
{
    using System;
    using Infrastructure;
    using Infrastructure.Nodes;

    public class Script : Node
    {
        public ReadOnlyMemory<char> Expression { get; set; }

        public Script(ReadOnlyMemory<char> script)
        {
            Expression = script;
        }

        protected override Node CloneCore() {
            return new Script(Expression);
        }

        public override Node Evaluate(Env env)
        {
            return new TextNode("[script unsupported]".AsMemory());
        }
    }
}
