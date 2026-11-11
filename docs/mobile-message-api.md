# Mobile Message API Documentation

## Overview 

The base URL for the API is `https://api.mobilemessage.com.au/`.

The Mobile Message API allows up to 5 simultaneous requests per account. If you exceed this limit, you'll receive an HTTP 429 error with the message: "Too many concurrent requests. Please wait." Simply wait for an existing request to complete before trying again.

### OpenAPI specification

A complete OpenAPI 3.0 specification of the v1 API is available at https://mobilemessage.com.au/assets/openapi.json. Use it to generate typed clients, import the API into tools like Postman or Insomnia, or power request validation.

### Throughput

Batches are accepted at up to 10,000 messages per request with 5 concurrent requests. Accepted messages are queued and submitted to Australian carriers at a sustained rate of over 400 messages per second — a 50,000-message campaign is typically fully submitted to carriers within about two minutes. There is no per-second cap on API submissions; HTTP 429 is only returned for more than 5 concurrent requests.

## Authentication

Use Basic Authentication with your API username and password to access the endpoints. Follow these steps:

1. Combine your `username:password`.
2. Encode the resulting string in Base64.
3. Add this string to the `Authorization` header as: `Authorization: Basic {base64_encoded_credentials}`.

### Multiple API keys and rotation

Your account can hold multiple active API keys at the same time, managed in the dashboard under Settings > API. To rotate a credential with zero downtime: create a new key, update your application to use it, confirm traffic has switched over on the API logs page in the dashboard, then delete the old key. Each key is independent, so the old key keeps working until the moment you delete it.

---

## Send SMS Messages

**POST** `/v1/messages`

This endpoint allows you to send one or more SMS messages. You can include up to 10,000 messages in a single request.

For automated integrations — particularly those sending large batches or implementing retry logic — we recommend including an `Idempotency-Key` header so that retries after a network error or timeout don't risk duplicate sends. Replayed requests return the original response with an `Idempotency-Replay: true` header; reusing a key with a different request payload returns HTTP 422. A retry that arrives while the original request is still running waits up to 3 seconds for it to finish and then returns its response; if it is still running after that, you get HTTP 409 and can retry with the same key. Keys are scoped to the API key that sent them and are kept for 24 hours. See [Safely retrying API requests with an Idempotency-Key](https://help.mobilemessage.com.au/api/api-idempotency-keys) for details.

### Top-level parameters

| Parameter | Type | Description |
|---|---|---|
| `messages` | Array of Objects | One or more message objects. |
| `enable_unicode` (optional) | Boolean | When true, messages that require UCS-2 (for example emojis or non-GSM characters) are sent using UCS-2. Defaults to false. Each message can still set `unicode` individually. |
| `max_parts` (optional) | Integer | Maximum SMS parts (credits) per message, applied to all messages in the batch. Messages exceeding this limit are skipped with status `error`. Default 10, range 1-99. |
| `ignore_unsubscribes` (optional) | Boolean | Set to `true` to bypass unsubscribe blocking for this send request. If omitted or set to `false`, normal unsubscribe blocking applies. Use with caution, as bypassing your unsubscribe list could result in spam complaints. |
| `shorten_urls` (optional) | Boolean or `"auto"` | Replace the links in every message in this request with short trackable links so clicks can be reported. Set it to `"auto"` to shorten whenever your account is able to, and send with the original links instead of an error when it is not. If omitted, your account default applies, which you can change in your dashboard under Settings. Each message can override this with its own `shorten_urls` field. See Link shortening below. |

### Message object fields

| Field | Type | Description |
|---|---|---|
| `to` | String | An Australian mobile number, in local (`0412345678`) or international (`61412345678`) format. Any other number is rejected with status `error` at no cost. |
| `message` | String | The message content. Up to 10 parts are supported: GSM-7: up to 1530 characters (10 x 153); UCS-2: up to 670 characters (10 x 67). If Unicode is not enabled, non-GSM characters are stripped. Supports placeholders: `{first_name}`, `{last_name}`, `{company}`, `{other}`, `{field_1}` through `{field_5}`, and `{optout}`. The contact placeholders are filled from the recipient's saved contact record (if one exists). See below for details. |
| `sender` | String | The mobile number or business name the message will come from. Must be a Sender ID registered in your account -- use GET /v1/senders to list your available senders. |
| `custom_ref` (optional) | String | A custom reference to help track the message. |
| `unicode` (optional) | Boolean | Overrides the top-level `enable_unicode` for this message. When true, the message will be sent using UCS-2 if required. |
| `scheduled_for` (optional) | String | UTC datetime to send this message (ISO 8601, e.g. `2026-04-01T09:00:00`). Must be at least 1 minute in the future. If omitted, the message is sent immediately. Scheduled messages can be cancelled via DELETE /v1/messages. |
| `shorten_urls` (optional) | Boolean or `"auto"` | Overrides the top-level `shorten_urls` for this message. Set it to `false` to leave one message in a batch with its original links, or `"auto"` to shorten this message only when the account is able to. |

### Contact placeholders

If the recipient's number matches a saved contact in your account, the placeholders `{first_name}`, `{last_name}`, `{company}`, `{other}`, and `{field_1}` through `{field_5}` will be replaced with the values from that contact record. If you have named your custom fields in the app (e.g. "Date of Birth"), you can also use the slug form `{date_of_birth}`. If no matching contact is found, placeholders are replaced with an empty string.

### {optout} token

Include `{optout}` anywhere in your message body and it will automatically be replaced with an opt-out instruction before sending. The replacement is always exactly 20 characters:

- **Dedicated or shared number senders** --> ` Opt out: Reply Stop`
- **Alphanumeric or own-number senders** --> ` OptOut mb.st/XXXXXX` (where `XXXXXX` is your account's unique 6-character opt-out code)

### Link shortening

With `shorten_urls` enabled, every link in the message is replaced with a short link on `addr.au` that is unique to that recipient, so clicks can be attributed to the individual message. Clicks appear on the send report in your dashboard and through GET /v1/link-tracking. Omitting the flag falls back to your account default, which is how integrations that cannot set a flag opt in.

`shorten_urls` accepts a third value, `"auto"`. With `"auto"`, the message is shortened whenever the account and the message qualify, and otherwise sent with its original links instead of an error. Check for the `short_links` object in each result to see whether a message was shortened. Use `"auto"` when a send must never fail over shortening, such as an automated integration. The rules below describe an explicit `shorten_urls: true`; with `"auto"`, every case listed as an error sends unshortened instead.

- Link shortening is available once your account has made its first credit purchase.
- Up to 3 different links per message. A message with more comes back with status `error` and is not sent.
- A link containing a personalisation tag such as `{first_name}` cannot be shortened, because it resolves to a different destination for every recipient. Remove the tag from the link or turn shortening off for that message.
- Each link can be up to 2048 characters long.
- Short links keep working for 90 days from the send, then stop resolving.
- The credit cost is calculated on the shortened message, so a long URL that fits into 22 characters can reduce the number of parts.

Short links are a fixed length, and the message parts are counted on the shortened text rather than your original URL:

- **First link** --> `https://addr.au/XXXXXX`, exactly 22 characters
- **Second and third links** --> `https://addr.au/XXXXXX2` and `https://addr.au/XXXXXX3`, 23 characters each

A shortened message carries a `short_links` object in its result, holding the `code` minted for that recipient and a `links` array of `{url, short_url}` pairs, one per shortened link, in the order the links appear in the message.

If short links cannot be issued at the moment of sending, the messages still go out with their original links and the response includes `"link_tracking": "unavailable"` at the top level. The send succeeds; only the tracking is missing.

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `send_id` | Integer | Unique ID for this send batch. Only present when the request contains 2 or more valid messages. Can be used to cancel all scheduled messages in this batch via DELETE /v1/messages. |
| `ignore_unsubscribes` | Boolean | Whether unsubscribe blocking was bypassed. |
| `shorten_urls` | Boolean | The link shortening setting the request ran with, either the value you sent or your account default. |
| `link_tracking` | String | Only present when short links could not be issued at the moment of sending, with the value `"unavailable"`. The messages went out with their original links. |
| `total_cost` | Integer | Total credits consumed. |
| `results` | Array | Per-message results. |
| `results[].to` | String | Recipient number. |
| `results[].message` | String | Message content sent. |
| `results[].sender` | String | Sender ID used. |
| `results[].custom_ref` | String | Your custom reference (if provided). |
| `results[].status` | String | `"success"`, `"error"`, or `"blocked"` (the recipient has unsubscribed and was not sent to). |
| `results[].cost` | Integer | Credits consumed for this message. |
| `results[].message_id` | String | UUID for tracking. |
| `results[].encoding` | String | `"gsm7"` or `"ucs2"` (only present if Unicode was enabled). |
| `results[].short_links` | Object | The short links issued for this recipient (only present when the message was shortened). Holds `code` and a `links` array of `{url, short_url}` pairs. |

Note: entries that are structurally invalid (not an object, or missing `to`, `message`, or `sender`) come back with only `status`, `error`, and `item` (the zero-based index of the entry in your request) — they carry no `to`/`message`/`sender` fields, so match errors to your input by `item`, not by recipient.
| `results[].send_id` | Integer | The send batch ID. |

---

## Send to List

**POST** `/v1/list-send`

Send an SMS to all contacts in a list in a single request. Supports per-contact variable substitution, `{optout}`, and scheduled delivery. Unsubscribed numbers are automatically filtered unless `ignore_unsubscribes` is set.

Use `GET /v1/messages?custom_ref=your-ref` to check delivery status per recipient after sending, or `GET /v1/sends?send_id=` for the totals of the whole send.

### Parameters

| Field | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list to send to. |
| `sender` | String | Your approved Sender ID. Use GET /v1/senders to list your available senders. |
| `message` | String | Message content. Supports `{first_name}`, `{last_name}`, `{company}`, `{other}`, `{field_1}`-`{field_5}` (or custom name slugs), and `{optout}`. Each contact's details are substituted individually. |
| `enable_unicode` (optional) | Boolean | When true, messages containing emojis or non-GSM characters are sent as UCS-2. Defaults to false (non-GSM characters are stripped). |
| `max_parts` (optional) | Integer | Maximum SMS parts (credits) per message. Messages that exceed this limit are skipped and counted in `total_skipped_too_long`. Default 10, range 1-99. |
| `custom_ref` (optional) | String | Reference stored against every message in this send. Reusing a reference your account already used for a list send in the last 24 hours returns HTTP 409, so a retried request cannot send the same campaign twice. |
| `ignore_unsubscribes` (optional) | Boolean | Bypass unsubscribe filtering. Default false. |
| `shorten_urls` (optional) | Boolean or `"auto"` | Replace the links in the message with short trackable links, with a code unique to each recipient, so clicks can be reported. If omitted, your account default applies. A list send uses one message template, so the setting applies to the whole send. See Link shortening under Send SMS Messages for the rules, and GET /v1/link-tracking for the results. A request that explicitly sets shorten_urls to true when the account has not made its first credit purchase returns HTTP 403, and an explicit request with more than 3 links or a link containing a personalisation tag returns HTTP 400. When the value is `"auto"`, or shortening comes only from the account default, these cases send with the original links instead of returning an error. |
| `scheduled_for` (optional) | String | UTC datetime to send (ISO 8601). Must be more than 1 minute in the future — values at or before that (including past datetimes) are sent immediately rather than rejected. If omitted, sends immediately. A scheduled list send can be cancelled in bulk by passing the `custom_ref` to DELETE /v1/messages. |
| `stagger_minutes` (optional) | Integer | Spread sending over this many minutes. Messages are divided into batches and sent gradually over the period. Requires 50+ recipients — with fewer, the value is silently ignored and the send goes out at once. Can be combined with `scheduled_for` to stagger from a future time. |

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `send_id` | Integer | Unique ID for this send batch. Can be used to cancel all scheduled messages via DELETE /v1/messages. |
| `list_id` | Integer | The list that was sent to. |
| `total_recipients` | Integer | Number of messages queued. |
| `total_blocked_unsubscribed` | Integer | Recipients blocked due to unsubscribe. |
| `total_skipped_too_long` | Integer | Messages skipped because they exceeded `max_parts`. |
| `total_cost` | Integer | Total credits consumed. |
| `scheduled_for` | String or null | The scheduled datetime, or null if sent immediately. |
| `stagger_minutes` | Integer or null | The stagger period applied, or null when not staggered. |
| `send_status` | String | `"queued"` (immediate), `"scheduled"` (future send), or `"staggered"` (stagger applied). |
| `shorten_urls` | Boolean | The link shortening setting the send ran with, either the value you sent or your account default. |
| `tracked_urls` | Array | The destinations now being tracked, in the order they appear in the message (only present when the send was shortened). Each recipient gets their own short link for each destination, so the short links themselves are per message. |
| `link_tracking` | String | Only present when short links could not be issued at the moment of sending, with the value `"unavailable"`. The send went out with its original links. |

---

## Send Status

**GET** `/v1/sends`

Read the status and running totals of one send, as returned by POST /v1/messages or POST /v1/list-send. The delivery, failure, reply and unsubscribe counts are the same figures the Mobile Message app reports for the send, so a campaign can be tracked with one request instead of paging through every recipient.

### Query parameters

| Parameter | Type | Description |
|---|---|---|
| `send_id` | Integer | The send to report on. Required. |

### Response fields

| Field | Type | Description |
|---|---|---|
| `send_id` | Integer | The send. |
| `status` | String | `processing`, `scheduled`, `sending`, `sent`, `cancelled` or `partial`. See below. |
| `total_recipients` | Integer | Recipients in the send. |
| `total_blocked_unsubscribed` | Integer or null | Recipients blocked due to unsubscribe, for sends made through POST /v1/list-send. `null` for sends made another way. |
| `delivered_count` | Integer | Messages confirmed delivered by the carrier. |
| `failed_count` | Integer | Messages the carrier could not deliver. |
| `reply_count` | Integer | Replies received to the send. |
| `unsub_count` | Integer | Recipients who opted out after the send. |
| `total_cost` | Integer | Credits consumed. Zero once a send has been cancelled and refunded. |
| `scheduled_for` | String or null | The scheduled datetime in UTC, or null if the send went out immediately. |
| `created_at` | String | When the send was created, in UTC. |

### Send statuses

| Status | Meaning |
|---|---|
| `processing` | A large list send that has been accepted, charged and is being prepared. It becomes `scheduled`, `sending` or `sent` on its own, usually within a minute, and the counts stay at zero until it does. |
| `scheduled` | The send is waiting for its scheduled time, or for its stagger batches. |
| `sending` | The send is on its way. |
| `sent` | Every message has been handed to the carrier. |
| `cancelled` | The send was cancelled and its unused credits returned. |
| `partial` | Some messages in the send were cancelled and the rest were sent. |

### Error responses

- Returns HTTP 400 if `send_id` is missing or not a number.
- Returns HTTP 404 if the send does not exist on your account.

---

## Message History

**GET** `/v1/messages`

Retrieve sent message history. You can look up a specific message by ID or custom reference, or browse paginated history with optional filters.

### Lookup by ID or reference

| Parameter | Type | Description |
|---|---|---|
| `message_id` | String | The unique message ID (UUID) returned when the message was sent. |
| `custom_ref` | String | Your custom reference (exact match). Returns all messages with this reference. |

### Paginated history

When neither `message_id` nor `custom_ref` is provided, returns paginated outbound message history.

| Parameter | Type | Description |
|---|---|---|
| `status` (optional) | String | Filter by status. One of: `pending`, `scheduled`, `sent`, `delivered`, `failed`, `cancelled`. |
| `from` (optional) | String | Filter messages sent on or after this date (YYYY-MM-DD, UTC). |
| `to` (optional) | String | Filter messages sent before this date (YYYY-MM-DD, UTC). |
| `limit` (optional) | Integer | Results per page. Default 50, max 200. |
| `offset` (optional) | Integer | Pagination offset. Default 0. |

### Response fields -- lookup by ID or reference

Note: the lookup response uses different field names to the paginated history response below.

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `results` | Array | Matching messages. |
| `results[].to` | String | Recipient number in international format. |
| `results[].message` | String | Message text. |
| `results[].sender` | String | Sender ID used. |
| `results[].custom_ref` | String | Your custom reference. |
| `results[].status` | String | Delivery status (e.g. `delivered`, `failed`, `pending`, `scheduled`, `sent`, `cancelled`). |
| `results[].cost` | String | Credits consumed, as a decimal string (e.g. `"1.00"`). |
| `results[].message_id` | String | UUID of the message. |
| `results[].requested_at` | String | UTC timestamp when the message was requested. |
| `results[].scheduled_for` | String or null | Scheduled delivery time, or null. |
| `results[].send_id` | Integer | The send batch ID. Only present when the message was part of a batch. |

### Response fields -- paginated history

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `total` | Integer | Total matching messages. |
| `limit` | Integer | Current page size. |
| `offset` | Integer | Current offset. |
| `results` | Array | Messages. |
| `results[].message_id` | String | UUID of the message. |
| `results[].recipient_number` | String | Recipient number in international format. |
| `results[].sender_id` | String | Sender ID used. |
| `results[].message_content` | String | Message text. |
| `results[].status` | String | Delivery status. |
| `results[].cost` | String | Credits consumed, as a decimal string (e.g. `"1.00"`). |
| `results[].custom_ref` | String | Your custom reference. |
| `results[].requested_at` | String | UTC timestamp when the message was requested. |
| `results[].scheduled_for` | String or null | Scheduled delivery time, or null. |
| `results[].send_id` | Integer | The send batch ID. Only present when the message was part of a batch. |

---

## Link Tracking

**GET** `/v1/link-tracking`

Read the click results for messages that went out with short links. Provide exactly one of `send_id`, `message_id` or `custom_ref`. With `send_id` the totals cover the whole send, and `include=recipients` adds a page of per-recipient rows so you can see exactly who clicked. With `message_id` or `custom_ref` the results cover that single message, which is how you read the clicks on a message sent on its own.

Automated link scanners in carrier and inbox security products fetch a link as soon as it is delivered. Those hits are recorded but excluded from every figure below, so the numbers reflect people rather than software.

### Query parameters

| Parameter | Type | Description |
|---|---|---|
| `send_id` | Integer | The send to report on, as returned by POST /v1/messages or POST /v1/list-send. Provide exactly one of `send_id`, `message_id` or `custom_ref`. |
| `message_id` | String | The UUID of a single message, as returned when it was sent. Returns the click results for that message alone. |
| `custom_ref` | String | Your own reference on a single message. Where several messages carry the same reference, the most recent one is used. |
| `include` (optional) | String | Set to `recipients` to include a page of per-recipient results. Any other value is ignored. Only applies with `send_id`. |
| `clicked` (optional) | Boolean | Set to `true` to return only the recipients who clicked, instead of every recipient. `recipients_total` then counts the clickers, and paging works the same way. Only applies with `include=recipients`. |
| `limit` (optional) | Integer | Recipients per page. Default 50, max 200. Only applies with `include=recipients`. |
| `offset` (optional) | Integer | Recipients to skip, for paging through a large send. Default 0. |

### Response fields (send_id lookup)

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `send_id` | Integer | The send these results belong to. |
| `link_tracking` | Boolean | `false` when the send went out without short links. That is a normal answer, not an error, and no click figures are returned with it. |
| `total_recipients` | Integer | Messages in the send. |
| `delivered_count` | Integer | Messages confirmed delivered so far. |
| `total_clicks` | Integer | Every click on every link in the send. |
| `unique_clicks` | Integer | Recipients who clicked at least once. |
| `click_rate` | Number or null | `unique_clicks` divided by `delivered_count`, rounded to 4 decimal places. Null when nothing has been delivered yet. |
| `urls` | Array | One entry per link per URL set, ordered by `url_set_id` then `url_index`. |
| `urls[].url_set_id` | Integer | The URL set this entry belongs to. Entries from different sets are different destinations even when they share a `url_index`. |
| `urls[].url_index` | Integer | Position of this link within its own URL set, starting at 1. |
| `urls[].url` | String | The destination the short link points at. |
| `urls[].clicks` | Integer | Clicks on this link. |
| `urls[].unique_clicks` | Integer | Recipients who clicked this link at least once. |
| `recipients_total` | Integer | Messages available to page through (only with `include=recipients`). |
| `limit` | Integer | Page size applied (only with `include=recipients`). |
| `offset` | Integer | Rows skipped (only with `include=recipients`). |
| `recipients` | Array | Per-recipient results, oldest message first (only with `include=recipients`). |
| `recipients[].message_id` | String | The message UUID returned when the message was sent. |
| `recipients[].to` | String | Recipient number in international format. |
| `recipients[].clicked` | Boolean | Whether this recipient clicked at least once. |
| `recipients[].click_count` | Integer | Clicks from this recipient. |
| `recipients[].first_click` | String or null | UTC datetime of the first click, or null if they have not clicked. |
| `recipients[].last_click` | String or null | UTC datetime of the most recent click, or null if they have not clicked. |

### Tracking a single message

A send is only created for a batch of two or more messages, so a message sent on its own has no `send_id`. Look it up with `message_id` or `custom_ref` instead and the results cover that one message. `include`, `limit` and `offset` describe a page of recipients, so they are ignored in this form. A message that went out without short links returns `link_tracking` false and no click figures, which is a normal answer rather than an error.

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. This reports on the request, not on the message. |
| `message_id` | String | The message UUID returned when the message was sent. |
| `custom_ref` | String | Your own reference. Only present when the message was sent with one. |
| `to` | String | Recipient number in international format. Not returned when `link_tracking` is false. |
| `message_status` | String | Delivery status of the message, the same value GET /v1/messages returns. Not returned when `link_tracking` is false. |
| `link_tracking` | Boolean | `false` when the message went out without short links. |
| `clicked` | Boolean | Whether the recipient clicked at least once. |
| `click_count` | Integer | Clicks from this recipient across every link in the message. |
| `first_click` | String or null | UTC datetime of the first click, or null if they have not clicked. |
| `last_click` | String or null | UTC datetime of the most recent click, or null if they have not clicked. |
| `urls` | Array | One entry per link in the message, ordered by `url_index`. |
| `urls[].url_index` | Integer | Position of this link within the message, starting at 1. |
| `urls[].url` | String | The destination the short link points at. |
| `urls[].clicks` | Integer | Clicks on this link from this recipient. |

### Sends with more than one set of links

A POST /v1/messages batch can carry different links in different messages. Every distinct combination of links in the batch is recorded as its own URL set, and `urls` returns one entry for each link in each set. Use `url_set_id` to tell the sets apart. `url_index` is only the position of a link inside its own set, so `url_index` 1 of one set and `url_index` 1 of another are different destinations.

A POST /v1/list-send sends one message template to the whole list, so it always has a single URL set.

The send totals at the top of the response cover the whole send across every set. `unique_clicks` on the send counts each recipient once even if they clicked links in more than one position, so the per-URL `unique_clicks` figures can add up to more than the send total.

### Error responses

| Status | Error | Cause |
|---|---|---|
| 400 | `"send_id", "message_id", or "custom_ref" is required.` | None of the three was provided. |
| 400 | `Provide only one of "send_id", "message_id", or "custom_ref".` | More than one of the three was provided. |
| 400 | `"send_id" is required.` | `send_id` was zero or not a number. |
| 404 | `Send not found.` | No send with that ID exists on your account. |
| 404 | `Message not found.` | No message with that ID or reference exists on your account. |
| 405 | `Invalid request method. Only GET is allowed.` | The endpoint was called with a method other than GET. |

---

## Cancel Scheduled Message

**DELETE** `/v1/messages`

Cancel one or more scheduled messages and receive a full credit refund. Only messages with `status=scheduled` can be cancelled. Provide `message_id`, `custom_ref`, or `send_id`.

### Request body

| Field | Type | Description |
|---|---|---|
| `message_id` | String | The UUID of a single scheduled message to cancel. |
| `custom_ref` | String | Cancel all scheduled messages with this custom reference (useful for cancelling a batch). |
| `send_id` | Integer | Cancel all scheduled messages in a bulk send. The `send_id` is returned when sending to multiple recipients via POST. |

### Response -- cancel by message_id

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `message_id` | String | The cancelled message UUID. |
| `cancelled` | Boolean | `true` if cancelled. |
| `credits_refunded` | Integer | Number of credits refunded. |

### Response -- cancel by custom_ref

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `custom_ref` | String | The custom reference used. |
| `cancelled_count` | Integer | Number of messages cancelled. |
| `credits_refunded` | Integer | Total credits refunded. |

### Response -- cancel by send_id

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `send_id` | Integer | The send batch ID. |
| `cancelled_count` | Integer | Number of messages cancelled. |
| `credits_refunded` | Integer | Total credits refunded. |

### Error responses

- Returns HTTP 404 if no matching scheduled message is found.
- Cancelling by `message_id`: returns HTTP 409 if the message exists but is not in a `scheduled` state.
- Cancelling by `send_id` or `custom_ref`: returns HTTP 404 when no scheduled messages remain for that reference, including when they have already been sent.
- Cancelling by `send_id` or `custom_ref`: returns HTTP 409 with `This send is still being processed, please retry shortly.` while a large list send is still being prepared (`processing`). Retry in a moment, once GET /v1/sends reports another status.

---

## Contacts

Manage your contact list. Contacts can be added to lists and used as personalisation sources when sending SMS.

### GET /v1/contacts

List contacts with optional filters. Multiple filters are combined with AND. Text fields use partial matching.

| Parameter | Type | Description |
|---|---|---|
| `number` (optional) | String | Exact match by phone number (Australian local or international format). |
| `first_name` (optional) | String | Partial match on first name. |
| `last_name` (optional) | String | Partial match on last name. |
| `company` (optional) | String | Partial match on company. |
| `other` (optional) | String | Partial match on the other/custom field. |
| `field_1` - `field_5` (optional) | String | Partial match on custom fields 1-5. Custom field names are configured in the app under Contacts > Manage Fields. |
| `limit` (optional) | Integer | Results per page. Default 50, max 200. |
| `offset` (optional) | Integer | Pagination offset. Default 0. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `total` | Integer | Total matching contacts. |
| `limit` | Integer | Current page size. |
| `offset` | Integer | Current offset. |
| `results` | Array | Contact records. |
| `results[].contact_id` | Integer | Contact ID. |
| `results[].number` | String | Phone number in international format. |
| `results[].first_name` | String | First name. |
| `results[].last_name` | String | Last name. |
| `results[].company` | String | Company. |
| `results[].other` | String | Custom field. |
| `results[].field_1` - `results[].field_5` | String | Custom contact fields 1-5. |

### POST /v1/contacts

Add a new contact. If duplicate contacts are disabled on your account, adding a number that already exists returns HTTP 409.

| Field | Type | Description |
|---|---|---|
| `number` | String | Phone number (required). |
| `first_name` (optional) | String | First name -- used in `{first_name}` variable substitution. |
| `last_name` (optional) | String | Last name -- used in `{last_name}` substitution. |
| `company` (optional) | String | Company -- used in `{company}` substitution. |
| `other` (optional) | String | Custom field -- used in `{other}` substitution. |
| `field_1` - `field_5` (optional) | String | Custom contact fields 1-5. Used in `{field_1}`-`{field_5}` substitution (or custom name slugs configured in the app). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `contact_id` | Integer | ID of the new contact. |
| `number` | String | Normalised phone number. |

### PATCH /v1/contacts

Update one or more fields on an existing contact. Only the fields you provide will be changed.

| Field | Type | Description |
|---|---|---|
| `contact_id` | Integer | ID of the contact to update (required). |
| `number` (optional) | String | New phone number. |
| `first_name` (optional) | String | First name. |
| `last_name` (optional) | String | Last name. |
| `company` (optional) | String | Company. |
| `other` (optional) | String | Custom field. |
| `field_1` - `field_5` (optional) | String | Custom contact fields 1-5. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `contact_id` | Integer | ID of the updated contact. |

### DELETE /v1/contacts

Remove a contact. The contact is also removed from all lists.

| Field | Type | Description |
|---|---|---|
| `contact_id` | Integer | ID of the contact to delete (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `contact_id` | Integer | ID of the removed contact. |
| `removed` | Boolean | `true` if removed. |

---

## Contact Lists

Organise contacts into named lists for use with Send to List.

### GET /v1/lists

List all contact lists including their contact count.

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `results` | Array | List records. |
| `results[].list_id` | Integer | List ID. |
| `results[].name` | String | List name. |
| `results[].created_at` | String | UTC timestamp of creation. |
| `results[].contact_count` | Integer | Number of contacts in the list. |

### POST /v1/lists

Create a contact list. If a list with the same name already exists, the existing list is returned rather than a duplicate being created.

| Field | Type | Description |
|---|---|---|
| `name` | String | The name for the new list (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | ID of the list. |
| `name` | String | Name of the list. |
| `existing` | Boolean | `true` when a list with this name already existed and was returned instead of created. |

### PATCH /v1/lists

Rename a list. Note: submitting the list's current name unchanged returns HTTP 404 (the update matches no rows) — treat it as a no-op, not a missing list.

| Field | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list to rename (required). |
| `name` | String | New name for the list (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | ID of the renamed list. |
| `name` | String | New name. |

### DELETE /v1/lists

Delete a list. By default only the list and its membership records are removed -- contact records are kept. Set `delete_contacts` to `true` to also permanently delete all contact records that were members of the list.

| Field | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list to delete (required). |
| `delete_contacts` (optional) | Boolean | If true, also permanently delete all contact records that were members of this list. Defaults to false. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | ID of the deleted list. |
| `removed` | Boolean | `true` if removed. |

---

## List Members

Add, remove, and view contacts within a specific list.

### GET /v1/list-contacts

| Parameter | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list to query (required). |
| `limit` (optional) | Integer | Default 50, max 200. |
| `offset` (optional) | Integer | Pagination offset. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | The list that was queried. |
| `total` | Integer | Total members. |
| `limit` | Integer | Current page size. |
| `offset` | Integer | Current offset. |
| `results` | Array | Contact records with membership info. |
| `results[].contact_id` | Integer | Contact ID. |
| `results[].number` | String | Phone number. |
| `results[].first_name` | String | First name. |
| `results[].last_name` | String | Last name. |
| `results[].company` | String | Company. |
| `results[].other` | String | Custom field. |
| `results[].field_1` - `results[].field_5` | String | Custom contact fields 1-5. |
| `results[].added` | String | UTC timestamp when added to the list. |

### POST /v1/list-contacts

Add a contact to a list. Has no effect if the contact is already a member.

| Field | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list (required). |
| `contact_id` (optional*) | Integer | ID of the contact to add. One of `contact_id` or `number` is required. |
| `number` (optional*) | String | Australian mobile number of the contact to add. One of `contact_id` or `number` is required. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | List ID. |
| `contact_id` | Integer | Contact ID. |
| `added` | Boolean | `true` if added. |

### DELETE /v1/list-contacts

Remove a contact from a list (the contact itself is not deleted).

| Field | Type | Description |
|---|---|---|
| `list_id` | Integer | ID of the list (required). |
| `contact_id` (optional*) | Integer | ID of the contact to remove. One of `contact_id` or `number` is required. |
| `number` (optional*) | String | Australian mobile number of the contact to remove. One of `contact_id` or `number` is required. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `list_id` | Integer | List ID. |
| `contact_id` | Integer | Contact ID. |
| `removed` | Boolean | `true` if removed. |

---

## Unsubscribes

Manage your opt-out list. Adding a number blocks it from receiving future messages, cancels any scheduled messages to that number, and removes the contact from all lists.

### GET /v1/unsubscribes

| Parameter | Type | Description |
|---|---|---|
| `number` (optional) | String | Filter by phone number. |
| `limit` (optional) | Integer | Results per page. Default 50, max 200. |
| `offset` (optional) | Integer | Pagination offset. Default 0. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `total` | Integer | Total unsubscribed numbers. |
| `limit` | Integer | Current page size. |
| `offset` | Integer | Current offset. |
| `results` | Array | Unsubscribe records. |
| `results[].number` | String | Phone number. |
| `results[].updated_at` | String | UTC timestamp of the unsubscribe. |

### POST /v1/unsubscribes

Add a number to your unsubscribe list.

| Field | Type | Description |
|---|---|---|
| `number` | String | Phone number to unsubscribe (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `number` | String | Normalised phone number. |
| `added` | Boolean | `true` if added. |

### DELETE /v1/unsubscribes

Remove a number from your unsubscribe list.

| Field | Type | Description |
|---|---|---|
| `number` | String | Phone number to resubscribe (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `number` | String | Normalised phone number. |
| `removed` | Boolean | `true` if removed. |

---

## Account Credit Balance

**GET** `/v1/account`

Retrieve your current SMS credit balance using your authenticated credentials. No extra parameters are required.

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `credit_balance` | Integer | Your current SMS credit balance. |
| `credit_price` | Decimal or null | Your effective price per credit in AUD, excluding GST, taken from your most recent credit purchase. `null` if the account has not purchased credits yet. Useful for converting credit usage into dollar spend in your own reporting. |

### Error responses

- **404** -- Account not found or no credit balance available.
- **405** -- Invalid request method. Only GET is allowed.

---

## Sender IDs

List your active Sender IDs or register your own mobile number as a sender via a two-step verification flow.

### GET /v1/senders

List all active Sender IDs on your account.

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `results` | Array | Sender records. |
| `results[].sender` | String | Sender ID value (alphanumeric name or phone number). |
| `results[].type` | String | Sender type (e.g. `alpha`, `own`, `dedicated`, `shared`). |
| `results[].label` | String | Label for this sender in your account. |
| `results[].is_default` | Boolean | Whether this is your default sender. |

### POST /v1/senders

Register your own mobile number as a sender. An SMS containing a confirmation link is sent to the number at no charge. The owner must click the link and confirm before the number is activated as a sender on your account.

| Field | Type | Description |
|---|---|---|
| `number` | String | The mobile number to register. |
| `label` (optional) | String | A label to identify this sender in your account. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"verification_sent"` on success. |
| `message` | String | Confirmation that a verification link has been sent. |

#### Error responses

- **409** -- The number is already an active sender on your account.
- **429** -- Too many pending verifications, or a verification was sent to that number in the last 5 minutes.

---

## ACMA Registration

Register a custom alphanumeric Sender ID (e.g. "MyBrand") with the Australian Communications and Media Authority (ACMA). All custom Sender IDs in Australia must be registered through the ACMA Sender ID Register. After submission, registrations are reviewed by Mobile Message before being forwarded to ACMA for approval.

If you are only registering a small number of Sender IDs, it may be easier to use the online platform at https://app.mobilemessage.com.au/ where the form guides you through each step. This API endpoint is best suited for businesses that need to register Sender IDs programmatically at scale, such as marketing agencies managing multiple clients.

### GET /v1/acma-registration

List your ACMA sender ID registrations along with your saved brands and partner organisations. If you include the `sender` query parameter, the ACMA status of that specific Sender ID is returned instead of the full listing.

| Parameter | Type | Description |
|---|---|---|
| `sender` (optional) | String | If provided, returns the ACMA registration status for this Sender ID instead of the full listing. |

#### Response fields (full listing)

The `registrations` array contains all custom (alphanumeric) Sender IDs on your account:

| Field | Type | Description |
|---|---|---|
| `registrations` | Array | All alphanumeric sender ID registrations. |
| `registrations[].sender_id` | Integer | Internal ID for this sender. Use this as the `sender_id` field in a POST request if you need to re-register a previously rejected sender. |
| `registrations[].sender` | String | The alphanumeric Sender ID text (e.g. "MyBrand"). |
| `registrations[].status` | String | Overall sender status: `active` (approved and ready to use for sending) or `pending` (registration in progress, not yet available for sending). |
| `registrations[].acma_status` | String or null | Current stage of the ACMA registration process. See ACMA status values below. |
| `registrations[].brand_id` | Integer or null | ID of the brand associated with this registration. Corresponds to an entry in the `brands` array. |
| `registrations[].partner_id` | Integer or null | ID of the on-behalf-of partner associated with this registration, or null if not applicable. Corresponds to an entry in the `partners` array. |
| `brands` | Array | Saved organisations. Pass the `id` as `brand_id` with `brand_mode` set to `existing` when submitting a registration to reuse a brand without re-entering its details. |
| `brands[].id` | Integer | Brand ID. |
| `brands[].brand_name` | String | Organisation name. |
| `brands[].abn` | String | Australian Business Number. |
| `brands[].website` | String | Website URL. |
| `partners` | Array | Saved on-behalf-of organisations. Pass the `id` as `partner_id` when submitting a registration on behalf of a previously used client. |
| `partners[].id` | Integer | Partner ID. |
| `partners[].partner_name` | String | Organisation name. |
| `partners[].abn` | String | Australian Business Number. |
| `partners[].website` | String | Website URL. |

#### ACMA status values

| Status | Meaning |
|---|---|
| `pending_internal_review` | Submitted and waiting for Mobile Message to review before forwarding to ACMA. |
| `pending_email_verification` | Approved by Mobile Message; waiting for identity verification to be completed. |
| `pending_acma_submission` | Verification complete; queued for submission to ACMA. |
| `pending_acma` | Submitted to ACMA and awaiting their decision. |
| `submitted_wait` | Submitted to ACMA; processing in progress. |
| `verified` | Approved by ACMA. The Sender ID is active and can be used for sending. |
| `rejected_internal` | Rejected during Mobile Message review. Contact support for details. |
| `acma_error` | An error occurred during ACMA submission. Contact support. |
| `null` | No ACMA registration has been submitted for this sender (legacy senders created before ACMA registration was required). |

#### Response fields (sender lookup)

When the `sender` query parameter is provided:

| Field | Type | Description |
|---|---|---|
| `sender` | String | The Sender ID text. |
| `sender_id` | Integer | Internal sender ID. |
| `acma_status` | String or null | ACMA registration status (see values above). |
| `brand_id` | Integer or null | Associated brand ID. |
| `chatbot_id` | Integer or null | Associated ACMA chatbot record ID. |
| `partner_id` | Integer or null | Associated partner ID. |

Returns HTTP 404 if the sender is not found on your account.

### POST /v1/acma-registration

Submit a new custom Sender ID for ACMA registration.

#### Sender ID fields

| Field | Type | Description |
|---|---|---|
| `sender` | String | The alphanumeric Sender ID to register. 1-11 characters, printable ASCII only, cannot be only numbers. |
| `sender_id` (optional) | Integer | An existing `sender_id` from your account to re-register (e.g. after a previous rejection). If omitted, a new sender record is created. |
| `sender_id_relation_type` | String | How the Sender ID relates to the organisation. One of: `registered_company_name`, `registered_business_name`, `registered_trade_mark`, `registered_domain_name`. |
| `sender_id_relation_detail` | String | Required unless `sender_id_relation_type` is `registered_company_name`. The value depends on the relation type: for `registered_business_name`, the registered business name as it appears on the ABR for the ABN provided (the Sender ID must contain this name or a recognisable abbreviation); for `registered_trade_mark`, the trademark name exactly as registered with IP Australia; for `registered_domain_name`, the `.au` domain name (e.g. `example.com.au`) where the domain registrant must match the ABN provided. |

#### Contact fields (always required)

The contact person at the brand/organisation that the Sender ID belongs to. When registering on behalf of a client, these should be the client's contact details, not yours (your details go in the on-behalf-of fields below).

| Field | Type | Description |
|---|---|---|
| `contact_first_name` | String | Contact first name at the brand/organisation. |
| `contact_last_name` | String | Contact last name at the brand/organisation. |
| `contact_email` | String | Contact email address at the brand/organisation. |

#### Brand / organisation fields

You can either reuse an existing brand or provide details for a new one.

| Field | Type | Description |
|---|---|---|
| `brand_mode` | String | `new` (default) or `existing`. Set to `existing` to reuse a previously saved brand by its `brand_id`. |
| `brand_id` | Integer | Required when `brand_mode` is `existing`. The brand ID from GET /v1/acma-registration. |
| `abn` | String | Australian Business Number (11 digits, spaces allowed). Required when `brand_mode` is `new`. |
| `business_name` | String | Registered business name. Required when `brand_mode` is `new`. |
| `address_line1` | String | Business street address. Required when `brand_mode` is `new`. |
| `address_line2` (optional) | String | Additional address line. |
| `suburb` | String | Suburb or city. Required when `brand_mode` is `new`. |
| `state` | String | Australian state or territory: `ACT`, `NSW`, `NT`, `QLD`, `SA`, `TAS`, `VIC`, or `WA`. Required when `brand_mode` is `new`. |
| `postcode` | String | Postcode. Required when `brand_mode` is `new`. |
| `website` | String | Business website URL. Required when `brand_mode` is `new`. |
| `business_phone` | String | Business phone number. Required when `brand_mode` is `new`. |

#### On-behalf-of fields (registering on behalf of a client)

Set `on_behalf` to `true` when you are registering a Sender ID for a business that is not your own. This is common for marketing agencies, IT service providers, or any business that sends SMS on behalf of their clients.

When registering on behalf of a client, the **Brand / organisation fields** above should contain the details of the **client** -- the business whose name will appear as the Sender ID. The **on-behalf-of fields** below should contain **your** business details as the applicant performing the registration.

For example, if your marketing agency "Acme Marketing" is registering the Sender ID "PizzaCo" for your client "Pizza Company Pty Ltd", the brand fields would contain Pizza Company's ABN, address and contact details, and the on-behalf-of fields would contain Acme Marketing's details.

You can reuse a previously saved partner by passing `partner_id` from the GET response instead of providing all applicant fields again.

| Field | Type | Description |
|---|---|---|
| `on_behalf` | Boolean | Set to `true` if registering on behalf of another business. Defaults to `false`. |
| `partner_id` (optional) | Integer | Reuse an existing partner by ID from GET /v1/acma-registration. If provided, the applicant fields below are not required. |
| `applicant_abn` | String | Applicant's ABN (11 digits). Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_name` | String | Applicant's organisation name. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_contact_first_name` | String | Applicant contact first name. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_contact_last_name` | String | Applicant contact last name. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_contact_email` | String | Applicant contact email. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_website` | String | Applicant's website URL. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_phone` | String | Applicant's business phone number (max 15 characters). Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_address_line1` | String | Applicant's street address. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_address_line2` (optional) | String | Additional address line. |
| `applicant_suburb` | String | Applicant's suburb or city. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_state` | String | Applicant's state or territory. Required when `on_behalf` is `true` and no `partner_id`. |
| `applicant_postcode` | String | Applicant's postcode. Required when `on_behalf` is `true` and no `partner_id`. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `message` | String | Confirmation message. |
| `sender_id` | Integer | Internal sender ID. |
| `brand_id` | Integer | Brand ID used or created. |
| `chatbot_id` | Integer | ACMA chatbot record ID. |
| `partner_id` | Integer or null | Partner ID (if on-behalf-of), or null. |
| `acma_status` | String | Initial status: `"pending_internal_review"`. |

#### Error responses

- **400** -- Validation error. A required field is missing or invalid. The `error` field describes the issue.
- **404** -- The specified `brand_id`, `partner_id`, or `sender_id` was not found on your account.
- **409** -- This Sender ID is already registered or has a pending ACMA submission. Contact support to make changes.

---

## Purchases

An API key can buy SMS credits and dedicated numbers, charged to a card saved on your account. Purchases are off for every key until you turn them on.

### Turning purchases on

In your dashboard, go to **Settings > API** and choose **Purchases** on the key. Each key has its own setting and its own daily spending limit. Only users who can manage billing can turn purchases on or raise a limit. The account owner is emailed when purchases are turned on for a key or a limit is raised. When a purchase goes through, its tax invoice goes to the billing notification email on the account and names the key. If no billing email is set, or the payment is held for review, the account owner is emailed about the purchase instead.

### Which card is charged

Cards are saved under **Settings > Billing**. If your account has one saved card, purchases use it. If it has more than one, include `card_last4` with the last 4 digits of the card to charge. A request that needs `card_last4` and leaves it out is answered with the list of saved cards (brand, last 4 digits and expiry) so you can choose one.

### Daily spending limit

Each key has a daily spending limit in AUD, including GST. It starts at $500 and can be changed on the same screen. A purchase that would take the key's spending over the limit in any 24 hours is declined with `daily_cap_exceeded`, and the response includes how much is left.

### Safe retries with an Idempotency-Key

Send an `Idempotency-Key` header with a new unique value, such as a UUID, for each purchase. If a request times out or the connection drops, send it again with the same key and the same body. You get the result of the original purchase and the card is not charged a second time. Without the header, every request is treated as a new purchase. Keys are kept for 24 hours.

### Payment reviews

Some payments are reviewed before credits are added. While any payment on your account is being reviewed, purchases through the API are paused and return `payment_in_review`.

### Purchase errors

Purchase errors include a `code` alongside the usual `error` message, so your integration can handle each case.

| Code | HTTP | Meaning |
|---|---|---|
| `purchases_disabled` | 403 | Purchases are turned off for this API key. |
| `test_account` | 403 | Purchases are not available on test accounts. |
| `payment_in_review` | 409 | A payment on the account is being reviewed. Try again once the review is finished. |
| `no_saved_card` | 402 | The account has no saved card. |
| `card_last4_required` | 400 | The account has more than one saved card. The response lists them in `cards`. |
| `card_not_found` | 400 | No saved card ends in the given `card_last4`. |
| `card_ambiguous` | 409 | More than one saved card ends in the given `card_last4`. |
| `invalid_card_last4` | 400 | `card_last4` is not 4 digits. |
| `daily_cap_exceeded` | 402 | The purchase would take the key over its daily spending limit. Includes `daily_cap` and `remaining`. |
| `card_declined` | 402 | The card issuer declined the payment. May include `decline_code`. |
| `authentication_required` | 402 | The card issuer wants the cardholder to approve this payment. Make the purchase in your dashboard instead. |
| `payment_incomplete` | 402 | The payment could not be completed without the cardholder. Make the purchase in your dashboard instead. |
| `purchase_in_progress` | 409 | Another purchase on the account is being processed. Try again in a moment. |
| `previous_purchase_processing` | 409 | An earlier credit purchase on the account is still being recorded. Try again in a moment. |
| `idempotency_conflict` | 409 | The `Idempotency-Key` was already used for a different purchase. |
| `payment_error` | 502 | Our payment provider could not be reached. Check your balance or numbers before trying again, or retry with the same `Idempotency-Key`. |
| `purchases_unavailable` | 503 | Purchases are temporarily unavailable. |

Reusing an `Idempotency-Key` with a different request body returns HTTP 422.

---

## Buy SMS Credits

**POST** `/v1/credit-purchases`

Buy SMS credits with your saved card. Credits are priced at your account's current rate, which is the same price you see when adding credits in your dashboard.

| Field | Type | Description |
|---|---|---|
| `credits` | Integer | The number of credits to buy. The minimum is 500, or your account's minimum if it has custom pricing. |
| `card_last4` (optional) | String | Last 4 digits of the saved card to charge. Required when the account has more than one saved card. |

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `credited`, `pending_review`, `processing` or `not_credited` (see below). |
| `credits` | Integer | Credits bought. |
| `amount` | String | Amount charged in AUD, including GST. |
| `currency` | String | Always `AUD`. |
| `card_last4` | String | Last 4 digits of the card charged. |
| `payment_intent_id` | String | Payment reference. |
| `credit_balance` | Integer | Your new balance. Only with `credited`. |
| `message` | String | Explanation, with `pending_review`, `processing` and `not_credited`. |

| status | HTTP | Meaning |
|---|---|---|
| `credited` | 201 | The credits were added. |
| `pending_review` | 202 | The payment went through and is being reviewed. The credits are added when the review is finished, and the account owner is emailed. |
| `processing` | 202 | The payment went through and the credits are being added. Check `GET /v1/account` for the new balance, or retry with the same `Idempotency-Key` for the final status. |
| `not_credited` | 200 | Only returned for a retry of an earlier purchase that was reviewed and not approved. No credits were added. |

Returns HTTP 400 with the code `invalid_credits` if `credits` is below your minimum. See Purchases for the other errors.

---

## Dedicated Numbers

List the dedicated numbers you can buy, then buy one for 12 months with your saved card. It renews yearly on the same card.

Before you buy a number through the API, claim your free dedicated number under **Settings > Sender IDs** in your dashboard. That is where we collect the service address every Australian number needs, and numbers bought through the API use the same address.

### GET /v1/available-numbers

List numbers available to buy, with your account's price. `eligible` shows whether this key can buy one right now, and `ineligible_reason` says why not (`test_account`, `purchases_disabled`, `payment_in_review`, `free_number_not_claimed` or `no_saved_card`). The saved card and daily limit are checked when you buy.

| Parameter | Type | Description |
|---|---|---|
| `limit` (optional) | Integer | How many numbers to return, from 1 to 100. Defaults to 20. |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `price` | String | Price of one number for 12 months, in AUD including GST. |
| `currency` | String | Always `AUD`. |
| `term_months` | Integer | Always `12`. |
| `eligible` | Boolean | Whether this key can buy a number right now. |
| `ineligible_reason` | String | Why not, when `eligible` is false. |
| `results[].number` | String | The number. |
| `results[].previously_yours` | Boolean | `true` for a number your account had before and let lapse. Buying it again restores it. |

### POST /v1/dedicated-numbers

Buy a dedicated number. If you leave out `number`, the next available number is assigned. A number that was previously yours is only restored when you name it.

| Field | Type | Description |
|---|---|---|
| `number` (optional) | String | A number from `GET /v1/available-numbers`. |
| `label` (optional) | String | A label for the number in your account, up to 50 characters. |
| `card_last4` (optional) | String | Last 4 digits of the saved card to charge. Required when the account has more than one saved card. |

#### Response fields

Returns HTTP 201 when the number is on your account and ready to send from. The number also appears in `GET /v1/senders`.

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `number` | String | The number now on your account. |
| `label` | String | Its label. |
| `renew_date` | String | Renewal date (YYYY-MM-DD). |
| `amount` | String | Amount charged in AUD, including GST. |
| `currency` | String | Always `AUD`. |
| `card_last4` | String | Last 4 digits of the card charged. |
| `payment_intent_id` | String | Payment reference. |

#### Error responses

| Code | HTTP | Meaning |
|---|---|---|
| `free_number_not_claimed` | 403 | Claim your free dedicated number in your dashboard first. |
| `invalid_number` | 400 | `number` is not a valid Australian mobile number. |
| `number_unavailable` | 404 or 409 | The number you named is not available to buy. |
| `no_numbers_available` | 409 | No numbers are available right now. |
| `number_taken_after_payment` | 409 | The number you named went to another customer as your payment completed. The response includes `charged: true`, and our team will contact you to arrange another number or a refund. |

See Purchases for the card, limit and payment errors.

---

## Inbound SMS

**GET** `/v1/inbound`

Retrieve paginated inbound SMS messages and opt-out replies received on your numbers. For real-time notification of inbound messages, configure a webhook.

### Parameters

| Parameter | Type | Description |
|---|---|---|
| `from` (optional) | String | Filter messages received on or after this date (YYYY-MM-DD, UTC). |
| `to` (optional) | String | Filter messages received before this date (YYYY-MM-DD, UTC). |
| `limit` (optional) | Integer | Results per page. Default 50, max 200. |
| `offset` (optional) | Integer | Pagination offset. Default 0. |

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `total` | Integer | Total inbound messages. |
| `limit` | Integer | Current page size. |
| `offset` | Integer | Current offset. |
| `results` | Array | Inbound message records. |
| `results[].message_id` | String | UUID of the inbound message. |
| `results[].from` | String | Sender's phone number. |
| `results[].to` | String | Your number that received the message. |
| `results[].message` | String | Message content. |
| `results[].type` | String | `"inbound"`, or `"unsubscribe"` when the reply was treated as an opt-out. |
| `results[].received_at` | String | UTC timestamp when received. |
| `results[].original_message_id` | String | The UUID of the original outbound message. Empty if no matching outbound message was found. |
| `results[].original_custom_ref` | String | Your custom reference for the original message. Empty if none was provided. |

---

## Webhooks (Delivery Receipts & Inbound Messages)

Configure webhooks to receive real-time notifications for inbound messages and delivery receipts.

### GET /v1/webhooks

Retrieve your current webhook URLs.

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `webhooks` | Object | Webhook configuration. |
| `webhooks.inbound` | String or null | Your inbound webhook URL, or null if not set. |
| `webhooks.status` | String or null | Your status webhook URL, or null if not set. |
| `has_signing_secret` | Boolean | `true` when a webhook signing secret is set on the account, which means every webhook sent is signed. |

### POST /v1/webhooks

Set a webhook URL.

| Field | Type | Description |
|---|---|---|
| `type` | String | Webhook type: `inbound` or `status` (required). |
| `url` | String | The HTTPS URL to receive webhook notifications (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `message` | String | Confirmation message. |
| `type` | String | The webhook type set. |
| `url` | String | The URL that was saved. |

The URL must be 1024 characters or fewer, and must resolve to a public host — URLs pointing at private or internal addresses are rejected with HTTP 400.

### DELETE /v1/webhooks

Remove a webhook URL.

| Field | Type | Description |
|---|---|---|
| `type` | String | Webhook type to remove: `inbound` or `status` (required). |

#### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"complete"` on success. |
| `message` | String | Confirmation message. |
| `type` | String | The webhook type removed. |

### Webhook URL Setup

Set your Inbound and/or Status URL in your account settings (or via the API above). These URLs will receive POST requests with a JSON payload when an event occurs.

### Webhook Payload Structure

#### Inbound webhook payload fields

| Field | Type | Description |
|---|---|---|
| `to` | String | Recipient's phone number. |
| `message` | String | Content of the SMS. |
| `sender` | String | The sender's ID or phone number. |
| `received_at` | String | UTC timestamp when the event was received. |
| `type` | String | Either `"inbound"` or `"unsubscribe"`. |
| `original_message_id` | String | The UUID of the original outbound message. Empty if no matching outbound message was found. |
| `original_custom_ref` | String | Your custom reference for the original message. Empty if none was provided. |

#### Matching a reply to the message it answers

`original_message_id` is the same value returned as `results[].message_id` when you sent the message.

Every recipient gets its own `message_id`. A send request with ten recipients returns ten results, each with a different `message_id`, even when the message text is identical and they all belong to the same job, order or campaign in your system. Store each `message_id` against the recipient it was returned for. Keeping a single ID per job means later recipients overwrite earlier ones and their replies stop matching.

A reply is matched on the two phone numbers involved. We link it to the most recent message sent to that mobile number from the number they replied to. If you send the same person several messages from the same sender, their reply matches the latest one.

To match on your own identifier instead, set `custom_ref` when you send. It is returned on the reply as `original_custom_ref`, and unlike `message_id` you control the value, so it can carry your own job or order number across every recipient of that job. Those messages can also be retrieved later with `GET /v1/messages?custom_ref=YOUR_REF`, which returns each recipient along with its `message_id`.

#### Status webhook payload fields

| Field | Type | Description |
|---|---|---|
| `to` | String | Recipient's phone number. |
| `message` | String | Content of the SMS. |
| `sender` | String | The sender's ID or phone number. |
| `received_at` | String | UTC timestamp when the event was received. |
| `status` | String | Either `"delivered"` or `"failed"`. |
| `message_id` | String | The unique message ID. |
| `custom_ref` | String | Your custom reference (if provided) for the outbound message. |
| `part_number` | Number | The part number for this delivery receipt. For non-concatenated messages this will be `1`. |
| `total_parts` | Number | Total number of parts for the outbound message. For non-concatenated messages this will be `1`. |

### Multipart message behaviour

If an outbound SMS is longer than 160 characters (or 70 characters for Unicode messages), it is sent as multiple message parts to the carrier networks. You should expect multiple status webhooks for that single send request, one per part.

### Webhook delivery, timeouts and retries

Webhooks are delivered as HTTP POST requests with a 5 second connection timeout and a 5 second response timeout. Any 2xx response marks the webhook as delivered. Any other response, or a timeout, schedules a retry.

- Up to 10 attempts are made per event.
- Retries back off exponentially: 1 minute after the first failure, then 2, 4, 8, 16 and 32 minutes, capping at 60 minutes between later attempts (each delay includes a small random jitter). An endpoint that stays unreachable is retried for roughly 4 hours in total before the event is marked failed.
- Each attempt is a fresh request. With webhook signing enabled, the `X-MM-Timestamp` and `X-MM-Signature` headers are regenerated for every attempt, so retried deliveries always pass timestamp-freshness checks.
- Webhooks are not guaranteed to arrive in order. Use the payload fields (`received_at`, and `part_number`/`total_parts` on status webhooks) rather than arrival order.
- Respond quickly and process asynchronously: a response slower than 5 seconds counts as a failed attempt and will be retried, which can deliver the same event more than once. Make your handler idempotent, for example on `message_id` plus `part_number` for status webhooks.

### Verifying webhook signatures (optional)

Webhook signing is optional. You generate a webhook signing secret in your account under Settings then API, in the Webhook Signing Secret section. Once a secret exists, every inbound and status webhook POST carries two extra headers so you can confirm the request came from Mobile Message. Accounts with no signing secret keep receiving webhooks exactly as before, with no extra headers.

#### Signature headers

| Header | Description |
|---|---|
| `X-MM-Timestamp` | The unix timestamp in seconds at the moment the request was signed. |
| `X-MM-Signature` | Lowercase hex HMAC-SHA256 of the signing string, keyed with the signing secret. |

#### Signing string

The signing string is the timestamp, a full stop, then the raw request body.

```
{timestamp}.{raw_body}
```

`{timestamp}` is the value of the `X-MM-Timestamp` header and `{raw_body}` is the exact body bytes that were sent, read before any JSON parsing. Parsing and re-encoding the JSON can change whitespace or key order and will produce a different signature, so always capture the raw body first. Recompute the HMAC with the secret and compare it to `X-MM-Signature` with a timing-safe comparison, then reject the request if it does not match.

Also reject any request where `X-MM-Timestamp` is more than 5 minutes away from your own clock, so an old request cannot be replayed later. Signatures are recomputed fresh on every retry attempt, so a retried webhook always arrives with a current timestamp and a matching signature.

#### PHP verification example

```php
$timestamp = $_SERVER['HTTP_X_MM_TIMESTAMP'] ?? '';
$signature = $_SERVER['HTTP_X_MM_SIGNATURE'] ?? '';
$rawBody   = file_get_contents('php://input');

if ($timestamp === '' || abs(time() - (int) $timestamp) > 300) {
    http_response_code(400);
    exit('Stale timestamp');
}

$expected = hash_hmac('sha256', $timestamp . '.' . $rawBody, $secret);

if (!hash_equals($expected, $signature)) {
    http_response_code(401);
    exit('Invalid signature');
}
```

#### Test vector

With the secret `abc123`, the timestamp `1754640000` and the raw body `{"test":1}`, the signing string is `1754640000.{"test":1}` and the signature is `52344b9592722e0241d82036e0920f4286bc0d47ba4624c5a1588193490a1efb`.

---

## Testing Your Integration

Every new account includes 50 free credits, so you can test against the live API with real messages straight away.

If you'd rather test without delivering to handsets or using credits, create a sandbox under Settings > API in the web app. A sandbox is a separate test account with its own API keys and webhooks, and you switch between it and your live account from the menu under your name. Anyone on your account who can manage API settings can create and use it. If you don't have an account yet, for example because you're reviewing an integration, email hello@mobilemessage.com.au and we'll set up a test account for you.

A sandbox (test account) behaves exactly like a live account at the API level, with sending disconnected:

- Requests are validated and answered exactly as on a live account: you receive real `message_id` values, per-message results, and cost calculations in responses.
- No messages are ever delivered to handsets, and no credits are consumed.
- Accepted messages are recorded with a final status, so message lookup flows can be exercised end to end. Scheduled messages are released at their scheduled time and given their final status the same way.
- Delivery receipt webhooks fire for test sends. Around 5 seconds after a message is accepted, a simulated delivery receipt is posted to the status webhook URL with the same payload as a real one, one webhook per message part. If the account has a signing secret, simulated webhooks are signed exactly like real ones.
- To test failure handling, send to any number ending in `000` (for example `0412345000`). That message is recorded as `failed` and its delivery receipt reports `failed`. All other test messages are reported as `delivered`.
- Inbound messages and inbound webhooks can be simulated with the test inbound endpoint below.
- Messages are sent from the sandbox number `61491570006` (0491 570 006), a number reserved for fiction that never belongs to a real phone.
- Short links in sandbox messages open a test page instead of the destination website. Clicks are still recorded, so link tracking can be tested.
- A sandbox accepts up to 10,000 messages per day (resetting at midnight UTC). Requests over the limit return `429`.
- Contacts, lists, unsubscribes, idempotency keys and the other account endpoints behave exactly as live.
- ACMA sender ID registration is not available on test accounts. Registrations are lodged with the real ACMA register, so `POST /v1/acma-registration` returns `403` on a test account and can only be used from a live account.
- Purchases are not available on test accounts. `POST /v1/credit-purchases` and `POST /v1/dedicated-numbers` return `403` with code `test_account`.

This makes a test account safe for CI: integration tests can run against the real API with no risk of live SMS traffic or charges.

### Simulating Inbound Messages

Test accounts can simulate a reply with `POST /v1/test-inbound`. This records the inbound message on the account exactly as a real reply would, including opt-out processing when the message starts with STOP, and posts to the account's inbound webhook URL if one is set. The endpoint returns `403` on live accounts and allows up to 100 simulated inbound messages per hour.

| Field | Type | Description |
|---|---|---|
| `from` | String | The mobile number the reply comes from (required). |
| `message` | String | The reply content (required). Start it with STOP to simulate an opt-out. |
| `to` | String | Which of the account's sender IDs received the reply (optional). Defaults to the sender of the last message sent to that number. |

Response:

```json
{ "status": "success", "type": "inbound", "to": "YourBrand", "from": "61412345678", "webhook_queued": true }
```

`type` is `unsubscribe` when the message was treated as an opt-out. The inbound webhook payload is identical to a real inbound message, including `original_message_id` and `original_custom_ref` when the reply matches a message previously sent to that number.

## Common API Errors and Solutions

| Error Message | HTTP/Message Code | Description and Solution |
|---|---|---|
| `Unauthorized` | 401 | Invalid API username or password. Check your credentials and ensure they are encoded correctly in your request. |
| `Missing or invalid "messages" parameter.` | 400 | The request body must contain a valid "messages" array. Ensure the JSON structure is correct and the "messages" array is present. |
| `Request body is not valid JSON.` | 400 | The request body could not be parsed as JSON. This is often a shell-quoting issue — for example, the Windows Command Prompt strips the double quotes from the JSON. Send a valid JSON object. |
| `Request body must be a JSON object.` | 400 | The body was valid JSON but not an object (for example a JSON array or a bare value). Wrap your fields in a single JSON object: `{ ... }`. |
| `"<field>" is required.` | 400 | A required field is missing or empty. The `error` message names the exact field — for example `"list_id"`, `"sender"`, or `"message"`. |
| `Invalid phone number format` | error | The phone number provided is incorrectly formatted. Ensure numbers are in either Australian local or international format. |
| `Message content cannot be empty` | error | Your message content is empty. |
| `Invalid sender. You do not have permission to use this sender.` | error | You have attempted to use a sender ID not registered in your account. Register the sender ID first or select an authorised sender. To view your sender IDs, login to your account and click Settings > Sender IDs. |
| `The recipient has unsubscribed and cannot receive messages.` | blocked | The recipient has unsubscribed from your messages. Remove this number from your recipient list or contact the recipient directly. |
| `Message contains non-GSM characters` | error | Your message contains unsupported characters (e.g., emojis). Use standard GSM characters only. |
| `Message exceeds the maximum allowed length.` | error | Reduce your message length. Limits are: GSM-7: 1530 characters (10 parts); UCS-2: 670 characters (10 parts). |
| `Insufficient credits to send the batch of messages.` | 403 | You do not have enough SMS credits for the request. Please add more credits to your account. |
| `Too many concurrent requests. Please wait.` | 429 | Your account has reached the maximum number of simultaneous requests (5). Wait briefly before trying again. |
| `A request with this Idempotency-Key is still being processed. Retry with the same key to get its result.` | 409 | You sent an `Idempotency-Key` again while the original request with that key was still running (usually a large batch). Nothing was sent or charged for this request. Retry with the same key and body to get the original response. |
| `Invalid request method.` | 405 | The HTTP method used is not supported by this endpoint. Check the documentation for the correct method (GET, POST, or DELETE). |
| `Message not found or is not in a scheduled state.` | 404 / 409 | Returned when attempting to cancel a message that does not exist (404) or is not currently scheduled (409). |
| `This phone number already exists in your contacts.` | 409 | Duplicate contact blocked. Your account has duplicate contacts disabled. Use a different number or enable duplicates in your account settings. |
| `You have too many pending verifications.` | 429 | You have reached the limit of 3 pending sender verifications. Wait for existing ones to complete or expire before adding another. |

---

## Simple API

**GET** `/simple/send-sms.php`

When you can't send a JSON body, use the Simple API endpoint. All parameters go in the query string. We recommend using the main /v1/messages endpoint whenever possible and only using this method when a JSON body is not possible.

### Query parameters

| Parameter | Required? | Description |
|---|---|---|
| `api_username` | Yes | Your API username. |
| `api_password` | Yes | Your API password. |
| `sender` | Yes | Your approved sender ID or phone number (for example `61412345678`). Use GET /v1/senders to list your available senders. |
| `to` | Yes | Recipient's phone number in international format (for example `61498765432`). |
| `message` | Yes | URL-encoded SMS text (spaces as `+`, special characters percent-encoded). |
| `custom_ref` | No | Your own tracking reference. |
| `unicode` | No | Set to `true` to allow UCS-2 when needed. If not set, non-GSM characters are stripped and GSM-7 limits apply. |
| `max_parts` | No | Maximum SMS parts (credits). Returns 400 if the message exceeds this limit. Default 10, range 1-99. |
| `ignore_unsubscribes` | No | Set to `true` to bypass unsubscribe blocking for this send. If omitted or set to `false`, normal unsubscribe blocking applies. Use with caution, as bypassing your unsubscribe list could result in spam complaints. |

### Length limits

By default up to 10 parts per message (GSM-7: 1530 septets; UCS-2: 670 characters). Use `max_parts` to lower or raise this limit (1-99).

### Response fields

| Field | Type | Description |
|---|---|---|
| `status` | String | `"success"` on success. |
| `message_id` | String | UUID for tracking. |
| `custom_ref` | String | Your tracking reference (if provided). |
| `to` | String | Recipient number. |
| `sender` | String | Sender ID used. |
| `message` | String | Message content sent. |
| `cost` | Integer | Credits consumed. |
| `ignore_unsubscribes` | Boolean | Whether unsubscribe blocking was bypassed. |
