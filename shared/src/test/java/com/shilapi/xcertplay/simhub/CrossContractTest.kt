package com.shilapi.xcertplay.simhub

import java.io.File
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The Kotlin half of the cross-codec contract net (CF-6). Reads the single source-of-truth corpus
 * `protocol/fixtures/cross/cross-contract.json` (the same file the C# mirror `CrossContractTests.cs`
 * reads) and checks that the Kotlin codec gives the verdict the vector expects for the `kotlin` side.
 * A verdict mismatch between the two codecs breaks at least one of the two suites. Locks the
 * DIM-1 (status.nav lenient), DIM-2 (hostId 1-128 in beacon/welcome) and DIM-3 corrections.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class CrossContractTest {
    private val vectors: JSONArray = JSONObject(
        File(locateFixtures(), "cross/cross-contract.json").readText(Charsets.UTF_8),
    ).getJSONArray("vectors")

    @Test fun theCrossCorpusIsThere() {
        assertEquals("expected the cross corpus vector count", EXPECTED_VECTOR_COUNT, vectors.length())
    }

    @Test fun theKotlinCodecMatchesTheExpectedVerdict() {
        for (index in 0 until vectors.length()) {
            val vector = vectors.getJSONObject(index)
            val name = vector.getString("name")
            val expected = vector.getString("kotlin")
            assertTrue("$name: kotlin verdict must be accept or reject, got $expected", expected == "accept" || expected == "reject")

            val result = SimHubProtocol.parse(vector.getString("wire"))
            val accepted = result is SimHubParseResult.Ok
            assertEquals(
                "$name (${vector.getString("note")}): expected $expected but got $result",
                expected == "accept",
                accepted,
            )
        }
    }

    companion object {
        private const val EXPECTED_VECTOR_COUNT = 20

        /** Gradle runs unit tests in the module directory; walk up to the repository root. */
        private fun locateFixtures(): File {
            var dir: File? = File("").absoluteFile
            while (dir != null) {
                val candidate = File(dir, "protocol/fixtures")
                if (File(candidate, "README.md").isFile) return candidate
                dir = dir.parentFile
            }
            error("protocol/fixtures not found above ${File("").absolutePath}")
        }
    }
}
