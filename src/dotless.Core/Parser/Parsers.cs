﻿//
// Here in, the parsing rules/functions
//
// The basic structure of the syntax tree generated is as follows:
//
//   Ruleset ->  Rule -> Value -> Expression -> Entity
//
// Here's some LESS code:
//
//    .class {
//      color: #fff;
//      border: 1px solid #000;
//      width: @w + 4px;
//      > .child {...}
//    }
//
// And here's what the parse tree might look like:
//
//     Ruleset (Selector '.class', [
//         Rule ("color",  Value ([Expression [Color #fff]]))
//         Rule ("border", Value ([Expression [Number 1px][Keyword "solid"][Color #000]]))
//         Rule ("width",  Value ([Expression [Operation "+" [Variable "@w"][Number 4px]]]))
//         Ruleset (Selector [Element '>', '.child'], [...])
//     ])
//
//  In general, most rules will try to parse a token with the `$()` function, and if the return
//  value is truly, will return a new node, of the relevant type. Sometimes, we need to check
//  first, before parsing, that's when we use `peek()`.
//


using System;
using System.Text.RegularExpressions;
using dotless.Core.Utils;

#pragma warning disable 665
// ReSharper disable RedundantNameQualifier

namespace dotless.Core.Parser
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices.ComTypes;
    using dotless.Core.Parser.Functions;
    using Exceptions;
    using Infrastructure;
    using Infrastructure.Nodes;
    using Tree;

    public class Parsers
    {
        public INodeProvider NodeProvider { get; set; }

        public Parsers(INodeProvider nodeProvider)
        {
            NodeProvider = nodeProvider;
        }

        //
        // The `primary` rule is the *entry* and *exit* point of the parser.
        // The rules here can appear at any level of the parse tree.
        //
        // The recursive nature of the grammar is an interplay between the `block`
        // rule, which represents `{ ... }`, the `ruleset` rule, and this `primary` rule,
        // as represented by this simplified grammar:
        //
        //     primary  →  (ruleset | rule)+
        //     ruleset  →  selector+ block
        //     block    →  '{' primary '}'
        //
        // Only at one point is the primary rule not called from the
        // block rule: at the root level.
        //
        public NodeList Primary(Parser parser)
        {
            Node node;
            var root = new NodeList();

            GatherComments(parser);
            while (node = MixinDefinition(parser) || ExtendRule(parser) || Rule(parser) || PullComments() || GuardedRuleset(parser) || Ruleset(parser) ||
                          MixinCall(parser) || Directive(parser))
            {
                NodeList comments;
                if (comments = PullComments())
                {
                    root.AddRange(comments);
                }

                comments = node as NodeList;
                if (comments)
                {
                    foreach (Comment c in comments)
                    {
                        c.IsPreSelectorComment = true;
                    }
                    root.AddRange(comments);
                }
                else
                {
                    var rule = node as Rule;
                    if (rule != null && (rule.Name.Span.EndsWith("+".AsSpan()) || rule.Name.Span.EndsWith("+_".AsSpan())))
                    {
                        rule.Merge = rule.Name.Span.EndsWith("+".AsSpan()) ? ", " : " ";
                        rule.Name = rule.Name.TrimRight('+', '_');
                    }
                    root.Add(node); 
                }

                GatherComments(parser);
                parser.Tokenizer.Match(';'); 
            }
            return root;
        }

        private NodeList CurrentComments { get; set; }

        /// <summary>
        ///  Gathers the comments and put them on the stack
        /// </summary>
        private void GatherComments(Parser parser)
        {
            Comment comment;
            while (comment = Comment(parser))
            {
                if (CurrentComments == null)
                {
                    CurrentComments = new NodeList();
                }
                CurrentComments.Add(comment);
            }
        }

        /// <summary>
        ///  Collects comments from the stack retrived when gathering comments
        /// </summary>
        private NodeList PullComments()
        {
            NodeList comments = CurrentComments;
            CurrentComments = null;
            return comments;
        }

        /// <summary>
        ///  The equivalent of gathering any more comments and pulling everything on the stack
        /// </summary>
        private NodeList GatherAndPullComments(Parser parser)
        {
            GatherComments(parser);
            return PullComments();
        }

        private Stack<NodeList> CommentsStack = new Stack<NodeList>();

        /// <summary>
        ///  Pushes comments on to a stack for later use
        /// </summary>
        private void PushComments()
        {
            CommentsStack.Push(PullComments());
        }

        /// <summary>
        ///  Pops the comments stack
        /// </summary>
        private void PopComments()
        {
            CurrentComments = CommentsStack.Pop();
        }

        // We create a Comment node for CSS comments `/* */`,
        // but keep the LeSS comments `//` silent, by just skipping
        // over them.e
        public Comment Comment(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;
            ReadOnlyMemory<char> comment = parser.Tokenizer.GetComment();

            if (!comment.Span.IsEmpty)
            {
                return NodeProvider.Comment(comment, parser.Tokenizer.GetNodeLocation(index));
            }

            return null;
        }

        //
        // Entities are tokens which can be found inside an Expression
        //

        //
        // A string, which supports escaping " and '
        //
        //     "milky way" 'he\'s the one!'
        //
        public Quoted Quoted(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;
            var escaped = false;
            var quote = parser.Tokenizer.CurrentChar;

            if (parser.Tokenizer.CurrentChar == '~')
            {
                escaped = true;
                quote = parser.Tokenizer.NextChar;
            }
            if (quote != '"' && quote != '\'')
                return null;

            if (escaped)
                parser.Tokenizer.Match('~');

            var str = parser.Tokenizer.GetQuotedString();

            if (str.IsEmpty)
                return null;

            return NodeProvider.Quoted(str, str.Slice(1, str.Length - 2), escaped, parser.Tokenizer.GetNodeLocation(index));
        }

        //
        // A catch-all word, such as:
        //
        //     black border-collapse
        //
        public Keyword Keyword(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            var k = parser.Tokenizer.MatchKeyword();
            if (k)
                return NodeProvider.Keyword(k.Value, parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

        //
        // A function call
        //
        //     rgb(255, 0, 255)
        //
        // We also try to catch IE's `alpha()`, but let the `alpha` parser
        // deal with the details.
        //
        // The arguments are parsed with the `entities.arguments` parser.
        //
        public Call Call(Parser parser)
        {
            var memo = Remember(parser);
            var index = parser.Tokenizer.Location.Index;

            var callName = ReadOnlyMemory<char>.Empty;

            if (parser.Tokenizer.PeekChar() == '%' && parser.Tokenizer.PeekChar(1) == '(')
            {
                callName = parser.Tokenizer.Match('%').Value;
                parser.Tokenizer.Advance(1); //advance once more for the parenthesis
            }
            else
            {
                var keywordMemo = Remember(parser);

                var keyword = parser.Tokenizer.MatchKeyword();

                if(keyword && parser.Tokenizer.PeekChar() == '(')
                {
                    parser.Tokenizer.Advance(1); //move past the (
                    callName = keyword.Value;

                    if (callName.Span.Equals("alpha".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        var alpha = Alpha(parser);
                        if (alpha != null)
                            return alpha;
                    }
                }
                else
                {
                    Recall(parser, keywordMemo);

                    var progid = parser.Tokenizer.MatchProgid();

                    if (!progid || parser.Tokenizer.PeekChar() != '(')
                    {
                        Recall(parser, keywordMemo);
                        return null;
                    }
                    parser.Tokenizer.Advance(1); //move past the (

                    callName = progid.Value;
                }
            }

            var args = Arguments(parser);

            if (!parser.Tokenizer.Match(')'))
            {
                Recall(parser, memo);
                return null;
            }

            return NodeProvider.Call(callName, args, parser.Tokenizer.GetNodeLocation(index));
        }

        public NodeList<Node> Arguments(Parser parser)
        {
            var args = new NodeList<Node>();
            Node arg;

            while ((arg = Assignment(parser)) || (arg = Expression(parser)))
            {
                args.Add(arg);
                if (!parser.Tokenizer.Match(','))
                    break;
            }
            return args;
        }

        // Assignments are argument entities for calls.
        // They are present in ie filter properties as shown below.
 	    //
        //     filter: progid:DXImageTransform.Microsoft.Alpha( *opacity=50* )	
        //
        public Assignment Assignment(Parser parser)
        {
            var memo = Remember(parser);
            var key = parser.Tokenizer.MatchWord();

            if (!key || !parser.Tokenizer.Match('='))
            {
                Recall(parser, memo);
                return null;
            }

            var value = Entity(parser);

            if (value) {
                return NodeProvider.Assignment(key.Value, value, key.Location);
            }

            return null;
        }

        public Node Literal(Parser parser)
        {
            return Dimension(parser) ||
                   Color(parser) ||
                   Quoted(parser);
        }

        //
        // Parse url() tokens
        //
        // We use a specific rule for urls, because they don't really behave like
        // standard function calls. The difference is that the argument doesn't have
        // to be enclosed within a string, so it can't be parsed as an Expression.
        //
        public Url Url(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            if (parser.Tokenizer.CurrentChar != 'u' || !parser.Tokenizer.MatchExact("url("))
                return null;

            GatherComments(parser);

            Node value = Quoted(parser);

            if (!value)
            {
                var memo = Remember(parser);
                value = Expression(parser);

                if (value && !parser.Tokenizer.Peek(')'))
                {
                    value = null;
                    Recall(parser, memo);
                }
            }
            else
            {
                value.PreComments = PullComments();
                value.PostComments = GatherAndPullComments(parser);
            }

            if (!value)
            {
                value = parser.Tokenizer.MatchAny(@"[^\)""']*") || new TextNode(ReadOnlyMemory<char>.Empty);
            }

            Expect(parser, ')');

            return NodeProvider.Url(value, parser.Importer, parser.Tokenizer.GetNodeLocation(index));
        }

        //
        // A Variable entity, such as `@fink`, in
        //
        //     width: @fink + 2px
        //
        // We use a different parser for variable definitions,
        // see `parsers.variable`.
        //
        public Variable Variable(Parser parser)
        {
            RegexMatchResult name;
            var index = parser.Tokenizer.Location.Index;

            if (name = parser.Tokenizer.MatchIdentifier())
                return NodeProvider.Variable(name.Value, parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

        //
        // An interpolated Variable entity, such as `@{foo}`, in
        //
        //     [@{foo}="value"]
        //
        public Variable InterpolatedVariable(Parser parser) {
            RegexMatchResult name;
            var index = parser.Tokenizer.Location.Index;

            if (parser.Tokenizer.CurrentChar == '@' && (name = parser.Tokenizer.Match(@"@\{(?<name>@?[a-zA-Z0-9_-]+)\}")))
                return NodeProvider.Variable(("@" + name.Match.Groups["name"].Value).AsMemory(), parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

        //
        // A Variable entity as like in a selector e.g.
        //
        //     @{var} {
        //     }
        //
        public Variable VariableCurly(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            if (parser.Tokenizer.CurrentChar == '@' && parser.Tokenizer.NextChar == '{')
            {
                var memo = Remember(parser);

                parser.Tokenizer.Advance(2);
                var variableName = parser.Tokenizer.MatchKeyword();

                if(!variableName || parser.Tokenizer.CurrentChar != '}')
                {
                    Recall(parser, memo);
                    return null;
                }

                parser.Tokenizer.Advance(1);
                return NodeProvider.Variable(("@" + variableName.Value.ToString()).AsMemory(), parser.Tokenizer.GetNodeLocation(index));
            }

            return null;
        }

        /// 
        /// A guarded ruleset placed inside another e.g.
        /// 
        ///    & when (@x = true) {
        ///    }
        /// 
        public GuardedRuleset GuardedRuleset(Parser parser)
        {
            var selectors = new NodeList<Selector>();

            var memo = Remember(parser);
            var index = memo.TokenizerLocation.Index;

            Selector s;
            while (s = Selector(parser))
            {
                selectors.Add(s);
                if (!parser.Tokenizer.Match(','))
                    break;

                GatherComments(parser);
            }

            if (parser.Tokenizer.MatchExact(@"when"))
            {
                GatherAndPullComments(parser);

                var condition = Expect(Conditions(parser), "Expected conditions after when (guard)", parser);
                var rules = Block(parser);
                
                return NodeProvider.GuardedRuleset(selectors, rules, condition, parser.Tokenizer.GetNodeLocation(index));
            }

            Recall(parser, memo);

            return null;
        }

        /// 
        /// An extend statement placed at the end of a rule
        /// 
        /// <param name="parser"></param>
        /// <returns></returns>
        public Extend ExtendRule(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            var memo = Remember(parser);

            var foundAmpersand = parser.Tokenizer.Match('&');
            var extendKeyword = parser.Tokenizer.MatchExact(":extend(");


            if (extendKeyword)
            {
                var exact = new List<Selector>();
                var partial = new List<Selector>();

                Selector s;
                while (s = Selector(parser))
                {
                    if (s.Elements.Count == 1 && !s.Elements.First().HasStringValue)
                    {
                        continue;
                    }

                    if (s.Elements.Count > 1 && s.Elements.Last().Value.Span.SequenceEqual("all".AsSpan()))
                    {
                        s.Elements.Remove(s.Elements.Last());
                        partial.Add(s);
                    }
                    else
                    {
                        exact.Add(s);
                    }
                    
                    if (!parser.Tokenizer.Match(','))
                    {
                        break;
                    }
                }

                if (!parser.Tokenizer.Match(')'))
                {
                    throw new ParsingException(@"Extend rule not correctly terminated",parser.Tokenizer.GetNodeLocation(index));
                }

                if (foundAmpersand)
                {
                    parser.Tokenizer.Match(';');
                }

                if (partial.Count == 0 && exact.Count == 0)
                {
                    return null;
                }

                return NodeProvider.Extend(exact,partial, parser.Tokenizer.GetNodeLocation(index));
            }

            Recall(parser, memo);
            return null;
        }

        //
        // A Hexadecimal color
        //
        //     #4F3C2F
        //
        // `rgb` and `hsl` colors are parsed through the `entities.call` parser.
        //
        public Color Color(Parser parser)
        {
            RegexMatchResult hex;

            var index = parser.Tokenizer.Location.Index;

            if (parser.Tokenizer.CurrentChar == '#' &&
                (hex = parser.Tokenizer.Match(@"#([a-fA-F0-9]{8}|[a-fA-F0-9]{6}|[a-fA-F0-9]{3})")))
                return NodeProvider.Color(hex[1], parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

        //
        // A Dimension, that is, a number and a unit
        //
        //     0.5em 95%
        //
        public Number Dimension(Parser parser)
        { 
            var c = parser.Tokenizer.CurrentChar;
            if (!(char.IsNumber(c) || c == '.' || c == '-' || c == '+'))
                return null;

            var index = parser.Tokenizer.Location.Index;

            var number = parser.Tokenizer.MatchNumber(true);

            if (!number)
            {
                return null;
            }

            TextNode unit = parser.Tokenizer.Match('%');

            if(!unit && char.IsLetter(parser.Tokenizer.CurrentChar))
            {
                unit = parser.Tokenizer.MatchExact("px", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("em", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("pc", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("ex", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("in", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("deg", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("ms", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("pt", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("cm", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("mm", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("ch", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("rem", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("vw", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("vh", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("vmin", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("vmax", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("vm", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("grad", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("rad", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("fr", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("gr", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("Hz", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("kHz", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("dpi", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("dpcm", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.MatchExact("dppx", StringComparison.OrdinalIgnoreCase) ||
                parser.Tokenizer.Match('s', 'S');
            }
            
            return NodeProvider.Number(number.Value, unit?.Value ?? ReadOnlyMemory<char>.Empty, parser.Tokenizer.GetNodeLocation(index)); ;
        }

        //
        // C# code to be evaluated
        //
        //     ``
        //
        public Script Script(Parser parser)
        {
            if (parser.Tokenizer.CurrentChar != '`')
                return null;

            var index = parser.Tokenizer.Location.Index;

            var script = parser.Tokenizer.MatchAny(@"`[^`]*`");

            if (!script)
            {
                return null;
            }

            return NodeProvider.Script(script.Value.ToString(), parser.Tokenizer.GetNodeLocation(index));
        }


        //
        // The variable part of a variable definition. Used in the `rule` parser
        //
        //     @fink:
        //
        public string VariableName(Parser parser)
        {
            var variable = Variable(parser);

            if (variable != null)
                return variable.Name.ToString();

            return null;
        }

        //
        // A font size/line-height shorthand
        //
        //     small/12px
        //
        // We need to peek first, or we'll match on keywords and dimensions
        //
        public Shorthand Shorthand(Parser parser)
        {
            if (!parser.Tokenizer.Peek(@"[@%\w.-]+\/[@%\w.-]+"))
                return null;

            var index = parser.Tokenizer.Location.Index;

            Node a = null;
            Node b = null;
            if ((a = Entity(parser)) && parser.Tokenizer.Match('/') && (b = Entity(parser)))
                return NodeProvider.Shorthand(a, b, parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

        //
        // Mixins
        //

        //
        // A Mixin call, with an optional argument list
        //
        //     #mixins > .square(#fff);
        //     .rounded(4px, black);
        //     .button;
        //
        // The `while` loop is there because mixins can be
        // namespaced, but we only support the child and descendant
        // selector for now.
        //
        public MixinCall MixinCall(Parser parser)
        {
            var elements = new NodeList<Element>();
            var index = parser.Tokenizer.Location.Index;
            bool important = false;

            RegexMatchResult e;
            Combinator c = null;

            PushComments();

            RegexMatchResult MatchName()
            {
                var memo = Remember(parser);
                var length = 0;
                if (parser.Tokenizer.Match('#', '.'))
                    length++;

                var name = parser.Tokenizer.MatchKeyword();
                Recall(parser, memo);
                
                if (!name)
                {
                    return null;
                }
                
                length += name.Value.Length;

                return parser.Tokenizer.ConsumeRange(length);

            }

            for (var i = parser.Tokenizer.Location.Index; e = MatchName(); i = parser.Tokenizer.Location.Index)
            {
                elements.Add(NodeProvider.Element(c, e, parser.Tokenizer.GetNodeLocation(index)));

                i = parser.Tokenizer.Location.Index;
                var match = parser.Tokenizer.Match('>');
                c = match != null ? NodeProvider.Combinator(match.Value, parser.Tokenizer.GetNodeLocation(index)) : null;
            }

            if (elements.Count == 0)
            {
                PopComments();
                return null;
            }

            var args = new List<NamedArgument>();
            if (parser.Tokenizer.Peek('(')) {
                var location = Remember(parser);
                const string balancedParenthesesRegex = @"\([^()]*(?>(?>(?'open'\()[^()]*)*(?>(?'-open'\))[^()]*)*)+(?(open)(?!))\)";

                var argumentList = parser.Tokenizer.Match(balancedParenthesesRegex);
                bool argumentListIsSemicolonSeparated = argumentList != null && argumentList.Value.ToString().Contains(';');

                char expectedSeparator = argumentListIsSemicolonSeparated ? ';' : ',';

                Recall(parser, location);
                parser.Tokenizer.Match('(');

                Expression arg;
                while (arg = Expression(parser, argumentListIsSemicolonSeparated))
                {
                    var value = arg;
                    ReadOnlyMemory<char> name = null;

                    if (arg.Value.Count == 1 && arg.Value[0] is Variable)
                    {
                        if (parser.Tokenizer.Match(':'))
                        {
                            value = Expect(Expression(parser), "expected value", parser);
                            name = (arg.Value[0] as Variable).Name;
                        }
                    }

                    args.Add(new NamedArgument { Name = name, Value = value });

                    if (!parser.Tokenizer.Match(expectedSeparator))
                        break;
                }
                Expect(parser, ')');
            }

            GatherComments(parser);

            if (!Important(parser).IsEmpty)
            {
                important = true;
            }

            // if elements then we've picked up chars so don't need to worry about remembering
            var postComments = GatherAndPullComments(parser);

            if (End(parser))
            {
                var mixinCall = NodeProvider.MixinCall(elements, args, important, parser.Tokenizer.GetNodeLocation(index));
                mixinCall.PostComments = postComments;
                PopComments();
                return mixinCall;
            }

            PopComments();
            return null;
        }

        private Expression Expression(Parser parser, bool allowList)
        {
            return allowList ? ExpressionOrExpressionList(parser) : Expression(parser);
        }

        //
        // A Mixin definition, with a list of parameters
        //
        //     .rounded (@radius: 2px, @color) {
        //        ...
        //     }
        //
        // Until we have a finer grained state-machine, we have to
        // do a look-ahead, to make sure we don't have a mixin call.
        // See the `rule` function for more information.
        //
        // We start by matching `.rounded (`, and then proceed on to
        // the argument list, which has optional default values.
        // We store the parameters in `params`, with a `value` key,
        // if there is a value, such as in the case of `@radius`.
        //
        // Once we've got our params list, and a closing `)`, we parse
        // the `{...}` block.
        //
        public MixinDefinition MixinDefinition(Parser parser)
        {
            bool hasEndCurly()
            {
                var curlyMemo = Remember(parser);
                var res = parser.Tokenizer.MatchUntil('}', true, true, '{', true);
                Recall(parser, curlyMemo);
                return res;
            }

            if ((parser.Tokenizer.CurrentChar != '.' && parser.Tokenizer.CurrentChar != '#') ||
                hasEndCurly())
                return null;

            var index = parser.Tokenizer.Location.Index;

            var memo = Remember(parser);

            var match = parser.Tokenizer.Match(@"([#.](?:[\w-]|\\(?:[a-fA-F0-9]{1,6} ?|[^a-fA-F0-9]))+)\s*\(");
            if (!match)
                return null;

            //mixin definition ignores comments before it - a css hack can't be part of a mixin definition,
            //so it may as well be a rule before the definition
            PushComments();
            GatherAndPullComments(parser); // no store as mixin definition not output

            var name = match[1];
            bool variadic = false;
            var parameters = new NodeList<Rule>();
            RegexMatchResult param = null;
            Node param2 = null;
            Condition condition = null;
            int i;
            while (true)
            {
                i = parser.Tokenizer.Location.Index;
                if (parser.Tokenizer.CurrentChar == '.' && parser.Tokenizer.MatchExact("..."))
                {
                    variadic = true;
                    break;
                }

                if (param = parser.Tokenizer.MatchIdentifier())
                {
                    GatherAndPullComments(parser);

                    if (parser.Tokenizer.Match(':'))
                    {
                        GatherComments(parser);
                        var value = Expect(Expression(parser), "Expected value", parser);

                        parameters.Add(NodeProvider.Rule(param.Value, value, parser.Tokenizer.GetNodeLocation(i)));
                    }
                    else if (parser.Tokenizer.MatchExact("..."))
                    {
                        variadic = true;
                        parameters.Add(NodeProvider.Rule(param.Value, null, true, parser.Tokenizer.GetNodeLocation(i)));
                        break;
                    }
                    else
                        parameters.Add(NodeProvider.Rule(param.Value, null, parser.Tokenizer.GetNodeLocation(i)));

                } else if (param2 = Literal(parser) || Keyword(parser))
                {
                    parameters.Add(NodeProvider.Rule(null, param2, parser.Tokenizer.GetNodeLocation(i)));
                } else
                    break;

                GatherAndPullComments(parser);

				if (!(parser.Tokenizer.Match(',') || parser.Tokenizer.Match(';')))
                    break;

                GatherAndPullComments(parser);
            }

            if (!parser.Tokenizer.Match(')'))
            {
                Recall(parser, memo);
            }

            GatherAndPullComments(parser);

            if (parser.Tokenizer.MatchExact("when"))
            {
                GatherAndPullComments(parser);

                condition = Expect(Conditions(parser), "Expected conditions after when (mixin guards)", parser);
            }

            var rules = Block(parser);

            PopComments();

            if (rules != null)
                return NodeProvider.MixinDefinition(name.AsMemory(), parameters, rules, condition, variadic, parser.Tokenizer.GetNodeLocation(index));

            Recall(parser, memo);

            return null;
        }

        /// <summary>
        ///  a list of , seperated conditions (, == OR)
        /// </summary>
        public Condition Conditions(Parser parser)
        {
            Condition condition, nextCondition;

            if (condition = Condition(parser)) {
                while(parser.Tokenizer.Match(',')) {
                    nextCondition = Expect(Condition(parser), ", without recognised condition", parser);

                    condition = NodeProvider.Condition(condition, "or".AsMemory(), nextCondition, false, parser.Tokenizer.GetNodeLocation());
                }
                return condition;
            }

            return null;
        }

        /// <summary>
        ///  A condition is used for mixin definitions and is made up
        ///  of left operation right
        /// </summary>
        public Condition Condition(Parser parser)
        {
            int index = parser.Tokenizer.Location.Index;
            bool negate = false;
            Condition condition;
            //var a, b, c, op, index = i, negate = false;

            if (parser.Tokenizer.MatchExact("not"))
            {
                negate = true;
            }

            Expect(parser, '(');

            Node left = Expect(Operation(parser) || Keyword(parser) || Quoted(parser), "unrecognised condition", parser);

            var op = parser.Tokenizer.MatchExact(">=") || parser.Tokenizer.MatchExact("=<") || parser.Tokenizer.Match('<','=','>');

            if (op)
            {
                Node right = Expect(Operation(parser) || Keyword(parser) || Quoted(parser), "unrecognised right hand side condition expression", parser);

                condition = NodeProvider.Condition(left, op.Value, right, negate, parser.Tokenizer.GetNodeLocation(index));
            }
            else
            {
                condition = NodeProvider.Condition(left, "=".AsMemory(), NodeProvider.Keyword("true".AsMemory(), parser.Tokenizer.GetNodeLocation(index)), negate, parser.Tokenizer.GetNodeLocation(index));
            }

            Expect(parser, ')');

            TextNode andOp;
            if (andOp = parser.Tokenizer.MatchExact("and"))
            {
                return NodeProvider.Condition(condition, andOp.Value, Condition(parser), false, parser.Tokenizer.GetNodeLocation(index));
            }

            return condition;
        }

        //
        // Entities are the smallest recognized token,
        // and can be found inside a rule's value.
        //
        public Node Entity(Parser parser)
        {
            return Literal(parser) || Variable(parser) || Url(parser) ||
                   Call(parser) || Keyword(parser) || Script(parser);
        }

        private Expression ExpressionOrExpressionList(Parser parser)
        {
            var memo = Remember(parser);

            List<Expression> entities = new List<Expression>();

            Expression entity;
            while (entity = Expression(parser))
            {
                entities.Add(entity);

                if (!parser.Tokenizer.Match(','))
                {
                    break;
                }
            }

            if (entities.Count == 0)
            {
                Recall(parser, memo);
                return null;
            }

            if (entities.Count == 1)
            {
                return entities[0];
            }

            return new Expression(entities.Cast<Node>(), true);
        }

        //
        // A Rule terminator. Note that we use `Peek()` to check for '}',
        // because the `block` rule will be expecting it, but we still need to make sure
        // it's there, if ';' was ommitted.
        // Also note that there might be multiple semicolons between consecutive
        // declarations, and those semicolons may be separated by whitespace.
        public bool End(Parser parser)
        {
            // Searches for a semicolon which may be followed by whitespace and other semicolons.
            var semi = parser.Tokenizer.Match(';');

            if(semi)
            {
                while (parser.Tokenizer.Match(';') || parser.Tokenizer.ConsumeWhitespace() > 0) ;
            }

            return semi || parser.Tokenizer.Peek('}');
        }

        //
        // IE's alpha function
        //
        //     alpha(opacity=88)
        //
        public Alpha Alpha(Parser parser)
        {
            Node value;

            var index = parser.Tokenizer.Location.Index;

            // Allow for whitespace on both sides of the equals sign since IE seems to allow it too

            bool MatchOpacity()
            {
                var memo = Remember(parser);

                if (!parser.Tokenizer.MatchExact("opacity", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if(!parser.Tokenizer.Match('='))
                {
                    Recall(parser, memo);
                    return false;
                }
                return true;
            }

            if (char.ToLower(parser.Tokenizer.CurrentChar) != 'o' || !MatchOpacity())
                return null;

            if (value = parser.Tokenizer.MatchNumber(allowDecimals: false, allowOperator: false) || Variable(parser))
            {
                Expect(parser, ')');

                return NodeProvider.Alpha(value, parser.Tokenizer.GetNodeLocation(index));
            }

            return null;
        }

        public bool PeekExact(Parser parser, string str, StringComparison comparison = StringComparison.Ordinal)
        {
            var memo = Remember(parser);
            var res = parser.Tokenizer.MatchExact(str, comparison);
            Recall(parser, memo);
            return res;
        }

        //
        // A Selector Element
        //
        //     div
        //     + h1
        //     #socks
        //     input[type="text"]
        //
        // Elements are the building blocks for Selectors,
        // they are made out of a `Combinator` (see combinator rule),
        // and an element name, such as a tag a class, or `*`.
        //
        public Element Element(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            GatherComments(parser);

            Combinator c = Combinator(parser);

            const string parenthesizedTokenRegex = @"\(((?<N>\()|(?<-N>\))|[^()@]*)+\)";

            PushComments();
            GatherComments(parser); // to collect, combinator must have picked up something which would require memory anyway


            if (PeekExact(parser, "when"))
            {
                return null;
            }

            Node e = ExtendRule(parser) 
                || NonPseudoClassSelector(parser)
                || PseudoClassSelector(parser)
                || PseudoElementSelector(parser)
                || parser.Tokenizer.Match('*') 
                || parser.Tokenizer.Match('&') 
                || Attribute(parser) 
                || parser.Tokenizer.MatchAny(parenthesizedTokenRegex)
                || parser.Tokenizer.Match(@"[\.#](?=@\{)") 
                || VariableCurly(parser);

            if (!e)
            {
                if (parser.Tokenizer.Match('(')) {
                    var variable = Variable(parser) ?? VariableCurly(parser);
                    if (variable)
                    {
                        parser.Tokenizer.Match(')');
                        e = NodeProvider.Paren(variable, parser.Tokenizer.GetNodeLocation(index));
                    }
                }
            }

            if (e)
            {
                c.PostComments = PullComments();
                PopComments();
                c.PreComments = PullComments();

                return NodeProvider.Element(c, e, parser.Tokenizer.GetNodeLocation(index));
            }

            PopComments();
            return null;
        }

        private static RegexMatchResult PseudoClassSelector(Parser parser) {
            return parser.Tokenizer.Match(@":(\\.|[a-zA-Z0-9_-])+");
        }

        private static RegexMatchResult PseudoElementSelector(Parser parser) {
            return parser.Tokenizer.Match(@"::(\\.|[a-zA-Z0-9_-])+");
        }

        private Node NonPseudoClassSelector(Parser parser) {
            var memo = Remember(parser);
            var match = parser.Tokenizer.Match(@"[.#]?(\\.|[a-zA-Z0-9_-])+");
            if (!match) {
                return null;
            }

            if (parser.Tokenizer.Match('(')) {
                // Argument list implies that we actually matched a mixin call
                // Rewind back to where we started and return a null match
                Recall(parser, memo);
                return null;
            }

            return match;
        }

        //
        // Combinators combine elements together, in a Selector.
        //
        // Because our parser isn't white-space sensitive, special care
        // has to be taken, when parsing the descendant combinator, ` `,
        // as it's an empty space. We have to check the previous character
        // in the input, to see if it's a ` ` character. More info on how
        // we deal with this in *combinator.js*.
        //
        public Combinator Combinator(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            TextNode match;
            if (match = parser.Tokenizer.Match('+','>','~'))
                return NodeProvider.Combinator(match.Value, parser.Tokenizer.GetNodeLocation(index));

            return NodeProvider.Combinator(char.IsWhiteSpace(parser.Tokenizer.GetPreviousCharIgnoringComments()) ? " ".AsMemory() : null, parser.Tokenizer.GetNodeLocation(index));
        }

        //
        // A CSS Selector
        //
        //     .class > div + h1
        //     li a:hover
        //
        // Selectors are made out of one or more Elements, see above.
        //
        public Selector Selector(Parser parser)
        {
            Element e;
            int realElements = 0;

            var elements = new NodeList<Element>();
            var index = parser.Tokenizer.Location.Index;

            GatherComments(parser);
            PushComments();

            if (parser.Tokenizer.Match('('))
            {
                var sel = Entity(parser);
                Expect(parser, ')');
                return NodeProvider.Selector(new NodeList<Element>() { NodeProvider.Element(null, sel, parser.Tokenizer.GetNodeLocation(index)) }, parser.Tokenizer.GetNodeLocation(index));
            }

            while (true)
            {
                e = Element(parser);
                if (!e)
                    break;

                realElements++;
                elements.Add(e);
            }

            if (realElements > 0)
            {
                var selector = NodeProvider.Selector(elements, parser.Tokenizer.GetNodeLocation(index));
                selector.PostComments = GatherAndPullComments(parser);
                PopComments();
                selector.PreComments = PullComments();

                return selector;
            }

            PopComments();
            //We have lost comments we have absorbed here.
            //But comments should be absorbed before selectors...
            return null;
        }

        public Node Tag(Parser parser)
        {
            return parser.Tokenizer.Match(@"[a-zA-Z][a-zA-Z-]*[0-9]?") || parser.Tokenizer.Match('*');
        }

        public Node Attribute(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;

            if (!parser.Tokenizer.Match('['))
                return null;

            Node key = InterpolatedVariable(parser) || parser.Tokenizer.MatchKeyword(allowSlashDot: true) || Quoted(parser);

            if (!key)
            {
                return null;
            }

            Node op = parser.Tokenizer.Match(@"[|~*$^]?=");
            Node val = Quoted(parser) || parser.Tokenizer.MatchKeyword();

            Expect(parser, ']');

            return NodeProvider.Attribute(key, op, val, parser.Tokenizer.GetNodeLocation(index));
        }

        //
        // The `block` rule is used by `ruleset` and `mixin.definition`.
        // It's a wrapper around the `primary` rule, with added `{}`.
        //
        public NodeList Block(Parser parser)
        {
            if (!parser.Tokenizer.Match('{'))
                return null;

            var content = Expect(Primary(parser), "Expected content inside block", parser);

            Expect(parser, '}');

            return content;
        }

        //
        // div, .class, body > p {...}
        //
        public Ruleset Ruleset(Parser parser)
        {
            var selectors = new NodeList<Selector>();

            var memo = Remember(parser);
            var index = memo.TokenizerLocation.Index;

            Selector s;
            while (s = Selector(parser))
            {
                selectors.Add(s);
                if (!parser.Tokenizer.Match(','))
                    break;

                GatherComments(parser);
            }

            NodeList rules;

            if (selectors.Count > 0 && (rules = Block(parser)) != null)
            {
                return NodeProvider.Ruleset(selectors, rules, parser.Tokenizer.GetNodeLocation(index));
            }

            Recall(parser, memo);

            return null;
        }

        public Rule Rule(Parser parser)
        {
            var memo = Remember(parser);
            PushComments();

            Variable variable = null;
            var name = Property(parser);
            bool interpolatedName = false;

            if (name.Span.IsEmpty) {
                variable = Variable(parser);
                if (variable != null) {
                    name = variable.Name;
                } else {
                    var interpolation = InterpolatedVariable(parser);
                    if (interpolation != null) {
                        interpolatedName = true;
                        name = interpolation.Name;
                    }
                }
            }

            var postNameComments = GatherAndPullComments(parser);

            if (!name.Span.IsEmpty && parser.Tokenizer.Match(':'))
            {
                Node value;

                var preValueComments = GatherAndPullComments(parser);

                if (name.Span.Equals("font".AsSpan(), StringComparison.Ordinal))
                {
                    value = Font(parser);
                }
                else if (MatchesProperty("filter", name.ToString()))
                {
                    value = FilterExpressionList(parser) || Value(parser);
                }
                else
                {
                    value = Value(parser);
                }


                // It's definitely a variable, but we couldn't parse the value to anything meaningful.
                // However, the value might still be useful in another context, e.g. as part of a selector
                // so let's catch the whole shebang:
                if (variable != null && value == null) {
                    value = parser.Tokenizer.MatchUntil(';');
                }

                var postValueComments = GatherAndPullComments(parser);

                if (End(parser))
                {
                    if (value == null)
                        throw new ParsingException(name + " is incomplete", parser.Tokenizer.GetNodeLocation());

                    value.PreComments = preValueComments;
                    value.PostComments = postValueComments;

                    var rule = NodeProvider.Rule(name, value,
                        parser.Tokenizer.GetNodeLocation(memo.TokenizerLocation.Index));
                    if (interpolatedName) {
                        rule.InterpolatedName = true;
                        rule.Variable = false;
                    }
                    rule.PostNameComments = postNameComments;
                    PopComments();
                    return rule;
                }
            }

            PopComments();
            Recall(parser, memo);

            return null;
        }

        private bool MatchesProperty(string expectedPropertyName, string actualPropertyName)
        {
            if (string.Equals(expectedPropertyName, actualPropertyName))
            {
                return true;
            }

            return Regex.IsMatch(actualPropertyName, string.Format(@"-(\w+)-{0}", expectedPropertyName));
        }

        private CssFunctionList FilterExpressionList(Parser parser)
        {
            var list = new CssFunctionList();
            Node expression;
            while (expression = FilterExpression(parser))
            {
                list.Add(expression);
            }

            if (!list.Any())
            {
                return null;
            }

            return list;
        }

        private Node FilterExpression(Parser parser)
        {
            const string functionNameRegex =
                @"\s*(blur|brightness|contrast|drop-shadow|grayscale|hue-rotate|invert|opacity|saturate|sepia|url)\s*\(";

            var index = parser.Tokenizer.Location.Index;

            GatherComments(parser);

            var url = Url(parser);
            if (url)
            {
                return url;
            }

            var nameToken = parser.Tokenizer.Match(functionNameRegex);
            if (nameToken == null)
            {
                return null;
            }

            var value = Value(parser);
            if (value == null)
            {
                return null;
            }

            Expect(parser, ')');

            var result = NodeProvider.CssFunction(nameToken.Match.Groups[1].Value.Trim(), value, parser.Tokenizer.GetNodeLocation(index));
            result.PreComments = PullComments();
            result.PostComments = GatherAndPullComments(parser);
            return result;
        }

        //
        // An @import directive
        //
        //     @import "lib";
        //
        // Depending on our environemnt, importing is done differently:
        // In the browser, it's an XHR request, in Node, it would be a
        // file-system operation. The function used for importing is
        // stored in `import`, which we pass to the Import constructor.
        //
        public Import Import(Parser parser)
        {

            var index = parser.Tokenizer.Location.Index;

            var importMatch = parser.Tokenizer.Match(@"@import(-(once))?\s+");
            if (!importMatch) {
                return null;
            }

            ImportOptions option = ParseOptions(parser);

            Node path = Quoted(parser) || Url(parser);
            if (!path) {
                return null;
            }

            var features = MediaFeatures(parser);

            Expect(parser, ';', "Expected ';' (possibly unrecognised media sequence)");

            if (path is Quoted)
                return NodeProvider.Import(path as Quoted, features, option, parser.Tokenizer.GetNodeLocation(index));

            if (path is Url)
                return NodeProvider.Import(path as Url, features, option, parser.Tokenizer.GetNodeLocation(index));

            throw new ParsingException("unrecognised @import format", parser.Tokenizer.GetNodeLocation(index));
        }

        private static ImportOptions ParseOptions(Parser parser)
        {
            var index = parser.Tokenizer.Location.Index;
            var optionsMatch = parser.Tokenizer.Match(@"\((?<keywords>.*)\)");
            if (!optionsMatch) {
                return ImportOptions.Once;
            }

            var allKeywords = optionsMatch.Match.Groups["keywords"].Value;
            var keywords = allKeywords.Split(',').Select(kw => kw.Trim());

            ImportOptions options = 0;
            foreach (var keyword in keywords)
            {
                try
                {
                    ImportOptions value = (ImportOptions) Enum.Parse(typeof (ImportOptions), keyword, true);
                    options |= value;
                }
                catch (ArgumentException)
                {
                    throw new ParsingException(string.Format("unrecognized @import option '{0}'", keyword), parser.Tokenizer.GetNodeLocation(index));
                }
            }

            CheckForConflictingOptions(parser, options, allKeywords, index);
            
            return options;
        }

        private static readonly ImportOptions[][] illegalOptionCombinations =
            {
                new[] {ImportOptions.Css, ImportOptions.Less},
                new[] {ImportOptions.Inline, ImportOptions.Css},
                new[] {ImportOptions.Inline, ImportOptions.Less},
                new[] {ImportOptions.Inline, ImportOptions.Reference},
                new[] {ImportOptions.Once, ImportOptions.Multiple},
                new[] {ImportOptions.Reference, ImportOptions.Css},
            };
        private static void CheckForConflictingOptions(Parser parser, ImportOptions options, string allKeywords, int index)
        {
            foreach (var illegalCombination in illegalOptionCombinations)
            {
                if (IsOptionSet(options, illegalCombination[0]) && IsOptionSet(options, illegalCombination[1]))
                {
                    throw new ParsingException(
                        string.Format(
                            "invalid combination of @import options ({0}) -- specify either {1} or {2}, but not both",
                            allKeywords,
                            illegalCombination[0].ToString().ToLowerInvariant(),
                            illegalCombination[1].ToString().ToLowerInvariant()
                            ),
                        parser.Tokenizer.GetNodeLocation(index));
                }
            }
        }

        private static bool IsOptionSet(ImportOptions options, ImportOptions test)
        {
            return (options & test) == test;
        }

        //
        // A CSS Directive
        //
        //     @charset "utf-8";
        //
        public Node Directive(Parser parser)
        {
            if (parser.Tokenizer.CurrentChar != '@')
                return null;

            var import = Import(parser);
            if (import)
                return import;

            var media = Media(parser);
            if (media)
                return media;

            GatherComments(parser);

            var index = parser.Tokenizer.Location.Index;

            var directiveName = parser.Tokenizer.MatchDirectiveName();
            ReadOnlyMemory<char> name;
            if (directiveName == null)
            {
                return null;
            }

            name = directiveName.Value;
            bool hasIdentifier = false, hasBlock = false, isKeyFrame = false;
            NodeList rules, preRulesComments = null, preComments = null;
            var nonVendorSpecificName = name;
            var dashIndex = 0;

            if (name.Span.StartsWith("@-".AsSpan()) && (dashIndex = name.Slice(2).Span.IndexOf('-')) > 0)
            {
                //+3 is built up of 2 from the slice above and then we want everything after the -
                nonVendorSpecificName = ("@" + name.Slice(dashIndex + 3).ToString()).AsMemory();
            }

            switch (nonVendorSpecificName.ToString())
            {
                case "@font-face":
                    hasBlock = true;
                    break;
                case "@page":
                case "@document":
                case "@supports":
                    hasBlock = true;
                    hasIdentifier = true;
                    break;
                case "@viewport":
                case "@top-left":
                case "@top-left-corner":
                case "@top-center":
                case "@top-right":
                case "@top-right-corner":
                case "@bottom-left":
                case "@bottom-left-corner":
                case "@bottom-center":
                case "@bottom-right":
                case "@bottom-right-corner":
                case "@left-top":
                case "@left-middle":
                case "@left-bottom":
                case "@right-top":
                case "@right-middle":
                case "@right-bottom":
                    hasBlock = true;
                    break;
                case "@keyframes":
                    isKeyFrame = true;
                    hasIdentifier = true;
                    break;
            }

            ReadOnlyMemory<char> identifier = ReadOnlyMemory<char>.Empty;

            preComments = PullComments();

            if (hasIdentifier)
            {
                GatherComments(parser);

                var identifierRegResult = parser.Tokenizer.MatchUntil('{');
                if (identifierRegResult != null)
                {
                    identifier = identifierRegResult.Value.Trim();
                }
            }

            preRulesComments = GatherAndPullComments(parser);

            if (hasBlock)
            {
                rules = Block(parser);

                if (rules != null) {
                    rules.PreComments = preRulesComments;
                    return NodeProvider.Directive(name, identifier, rules, parser.Tokenizer.GetNodeLocation(index));
                }
            }
            else if (isKeyFrame)
            {
                var keyframeblock = KeyFrameBlock(parser, name, identifier, index);
                keyframeblock.PreComments = preRulesComments;
                return keyframeblock;
            }
            else
            {
                Node value;
                if (value = Expression(parser)) {
                    value.PreComments = preRulesComments;
                    value.PostComments = GatherAndPullComments(parser);

                    Expect(parser, ';', "missing semicolon in expression");

                    var directive = NodeProvider.Directive(name, value, parser.Tokenizer.GetNodeLocation(index));
                    directive.PreComments = preComments;
                    return directive;
                }
            }

            throw new ParsingException("directive block with unrecognised format", parser.Tokenizer.GetNodeLocation(index));
        }

        public Expression MediaFeature(Parser parser)
        {
            NodeList features = new NodeList();
            var outerIndex = parser.Tokenizer.Location.Index;

            while (true)
            {
                GatherComments(parser);

                var keyword = Keyword(parser);
                if (keyword)
                {
                    keyword.PreComments = PullComments();
                    keyword.PostComments = GatherAndPullComments(parser);
                    features.Add(keyword);
                }
                else if (parser.Tokenizer.Match('('))
                {
                    GatherComments(parser);

                    var memo = Remember(parser);
                    var index = parser.Tokenizer.Location.Index;
                    var property = Property(parser);

                    var preComments = GatherAndPullComments(parser);

                    // in order to support (color) and have rule/*comment*/: we need to keep :
                    // out of property
                    if (!property.IsEmpty && !parser.Tokenizer.Match(':'))
                    {
                        Recall(parser, memo);
                        property = null;
                    }

                    GatherComments(parser);

                    memo = Remember(parser);

                    var entity = Entity(parser);

                    if (!entity || !parser.Tokenizer.Match(')')) 
                    {
                        Recall(parser, memo);

                        // match "3/2" for instance
                        var unrecognised = parser.Tokenizer.Match(@"[^\){]+");
                        if (unrecognised)
                        {
                            entity = NodeProvider.TextNode(unrecognised.Value, parser.Tokenizer.GetNodeLocation());
                            Expect(parser, ')');
                        }
                    }

                    if (!entity)
                    {
                        return null;
                    }

                    entity.PreComments = PullComments();
                    entity.PostComments = GatherAndPullComments(parser);

                    if (!property.IsEmpty)
                    {
                        var rule = NodeProvider.Rule(property, entity, parser.Tokenizer.GetNodeLocation(index));
                        rule.IsSemiColonRequired = false;
                        features.Add(NodeProvider.Paren(rule, parser.Tokenizer.GetNodeLocation(index)));
                    }
                    else
                    {
                        features.Add(NodeProvider.Paren(entity, parser.Tokenizer.GetNodeLocation(index)));
                    }
                }
                else
                {
                    break;
                }
            }

            if (features.Count == 0)
                return null;

            return NodeProvider.Expression(features, parser.Tokenizer.GetNodeLocation(outerIndex));
        }

        public Value MediaFeatures(Parser parser)
        {
            List<Node> features = new List<Node>();
            int index = parser.Tokenizer.Location.Index;

            while (true)
            {
                Node feature = MediaFeature(parser) || Variable(parser);
                if (!feature)
                {
                    return null;
                }

                features.Add(feature);

                if (!parser.Tokenizer.Match(','))
                    break;
            }

            return NodeProvider.Value(features, null, parser.Tokenizer.GetNodeLocation(index));
        }

        public Media Media(Parser parser)
        {
            if (!parser.Tokenizer.MatchExact("@media"))
                return null;

            var index = parser.Tokenizer.Location.Index;

            var features = MediaFeatures(parser);

            var preRulesComments = GatherAndPullComments(parser);

            var rules = Expect(Block(parser), "@media block with unrecognised format", parser);

            rules.PreComments = preRulesComments;
            return NodeProvider.Media(rules, features, parser.Tokenizer.GetNodeLocation(index));
        }

        public Directive KeyFrameBlock(Parser parser, ReadOnlyMemory<char> name, ReadOnlyMemory<char> identifier, int index)
        {
            if (!parser.Tokenizer.Match('{'))
                return null;

            NodeList keyFrames = new NodeList();
            const string identifierRegEx = "from|to|([0-9\\.]+%)";

            while (true)
            {
                GatherComments(parser);

                NodeList keyFrameElements = new NodeList();

                while(true) {
                    RegexMatchResult keyFrameIdentifier;

                    if (keyFrameElements.Count > 0)
                    {
                        keyFrameIdentifier = Expect(parser.Tokenizer.Match(identifierRegEx), "@keyframe block unknown identifier", parser);
                    }
                    else
                    {
                        keyFrameIdentifier = parser.Tokenizer.Match(identifierRegEx);
                        if (!keyFrameIdentifier)
                        {
                            break;
                        }
                    }
                    
                    keyFrameElements.Add(new Element(null, keyFrameIdentifier));

                    GatherComments(parser);

                    if(!parser.Tokenizer.Match(','))
                        break;

                    GatherComments(parser);
                }

                if (keyFrameElements.Count == 0)
                    break;
                
                var preComments = GatherAndPullComments(parser);

                var block = Expect(Block(parser), "Expected css block after key frame identifier", parser);

                block.PreComments = preComments;
                block.PostComments = GatherAndPullComments(parser);

                keyFrames.Add(NodeProvider.KeyFrame(keyFrameElements, block, parser.Tokenizer.GetNodeLocation()));
            }

            Expect(parser, '}', "Expected start, finish, % or '}}' but got {1}");

            return NodeProvider.Directive(name, identifier, keyFrames, parser.Tokenizer.GetNodeLocation(index));
        }

        public Value Font(Parser parser)
        {
            var value = new NodeList();
            var expression = new NodeList();
            Node e;

            var index = parser.Tokenizer.Location.Index;

            while (e = Shorthand(parser) || Entity(parser))
            {
                expression.Add(e);
            }
            value.Add(NodeProvider.Expression(expression, parser.Tokenizer.GetNodeLocation(index)));

            if (parser.Tokenizer.Match(','))
            {
                while (e = Expression(parser))
                {
                    value.Add(e);
                    if (!parser.Tokenizer.Match(','))
                        break;
                }
            }
            return NodeProvider.Value(value, Important(parser), parser.Tokenizer.GetNodeLocation(index));
        }

        //
        // A Value is a comma-delimited list of Expressions
        //
        //     font-family: Baskerville, Georgia, serif;
        //
        // In a Rule, a Value represents everything after the `:`,
        // and before the `;`.
        //
        public Value Value(Parser parser)
        {
            var expressions = new NodeList();

            var index = parser.Tokenizer.Location.Index;

            Node e;
            while (e = Expression(parser))
            {
                expressions.Add(e);
                if (!parser.Tokenizer.Match(','))
                    break;
            }

            GatherComments(parser);

            var important = string.Join(
                " ",
                new[]
                {
                    IESlash9Hack(parser),
                    Important(parser)
                }.Where(x => !x.IsEmpty).ToArray()
            );

            if (expressions.Count > 0 || parser.Tokenizer.Peek(';'))
            {
                var value = NodeProvider.Value(expressions, important.AsMemory(), parser.Tokenizer.GetNodeLocation(index));

                if (!string.IsNullOrEmpty(important))
                {
                    value.PreImportantComments = PullComments();
                }

                return value;
            }

            return null;
        }

        public ReadOnlyMemory<char> Important(Parser parser)
        {
            if (parser.Tokenizer.CurrentChar != '!')
                return null;

            var memo = Remember(parser);

            int consumedChars = parser.Tokenizer.Advance(1); //Advance consumes excess whitespace

            var importantTag = parser.Tokenizer.MatchExact("important");

            Recall(parser, memo);

            if (importantTag)
            {
                return parser.Tokenizer.ConsumeRange(consumedChars + importantTag.Value.Length).Value;
            }

            return ReadOnlyMemory<char>.Empty;
        }

        public ReadOnlyMemory<char> IESlash9Hack(Parser parser)
        {
            var slashNine = parser.Tokenizer.MatchExact("\\9");
            return slashNine == null ? ReadOnlyMemory<char>.Empty : slashNine.Value;
        }

        public Expression Sub(Parser parser)
        {
            if (!parser.Tokenizer.Match('('))
                return null;

            var memo = Remember(parser);

            var e = Expression(parser);
            if (e != null && parser.Tokenizer.Match(')'))
                return e;

            Recall(parser, memo);

            return null;
        }

        public Node Multiplication(Parser parser)
        {
            GatherComments(parser);

            var m = Operand(parser);
            if (!m)
                return null;

            Node operation = m;

            while (true)
            {
                GatherComments(parser); // after left operand

                var index = parser.Tokenizer.Location.Index;
                var op = parser.Tokenizer.Match('/','*');

                GatherComments(parser); // after operation

                Node a = null;
                if (op && (a = Operand(parser)))
                    operation = NodeProvider.Operation(op.Value, operation, a, parser.Tokenizer.GetNodeLocation(index));
                else
                    break;
            }
            return operation;
        }

        public Node UnicodeRange(Parser parser)
        {
            const string rangeRegex = "(U\\+[0-9a-f]+(-[0-9a-f]+))";
            const string valueOrWildcard = "(U\\+[0-9a-f?]+)";

            if (char.ToLower(parser.Tokenizer.CurrentChar) != 'u' || parser.Tokenizer.NextChar != '+')
                return null;

            return parser.Tokenizer.Match(rangeRegex, true)
                   ?? parser.Tokenizer.Match(valueOrWildcard, true);
        }

        public Node Operation(Parser parser)
        {
            bool isStrictMathMode = parser.StrictMath;
            try
            {
                // Set Strict Math to false so as not to require extra parens in nested expressions
                parser.StrictMath = false;
                if (isStrictMathMode)
                {
                    var beginParen = parser.Tokenizer.Match('(');
                    if (beginParen == null)
                    {
                        return null;
                    }
                }

                var m = Multiplication(parser);
                if (!m)
                    return null;

                Operation operation = null;
                while (true)
                {
                    GatherComments(parser);

                    var index = parser.Tokenizer.Location.Index;

                    TextNode op = parser.Tokenizer.MatchWithFollowingWhitespace('-', '+');
                    if (!op && !char.IsWhiteSpace(parser.Tokenizer.GetPreviousCharIgnoringComments()))
                        op = parser.Tokenizer.Match('-', '+');

                    Node a = null;
                    if (op && (a = Multiplication(parser)))
                        operation = NodeProvider.Operation(op.Value, operation ?? m, a,
                            parser.Tokenizer.GetNodeLocation(index));
                    else
                        break;
                }

                if (isStrictMathMode)
                {
                    Expect(parser, ')', "Missing closing paren.");
                }

                return operation ?? m;
            }
            finally
            {
                parser.StrictMath = isStrictMathMode;
            }
        }

        //
        // An operand is anything that can be part of an operation,
        // such as a Color, or a Variable
        //
        public Node Operand(Parser parser)
        {
            CharMatchResult negate = null;

            char[] possibleNext = new[] { '@', '(' };
            if (parser.Tokenizer.CurrentChar == '-' && possibleNext.Contains(parser.Tokenizer.NextChar))
            {
                negate = parser.Tokenizer.Match('-');
                GatherComments(parser);
            }

            var operand = Sub(parser) ??
                          Dimension(parser) ??
                          Color(parser) ??
                          (Node)Variable(parser);

            if (operand != null)
            {
                return negate ?
                    NodeProvider.Operation("*".AsMemory(), NodeProvider.Number("-1".AsMemory(), ReadOnlyMemory<char>.Empty, negate.Location), operand, negate.Location) :
                    operand;
            }

            if (parser.Tokenizer.CurrentChar == 'u' && PeekExact(parser, @"url("))
                return null;

            return Call(parser) || Keyword(parser);
        }

        //
        // Expressions either represent mathematical operations,
        // or white-space delimited Entities.
        //
        //     1px solid black
        //     @var * 2
        //
        public Expression Expression(Parser parser)
        {
            Node e;
            var entities = new NodeList();

            var index = parser.Tokenizer.Location.Index;

#if CSS3EXPERIMENTAL
            while (e = RepeatPattern(parser) || Operation(parser) || Entity(parser))
#else 
            while (e = UnicodeRange(parser) || Operation(parser) || Entity(parser) || parser.Tokenizer.Match('-', '+', '*', '/'))
#endif
            {
                e.PostComments = PullComments();
                entities.Add(e);
            }

            if (entities.Count > 0)
                return NodeProvider.Expression(entities, parser.Tokenizer.GetNodeLocation(index));

            return null;
        }

#if CSS3EXPERIMENTAL
        /// <summary>
        ///  A repeat entity.. such as "(0.5in * *)[2]"
        /// </summary>
        public Node RepeatPattern(Parser parser)
        {
            if (parser.Tokenizer.Peek(@"\([^;{}\)]+\)\[")) {
                var index = parser.Tokenizer.Location.Index;

                parser.Tokenizer.Match('(');
                var value = Expression(parser);
                Expect(parser, ')');
                Expect(parser, '[');
                var repeat = Expect(Entity(parser), "Expected repeat entity", parser);
                Expect(parser, ']');

                return NodeProvider.RepeatEntity(NodeProvider.Paren(value, index), repeat, index);
            }

            return null;
        }
#endif

        public ReadOnlyMemory<char> Property(Parser parser)
        {
            var currentChar = parser.Tokenizer.CurrentChar;

            if (currentChar != '*' && currentChar != '-' && currentChar != '_' && !char.IsLetter(currentChar))
            {
                return null;
            }

            var memo = Remember(parser);
            int length = 0;
            if (parser.Tokenizer.Match('*'))
                length++;
            if (parser.Tokenizer.Match('-'))
                length++;

            var propertyName = parser.Tokenizer.MatchKeyword(allowLeadingDigit: false);

            if (!propertyName)
            {
                Recall(parser, memo);
                return ReadOnlyMemory<char>.Empty;
            }

            length += propertyName.Value.Length;

            if (parser.Tokenizer.Match('+'))
                length++;

            if (parser.Tokenizer.Match('_'))
                length++;

            Recall(parser, memo);
            return parser.Tokenizer.ConsumeRange(length).Value;
        }

        public void Expect(Parser parser, char expectedString)
        {
            Expect(parser, expectedString, null);
        }

        public void Expect(Parser parser, char expectedString, string message)
        {
            if (parser.Tokenizer.Match(expectedString))
                return;

            message = message ?? "Expected '{0}' but found '{1}'";

            throw new ParsingException(string.Format(message, expectedString, parser.Tokenizer.CurrentChar), parser.Tokenizer.GetNodeLocation());
        }

        public T Expect<T>(T node, string message, Parser parser) where T:Node
        {
            if (node)
                return node;

            throw new ParsingException(message, parser.Tokenizer.GetNodeLocation());
        }


        public class ParserLocation
        {
            public NodeList Comments { get; set; }
            public Location TokenizerLocation { get; set; }
        }

        public ParserLocation Remember(Parser parser)
        {
            return new ParserLocation() { Comments = CurrentComments, TokenizerLocation = parser.Tokenizer.Location };
        }

        public void Recall(Parser parser, ParserLocation location)
        {
            CurrentComments = location.Comments;
            parser.Tokenizer.Location = location.TokenizerLocation;
        }
    }
}
