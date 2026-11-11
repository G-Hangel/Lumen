using System;

namespace Lumen.Tools;

public sealed class ToolException : Exception
{
    public ToolException(string code) : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
