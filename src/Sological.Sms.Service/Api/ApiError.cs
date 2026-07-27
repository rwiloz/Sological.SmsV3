namespace Sological.Sms.Service.Api;

/// <summary>The one error envelope every API error returns (engineering guide): short
/// code + human text. Never ex.Message, never stack traces.</summary>
public sealed record ApiError(string Error, string Message);
