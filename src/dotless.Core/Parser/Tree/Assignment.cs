namespace dotless.Core.Parser.Tree
{
    using System;
    using Infrastructure;
    using Infrastructure.Nodes;

    public class Assignment : Node
    {
        public ReadOnlyMemory<char> Key { get; set; }
        public Node Value { get; set; }

        public Assignment(ReadOnlyMemory<char> key, Node value)
        {
            Key = key;
            Value = value;
        }

        public override Node Evaluate(Env env)
        {
            return new Assignment(Key, Value.Evaluate(env)) {Location = Location};
        }

        protected override Node CloneCore() {
            return new Assignment(Key, Value.Clone());
        }

        public override void AppendCSS(Env env)
        {
            env.Output
                .Append(Key)
                .Append("=")
                .Append(Value);
        }

        public override void Accept(Plugins.IVisitor visitor)
        {
            Value = VisitAndReplace(Value, visitor);
        }
    }
}
