namespace dotless.Core.Parser.Tree
{
    using System;
    using Infrastructure;
    using Infrastructure.Nodes;
    using Plugins;
    using dotless.Core.Utils;

    public class Element : Node
    {
        public Combinator Combinator { get; set; }

        private ReadOnlyMemory<char> _value;
        public ReadOnlyMemory<char> Value { 
            get { return _value; } 
            set { 
                _value = value; HasStringValue = true; 
            } 
        }
        public bool HasStringValue { get; private set; }
        public Node NodeValue { get; set; }

        public Element(Combinator combinator, ReadOnlyMemory<char> textValue) : this(combinator)
        {
            Value = textValue.Trim();
            HasStringValue = true;
        }

        public Element(Combinator combinator, Node value) : this(combinator)
        {
            if (value is TextNode textValue && !(value is Quoted))
            {
                Value = textValue.Value.Trim();
                HasStringValue = true;
            }
            else
            {
                NodeValue = value;
            }
        }

        private Element(Combinator combinator)
        {
            Combinator = combinator ?? new Combinator(System.ReadOnlyMemory<char>.Empty);
        }

        public override Node Evaluate(Env env)
        {
            if (NodeValue != null)
            {
                var newNodeValue = NodeValue.Evaluate(env);

                return new Element(Combinator, newNodeValue)
                    .ReducedFrom<Element>(this);
            }
            else
                return this;
        }

        protected override Node CloneCore() {
            if (NodeValue != null) {
                return new Element((Combinator) Combinator.Clone(), NodeValue.Clone());
            }

            return new Element((Combinator) Combinator.Clone(), Value);
        }

        public override void AppendCSS(Env env)
        {
            env.Output
                .Append(Combinator)
                .Push();

            if (NodeValue != null)
            {
                env.Output.Append(NodeValue)
                    .Trim();
            }
            else
            {
                env.Output.Append(Value);
            }
            
            env.Output
                .PopAndAppend();
        }

        public override void Accept(IVisitor visitor)
        {
            Combinator = VisitAndReplace(Combinator, visitor);

            NodeValue = VisitAndReplace(NodeValue, visitor, true);
        }

        internal Element Clone() {

            if (HasStringValue) {
                return new Element(Combinator, Value);
            }
            return new Element(Combinator, NodeValue);
        }
    }
}
