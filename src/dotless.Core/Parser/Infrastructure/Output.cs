namespace dotless.Core.Parser.Infrastructure
{
    using System;
    using System.Globalization;
    using System.Collections.Generic;
    using System.Text;
    using Nodes;
    using dotless.Core.Utils;

    public class Output
    {
        private Env Env { get; set; }
        private MemList Builder { get; set; }
        private Stack<MemList> BuilderStack { get; set; }

        public Output(Env env)
        {
            Env = env;
            BuilderStack = new Stack<MemList>();

            Push();
        }

        public Output Push()
        {
            Builder = new MemList();

            BuilderStack.Push(Builder);

            return this;
        }

        public MemList Pop()
        {
            if (BuilderStack.Count == 1)
                throw new InvalidOperationException();

            var sb = BuilderStack.Pop();

            Builder = BuilderStack.Peek();

            return sb;
        }

        public void Reset(string s)
        {
            Builder = new MemList() { s.AsMemory() };

            BuilderStack.Pop();
            BuilderStack.Push(Builder);
        }

        public Output PopAndAppend()
        {
            return Append(Pop());
        }

        public Output Append(Node node)
        {
            if (node != null)
            {
                if (node.PreComments)
                    node.PreComments.AppendCSS(Env);

                node.AppendCSS(Env);

                if (node.PostComments)
                    node.PostComments.AppendCSS(Env);
            }

            return this;
        }

        public Output Append(string s)
        {
            Builder.Add(s.AsMemory());

            return this;
        }

        public Output Append(ReadOnlyMemory<char> s)
        {
            Builder.Add(s);

            return this;
        }

        public Output Append(char? s)
        {
            if (s.HasValue)
            {
                Builder.Add(new ReadOnlyMemory<char>(new[] { s.Value }));
            }
            return this;
        }

        public Output Append(MemList sb)
        {
            Builder.AddRange(sb);

            return this;
        }

        public Output AppendMany<TNode>(IEnumerable<TNode> nodes)
            where TNode : Node
        {
            return AppendMany(nodes, null);
        }

        public Output AppendMany<TNode>(IEnumerable<TNode> nodes, ReadOnlyMemory<char> join)
            where TNode : Node
        {
            return AppendMany(nodes, n => Env.Output.Append(n), join);
        }

        public Output AppendMany(IEnumerable<string> list, ReadOnlyMemory<char> join)
        {
            return AppendMany(list, (item, sb) => sb.Add(item.AsMemory()), join);
        }

        public Output AppendMany(IEnumerable<ReadOnlyMemory<char>> list, ReadOnlyMemory<char> join)
        {
            return AppendMany(list, (item, sb) => sb.Add(item), join);
        }

        public Output AppendMany<T>(IEnumerable<T> list, Func<T, string> toString, ReadOnlyMemory<char> join)
        {
            return AppendMany(list, (item, sb) => sb.Add(toString(item).AsMemory()), join);
        }

        public Output AppendMany<T>(IEnumerable<T> list, Action<T> toString, ReadOnlyMemory<char> join)
        {
            return AppendMany(list, (item, sb) => toString(item), join);
        }

        public Output AppendMany<T>(IEnumerable<T> list, Action<T, MemList> toString, ReadOnlyMemory<char> join)
        {
            var first = true;
            var hasJoinString = !join.IsEmpty;

            foreach (var item in list)
            {
                if (!first && hasJoinString)
                    Builder.Add(join);

                first = false;
                toString(item, Builder);
            }

            return this;
        }

        public Output AppendMany(IEnumerable<MemList> buildersToAppend)
        {
            return AppendMany(buildersToAppend, null);
        }

        public Output AppendMany(IEnumerable<MemList> buildersToAppend, ReadOnlyMemory<char> join)
        {
            return AppendMany(buildersToAppend, (b, output) => output.AddRange(b), join);
        }

        public Output AppendFormat(string format, params object[] values) {
            return AppendFormat(CultureInfo.InvariantCulture, format, values);
        }

        public Output AppendFormat(IFormatProvider formatProvider, string format, params object[] values)
        {
            Builder.Add(string.Format(formatProvider, format, values).AsMemory());

            return this;
        }

        public Output Indent(int amount)
        {
            if (amount > 0)
            {
                var indentation = new string(' ', amount);
                for (int i = 0; i < Builder.Count; i++)
                {
                    if (Builder[i].Span.Contains("\n".AsSpan(), StringComparison.Ordinal))
                    {
                        Builder[i] = Builder[i].ToString().Replace("\n", "\n" + indentation).AsMemory();
                    }
                }

                Builder.Insert(0, indentation.AsMemory());
            }

            return this;
        }

        /// <summary>
        ///  Trims whitespace
        /// </summary>
        public Output Trim()
        {
            return this.TrimLeft(null).TrimRight(null);
        }

        /// <summary>
        /// Trims the character passed or whitespace if it has no value from the left
        /// </summary>
        public Output TrimLeft(char? c)
        {
            while(Builder.Count > 0)
            {
                Builder[0] = TrimLeft(Builder[0], c);

                if (Builder[0].Length > 0)
                    break;

                Builder.RemoveAt(0);
            }
            
            return this;
        }

        private ReadOnlyMemory<char> TrimLeft(ReadOnlyMemory<char> input, char? c)
        {
            int trimLength = 0;
            if (c.HasValue)
            {
                while (input.Length > trimLength)
                {
                    if (input.Span[trimLength] == c.Value)
                    {
                        trimLength++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                while (input.Length > trimLength)
                {
                    if (char.IsWhiteSpace(input.Span[trimLength]))
                    {
                        trimLength++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return input.Slice(trimLength);
        }

        /// <summary>
        /// Trims the character passed or whitespace if it has no value from the left
        /// </summary>
        public Output TrimRight(char? c)
        {
            while (Builder.Count > 0)
            {
                var lastIndex = Builder.Count - 1;
                Builder[lastIndex] = TrimRight(Builder[lastIndex], c);

                if (Builder[lastIndex].Length > 0)
                    break;

                Builder.RemoveAt(lastIndex);
            }

            return this;
        }

        private ReadOnlyMemory<char> TrimRight(ReadOnlyMemory<char> input, char? c)
        {
            int lastIndex = input.Length-1;
            if (c.HasValue)
            {
                while (lastIndex >= 0)
                {
                    if (input.Span[lastIndex] == c.Value)
                    {
                        lastIndex--;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                while (lastIndex >= 0)
                {
                    if (char.IsWhiteSpace(input.Span[lastIndex]))
                    {
                        lastIndex--;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return input.Slice(0, lastIndex+1);
        }


        public override string ToString()
        {
            return Builder.ToString();
        }
    }
}
