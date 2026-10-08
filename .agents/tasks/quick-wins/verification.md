# Vérification — Audit Quick Wins (QW-1 … QW-5)

Branche : `chore/audit-quick-wins` (worktree `.worktrees/quick-wins-audit`).

## Partie vérifiée par build (C# / .NET)

Commandes lancées depuis la racine du worktree :

- `dotnet test plugin/RigPlay.Tests`
  - Résultat : **689 tests, 689 passés, 0 échec, 0 ignoré** (outcome `Completed`).
  - Baseline avant changements : 681 tests. +8 nouveaux :
    - QW-2 : 4 lignes `[InlineData]` ajoutées à `ContentRules` (beacon/welcome valides + beacon/welcome `hostId` vide) et 2 `[Fact]`
      (`ABeaconHostIdOf129CharactersIsInvalid`, `AWelcomeHostIdOf129CharactersIsInvalid`).
    - QW-3 : 1 vecteur invalide `pcm-payload-over-8192` ajouté à `protocol/fixtures/audio-header.json`, exécuté par
      `AudioHeaderTests.RejectsInvalidVector` **et** `ProtocolFixturesTests.AnInvalidAudioVectorIsRejected` (2 exécutions).
- `dotnet build plugin/RigPlay -c Release`
  - Résultat : **Build succeeded, 0 Warning(s), 0 Error(s)** (`net48` → `RigPlay.dll`).

SDK utilisé : .NET 10 SDK ; les projets ciblent `net8.0` (tests) et `net48` (plugin), compilés sans souci.

### Note sur QW-3 côté C#

Le plan supposait que « C# rejette déjà » les payloads > 8192 octets. En réalité, seul le codec
`plugin/RigPlay/Protocol/AudioHeader.cs` (`AudioDatagram.Validate`) appliquait le plafond §10.2 ; le codec sœur
`plugin/RigPlay/Audio/AudioHeader.cs` (`AudioHeader.TryParse`, utilisé par `AudioHeaderTests.RejectsInvalidVector`)
ne vérifiait **que** la borne basse (multiple de `BlockAlign`). Le nouveau vecteur partagé était donc accepté par ce
second codec, faisant échouer le test générique.

Correction appliquée, strictement dans l'intention de QW-3 (faire respecter le plafond §10.2 des deux côtés) :
- ajout de `AudioHeaderError.PayloadTooLarge` ;
- garde `if (payload > MaxPayloadBytes) return AudioHeaderError.PayloadTooLarge;` dans `AudioHeader.TryParse`,
  placée avant la branche spécifique au format, à l'identique de `AudioDatagram.Validate`.
- mise à jour du compteur figé `FixtureDescribesTheSameLayout` : `invalid` passe de 15 à 16.

Aucun comportement utilisateur au-delà de l'intention de QW-3 : les datagrammes PCM trop gros sont rejetés, ce qui
est précisément l'objet du quick win ; les deux codecs C# sont désormais cohérents avec la spec et entre eux.

## Partie NON compilée localement (Kotlin / Android)

Cet environnement n'a **ni JDK 25 ni Android SDK** : les changements Kotlin ne peuvent **pas** être compilés ni
testés ici. Ils sont **non compilés localement, à valider par la CI** (`.github/workflows/ci.yml`, commande
`./gradlew :shared:testDebugUnitTest :common:testDebugUnitTest :mobile:lintDebug :mobile:assembleDebug`).
Leur correction a été assurée par lecture attentive et cohérence avec le C# et les tests existants :

- QW-1 — `SimHubProtocol.kt` : décodage **lenient** de `status.nav` (nouveau `decodeNav` + `Fields.optJsonObject`),
  calqué sur le `DecodeNav` C# : `nav` non-objet ⇒ absent ; membre de mauvais type/hors borne ⇒ absent ; `maneuver`
  manquant/invalide ⇒ `nav` absent ; `road` blanc ⇒ absent ; un `nav` défectueux ne rend jamais le `status` Malformed.
  Encodage inchangé.
- QW-1 (test) — `ProtocolFixturesTest.kt` : la ligne `assertMalformed(...,"nav":{"distanceM":5}...,"status")` est
  remplacée par une assertion de `status` **valide** avec `nav` absent, plus trois cas lenient supplémentaires
  (nav sans `maneuver`, membre de mauvais type conservé partiellement, `nav` non-objet).
- QW-3 — `SimHubAudioHeader.kt` : constante `MAX_PAYLOAD_BYTES = 8192` + garde `payloadLength > MAX_PAYLOAD_BYTES`
  avant la branche de format dans `decode()`. Rejette le vecteur partagé `pcm-payload-over-8192` (8194 octets).
- QW-4 — `CarPlayHostActivity.kt` : `teardownExecutor.shutdown()` et `airPlayCommandExecutor.shutdown()` ajoutés dans
  `onDestroy()` (idempotents, `shutdown()` tolère un double appel), avant `super.onDestroy()`.
- QW-5 — `RigPlayActivity.kt` : extraction d'un helper `stopTestTone()` (annule le `toneStop` différé, stoppe/libère
  `testToneTrack`), appelé au début de `playTestTone()` (remplace le nettoyage inline) et dans `onPause()`.

## Portée

Seuls QW-1 … QW-5 sont implémentés. QW-6 … QW-9 ne sont pas touchés. `docs/protocol.md` n'est pas modifié.
