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

Pairing mode also temporarily opens the listener on the configured non-loopback interfaces (section 6); outside pairing mode and outside an existing paired-device connection the service refuses unpaired sources.

### 3. Pairing handshake

The phone opens the TLS connection to `host:port`, validating **only** the SPKI pin (a self-signed certificate has no chain to validate; hostname verification is replaced by pin verification). It then sends, on the `/v1/pair` endpoint:

```
PairRequest {
  pairingId, deviceName, devicePublicKey (Ed25519, base64url raw 32B),
  approvalPublicKey (Ed25519, base64url raw 32B),
  clientNonce (base64url 16B),
  exporter (base64url 32B),
  proof (base64url 32B)
}
proof = HMAC-SHA256(psk, "optimus-pair-v1" || pairingId || devicePublicKey || approvalPublicKey || clientNonce || exporter || spkiFingerprint)
```

`exporter` is TLS keying material exported per RFC 5705 with label `EXPORTER-optimus-pair-v1`, 32 bytes, empty context. Including it binds the proof to this exact TLS connection, so a proof captured on one connection cannot be presented on another.

Core verifies, in this order, and on any failure replies `PairResult { accepted: false, reason }` with a generic reason and burns the attempt:

1. `pairingId` matches the outstanding attempt and has not been used.
2. `exp` has not passed.
3. `exporter` equals the server-side exported value for this connection.
4. `proof` matches, compared in constant time.
5. The device public keys are valid Ed25519 points and are not already registered.

On success Core creates a device record, burns the `psk` and `pairingId`, and replies:

```
PairResult {
  accepted: true, deviceId, pcName, spkiFingerprint,
  serverPublicKey (Ed25519), serverNonce,
  signature = Ed25519(K_server_sign, "optimus-pair-result-v1" || deviceId || devicePublicKey || spkiFingerprint || serverNonce || exporter)
}
```

The phone verifies the signature against `serverPublicKey`, then stores `deviceId`, `serverPublicKey`, and `spkiFingerprint`.

The PC displays the new device name and a 6-character verification code derived as the first 30 bits of `SHA-256("optimus-verify-v1" || deviceId || spkiFingerprint || devicePublicKey)` rendered in Crockford base32; the phone displays the same code. The user confirms the codes match on both surfaces before the device becomes active. This defeats an attacker who obtains the QR contents out of band and races the real phone, because two devices would show different codes and the real user would see an unexpected code on the PC.

Device record fields: `deviceId`, `deviceName`, `devicePublicKey`, `approvalPublicKey`, `platform`, `pairedAtUtc`, `lastSeenUtc`, `revoked`, `revokedAtUtc`.

### 4. Rate limits and lockout

| Control | Limit |
| --- | --- |
| Pairing attempts per outstanding `pid` | 3, then the attempt is burned |
| Pairing attempts per source address | 5 per 10 minutes |
| Failed `Hello` authentications per source address | 10 per minute, then a 10-minute block |
| Concurrent pairing attempts | 1 |
| TLS handshakes per source address | 30 per minute |

Blocks are recorded in memory only and cleared on Core restart, which is acceptable because the underlying secrets are single-use and short-lived.

### 5. Per-connection authentication and replay protection

Every device connection authenticates in `Hello` (ADR-002 section 1):

```
Hello.auth {
  deviceId,
  timestampUtc,
  clientNonce (base64url 16B),
  exporter (base64url 32B, label "EXPORTER-optimus-v1"),
  signature = Ed25519(K_device, "optimus-hello-v1" || deviceId || protocolMajor || timestampUtc || clientNonce || exporter || spkiFingerprint)
}
```

Core accepts only if all hold:

1. The device record exists and `revoked` is false.
2. The signature verifies against the stored `devicePublicKey`.
3. `exporter` matches the server-exported value for this TLS connection, compared in constant time.
4. `abs(now - timestampUtc) <= 60 s`.
5. `clientNonce` has not been seen for this `deviceId` within the last 5 minutes.

Property 3 is the primary replay defence: a captured `Hello` is bound to a TLS session whose keying material an attacker cannot reproduce. Properties 4 and 5 bound the window further and stop a same-connection replay.

`Welcome` carries the server-side counterpart signed with `K_server_sign` over the same exporter and both nonces, so the device confirms it is talking to the paired PC and not a machine that merely holds the certificate.

The connection is bound to the device for its lifetime. `deviceId` is never accepted from any later message; Core always uses the authenticated identity of the connection.

### 6. Interface binding and the local Shell client

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

### 7. Secret storage

**PC.** All secrets live under `%LOCALAPPDATA%\Optimus\secrets\`, with a directory DACL granting only the current user SID and `SYSTEM`:

| Secret | Storage |
| --- | --- |
| Certificate private key | Windows CNG, non-exportable, `Cert:\CurrentUser\My` |
| `K_server_sign` (Ed25519 identity signing key) | 32 bytes sealed with `ProtectedData.Protect(scope: CurrentUser, optionalEntropy: SHA-256("optimus-secrets-v1" || userSid))` |
| Device records, including device public keys | Same DPAPI envelope, one JSON document |
| Provider credentials or tokens held by adapters | Same DPAPI envelope, one file per provider, never logged |
| `K_conf` (confirmation MAC key) | **Memory only**, regenerated at every Core start, never persisted, zeroed on shutdown |
| Pairing `psk` | Memory only, zeroed on use or expiry |

Rationale for the memory-only confirmation key: a confirmation must not survive a Core restart, because the user cannot have observed the draft that a pre-restart confirmation refers to in the restarted UI. Losing outstanding confirmations on restart is a feature.

**Android.** Two hardware-backed Ed25519 keys in Android Keystore, `StrongBox` when the device reports it and the software-backed TEE otherwise:

| Key alias | Purpose | Authentication policy |
| --- | --- | --- |
| `optimus_device_v1` | Signs `Hello` | `setUserAuthenticationRequired(false)`; usable while the device is unlocked. `setUnlockedDeviceRequired(true)`. |
| `optimus_approval_v1` | Signs `ApprovalResponse` for `Consequential` operations | `setUserAuthenticationRequired(true)`, `setUserAuthenticationParameters(0, AUTHENTICATORS_BIOMETRIC_STRONG)`, `setInvalidatedByBiometricEnrollment(true)`, `setUnlockedDeviceRequired(true)` |

Both keys are non-exportable. Server public key, `spkiFingerprint`, `deviceId`, and endpoint addresses are stored in a `EncryptedSharedPreferences` file keyed by a Keystore AES master key. Nothing sensitive is written to logs, `WorkManager` inputs, or backups: the app sets `android:allowBackup="false"` and `android:dataExtractionRules` excluding all app storage.

**Desktop equivalent of biometric approval.** Consequential approvals on the PC require `Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync`, which uses Windows Hello or the account password. A `Consequential` approval is refused if the verifier reports `DeviceNotPresent` or `DisabledByPolicy`; the user is told to approve on a paired phone instead.

### 8. Revocation and unpairing

- Tray settings list every device with name, platform, paired date, and last seen.
- "Revoke" sets `revoked`, closes any live connection with `4403`, drops queued prompts that originated on that device, and invalidates outstanding confirmations and approvals bound to it.
- "Revoke all" additionally rotates `K_server_sign`.
- The phone app offers "Forget PC", which deletes both Keystore keys and the stored server identity.
- A revoked device that reconnects gets `4403` with no information about why, and its `deviceId` is not reusable.

### 9. Logging and diagnostics

Security-relevant events are recorded in the local text event history: pairing shown, pairing accepted or rejected with a coded reason, device revoked, interface binding changed, authentication failure counts, and confirmation or approval rejections with their reason codes. Never recorded: `psk`, tokens, signatures, exporter values, private keys, transcript text, or draft text.

## Alternatives considered

- **Full certificate pinning instead of SPKI pinning.** Rejected: any SAN change, which is routine when the Tailscale address changes, would force every device to re-pair.
- **Password or PIN pairing.** Rejected: weaker than a 256-bit single-use secret carried by QR, and it adds a typing step on the phone.
- **mTLS with a client certificate per device.** A reasonable alternative, and it also binds to the TLS session. Rejected because Android client-certificate handling in OkHttp plus Keystore-backed keys is more fragile than an application-layer Ed25519 signature, and because the approval key needs an application-layer signature anyway; using one mechanism for both keeps the verification code single-path.
- **Deriving a long-term shared symmetric key from the pairing `psk`.** Rejected: a symmetric secret must be stored verifiably on both ends, and a compromised PC store would then let an attacker impersonate the phone. Asymmetric device keys keep the phone the only holder of its signing key.
- **A named-pipe local API for the Shell instead of loopback TLS.** Rejected: it reintroduces the second orchestration surface that ADR-001 section 2 removes.

## Consequences

- Re-pairing is required only on a key rotation, not on network changes.
- The verification-code step adds about three seconds to pairing and closes the QR-race attack.
- Consequential approvals cannot be granted from a PC without Windows Hello configured; this is surfaced during setup, not at approval time.
- `K_conf` being memory-only means a Core crash during confirmation costs the user one re-confirmation.
- The local capability token is only as strong as the user account boundary; the threat model states this explicitly as a residual risk.

## Verification

- T020 unit tests: proof and signature verification vectors, constant-time comparison, expiry, nonce replay within and across connections, exporter mismatch, revoked device, and rate-limit lockout.
- T020 integration test: full pair, verification-code match, connect, revoke, reconnect refused with `4403`.
- T004 test: `localToken` presented on a non-loopback connection is rejected with `4401`.
- T023 instrumented test on the Pixel 9a: `optimus_approval_v1` requires a biometric prompt per use and is invalidated by a new biometric enrolment.
- T029 release audit checks that no secret path appears in logs, that `allowBackup` is false, and that the listener never binds `0.0.0.0`.

## Related documents

- `docs/security/THREAT_MODEL.md`
- `docs/adr/ADR-002-device-protocol-and-compatibility.md`
- `docs/adr/ADR-005-confirmation-sessions-and-approvals.md`
