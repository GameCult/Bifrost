namespace Bifrost.Web.Features.Registration;

/// <summary>
/// The training notice a registrant accepts. DRAFT wording: Eureka replaces <see cref="Text"/>. Changing the wording
/// in substance means a new <see cref="Version"/> (a positive integer, no leading zeros), because a receipt records the
/// version the person accepted.
/// </summary>
public static class TrainingNotice
{
    public const int Version = 1;

    public const string Text =
        "Chat messages and activity on GameCult chat may be used to train and evaluate AI models. " +
        "No Discord material is used. " +
        "Your consent is voluntary. It is recorded with this notice version and the time you give it, " +
        "and you can withdraw it at any time. Withdrawing stops future use; it cannot recall use that already happened.";
}
