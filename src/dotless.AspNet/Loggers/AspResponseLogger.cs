using dotless.Core.configuration;

namespace dotless.Core.Loggers
{
    using Response;

    public class AspResponseLogger : Logger
    {
        public IResponse Response { get; set; }

        public AspResponseLogger(LogLevel level, IResponse response) : base(level)
        {
            Response = response;
        }

        public AspResponseLogger(DotlessConfiguration config, IResponse response) : this(config.LogLevel, response) { }

        protected override void Log(string message)
        {
            Response.WriteCss(message);
        }
    }
}