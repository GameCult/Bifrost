namespace Bifrost.Web.Configuration;

/// <summary>The chat host's pull of merge notices (MergeNoticeFeed). An empty token turns both routes off (404); startup does not require it.</summary>
public sealed class ChatNoticesOptions
{
    public const string SectionName = "ChatNotices";

    /// <summary>The bearer the chat host presents (environment <c>ChatNotices__Token</c>). Its own secret: it grants nothing else.</summary>
    public string Token { get; init; } = string.Empty;
}
