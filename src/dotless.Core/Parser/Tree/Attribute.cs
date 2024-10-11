using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dotless.Core.Parser.Infrastructure;
using dotless.Core.Parser.Infrastructure.Nodes;

namespace dotless.Core.Parser.Tree {
    public class Attribute : Node
    {
        public Node Name { get; set; }
        public Node Op { get; set; }
        public Node Value { get; set; }

        public Attribute(Node name, Node op, Node value)
        {
            Name = name;
            Op = op;
            Value = value;
        }

        protected override Node CloneCore() {
            return new Attribute(Name.Clone(), Op.Clone(), Value.Clone());
        }

        public override Node Evaluate(Env env)
        {
            var nameMem = Name.Evaluate(env).ToCSS(env);
            var opMem = Op == null ? ReadOnlyMemory<char>.Empty : Op.Evaluate(env).ToCSS(env);
            var valueMem = Value == null ? ReadOnlyMemory<char>.Empty : Value.Evaluate(env).ToCSS(env);


            var buf = new Memory<char>(new char[nameMem.Length + opMem.Length + valueMem.Length + 2]);
            buf.Span[0] = '[';
            var writeBuf = buf.Slice(1);
            
            nameMem.CopyTo(writeBuf);
            writeBuf = writeBuf.Slice(nameMem.Length);

            opMem.CopyTo(writeBuf);
            writeBuf = writeBuf.Slice(opMem.Length);

            valueMem.CopyTo(writeBuf);
            writeBuf = writeBuf.Slice(valueMem.Length);

            writeBuf.Span[0] = ']';

            return new TextNode(buf);
        }
    }
}
