# ADR-003: Pairing, certificate pinning, authentication, replay protection, and secret storage

- Status: Accepted
- Date: 2026-09-04
- Owner: Claude Opus 5
- Related tasks: T001, T004, T020, T021, T023

## Context

The PC service exposes the only path into a system that can send prompts to coding agents and approve consequential operations. There is no hosted account, relay, or CA: trust must be established locally, out of band, through a QR code shown on the PC and scanned by the phone (`PROJECT_PLAN.md`).

The design must resist a LAN attacker, a machine-local malicious process, a stolen phone, and replay of captured traffic, while keeping the paired-device experience frictionless enough that hold-to-talk stays instant.

## Decision

### 1. PC identity

On first run Core generates a self-signed X.509 certificate:

- Key: ECDSA P-256, generated in the Windows CNG software KSP with `NCRYPT_ALLOW_KEY_EXPORT_FLAG` cleared, so the private key is non-exportable.
- Subject and issuer: `CN=Optimus PC, O=Optimus Voice OS, serialNumber=<random 16 bytes hex>`.
- SANs: every configured trusted address (loopback, the LAN adapter address, the Tailscale address) plus the machine DNS name and the Tailscale MagicDNS name.
- Validity: 10 years. Signature: `ecdsa-with-SHA256`.
- Stored in the current user certificate store under `Cert:\CurrentUser\My`, thumbprint recorded in Core configuration.

**PC identity is the SHA-256 of the certificate SubjectPublicKeyInfo (SPKI)**, rendered as base64url. Pinning the SPKI rather than the whole certificate lets Core re-issue a certificate with new SANs, for example when the Tailscale address changes, without re-pairing.

Certificate rotation with a *new key* is treated as a new PC identity and requires re-pairing. The tray exposes an explicit "Rotate identity" action that revokes all device records first, so an unpaired device cannot linger against a rotated key.

### 2. QR pairing payload

The QR encodes a single URI:

```
optimus://pair?v=1&pid=<ULID>&host=<addr>&port=<port>&alt=<addr,addr>&fp=<base64url-sha256-spki>&psk=<base64url-32B>&exp=<unix-seconds>
```

- `pid` identifies this pairing attempt and is the dedupe key.
- `fp` is the SPKI pin.
- `psk` is 32 bytes from a CSPRNG, valid for **120 seconds**, single use.
- `alt` lists additional reachable addresses so the phone can pair on LAN and later reconnect over Tailscale without a second pairing.
- The QR is displayed only in the PC settings window, only while that window has focus, and is erased from memory and screen when the window loses focus, when the pairing succeeds, or at `exp`.
- Only one pairing attempt is outstanding at a time. Generating a new QR invalidates the previous `pid` and `psk`.

Pairing mode also temporarily opens the listener on the configured non-loopback interfaces (section 7); outside pairing mode and outside an existing paired-device connection the service refuses unpaired sources.

### 3. Signature algorithm and TLS client stack

**Every asymmetric signature in Optimus is ECDSA on NIST P-256 with SHA-256**, DER-encoded per X9.62, carried base64url without padding. Public keys are X.509 `SubjectPublicKeyInfo` DER, base64url.

This is a correction to an earlier draft that specified Ed25519. Ed25519 is not implementable on either side of this product: the `AndroidKeyStore` provider does not offer Ed25519 through `KeyPairGenerator`, so an Ed25519 key could not be hardware-backed or biometric-bound, and .NET 8 has no Ed25519 primitive in the framework. P-256 is offered by `AndroidKeyStore` (including StrongBox on supported devices) and by .NET `ECDsa` over CNG, so the same algorithm works on both ends with no third-party cryptography dependency.

Signature verification must reject non-canonical DER and must not accept a signature whose `s` value is in the upper half of the curve order (low-S normalization), so a signature cannot be trivially mutated into a second valid encoding.

**TLS client stack.** The mandated Android client is **OkHttp 4.12.0**, used for both the pairing request and the WebSocket, configured with:

- A custom `X509TrustManager` whose `checkServerTrusted` computes `SHA-256(SubjectPublicKeyInfo)` of the leaf certificate and compares it in constant time against the stored pin, ignoring chain and CA trust anchors entirely.
- A `HostnameVerifier` that returns true only when the pin check for that session already succeeded.
- `ConnectionSpec.RESTRICTED_TLS` narrowed to TLS 1.3.

OkHttp's `CertificatePinner` is **not** used: it applies pins only after a successful chain validation, which a self-signed certificate cannot pass. Fixing the stack here means T021 makes no TLS integration decision.

**No TLS keying-material export (RFC 5705) is used anywhere in this protocol.** An earlier draft bound signatures to a TLS exporter value; that is not reachable from the Android WebSocket stack without owning a Conscrypt socket, which is a different dependency and integration decision that T020 and T021 cannot infer. Connection binding is instead provided by an explicit, single-use **server challenge**, which every TLS stack can carry, which is fully specified below, and which can be exercised by protocol vectors.

### 4. Pairing handshake

The phone opens the TLS connection to `host:port` and validates the leaf by SPKI pin as described above. The `/v1/pair` exchange is:

1. Core sends `PairChallenge { serverChallenge (base64url 32B), serverTimeUtc }` immediately after the connection opens. The challenge is CSPRNG-generated, bound to this connection, single-use, and discarded when the connection closes or after 30 s.
2. The phone replies:

```
PairRequest {
  pairingId, deviceName, platform,
  devicePublicKey   (P-256 SPKI DER, base64url),
  approvalPublicKey (P-256 SPKI DER, base64url),
  clientNonce (base64url 16B),
  serverChallenge (echoed),
  proof (base64url 32B)
}
proof = HMAC-SHA256(psk, canonical("optimus-pair-v1",
          pairingId, devicePublicKey, approvalPublicKey, clientNonce, serverChallenge, spkiFingerprint))
```

`canonical(...)` is the canonical byte encoding defined in `docs/specs/WIRE_PROTOCOL.md` section 3, so the proof does not depend on JSON key order or whitespace.

Core verifies, in this order, and on any failure replies `PairResult { accepted: false, reason }` with a generic reason and burns the attempt:

1. `pairingId` matches the outstanding attempt and has not been used.
2. `exp` has not passed.
3. The echoed `serverChallenge` equals the outstanding challenge for this connection, compared in constant time, and has not already been consumed.
4. `proof` matches, compared in constant time.
5. Both public keys decode as valid P-256 points on the curve, are not the point at infinity, and are not already registered.

Check 3 is what binds the proof to this connection: a `PairRequest` captured on one connection carries a challenge that no other connection will ever issue.

On success Core creates a device record, burns the `psk`, the `pairingId`, and the challenge, and replies:

```
PairResult {
  accepted: true, deviceId, pcName, spkiFingerprint,
  serverPublicKey (P-256 SPKI DER, base64url), serverNonce,
  signature = ECDSA-P256-SHA256(K_server_sign, canonical("optimus-pair-result-v1",
                deviceId, devicePublicKey, spkiFingerprint, serverNonce, serverChallenge))
}
```

The phone verifies the signature against `serverPublicKey`, then stores `deviceId`, `serverPublicKey`, and `spkiFingerprint`.

The PC displays the new device name and a 6-character verification code derived as the first 30 bits of `SHA-256("optimus-verify-v1" || deviceId || spkiFingerprint || devicePublicKey)` rendered in Crockford base32; the phone displays the same code. The user confirms the codes match on both surfaces before the device becomes active. This defeats an attacker who obtains the QR contents out of band and races the real phone, because two devices would show different codes and the real user would see an unexpected code on the PC.

Device record fields: `deviceId`, `deviceName`, `devicePublicKey`, `approvalPublicKey`, `platform`, `pairedAtUtc`, `lastSeenUtc`, `revoked`, `revokedAtUtc`.

### 5. Rate limits and lockout

| Control | Limit |
| --- | --- |
| Pairing attempts per outstanding `pid` | 3, then the attempt is burned |
| Pairing attempts per source address | 5 per 10 minutes |
| Failed `Hello` authentications per source address | 10 per minute, then a 10-minute block |
| Concurrent pairing attempts | 1 |
| TLS handshakes per source address | 30 per minute |

Blocks are recorded in memory only and cleared on Core restart, which is acceptable because the underlying secrets are single-use and short-lived.

### 6. Per-connection authentication and replay protection

Every connection begins with a server challenge, issued before `Hello` (ADR-002 section 1):

1. Core sends `Challenge { serverChallenge (base64url 32B), serverTimeUtc }` as the first frame after the WebSocket opens. It is CSPRNG-generated, held only for this connection, single-use, and expires with the 5 s handshake budget in ADR-002 section 5.
2. The device replies:

```
Hello.auth {
  deviceId,
  timestampUtc,
  clientNonce (base64url 16B),
  serverChallenge (echoed),
  signature = ECDSA-P256-SHA256(K_device, canonical("optimus-hello-v1",
                deviceId, protocolMajor, timestampUtc, clientNonce, serverChallenge, spkiFingerprint))
}
```

Core accepts only if all hold:

1. The device record exists and `revoked` is false.
2. The echoed `serverChallenge` equals the outstanding challenge for this connection, compared in constant time, and has not already been consumed.
3. The signature verifies against the stored `devicePublicKey`, with the DER and low-S rules from section 3.
4. `abs(now - timestampUtc) <= 60 s`.
5. `clientNonce` has not been seen for this `deviceId` within the last 5 minutes.

Property 2 is the primary replay defence. A captured `Hello` carries a challenge that Core issued once, for one connection, and consumed; it can never be presented again, on that connection or any other. Properties 4 and 5 bound the window further and give a precise rejection reason. Nothing here depends on TLS keying-material export, so the whole exchange is reproducible in a protocol vector and testable without a live TLS stack.

**Challenge storage.** Outstanding challenges live in a per-connection slot, not a shared table, so there is no cross-connection lookup and no unbounded growth. Consumed `clientNonce` values live in a per-device bounded set of 512 entries with a 5-minute expiry; eviction is by age, and a full set for a device within the window is itself a `RATE_LIMITED` condition rather than a silent overwrite.

`Welcome.serverAuth` carries `serverNonce` and `ECDSA-P256-SHA256(K_server_sign, canonical("optimus-welcome-v1", deviceId, clientNonce, serverNonce, serverChallenge))`, so the device confirms it is talking to the paired PC and not a machine that merely holds the certificate.

The connection is bound to the device for its lifetime. `deviceId` is never accepted from any later message; Core always uses the authenticated identity of the connection.

### 7. Interface binding and the local Shell client

Kestrel binds to:

- `127.0.0.1:<port>` always.
- Each explicitly configured trusted interface address, and only while at least one device is paired or pairing mode is active. Default configuration is loopback only.

Optimus never binds `0.0.0.0`. Changing the bound interface set is a settings action that names the exact addresses, and Core logs the change to the local event history.

Requests arriving on a non-loopback interface from an address that is neither in the configured allow list nor a Tailscale peer address are refused before TLS negotiation completes where possible, and otherwise closed with `4403`.

The desktop Shell authenticates with a **local capability token**:

- 32 random bytes, generated at each Core start, written to `%LOCALAPPDATA%\Optimus\runtime\shell.token` with a DACL granting read access only to the current user SID, and deleted on clean shutdown.
- The Shell reads it and presents it in `Hello.auth.localToken` instead of a device signature, over the loopback connection only. A `localToken` presented on a non-loopback connection is an immediate `4401`.
- Core additionally verifies the peer is loopback and that the connecting process runs as the same user SID (via `GetExtendedTcpTable` owner lookup).
- The token grants a device identity of `local-shell` with the same authority as a paired phone, no more.

This is deliberately not stronger than the machine boundary: a malicious process running as the same user can already read DPAPI-protected data and inject into the Shell. That threat is addressed in `docs/security/THREAT_MODEL.md` under local malware, not here.

### 8. Secret storage

**PC.** All secrets live under `%LOCALAPPDATA%\Optimus\secrets\`, with a directory DACL granting only the current user SID and `SYSTEM`:

| Secret | Storage |
| --- | --- |
| Certificate private key | Windows CNG, non-exportable, `Cert:\CurrentUser\My` |
| `K_server_sign` (P-256 identity signing key) | Windows CNG, non-exportable, persisted key container `Optimus.ServerSign.v1`, DACL restricted to the current user SID |
| Device records, including device public keys | `ProtectedData.Protect(scope: CurrentUser, optionalEntropy: SHA-256("optimus-secrets-v1" || userSid))`, one JSON document |
| `K_conf` (confirmation MAC key) | **Memory only**, regenerated at every Core start, never persisted, zeroed on shutdown |
| Pairing `psk`, `serverChallenge` values | Memory only, zeroed on use or expiry |

`K_server_sign` lives in a CNG key container rather than a DPAPI-sealed byte array so the private key is never present in Optimus process memory in raw form. Device records are not key material and are sealed with DPAPI.

Rationale for the memory-only confirmation key: a confirmation must not survive a Core restart, because the user cannot have observed the draft that a pre-restart confirmation refers to in the restarted UI. Losing outstanding confirmations on restart is a feature.

**Optimus stores no provider credentials.** This is a hard boundary, not a default:

- Adapters invoke a provider CLI that the user has already authenticated in that provider's own tool, and authentication material stays in the provider's own credential store.
- Optimus must not read, copy, transform, persist, inject, or display provider tokens, cookies, API keys, or session files, and must not pass them on a command line or in an environment variable it constructs.
- When a provider reports that it is not authenticated, the adapter returns `AuthRequired`, Core surfaces `PROVIDER_AUTH_REQUIRED`, and the user is told to authenticate in the provider's own tool. Optimus never offers to collect the credential.
- This keeps the `PROJECT_PLAN.md` boundary that agent cloud behavior is unchanged, and it keeps provider account compromise off the list of things an Optimus defect can cause.

**Android.** Two hardware-backed **ECDSA P-256** keys in Android Keystore, `StrongBox` when `PackageManager.hasSystemFeature(FEATURE_STRONGBOX_KEYSTORE)` reports it and the TEE otherwise. Generation uses `KeyPairGenerator.getInstance("EC", "AndroidKeyStore")` with `setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))` and `setDigests(DIGEST_SHA256)`:

| Key alias | Purpose | Authentication policy |
| --- | --- | --- |
| `optimus_device_v1` | Signs `Hello` | `setUserAuthenticationRequired(false)`, `setUnlockedDeviceRequired(true)`. Usable while the device is unlocked. |
| `optimus_approval_v1` | Signs `ApprovalResponse` for `Consequential` operations | `setUserAuthenticationRequired(true)`, `setUserAuthenticationParameters(0, AUTHENTICATORS_BIOMETRIC_STRONG)`, `setInvalidatedByBiometricEnrollment(true)`, `setUnlockedDeviceRequired(true)` |

`setUserAuthenticationParameters(0, ...)` means per-use authentication: every signature requires a fresh `BiometricPrompt` success, and the signature object is obtained from `CryptoObject` bound to that authentication. If StrongBox generation fails with `StrongBoxUnavailableException`, the app retries once against the TEE and records which backing was used; the backing is reported to Core in `Hello` and shown in the PC device list, so the user can see whether a device is StrongBox-backed.

Both keys are non-exportable. Server public key, `spkiFingerprint`, `deviceId`, and endpoint addresses are stored in an `EncryptedSharedPreferences` file keyed by a Keystore AES master key. Nothing sensitive is written to logs, `WorkManager` inputs, or backups: the app sets `android:allowBackup="false"` and `android:dataExtractionRules` excluding all app storage.

**Desktop approval is a local verification result, not a signature.** `Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync` returns a `UserConsentVerificationResult` enumeration. It performs no cryptographic operation over Optimus data and produces no token, so the desktop cannot produce an attestation comparable to the Android one. Calling it from a WPF process additionally requires WinRT interop that supplies the owning window handle.

The honest design is therefore:

- The Shell calls `UserConsentVerifier` for a `Consequential` approval and reports the plain result to Core as `attestationKind: "LocalVerification"` with `verifiedAtUtc` and the `approvalId` it verified.
- Core accepts `LocalVerification` **only** on a loopback connection authenticated by the local capability token (section 7), only for the single pending `approvalId` it names, only within 60 s of `verifiedAtUtc`, and only once.
- Core does not treat `LocalVerification` as cryptographic proof. Its trust is bounded by the same-user boundary, exactly like the capability token, and `docs/security/THREAT_MODEL.md` records that explicitly.
- If the verifier reports `DeviceNotPresent`, `DisabledByPolicy`, or `NotConfiguredForUser`, the Shell reports the failure and Core refuses the approval with `APPROVAL_ATTESTATION_INVALID`; the user is told to approve on a paired phone.
- Policy `approvals.consequentialAttestationPolicy` selects the accepted kinds. `AllowLocalVerification` is the default when no phone is paired. `RequireDeviceSignature` is the default from the moment a phone is paired, and it makes Core refuse desktop `LocalVerification` with `APPROVAL_ATTESTATION_NOT_PERMITTED`, directing the decision to the phone. The setting is shown during pairing and is changeable in tray settings.

### 9. Revocation and unpairing

- Tray settings list every device with name, platform, paired date, and last seen.
- "Revoke" sets `revoked`, closes any live connection with `4403`, drops queued prompts that originated on that device, and invalidates outstanding confirmations and approvals bound to it.
- "Revoke all" additionally rotates `K_server_sign`.
- The phone app offers "Forget PC", which deletes both Keystore keys and the stored server identity.
- A revoked device that reconnects gets `4403` with no information about why, and its `deviceId` is not reusable.

### 10. Logging and diagnostics

Security-relevant events are recorded in the local text event history: pairing shown, pairing accepted or rejected with a coded reason, device revoked, interface binding changed, authentication failure counts, and confirmation or approval rejections with their reason codes. Never recorded: `psk`, tokens, signatures, challenge or nonce values, private keys, provider credentials, transcript text, or draft text.

## Alternatives considered

- **Full certificate pinning instead of SPKI pinning.** Rejected: any SAN change, which is routine when the Tailscale address changes, would force every device to re-pair.
- **Password or PIN pairing.** Rejected: weaker than a 256-bit single-use secret carried by QR, and it adds a typing step on the phone.
- **mTLS with a client certificate per device.** A reasonable alternative, and it also binds to the TLS session. Rejected because Android client-certificate handling in OkHttp plus Keystore-backed keys is more fragile than an application-layer signature, and because the approval key needs an application-layer signature anyway; using one mechanism for both keeps the verification code single-path.
- **Ed25519 device and approval keys.** Rejected on feasibility, not preference: `AndroidKeyStore` does not expose Ed25519 through `KeyPairGenerator`, so the key could be neither hardware-backed nor biometric-bound, and .NET 8 has no Ed25519 primitive. Hardware-backed P-256 is available on both ends and needs no third-party cryptography library.
- **RFC 5705 TLS exporter channel binding.** Rejected on feasibility: reaching exporter bytes from Android requires the application to own a Conscrypt socket and to integrate it through the WebSocket client, which is a materially different dependency decision that neither T020 nor T021 could infer. A single-use server challenge gives the same anti-replay binding, works on any TLS stack, and is exercisable in protocol vectors without a live TLS session.
- **Treating the Windows Hello result as an attestation token.** Rejected as false: `UserConsentVerifier` returns an enumeration and signs nothing. Desktop consequential approval is specified as a local verification result with its trust boundary stated, and the default once a phone is paired is to require the phone.
- **Deriving a long-term shared symmetric key from the pairing `psk`.** Rejected: a symmetric secret must be stored verifiably on both ends, and a compromised PC store would then let an attacker impersonate the phone. Asymmetric device keys keep the phone the only holder of its signing key.
- **A named-pipe local API for the Shell instead of loopback TLS.** Rejected: it reintroduces the second orchestration surface that ADR-001 section 2 removes.

## Consequences

- Re-pairing is required only on a key rotation, not on network changes.
- The verification-code step adds about three seconds to pairing and closes the QR-race attack.
- Consequential approvals cannot be granted from a PC without Windows Hello configured; this is surfaced during setup, not at approval time. Once a phone is paired, the default policy sends consequential approvals to the phone, where the decision is cryptographically bound.
- Optimus holds no provider credentials, so a compromise of Optimus storage cannot yield provider account access. The cost is that Optimus cannot repair a provider authentication problem for the user; it can only report it.
- `K_conf` being memory-only means a Core crash during confirmation costs the user one re-confirmation.
- The local capability token is only as strong as the user account boundary; the threat model states this explicitly as a residual risk.

## Verification

- T020 unit tests: proof and P-256 signature verification vectors, non-canonical DER and high-S signatures rejected, constant-time comparison, expiry, nonce replay within and across connections, challenge mismatch, challenge reuse on the same connection, revoked device, and rate-limit lockout.
- T020 integration test: full pair, verification-code match, connect, revoke, reconnect refused with `4403`.
- T004 test: `localToken` presented on a non-loopback connection is rejected with `4401`.
- T023 instrumented test on the Pixel 9a: `optimus_approval_v1` is generated as a hardware-backed P-256 key, requires a `BiometricPrompt` success per signature through a bound `CryptoObject`, and is invalidated by a new biometric enrolment.
- T004 and T023 tests: `attestationKind: "LocalVerification"` is refused on a non-loopback connection, refused for an `approvalId` other than the one it names, refused after 60 s, refused a second time, and refused entirely with `APPROVAL_ATTESTATION_NOT_PERMITTED` under the `RequireDeviceSignature` policy.
- T016, T017, and T029 audit that no Optimus code path reads, writes, or transports provider credential material, and that no Optimus-owned file contains a provider token.
- T029 release audit checks that no secret path appears in logs, that `allowBackup` is false, and that the listener never binds `0.0.0.0`.

## Related documents

- `docs/security/THREAT_MODEL.md`
- `docs/adr/ADR-002-device-protocol-and-compatibility.md`
- `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
