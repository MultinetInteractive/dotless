using System;
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
            StringBuilder sb = new StringBuilder();
            foreach (var item in this)
            {
                sb.Append(item);
            }
            return sb.ToString();
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

}
