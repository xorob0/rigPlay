# Security

## Reporting

Use GitHub's private vulnerability reporting on this repository for anything sensitive. Do not post
credentials, pairing tokens, diagnostic reports you have not reviewed, or identity files in public issues.

## Accessory identity

Connecting to an iPhone needs an accessory certificate and private key. rigPlay uses the experimental
Carlinkit-derived identity described in [docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md). Any APK
that bundles it exposes the private key to everyone who has the APK. Building locally, removing Git history
or obfuscating does not make a bundled shared key confidential or revoke copies already distributed.
Current acceptance by iPhones is not Apple certification and does not guarantee future compatibility.

The public Git tree and source archives contain no accessory keys and no Android signing secrets.
`scripts/check_public_tree.py` fails CI on credential containers and private-key blocks in tracked files.
Tests generate synthetic identities at runtime. A build includes the identity only when you point
`RIGPLAY_AUTH_ASSETS_DIR` at it, and then only the two expected files (see
[docs/BUILD.md](docs/BUILD.md#accessory-identity-required-to-connect-to-an-iphone)). The release workflow
takes the identity and the signing key from encrypted GitHub repository secrets, decodes them only inside its
runner and deletes them after the build; pull-request builds never see them.

The Android signing key is separate, stays with the repository owner and in those secrets, and is never
bundled.

## PC ↔ tablet link

Protocol 1 between the tablet and the SimHub plugin has no encryption and assumes a trusted home network.
The plugin accepts connections only from private and link-local addresses, pairs tablets with a PIN shown
on the PC, and stores only a hash of each tablet's token. Someone on your network can still read the
traffic and replay a sniffed token. The threat model is in [docs/protocol.md §15](docs/protocol.md#15-security).
