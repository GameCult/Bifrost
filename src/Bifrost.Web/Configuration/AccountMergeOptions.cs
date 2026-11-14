namespace Bifrost.Web.Configuration;

/// <summary>
/// Gates on the account-merge page (section <c>AccountMerge</c>). Dark by default: a merge Bifrost confirms against a Heimdall without
/// the identity-move route stays Unknown forever and freezes the folded account, so set <c>AccountMerge__Enabled</c> only after
/// Heimdall's identity-moves is deployed and probed.
/// </summary>
public sealed class AccountMergeOptions
{
    public const string SectionName = "AccountMerge";

    public bool Enabled { get; init; }
}
