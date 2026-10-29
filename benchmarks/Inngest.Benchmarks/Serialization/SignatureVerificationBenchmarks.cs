using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Inngest.Signing;

namespace Inngest.Benchmarks.Serialization;

// Checking X-Inngest-Signature on a call: parsing the header and an HMAC-SHA256 over the whole body.
[MemoryDiagnoser]
public class SignatureVerificationBenchmarks
{
    private SigningKey _key = null!;
    private RequestSigner _signer = null!;
    private byte[] _body = null!;
    private string _signature = null!;

    [Params(1024, 1024 * 1024)]
    public int BodyBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _key = SigningKey.Create($"signkey-prod-{Convert.ToHexStringLower(SHA256.HashData("benchmarks"u8))}")!;
        _signer = new RequestSigner(new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_793_268_000)));
        _body = CreateBody(BodyBytes);
        _signature = _signer.Sign(_body, _key);

        Expect.That(_signer.Verify(_signature, _body, _key, fallbackKey: null).IsValid, "the signature of the body verifies");
        var tampered = _body.ToArray();
        tampered[^2] ^= 1;
        Expect.That(!_signer.Verify(_signature, tampered, _key, fallbackKey: null).IsValid, "the signature of another body does not verify");
    }

    [Benchmark]
    public bool VerifySignature() => _signer.Verify(_signature, _body, _key, fallbackKey: null).IsValid;

    // A call whose one step has an output big enough to make the body the size asked for.
    private static byte[] CreateBody(int size)
    {
        const string head = "{\"steps\":{\"1f9c\":{\"data\":\"";
        const string tail = "\"}}}";
        var body = Encoding.UTF8.GetBytes(head + new string('x', size - head.Length - tail.Length) + tail);
        Expect.Equal(size, body.Length, "the size of the body");
        return body;
    }
}
