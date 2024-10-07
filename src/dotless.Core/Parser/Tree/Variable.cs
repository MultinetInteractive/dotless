namespace dotless.Core.Parser.Tree
{
    using System;
    using Exceptions;
    using Infrastructure;
    using Infrastructure.Nodes;

    public class Variable : Node
    {
        public ReadOnlyMemory<char> Name { get; set; }

        public Variable(string name)
        {
            Name = name.AsMemory();
        }

        public Variable(ReadOnlyMemory<char> name)
        {
            Name = name;
        }

        protected override Node CloneCore() {
            return new Variable(Name);
        }

        public override Node Evaluate(Env env)
        {
            var name = Name;
            if (name.Span.StartsWith("@@".AsSpan()))
            {
                var v = new Variable(name.Slice(1)).Evaluate(env);
                name = ('@' + (v is TextNode ? (v as TextNode).Value.ToString() : v.ToCSS(env).ToString())).AsMemory();
            }

            if (env.IsEvaluatingVariable(name)) {
                throw new ParsingException("Recursive variable definition for " + name, Location);
            }

            var variable = env.FindVariable(name);

            if (variable) {
                return variable.Value.Evaluate(env.CreateVariableEvaluationEnv(name));
            }

            throw new ParsingException("variable " + name + " is undefined", Location);
        }
    }
}
