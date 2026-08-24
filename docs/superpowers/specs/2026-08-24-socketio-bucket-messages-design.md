# Realtime Bucket Messages over Socket.IO — Design

**Date:** 2026-08-24
**Status:** Implemented — `src/XcavateProfileApi/SocketIo/`, tests in
`tests/XcavateBuckets.Tests/SocketIo*`. The API surface is documented in
[README.md](../../../README.md).
**Builds on:** [2026-08-01-push-notifications-design.md](2026-08-01-push-notifications-design.md)
(reuses its `IBucketNotifier` seam and queue/dispatcher pattern).

## Goal

Let a client connect with a **standard socket.io client library** and receive a bucket's new
messages in real time, instead of polling the GraphQL `messages` query. The surface is
deliberately minimal: bucket messages only. Membership changes, namespace events, tag events and
everything else the API knows stay off the socket.

## Why a hand-rolled Socket.IO server

The requirement is the Socket.IO **wire standard**, so that `socket.io-client` (JS),
`socket_io_client` (Dart/Flutter), `python-socketio` etc. work out of the box. .NET has no
maintained Socket.IO *server* implementation (SignalR is its own protocol; the NuGet socket.io
packages are clients). The protocol needed here is small, though: Engine.IO v4 framing over a
websocket (one packet per text frame, first character is the packet type) carrying Socket.IO v5
packets. `SocketIoProtocol` implements exactly that as pure string functions, and
`SocketIoConnection` runs the session state machine over an ASP.NET Core `WebSocket`.

**Websocket transport only.** The socket.io standard starts sessions on HTTP long-polling and
upgrades; supporting that means payload batching, an upgrade probe and per-session HTTP state —
roughly tripling the protocol surface for clients this API does not have (every target platform
supports websockets). Clients therefore must connect with `transports: ["websocket"]`, a
first-class socket.io client option. A polling request gets a 400 whose body says exactly that.

## Endpoint and handshake

- Path: `/socket.io/` (the socket.io default), mounted by `UseBucketSocketIo()` as a branch, so
  it never shadows other routes. Only `EIO=4` + `transport=websocket` upgrade requests are
  accepted; anything else is a 400 in the socket.io error-body shape `{code, message}`.
- Handshake: server sends the Engine.IO open packet
  (`0{"sid":...,"upgrades":[],"pingInterval":25000,"pingTimeout":20000,"maxPayload":1000000}`),
  client connects the default namespace (`40`), server acks (`40{"sid":...}`). Any other
  namespace gets `44…{"message":"Invalid namespace"}` (`connect_error` on the client).
- Heartbeat: server pings (`2`) every `pingInterval`; a client that stays silent past
  `pingInterval + pingTimeout` is dropped. Socket.io clients answer automatically.
- The socket.io `auth` payload on connect is accepted and ignored.

## Event surface

Client → server (both support the socket.io ack callback):

| Event | Argument | Effect |
|---|---|---|
| `subscribe` | bucket id as string, number, or `{"bucketId": ...}` | Start receiving the bucket's messages. Fails with `UNKNOWN_BUCKET` when the bucket does not exist, `INVALID_INPUT` when the argument does not parse. |
| `unsubscribe` | same | Stop. Idempotent — unsubscribing when not subscribed succeeds. |

Ack payload: `{ok: true, bucketId: "5"}` or `{ok: false, bucketId, error: {code, message}}`.
An unknown event acks `{ok: false, error: {code: "UNKNOWN_EVENT"}}`. When no ack was requested,
failures are emitted as a `subscriptionError` event (`{action, bucketId, error}`) instead, so
callback-less clients are not left guessing.

Server → client:

| Event | Payload |
|---|---|
| `message` | The new message, camelCase, field-for-field the GraphQL `Message` type (ids as strings like the `BigInt` scalar, composite `id`, ISO-8601 UTC `createdAt`, nullable fields present as `null`) plus an explicit `bucketId` for demuxing multi-bucket subscriptions. |
| `subscriptionError` | See above. |

## Authorization

None, on purpose: every GraphQL **query** on the bucket schema is public, including `messages`,
so the socket exposes nothing a plain unauthenticated POST could not already read. Message
content is E2E-encrypted by design (the API stores what contributors wrote; `encryptionKey`
gates writing, not reading). If reads ever become gated, the socket must gate with them — that
would need a websocket-specific signature convention, since the existing scheme signs the
request body and a websocket handshake has none.

## Architecture

Mirrors the push-notification pipeline, component for component:

| Piece | Role |
|---|---|
| `SocketIoBucketNotifier` (scoped `IBucketNotifier`) | On `MessageWrittenAsync`, encodes the `42["message",…]` frame **once** and enqueues it. `MemberAddedAsync` is a no-op. Never throws. |
| `SocketIoBroadcastQueue` (singleton) | Bounded `Channel<BucketBroadcast>` (10 000), drop + log when full. |
| `SocketIoBroadcastDispatcher` (`BackgroundService`) | Drains the queue, fans each frame out to the bucket's subscribers. |
| `BucketSubscriptionRegistry` (singleton) | bucket id → subscribers; lock on rare mutations, lock-free concurrent reads on the hot fan-out path. |
| `SocketIoConnection` | Per-session state machine: receive loop, single-writer send loop over a bounded outgoing channel, heartbeat loop. A client that cannot drain its queue is dropped rather than buffered without bound. |
| `SocketIoConnectionHandler` (singleton) | HTTP entry: validates the handshake query, accepts the websocket, runs the connection. Bucket-existence checks create their own DI scope, since connections outlive any request scope. |

`CompositeBucketNotifier` fans the domain's single `IBucketNotifier` slot out to the socket
notifier (always on) and `PushBucketNotifier` (only with `NOTIFICATIONS_API_KEY`). Registered in
`Program.cs` before `AddBucketDomain()` so the domain's `TryAddScoped` `NullBucketNotifier`
default stays out of the way. The domain project is untouched.

## Delivery semantics

Best-effort, at-most-once, no replay. The notifier runs inside the mutation's transaction before
commit (the `IBucketNotifier` contract), so a rolled-back write can in principle produce a
phantom event — same accepted trade-off as push notifications. Slow consumers and full queues
drop frames or connections rather than exert backpressure on mutations. Subscriptions are
per-connection state: after a reconnect the client re-subscribes (socket.io clients auto-
reconnect; re-subscribing in the `connect` handler makes that seamless) and re-fetches anything
missed through the GraphQL `messages` query. No config: the feature has no external backend and
is always on.

## Testing

- `SocketIoProtocolTests` — the wire format against the socket.io v4 spec, pure string
  round-trips.
- `SocketIoRegistryTests` — subscription bookkeeping with a hand-written fake subscriber.
- `SocketIoNotifierTests` — a written `Message` becomes exactly one correctly shaped frame;
  membership events become nothing.
- `SocketIoIntegrationTests` — end to end over `GraphQLHost` (which now runs the websocket
  pipeline and takes optional `SocketIoOptions` for short test heartbeats) with
  `SocketIoTestClient`, a minimal client written from the spec rather than the server code:
  handshake shape, namespace rejection, error acks, signed `write` → `message` event, per-bucket
  filtering, unsubscribe, multi-subscriber fan-out, `subscriptionError`, polling/EIO≠4
  rejection, and both heartbeat outcomes.
- E2E docker tests are unaffected; the GraphQL schema snapshot is untouched (no schema change).
