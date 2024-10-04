using System;
using System.Collections.Generic;
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
}
