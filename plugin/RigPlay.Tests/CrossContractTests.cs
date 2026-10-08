// SPDX-License-Identifier: GPL-3.0-only
// CrossContractTests.cs: the C# half of the cross-codec contract net (CF-6). Reads the single source-of-truth
// corpus protocol/fixtures/cross/cross-contract.json (copied next to the test assembly by the recursive glob in
// RigPlay.Tests.csproj) and checks that MessageCodec gives the verdict the vector expects for the C# side. The
// Kotlin mirror (CrossContractTest.kt) reads the same corpus and checks its own column; a verdict mismatch
// between the two codecs breaks at least one of the two suites. Locks DIM-1 / DIM-2 / DIM-3.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class CrossContractTests
    {
        private const int ExpectedVectorCount = 20;

        public static string CrossCorpusPath =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", "cross", "cross-contract.json");

        private static JArray Vectors => (JArray)JObject.Parse(File.ReadAllText(CrossCorpusPath))["vectors"];

        public static IEnumerable<object[]> CrossVectors()
        {
            return Vectors.Select(v => new object[] { (string)v["name"] });
        }

        [Fact]
        public void TheCrossCorpusIsThere()
        {
            Assert.True(File.Exists(CrossCorpusPath),
                "cross corpus not copied to " + CrossCorpusPath + " - check the recursive <None Include> glob in RigPlay.Tests.csproj");
            Assert.Equal(ExpectedVectorCount, Vectors.Count);
        }

        [Theory]
        [MemberData(nameof(CrossVectors))]
        public void TheCsharpCodecMatchesTheExpectedVerdict(string name)
        {
            var vector = Vectors.Single(v => (string)v["name"] == name);
            var wire = (string)vector["wire"];
            var expected = (string)vector["csharp"];
            Assert.True(expected == "accept" || expected == "reject", name + ": csharp verdict must be accept or reject, got " + expected);

            var result = MessageCodec.TryDecode(wire);
            Assert.True(result.Ok == (expected == "accept"),
                name + " (" + (string)vector["note"] + "): expected " + expected
                    + " but got " + (result.Ok ? "accept" : "reject (" + result.Failure + " " + result.Reason + ")"));
        }
    }
}
