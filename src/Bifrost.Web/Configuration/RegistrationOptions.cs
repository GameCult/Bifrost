namespace Bifrost.Web.Configuration;

/// <summary>Gates on the anonymous native registration (section <c>Bifrost:Registration</c>).</summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Bifrost:Registration";

    public const int MinDifficulty = 1;

    public const int MaxDifficulty = 24;

    public const int MinFallbackWaitSeconds = 5;

    public const int MaxFallbackWaitSeconds = 300;

    public RegistrationCaptchaOptions Captcha { get; init; } = new();

    public RegistrationLimitsOptions Limits { get; init; } = new();
}

/// <summary>One budget on GET /auth/bifrost/challenge and POST /auth/bifrost/register together, per source address in one fixed window.</summary>
public sealed class RegistrationLimitsOptions
{
    public int PerAddressPermits { get; init; } = 10;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxTrackedKeys { get; init; } = 10_000;

    /// <summary>Time-lock submissions (no script, no proof of work) per address per <see cref="FallbackWindow"/>; far tighter than <see cref="PerAddressPermits"/> because they cost the client nothing.</summary>
    public int FallbackPermits { get; init; } = 3;

    public TimeSpan FallbackWindow { get; init; } = TimeSpan.FromHours(1);
}

public sealed class RegistrationCaptchaOptions
{
    /// <summary>Leading zero bits the SHA-256 of <c>challenge:nonce</c> must have. 18 is about 262k hashes, a second or two in a browser.</summary>
    public int Difficulty { get; init; } = 18;

    /// <summary>Seconds after the challenge was issued before a <c>waited</c> (no proof of work) submission is accepted.</summary>
    public int FallbackWaitSeconds { get; init; } = 30;
}
