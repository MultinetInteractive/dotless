using dotless.Core.configuration;

namespace dotless.Core.Loggers
{
    using System;

    public class ConsoleLogger : Logger
    {
        public ConsoleLogger(LogLevel level) : base(level) {}
        public ConsoleLogger(DotlessConfiguration config) : this(config.LogLevel) { }

        protected override void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}