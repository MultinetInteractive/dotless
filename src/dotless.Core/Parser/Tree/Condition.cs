﻿using System.Linq;
using dotless.Core.Utils;

namespace dotless.Core.Parser.Tree
{
    using Infrastructure;
    using Infrastructure.Nodes;
    using Plugins;
    using System;
    using dotless.Core.Exceptions;

    public class Condition : Node
    {
        public Node Left { get; set; }
        public Node Right { get; set; }
        public ReadOnlyMemory<char> Operation { get; set; }
        public bool Negate { get; set; }

        public bool IsDefault { get; private set; }

        public Condition(Node left, ReadOnlyMemory<char> operation, Node right, bool negate)
        {
            Left = left;
            Right = right;
            Operation = operation.Trim();
            Negate = negate;
        }

        protected override Node CloneCore() {
            return new Condition(Left.Clone(), Operation, Right.Clone(), Negate);
        }

        public override void AppendCSS(Env env)
        {
        }

        public override Node Evaluate(Env env)
        {
            var leftCall = Left as Call;
            if (leftCall != null && leftCall.Name.Span.SequenceEqual("default".AsSpan())) {
                IsDefault = true;
            }

            var lValue = Left.Evaluate(env);
            var rValue = Right.Evaluate(env);

            bool value = Evaluate(lValue, Operation, rValue);

            if (Negate)
            {
                value = !value;
            }

            return new BooleanNode(value);
        }

        private bool Evaluate(Node lValue, ReadOnlyMemory<char> operation, Node rValue)
        {
            switch (operation.ToString())
            {
                case "or":
                    return ToBool(lValue) || ToBool(rValue);
                case "and":
                    return ToBool(lValue) && ToBool(rValue);
                default:
                    int result;
                    IComparable lValueComparable = lValue as IComparable;
                    if (lValueComparable != null)
                    {
                        result = lValueComparable.CompareTo(rValue);
                    }
                    else
                    {
                        IComparable rValueComparable = rValue as IComparable;
                        if (rValueComparable != null)
                        {
                            result = rValueComparable.CompareTo(lValue);

                            // had to compare right to left so reverse
                            if (result < 0)
                            {
                                result = 1;
                            }
                            else if (result > 0)
                            {
                                result = -1;
                            }
                        }
                        else
                        {
                            throw new ParsingException("Cannot compare objects in mixin guard condition", Location);
                        }
                    }

                    if (result == 0)
                    {
                        return operation.Span.SequenceEqual("=".AsSpan()) || operation.Span.SequenceEqual(">=".AsSpan()) || operation.Span.SequenceEqual("=<".AsSpan());
                    }
                    else if (result < 0)
                    {
                        return operation.Span.SequenceEqual("<".AsSpan()) || operation.Span.SequenceEqual("=<".AsSpan());
                    }
                    else if (result > 0)
                    {
                        return operation.Span.SequenceEqual(">".AsSpan()) || operation.Span.SequenceEqual(">=".AsSpan());
                    }
                    break;
            }

            throw new ParsingException("C# compiler can't work out it is impossible to get here", Location);
        }

        /// <summary>
        ///  Evaluates and returns if the condition passes true
        /// </summary>
        public bool Passes(Env env)
        {
            return ToBool(Evaluate(env));
        }

        /// <summary>
        ///  At the moment should only need to be called on the eval'd result of a Condition
        /// </summary>
        private static bool ToBool(Node node)
        {
            BooleanNode bNode = node as BooleanNode;

            if (bNode == null)
                return false;

            return bNode.Value;
        }

        public override void Accept(IVisitor visitor)
        {
            Left = VisitAndReplace(Left, visitor);
            Right = VisitAndReplace(Right, visitor);
        }
    }
}
