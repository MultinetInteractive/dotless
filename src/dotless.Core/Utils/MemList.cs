using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dotless.Core.Utils
{
    public class MemList : List<ReadOnlyMemory<char>>
    {
        public override string ToString()
        {
            if (this is null || this.Count == 0)
                return string.Empty;
            if (this.Count == 1)
                return this[0].ToString();

            var length = this.Sum(p => p.Length);
            var bufArr = ArrayPool<char>.Shared.Rent(length);
            Span<char> buffer = new Span<char>(bufArr, 0, length);
            int currentPos = 0;
            foreach (var token in this)
            {
                token.Span.CopyTo(buffer.Slice(currentPos, token.Length));
                currentPos += token.Length;
            }

            var retValue = buffer.ToString();
            ArrayPool<char>.Shared.Return(bufArr);
            return retValue;
        }

        public ReadOnlyMemory<char> ToMemory()
        {
            if (this is null || this.Count == 0)
                return ReadOnlyMemory<char>.Empty;
            if (this.Count == 1)
                return this[0];
            Memory<char> buffer = new Memory<char>(new char[this.Sum(p => p.Length)]);
            int currentPos = 0;
            foreach (var token in this)
            {
                token.Span.CopyTo(buffer.Span.Slice(currentPos, token.Length));
                currentPos += token.Length;
            }

            return buffer;
        }
    }


    public class MemListComparer : IEqualityComparer<MemList>
    {
        public bool Equals(MemList x, MemList y)
        {
            if(x.Count != y.Count) return false;

            for (int i = 0; i < x.Count; i++)
            {
                if (!x[i].Span.SequenceEqual(y[i].Span)) 
                    return false;
            }
            return true;
        }

        public int GetHashCode(MemList obj)
        {
            return base.GetHashCode();
        }

        public static MemListComparer Default = new MemListComparer();
    }

    public class MemComparer : IEqualityComparer<ReadOnlyMemory<char>>
    {
        private StringComparison _comparison;

        public MemComparer(StringComparison comparison)
        {
            _comparison = comparison;
        }

        public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y)
        {
            return x.Span.Equals(y.Span, _comparison);
        }

        public int GetHashCode(ReadOnlyMemory<char> obj)
        {
            return base.GetHashCode();
        }

        public static MemComparer Ordinal = new MemComparer(StringComparison.Ordinal);
        public static MemComparer OrdinalIgnoreCase = new MemComparer(StringComparison.OrdinalIgnoreCase);
    }

}
