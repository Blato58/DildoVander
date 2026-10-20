namespace Kufr.Backend.Features.PersonaLab;

internal sealed record PersonaLabOptions
{
    public int MaxConcurrentSessions { get; init; } = 4;
    public int MaxConcurrentSuites { get; init; } = 2;
    public int MaxSessionSeconds { get; init; } = 180;
    public int MaxSuiteSeconds { get; init; } = 900;
    internal static PersonaLabOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("PersonaLab");
        var options = new PersonaLabOptions
        {
            MaxConcurrentSessions = section.GetValue("MaxConcurrentSessions", 4),
            MaxConcurrentSuites = section.GetValue("MaxConcurrentSuites", 2),
            MaxSessionSeconds = section.GetValue("MaxSessionSeconds", 180),
            MaxSuiteSeconds = section.GetValue("MaxSuiteSeconds", 900)
        };
        if (options.MaxConcurrentSessions is < 1 or > 16 || options.MaxConcurrentSuites is < 1 or > 4
            || options.MaxSessionSeconds is < 30 or > 180 || options.MaxSuiteSeconds is < 30 or > 1800)
        { throw new InvalidOperationException("Invalid PersonaLab technical limits."); }
        return options;
    }
}
