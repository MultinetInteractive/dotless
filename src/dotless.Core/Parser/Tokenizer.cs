﻿using System.Diagnostics;

namespace dotless.Core.Parser
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Exceptions;
    using Infrastructure.Nodes;
    using Utils;
    using static System.Net.Mime.MediaTypeNames;

    [DebuggerDisplay("{Remaining}")]
    public class Tokenizer
    {
        public int Optimization { get; set; }

        private ReadOnlyMemory<char> _input; // LeSS input string
        private List<Chunk> _chunks; // chunkified input
        private int _i; // current index in `input`
        private int _j; // current chunk
        private int _current; // index of current chunk, in `input`
        private int _lastCommentStart = -1; // the start of the last collection of comments
        private int _lastCommentEnd = -1; // the end of the last collection of comments
        private int _inputLength;
        private readonly string _commentRegEx = @"(//[^\n]*|(/\*(.|[\r\n])*?\*/))";
        private readonly string _quotedRegEx = @"(""((?:[^""\\\r\n]|\\.)*)""|'((?:[^'\\\r\n]|\\.)*)')";
        private string _fileName;

        //Increasing throughput through tracing of Regex
        private IDictionary<string, Regex> regexCache = new Dictionary<string, Regex>();

        public Tokenizer(int optimization)
        {
            Optimization = optimization;
        }

        public void SetupInput(ReadOnlyMemory<char> input, string fileName)
        {
            _fileName = fileName;
            _i = _j = _current = 0;
            _chunks = new List<Chunk>();
            _input = input;

            if (_input.Span.Contains("\r\n".AsSpan(), StringComparison.Ordinal))
            {
                _input = input.ToString().Replace("\r\n", "\n").AsMemory();
            }
            _inputLength = _input.Length;

            // Split the input into chunks,
            // Either delimited by /\n\n/ or
            // delmited by '\n}' (see rationale above),
            // depending on the level of optimization.

            if(Optimization == 0)
                _chunks.Add(new Chunk(_input));
            else
            {
                var skip = new Regex(@"\G(@\{[a-zA-Z0-9_-]+\}|[^\""'{}/\\\(\)]+)");

                var comment = GetRegex(this._commentRegEx, RegexOptions.None);
                var quotedstring = GetRegex(this._quotedRegEx, RegexOptions.None);
                var level = 0;
                var lastBlock = 0;
                var inParam = false;
                
                int i = 0;
                while(i < _inputLength)
                {
                    Match match;
                    if (_input.Span[i] == '@')
                    {
                        match = skip.Match(_input.ToString(), i);
                        if (match.Success)
                        {
                            Chunk.Append(match.Value.AsMemory(), _chunks);
                            i += match.Length;
                            continue;
                        }
                    }
                    
                    var c = _input.Span[i];
                    
                    if(i < _inputLength - 1 && c == '/')
                    {
                        var cc = _input.Span[i + 1];
                        if ((!inParam && cc == '/') || cc == '*')
                        {
                            match = comment.Match(_input.ToString(), i);
                            if(match.Success)
                            {
                                i += match.Length;
                                _chunks.Add(new Chunk(match.Value.AsMemory(), ChunkType.Comment));
                                continue;
                            } else
                            {
                                throw new ParsingException("Missing closing comment", GetNodeLocation(i));
                            }
                        }
                    }
                    
                    if(c == '"' || c == '\'')
                    {
                        match = quotedstring.Match(_input.ToString(), i);
                        if(match.Success)
                        {
                            i += match.Length;
                            _chunks.Add(new Chunk(match.Value.AsMemory(), ChunkType.QuotedString));
                            continue;
                        } else
                        {
                            throw new ParsingException(string.Format("Missing closing quote ({0})", c), GetNodeLocation(i));
                        }
                    }
                    
                    // we are not in a quoted string or comment - process '{' level
                    if(!inParam && c == '{')
                    {
                        level++;
                        lastBlock = i;
                    }
                    else if (!inParam && c == '}')
                    {
                        level--;
                        
                        if(level < 0)
                            throw new ParsingException("Unexpected '}'", GetNodeLocation(i));
                        
                        Chunk.Append(c, _chunks, true);
                        i++;
                        continue;
                    } if (c == '(')
                    {
                        inParam = true;
                    }
                    else if (c == ')')
                    {
                        inParam = false;
                    }
                    
                    Chunk.Append(c, _chunks);
                    i++;
                }
                
                if(level > 0)
                    throw new ParsingException("Missing closing '}'", GetNodeLocation(lastBlock));

                _input =  Chunk.CommitAll(_chunks);

                _inputLength = _input.Length;
            }

            Advance(0); // skip any whitespace characters at the start.
        }

        public ReadOnlyMemory<char> GetComment()
        {
            // if we've hit the end we might still be looking at a valid chunk, so return early
            if (_i == _inputLength) {
                return null;
            }

            ReadOnlyMemory<char> val;
            int startI = _i;
            int endI = 0;

            if  (Optimization == 0)
            {
                if (this.CurrentChar != '/')
                    return null;

                var comment = this.Match(this._commentRegEx);
                if (comment == null)
                {
                    return null;
                }
                val = comment.Value;
                endI = startI + comment.Value.Length;
            }
            else
            {
                if (_chunks[_j].Type == ChunkType.Comment)
                {
                    val = _chunks[_j].Value;
                    endI = _i + _chunks[_j].Value.Length;
                    Advance(_chunks[_j].Value.Length);
                }
                else
                {
                    return null;
                }
            }

            if (_lastCommentEnd != startI)
            {
                _lastCommentStart = startI;
            }

            _lastCommentEnd = endI;

            return val;
        }

        public ReadOnlyMemory<char> GetQuotedString()
        {
            // if we've hit the end we might still be looking at a valid chunk, so return early
            if (_i == _inputLength) {
                return null;
            }
            
            if (Optimization == 0) {
                if (this.CurrentChar != '"' && this.CurrentChar != '\'')
                    return null;
                
                var quotedstring = this.Match(this._quotedRegEx);
                return quotedstring.Value;
            } else {
                if (_chunks[_j].Type == ChunkType.QuotedString) {
                    ReadOnlyMemory<char> val = _chunks[_j].Value;
                    Advance(_chunks[_j].Value.Length);
                    return val;
                }
            }
            return null;
        }

        //
        // Parse from a token, regexp or string, and move forward if match
        //

        public CharMatchResult Match(params char[] chars)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            if (chars.Contains(_input.Span[_i]))
            {
                var index = _i;

                Advance(1);

                return new CharMatchResult(_input.Slice(index, 1)) { Location = GetNodeLocation(index) };
            }

            return null;
        }

        public RegexMatchResult MatchNumber(bool allowDecimals, bool allowOperator = true)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;
            var x = 0;
            var requiredLength = 0;

            char Current(int offset = 0)
            {
                return _chunks[_j].Value.Span[startingPosition + x + offset];
            }

            if (allowOperator && (Current() == '-' || Current() == '+'))
            {
                x++;
                requiredLength++;
            }
                

            while (startingPosition + x < _chunks[_j].Value.Length && char.IsDigit(Current()))
                x++;

            if(allowDecimals && (startingPosition + x + 1 < _chunks[_j].Value.Length) && Current() == '.' && char.IsDigit(Current(1)))
            {
                x++;
                while (startingPosition + x < _chunks[_j].Value.Length && char.IsDigit(Current()))
                    x++;
            }

            if (x > requiredLength)
            {
                var res = new RegexMatchResult(_chunks[_j].Value.Slice(startingPosition, x), GetNodeLocation(startingPosition));
                Advance(x);

                return res;
            }

            return null;
        }

        public CharMatchResult MatchWithFollowingWhitespace(params char[] chars)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            int x = 0;

            if (startingPosition + 1 > _chunks[_j].Value.Length)
                return null;

            CharMatchResult result = null;

            if (chars.Contains(_chunks[_j].Value.Span[startingPosition + x]))
            {
                x++;
                result = new CharMatchResult(_chunks[_j].Value.Slice(startingPosition, 1)) { Location = GetNodeLocation(startingPosition) };
            }

            if(result)
            {
                var foundWhitespace = false;
                while(_chunks[_j].Value.Length > startingPosition + x && char.IsWhiteSpace(_chunks[_j].Value.Span[startingPosition + x]))
                {
                    x++;

                    foundWhitespace = true;
                }

                if (foundWhitespace)
                {
                    Advance(x);
                    return result;
                }
            }

            return null;
        }

        public int ConsumeWhitespace()
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return 0;
            }

            var retVal = 0;
            while (_i < _inputLength && char.IsWhiteSpace(_input.Span[_i]))
            {
                Advance(1);
                retVal++;
            }

            return retVal;
        }

        public RegexMatchResult ConsumeRange(int length)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            if (startingPosition + length > _chunks[_j].Value.Length)
                return null;

            var res = new RegexMatchResult(_chunks[_j].Value.Slice(startingPosition, length), GetNodeLocation(startingPosition));
            Advance(length);          
            return res;
        }

        public RegexMatchResult Match(string tok)
        {
            return Match(tok, false);
        }

        public RegexMatchResult MatchUntil(char c, bool matchUntilLastInstance = false, bool includeEndChar = false, char? charThatResetsCounter = null, bool failIfResetCharIsFoundBeforeEndChar = false)
        {
            if (_i == _input.Length)
            {
                return null;
            }

            var startingPosition = _i;

            if (startingPosition > _input.Length)
                return null;

            int endLength = 0;

            var x = 0;

            while (startingPosition + x < _input.Length)
            {
                var currentChar = _input.Span[startingPosition + x];
                if (currentChar == c)
                {
                    if (includeEndChar)
                        endLength = x + 1;
                    else
                        endLength = x;

                    if (!matchUntilLastInstance)
                    {
                        break;
                    }
                }

                x++;

                if (charThatResetsCounter != null && currentChar == charThatResetsCounter.Value)
                {
                    if (failIfResetCharIsFoundBeforeEndChar && endLength == 0) //no endcharFound
                    {
                        x = 0;
                        break;
                    }

                    if (endLength > 0) //if we've seen the endingtoken we shouldn't start a new matchingprocess
                    {
                        break;
                    }

                    startingPosition += x;
                    endLength = 0;
                    x = 0;
                }
            }

            if (x > 0)
            {
                var nLength = endLength > 0 ? endLength : x;
                var res = new RegexMatchResult(_input.Slice(startingPosition, nLength), GetNodeLocation(startingPosition));
                Advance(nLength);

                return res;
            }
            
            return null;
        }

        public RegexMatchResult MatchExact(string tok, StringComparison stringComparison = StringComparison.Ordinal)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            if (startingPosition + tok.Length > _chunks[_j].Value.Length)
                return null;

            var index = _i;
            var length = tok.Length;

            var spanToCompare = _chunks[_j].Value.Slice(startingPosition, length);

            if (tok.AsSpan().Equals(spanToCompare.Span, stringComparison))
            {
                Advance(length);
                return new RegexMatchResult(spanToCompare, GetNodeLocation(index));
            }

            return null;
        }

        public RegexMatchResult MatchProgid()
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            //8 is progId + at least one word character
            if (startingPosition + 8 > _chunks[_j].Value.Length)
                return null;

            if (!_chunks[_j].Value.Slice(startingPosition, 7).Span.Equals("progid:".AsSpan(), StringComparison.Ordinal))
                return null;

            var x = 7;

            char Current()
            {
                return _chunks[_j].Value.Span[startingPosition + x];
            }

            while (_chunks[_j].Value.Length > (startingPosition + x) && (char.IsLetterOrDigit(Current()) || Current() == '_' || Current() == '.'))
            {
                x++;
            }

            //we need at least one character after progid:
            if (x > 7)
            {
                var res = new RegexMatchResult(_chunks[_j].Value.Slice(startingPosition, x), GetNodeLocation(startingPosition));
                Advance(x);

                return res;
            }
            else //nothing
            {
                return null;
            }
        }

        public RegexMatchResult MatchIdentifier()
        {
            return MatchKeyword(requireStartingAt: true);
        }
        public RegexMatchResult MatchKeyword(bool requireStartingAt = false, bool allowLeadingDigit = true)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            var x = 0;
            var requiredLength = 0;

            char Current()
            {
                return _chunks[_j].Value.Span[startingPosition + x];
            }

            if (requireStartingAt)
            {
                if (Current() != '@')
                    return null;
                requiredLength++;
                x++;

                if (Current() == '@') //allow one more @ char
                {
                    x++;
                    requiredLength++;
                }
            }

            if (!allowLeadingDigit)
            {
                if (_chunks[_j].Value.Length > (startingPosition + x) && char.IsDigit(Current()))
                    return null;
            }

            while (_chunks[_j].Value.Length > (startingPosition + x) && (char.IsLetterOrDigit(Current()) || Current() == '_' || Current() == '-'))
            {
                x++;
            }

            if (x > requiredLength)
            {
                var res = new RegexMatchResult(_chunks[_j].Value.Slice(startingPosition, x), GetNodeLocation(startingPosition));
                Advance(x);

                return res;
            }
            else //nothing
            {
                return null;
            }
        }

        public RegexMatchResult MatchDirectiveName()
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            var x = 0;

            char Current()
            {
                return _chunks[_j].Value.Span[startingPosition + x];
            }

            if (Current() != '@')
                return null;

            x++;

            while (_chunks[_j].Value.Length > (startingPosition + x) && (char.IsLower(Current()) || Current() == '-'))
            {
                x++;
            }

            if (x > 0)
            {
                var res = new RegexMatchResult(_chunks[_j].Value.Slice(startingPosition, x), GetNodeLocation(startingPosition));
                Advance(x);

                return res;
            }
            else //nothing
            {
                return null;
            }
        }

        public RegexMatchResult Match(string tok, bool caseInsensitive)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text) {
                return null;
            }

            var options = RegexOptions.None;
            if (caseInsensitive)
                options |= RegexOptions.IgnoreCase;

            var regex = GetRegex(tok, options);

            var match = regex.Match(_chunks[_j].Value.Slice(_i - _current).ToString());

            if (!match.Success)
                return null;

            var index = _i;

            Advance(match.Length);

            return new RegexMatchResult(match) {Location = GetNodeLocation(index)};
        }

        // Match a string, but include the possibility of matching quoted and comments
        public RegexMatchResult MatchAny(string tok)
        {
            if (_i == _inputLength)
            {
                return null;
            }

            var regex = GetRegex(tok, RegexOptions.None);

            var match = regex.Match(_input.ToString(), _i);

            if (!match.Success)
                return null;

            Advance(match.Length);

            if (_i > _current && _i < _current + _chunks[_j].Value.Length)
            {
                //If we absorbed the start of an inline comment then turn it into text so the rest can be absorbed
                if (_chunks[_j].Type == ChunkType.Comment && _chunks[_j].Value.Span.StartsWith("//".AsSpan(), StringComparison.Ordinal))
                {
                    _chunks[_j].Type = ChunkType.Text;
                }
            }

            return new RegexMatchResult(match);
        }

        public int Advance(int length)
        {
            if (_i == _inputLength) //only for empty cases as there may not be any chunks
                return 0;

            int startvalue = _i;

            // The match is confirmed, add the match length to `i`,
            // and consume any extra white-space characters (' ' || '\n')
            // which come after that. The reason for this is that LeSS's
            // grammar is mostly white-space insensitive.
            _i += length;
            var endIndex = _current + _chunks[_j].Value.Length;

            while (true)
            {
                if(_i == _inputLength)
                    break;

                if (_i >= endIndex)
                {
                    if (_j < _chunks.Count - 1)
                    {
                        _current = endIndex;
                        endIndex += _chunks[++_j].Value.Length;
                        continue; // allow skipping multiple chunks
                    }
                    else
                        break;
                }

                if (!char.IsWhiteSpace(_input.Span[_i]))
                    break;

                _i++;
            }

            return _i - startvalue;
        }

        // Same as Match, but don't change the state of the parser,
        // just return the match.

        public bool Peek(char tok)
        {
            if (_i == _inputLength)
                return false;

            return _input.Span[_i] == tok;
        }

        public bool Peek(string tok)
        {
            var regex = GetRegex(tok, RegexOptions.None);

            var match = regex.Match(_input.ToString(), _i);

            return match.Success;
        }

        public bool PeekAfterComments(char tok)
        {
            var memo = this.Location;

            while(!GetComment().Span.IsEmpty);

            var peekSuccess = Peek(tok);

            this.Location = memo;

            return peekSuccess;
        }

        public char? PeekChar(int offset = 0)
        {
            if (_i == _inputLength || _chunks[_j].Type != ChunkType.Text)
            {
                return null;
            }

            var startingPosition = _i - _current;

            if (_chunks[_j].Value.Span.Length > (startingPosition + offset))
            {
                return _chunks[_j].Value.Span[startingPosition + offset];
            }
            return null;
        }

        private Regex GetRegex(string pattern, RegexOptions options)
        {
            if (!regexCache.ContainsKey(pattern))
                regexCache.Add(pattern, new Regex(@"\G" + pattern, options));

            return regexCache[pattern];
        }

        public char GetPreviousCharIgnoringComments()
        {
            if  (_i == 0) {
                return '\0';
            }

            if  (_i != _lastCommentEnd) {
                return PreviousChar;
            }

            int i = _lastCommentStart - 1;

            if  (i < 0) {
                return '\0';
            }

            return _input.Span[i];
        }

        public char PreviousChar
        {
            get { return _i == 0 ? '\0' : _input.Span[_i - 1]; }
        }

        public char CurrentChar
        {
            get { return _i == _inputLength ? '\0' : _input.Span[_i]; }
        }

        public char NextChar
        {
            get { return _i + 1 == _inputLength ? '\0' : _input.Span[_i + 1]; }
        }

        public bool HasCompletedParsing()
        {
            return _i == _inputLength;
        }

        public Location Location
        {
            get 
            {
                return new Location
                {
                    Index = _i,
                    CurrentChunk = _j,
                    CurrentChunkIndex = _current
                };
            }
            set
            {
                _i = value.Index;
                _j = value.CurrentChunk;
                _current = value.CurrentChunkIndex;
            }
        }

        public NodeLocation GetNodeLocation(int index)
        {
            return new NodeLocation(index, this._input, this._fileName);
        }

        public NodeLocation GetNodeLocation()
        {
            return GetNodeLocation(this.Location.Index);
        }

        private enum ChunkType
        {
            Text,
            Comment,
            QuotedString
        }

        private class Chunk
        {
            private MemList _builder;

            public Chunk(ReadOnlyMemory<char> val)
            {
                Value = val;
                Type = ChunkType.Text;
            }

            public Chunk(ReadOnlyMemory<char> val, ChunkType type)
            {
                Value = val;
                Type = type;
            }

            public Chunk()
            {
                _builder = new MemList();
                Type = ChunkType.Text;
            }

            public ChunkType Type { get; set; }

            public ReadOnlyMemory<char> Value { get; set; }

            private bool _final;

            public void Append(ReadOnlyMemory<char> str)
            {
                _builder.Add(str);
            }

            public void Append(char c)
            {
                _builder.Add(new[] { c });
            }

            private static Chunk ReadyForText(List<Chunk> chunks)
            {
                Chunk last = chunks.LastOrDefault();
                if  (last == null || last.Type != ChunkType.Text || last._final == true)
                {
                    last = new Chunk();
                    chunks.Add(last);
                }
                return last;
            }

            public static void Append(char c, List<Chunk> chunks, bool final)
            {
                Chunk chunk = ReadyForText(chunks);
                chunk.Append(c);
                chunk._final = final;
            }

            public static void Append(char c, List<Chunk> chunks)
            {
                Chunk chunk = ReadyForText(chunks);
                chunk.Append(c);
            }

            public static void Append(ReadOnlyMemory<char> s, List<Chunk> chunks)
            {
                Chunk chunk = ReadyForText(chunks);
                chunk.Append(s);
            }

            public static ReadOnlyMemory<char> CommitAll(List<Chunk> chunks)
            {
                MemList all = new MemList();
                foreach (Chunk chunk in chunks)
                {
                    if (chunk._builder != null)
                    {
                        ReadOnlyMemory<char> val = chunk._builder.ToString().AsMemory();
                        chunk._builder = null;
                        chunk.Value = val;
                    }

                    all.Add(chunk.Value);
                }
                return all.ToString().AsMemory();
            }
        }

        private ReadOnlyMemory<char> Remaining
        {
            get { return _input.Slice(_i); }
        }
    }

    public struct Location 
    {
        public int Index { get; set; }
        public int CurrentChunk { get; set; }
        public int CurrentChunkIndex { get; set; }
    }
}
