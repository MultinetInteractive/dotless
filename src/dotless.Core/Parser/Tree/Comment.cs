namespace dotless.Core.Parser.Tree
{
    using System;
    using Infrastructure;
    using Infrastructure.Nodes;

    public class Comment : Node
    {
        public ReadOnlyMemory<char> Value { get; set; }
        public bool IsValidCss { get; set; }
        public bool IsSpecialCss { get; set; }
        public bool IsPreSelectorComment { get; set; }
        private bool IsCSSHack { get; set; }

        public Comment(ReadOnlyMemory<char> value)
        {
            Value = value;
            IsValidCss = !value.Span.StartsWith("//".AsSpan());
            IsSpecialCss = value.Span.StartsWith("/**".AsSpan()) || value.Span.StartsWith("/*!".AsSpan());
            IsCSSHack = value.Span.Equals("/**/".AsSpan(), StringComparison.Ordinal) || value.Span.Equals("/*\\*/".AsSpan(), StringComparison.Ordinal);
        }

        protected override Node CloneCore() {
            return new Comment(Value);
        }

        public override void AppendCSS(Env env)
        {
            if (IsReference || env.IsCommentSilent(IsValidCss, IsCSSHack, IsSpecialCss)) {
                return;
            }

            env.Output.Append(Value);

            if (!IsCSSHack && IsPreSelectorComment)
            {
                env.Output.Append("\n");
            }
        }
    }
}
