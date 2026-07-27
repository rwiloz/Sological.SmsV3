using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sological.Sms.Core.Entities;

namespace Sological.Sms.Service.Data;

/// <summary>Stores enums as lowercase member names — the design §3 spelling
/// (queued, smscentral, pending, …).</summary>
public sealed class LowercaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => v.ToString()!.ToLowerInvariant(),
    v => Enum.Parse<TEnum>(v, true))
    where TEnum : struct, Enum;

/// <summary>The webhook event types' wire names carry a dot (sms.delivery / sms.inbound),
/// so they map explicitly instead of via the lowercase convention.</summary>
public sealed class WebhookEventTypeConverter() : ValueConverter<WebhookEventType, string>(
    v => WebhookEventNames.ToWire(v),
    v => WebhookEventNames.FromWire(v));
