namespace dotless.Core.Parser
{
    using System;

    public class NodeLocation
    {
        public int Index { get; set; }
        public ReadOnlyMemory<char> Source { get; set; }
        public string FileName { get; set; }

        public NodeLocation(int index, ReadOnlyMemory<char> source, string filename)
        {
            Index = index;
            Source = source;
            FileName = filename;
        }
    }
}
