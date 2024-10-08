using System;

namespace dotless.Core.Parser.Tree
{
    using System.Linq;
    using System.Text;
    using Infrastructure;
    using Infrastructure.Nodes;
    using Plugins;

    public class Call : Node
    {
        public ReadOnlyMemory<char> Name { get; set; }
        public NodeList<Node> Arguments { get; set; }

        public Call(ReadOnlyMemory<char> name, NodeList<Node> arguments)
        {
            Name = name;
            Arguments = arguments;
        }

        protected Call()
        {
        }

        protected override Node CloneCore() {
            return new Call(Name, (NodeList<Node>) Arguments.Clone());
        }

        public override Node Evaluate(Env env)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            var args = Arguments.Select(a => a.Evaluate(env));

            var function = env.GetFunction(Name);

            if (function != null)
            {
                function.Name = Name.ToString();
                function.Location = Location;
                return function.Call(env, args).ReducedFrom<Node>(this);
            }

            env.Output.Push();
            
            env.Output
                .Append(Name)
                .Append("(")
                .AppendMany(args, env.Compress ? ",".AsMemory() : ", ".AsMemory())
                .Append(")");

            var css = env.Output.Pop();

            return new TextNode(css.ToMemory()).ReducedFrom<Node>(this);
        }

        public override void Accept(IVisitor visitor)
        {
            Arguments = VisitAndReplace(Arguments, visitor);
        }
    }
}
