using System.Text.Json;

namespace Inngest.Tests.Encryption;

// Everything here was made by @inngest/middleware-encryption 2.0.0 itself, on Node with the libsodium-wrappers 0.7.13
// and crypto-js 4.2.0 it depends on: its middleware ran step handlers, sent events and read steps, and these are what it
// wrote. Nonces and salts are random, so each envelope is one that only the TypeScript package could have produced.
// The plaintext of each is the JSON text that JSON.stringify gave the package.
internal static class JsVectors
{
    public const string Key = "test-fixture-key";
    public const string SecondKey = "second-test-key";

    // crypto_generichash(32, key) as the strategy calls it, with the key before and after String.prototype.trim.
    public static readonly (string Key, string Trimmed, string Hash)[] KeyHashes =
    [
        ("test-fixture-key", "test-fixture-key", "f87d3d2980d5e8580a55b48f20d4fa474d9315e10af565076a350a697b009c36"),
        ("second-test-key", "second-test-key", "c9cb23754168908ae934ce6c9a3fe9f8fbc007f64b64d79509443d094c6ef70d"),
        ("  padded-key \n", "padded-key", "9cf88fdb13a6faaf2d541a1bf54a022ab01b328b25453c30c884c0aa88d6f16c"),
        ("schl\u00fcssel-\u65e5\u672c\u8a9e-\ud83d\udd11", "schl\u00fcssel-\u65e5\u672c\u8a9e-\ud83d\udd11", "210bb5e559a95ce7c9400ae7fd48a2880b60fa053ce46b8e5af3752e6f2fa82a"),
        ("\ufeffbom-key\u00a0", "bom-key", "57ef07feb384354d99b71a2c48bb87a250b10b08c40f0cfd8265f961b4b929d0"),
    ];

    // crypto_secretbox_easy(message, nonce, key) with the derived form of Key, where the message is
    // (i * 31 + 7) & 0xFF for each i below Length, which runs the stream across its 64-byte blocks.
    public const string BoxNonce = "05101b26313c47525d68737e89949faab5c0cbd6e1ecf702";

    public static readonly (int Length, string Box)[] Boxes =
    [
        (0, "6fa50beeedaa5e2e1e354be6d47d50fa"),
        (1, "296ac5cb1bf0694dc418c8e8a5ec6a4434"),
        (31, "88718244ec06eefd15a0a244cd261267343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230e"),
        (32, "bc965d3e471b3ac0fa7ace9a79e0bddf343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0"),
        (33, "728a9d01d87fb94ea56358164ed820f3343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0de"),
        (63, "8bf83f2874a4288a99b3a3ac0adc1214343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0dedaa457c32b3e099447701776715d12f19603a662126c0e2f008fc76e60e3"),
        (64, "4068837aaf9c567bfd08974c335f247c343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0dedaa457c32b3e099447701776715d12f19603a662126c0e2f008fc76e60e322"),
        (65, "af915c380981f00cd36de698e75690aa343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0dedaa457c32b3e099447701776715d12f19603a662126c0e2f008fc76e60e322c3"),
        (200, "8a8e414fb490351d7872737ad8cd7ef7343d557e77b994130a7ac1ce3422f1d28ddcefd2b21e1559916fc4d5cb230ef0dedaa457c32b3e099447701776715d12f19603a662126c0e2f008fc76e60e322c3802d473705b1aed7e1a3e278aa25acff8b7abd3b72b23f502d9cb86f445cb6d3a084466183e4d6c19181c74dcb42a5ccebccaec91e41b52f995f45ca3dffcd83d9ad7661aead6c063fdb3b17c9ed468c7ac66b34414af44b2b27dbcb45bc20de9ce2dce0357cad4ebdb36b1d9f8f385b7e4419b9698a99f30e48d5501abf09f66cb0f7cf962887"),
    ];

    // Written by the default strategy, inngest/libsodium, with Key.
    public static readonly Vector[] LibSodium =
    [
        new("object", "test-fixture-key", """{"hello":"world","nested":{"num":42}}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"WViUOLZkf4gwusMBI6qnFP1nYM2m62fMvyjfFwbNeVAVNLKD5pNZ7gik+sKbLRIl6pJTX0CiuD6IsT6RFrXK1zoyjPfHiUqo0NkLeHM="}"""),
        new("string", "test-fixture-key", "\"s3cret\"", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"//dwBa/jwq9E9uQzO6Y4DeKi67T2//TpF0V6wK+QOetOSjnC8Vs64VrOWfpGrq7T"}"""),
        new("number", "test-fixture-key", "42", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"BEX8whZngZn3PQSgLaMGh182+vFPnNiXizsyU1ppAbyuLqfDVj2VigTh"}"""),
        new("array", "test-fixture-key", """[1,"two",{"three":3},null,true]""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"a01rQgCw/Ep2gIlvr51P7y5teXoJQKFB3hQYeWtBR2zEg0wKaX64BG/a5Mev9wDvhFYwgN39lqg5pTOGkMabrtGH99tbE9A="}"""),
        new("boolean", "test-fixture-key", "true", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"VmazY9DZYHT63lNOIioixH1tUTfPd/1uTcC7z6APfk3QvLSqCNcK2/f7A7A="}"""),
        new("unicode", "test-fixture-key", "\"h\u00e9llo w\u00f6rld \u2713 \u65e5\u672c\u8a9e \ud83d\ude00 \\\"quoted\\\" \\\\ back \\n newline \\t tab \\u0001 control\"", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"g8tUbKiPDhTywKXshxZrTUB53cz0w/062ozoD+xnJBsW6gGCwuMBDIavtYPWjEqvWz9tnOhTW8X5s150bLgufVdqAbved1QOFaLBTa9K8HRfYjy8U0tkFuYDBV1afxyszkKBvJIaGPSYdMJkwcq9cD7W1Hw1IvietLY7PinQ"}"""),
        new("emptyObject", "test-fixture-key", "{}", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"VHS0k60QlPXFBacfcxiPUqOI9uFMReaX5VQDiUzYQoLiQW+SDWdEbDz/"}"""),
        new("long", "test-fixture-key", """{"text":"012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"aWbM/Ii3qhORFnMFbDu7xYb4NuryxNEc1Qmwaa9zAcCqKRikGMHAZ9T6UOL1hylwWtkrf+GbLu5eLcQiCok0LlJT00FIzHErzojH3gNhUMkcsgSm/m2QqIdKrrpzJaPibRb16z2JtXwnEPDrtZ1/Y1c9LavFXf3Erhc91v+86/eg6AOOYd0Qn2ageqBZdwVBjzsoNUJzLuEkbCakoKH9LG9ygMn5wF9eHqhwZ9kHu7NXox0JcMGSEWaomFIGK05a/4CmkWh7VKG6ZKCZWrYRxzaZqrBojf0+JdxWCyceUtA8th0hzkDF40fdETX8J7uyKGCAZx54kw6UxSJo1/PzdHYTSvL79F0QByyFiEv8Bdoy/t7jqbJfqxWmUdbdXJxYdL0bG8iST5gjTi3GoPHZJIUwpRCuxnwwBDI7iNIGsmRkZ+wLyOcFepzV9lXwGl8geRsHkwSZLRaoTVM7tq3J"}"""),
    ];

    // The same with other keys: surrounding white space, non-ASCII text, and a byte order mark and a non-breaking space,
    // which String.prototype.trim removes.
    public static readonly Vector[] OtherKeys =
    [
        new("secondary", "second-test-key", """{"who":"secondary","n":1}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"FJ7uS66GLu9bm5aROUOZ4AmfAHdTDWCfh8nbhH5HpnLquqofpm+3OiH/CP9V4AnMtLj4nMSDiECD+kqLjcEd9A8="}"""),
        new("padded", "  padded-key \n", """{"who":"padded","n":1}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"8O5u1skpjsS9Eq7xNK5nK2N4PwU1BkClAScDIQcr6j4AZBYM5XijW1CrTTkiLgIuZVrcgF6cSoZZ9tsyX7k="}"""),
        new("unicode", "schl\u00fcssel-\u65e5\u672c\u8a9e-\ud83d\udd11", """{"who":"unicode","n":1}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"/iiFnXss0xB9A7Fa1fmZnI55S0xZzsUWZVZqx+jq6ApISnMHoq7vS5FLJvVl6KPL9L40UXMORAdR8wY58RCL"}"""),
        new("bom", "\ufeffbom-key\u00a0", """{"who":"bom","n":1}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"lRKDK/s+Pseyu4GZx/sM76IPglOcFkkxk4PlrRQiYWtRasZYPZePfaO7spyRC77wZOYEeHVkn2VknOw="}"""),
    ];

    // Written by the v0 versions of the package, and by forceEncryptWithV0: AES from crypto-js, with no strategy.
    public static readonly Vector[] AesV0 =
    [
        new("object", "test-fixture-key", """{"hello":"world","nested":{"num":42}}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+4Riksv6wfL3PFpb8GOaG31Hulx5TdEINuxn3lYpdpTZ0R4hL5kZG4eOjf1rjsp7LmTpHqMpGEdQ=="}"""),
        new("string", "test-fixture-key", "\"s3cret\"", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX19LDX6Ba8ehB6XUS2bW0YAboo8EcB2h52E="}"""),
        new("number", "test-fixture-key", "42", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1/hjUp7PDu046prVawAjHSYz9CosLscQxI="}"""),
        new("array", "test-fixture-key", """[1,"two",{"three":3},null,true]""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+HH3VruveMAVolKrWSsbONtyNhNKZh1bBwcYeJoBYcgw8iFL7R7g1q"}"""),
        new("boolean", "test-fixture-key", "true", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX19eIrG5IKjpvWJiecRSqjyXcsWBh6v+DAs="}"""),
        new("unicode", "test-fixture-key", "\"h\u00e9llo w\u00f6rld \u2713 \u65e5\u672c\u8a9e \ud83d\ude00 \\\"quoted\\\" \\\\ back \\n newline \\t tab \\u0001 control\"", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+4T52ehEHw8FPs09gUqmAVQCQbFmcU/6SDjs5yw1Kb3ezPYrggV0czGv7tb8GmUVlgBXB9THjIJJkxaNRCW9ZPqRu65Nt3YLnxvm74FolESo6BrcQrnOYbxaWSbAh81+sBcYsckE90yw=="}"""),
        new("emptyObject", "test-fixture-key", "{}", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+c3iq5uIpAVCFGfYeNm1JYgiRafr9GkPs="}"""),
        new("long", "test-fixture-key", """{"text":"012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1/0Z95/ZDVYz3S5hu9OqWT6Q7V0lgmnlgw5Ya4QtOASeZXwdWwtvXS1zIV31WpEMxB6Knh4JtGzi/s30roCWIdHtA8fTqg1orPXkVgqjjjjIKo9x83xmh+kMgdZSy81a7iHA4+2FqVDn3n1OyvxW9DP2QpeOmR/1BhG6CXjuhTehXY9vRzAXE8hGnxNRUn/085UOXAVAfgtO/QgCY6CDhKqFrb6yjbgmnz4YDZ72Ftbzkm8cIa3rT0guMa9DQ/rrA7WBiUvkFLVTSiwACEBNZWOI6MwkYZqEQMiYjC0xjD9iWPJoi044iP70EFWtCt97RfntQABzTViZN0HssZ8W7Rd3RIHqjedTagbj5lzvHxmnMeBV+3amdJw1VqB7IWtul0z1i4OckzYeOJWPfMinbtgAQXuqRitOhRRglrKgzOhxTOJhvgqllPQ"}"""),
    ];

    // The same with other keys.
    public static readonly Vector[] AesOtherKeys =
    [
        new("secondary", "second-test-key", """{"who":"secondary","n":1}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX18iir5cD0vZllOwoFRAe1Ajym0TPgdZ8AQ8FllXxo7MW3StXlm/IDBI"}"""),
        new("padded", "  padded-key \n", """{"who":"padded","n":1}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+Mc6BOVZ1tApsDyybvwfo4jYi7H3sxx6izHobr4+VQY9JcJdnEZJ/J"}"""),
        new("unicode", "schl\u00fcssel-\u65e5\u672c\u8a9e-\ud83d\udd11", """{"who":"unicode","n":1}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX189r/ImAm7qou8W7CmmdO79wAUwCNiO8bL1HcQOY8VIH6/Te0/Elooe"}"""),
        new("bom", "\ufeffbom-key\u00a0", """{"who":"bom","n":1}""", """{"__ENCRYPTED__":true,"data":"U2FsdGVkX1+KX+Ix1y7IGGcsN5BDmDYj8ktrDG0ftwlhd41dDV41zkaisaMR5SLp"}"""),
    ];

    // Written by the package's AESEncryptionService as its encryption service: AES with the strategy inngest/aes.
    public static readonly Vector[] AesStrategy =
    [
        new("object", "test-fixture-key", """{"hello":"world","nested":{"num":42}}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX1/4lnJ+8KdV7tVGY3CTWUlmxFg9D1Y+zrIA8pq99BTd/CnyMtQVxPhQswciiq29F1rOIS8m3PKzAw=="}"""),
        new("string", "test-fixture-key", "\"s3cret\"", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX19TOvBAQwaXoj4hV/ZpRUySumoC6xFgwb4="}"""),
        new("number", "test-fixture-key", "42", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX18FLuNAkmWU2cGfc5Be54Yt9eGHrmB3qmY="}"""),
        new("array", "test-fixture-key", """[1,"two",{"three":3},null,true]""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX1+YcqDvrHLMtkl23aKabUpZk0vbvxFdmCU662mOeoUB8giX8GAoOpEH"}"""),
        new("boolean", "test-fixture-key", "true", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX19wj14+7WyF+rdqSWXBvycp2QSI1VvrK4E="}"""),
        new("unicode", "test-fixture-key", "\"h\u00e9llo w\u00f6rld \u2713 \u65e5\u672c\u8a9e \ud83d\ude00 \\\"quoted\\\" \\\\ back \\n newline \\t tab \\u0001 control\"", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX18MlROH/tFjo6Gz8xo3XB42eY9zVCrN0mqSUQC+dq08ZewBqxdqi/McKrFsVOc2+dqJ+VCd8xs7bHSYf3Eos/vWVUxjPnEJzCFAMexXyWCXKu9+TNIVcmrS3X+DOHNji8iJb7NafUIpBA=="}"""),
        new("emptyObject", "test-fixture-key", "{}", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX18QCpPn1vzKa0MuMlZl7vHGS1ODgYnYTro="}"""),
        new("long", "test-fixture-key", """{"text":"012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"}""", """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/aes","data":"U2FsdGVkX1/4djIDmQFwV+Hpilyoh0Btk9+Dd/ufnzLmC9k4xr3VPZDyAQd3ZWUKOaxF9R22ZilS0Z+xGiU8HzRgGH2G9ggANeCcyQfSfa2My4C/wASy2y1/YM+lbFXe3K+bGIlUbIGbw0i3GA8Y8vRIienHHd5zRXzWdImcFsXFnHMnifhQU7ItwgKfk0VXVOvoGyfWDnuQ68byCXaSMyST8RGU6QqHN0t0ZvVYoANhHHu3ESTJy1vAiiAgcSLPU1Ff67F68XbJ+JeUN23ggeeODfi/kDo0rDzU2ftZ830KPdBmKXOCMwS21ZB+PoeJS5zQJaGHaZuCKQVsLljMt5E4FHhulaqAyHAirD1xYgebsIiGial7887wEZGDZe2HJN0FC0pz855wx8CxzwieeiQ0eH+zk+SDurXCOvzliZrjEpuiQ3ZJ8iSGqowoTekd"}"""),
    ];

    // The event that transformSendEvent made of Event, with the default field, with secret_data as the field, and with
    // forceEncryptWithV0.
    public const string Event = """{"name":"app/thing.happened","data":{"public_field":"visible","count":3,"encrypted":{"secret":"data","list":[1,2,3]},"secret_data":{"other":"field"}},"id":"evt-1","ts":1793268000000}""";
    public const string EncryptedEvent = """{"name":"app/thing.happened","data":{"public_field":"visible","count":3,"encrypted":{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"IVhfVaXvshbZ57QB0tN/CX0yM9GG21Tu0J/NFREF4pnEfHal1hKU4XF0pFnfP0O0EsZFPBcHgfnB7HKzOSTbHsfjw6N4iRyx"},"secret_data":{"other":"field"}},"id":"evt-1","ts":1793268000000}""";
    public const string EncryptedCustomFieldEvent = """{"name":"app/thing.happened","data":{"public_field":"visible","count":3,"encrypted":{"secret":"data","list":[1,2,3]},"secret_data":{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"hzEjtqpN/+7XsYAQzv+vx/kXFr09yyOnnqSkjmtKGqsT3WYy6+NBKSwDCTVCKFPHjZIOX96YzE8h"}},"id":"evt-1","ts":1793268000000}""";
    public const string EncryptedAesEvent = """{"name":"app/thing.happened","data":{"public_field":"visible","count":3,"encrypted":{"__ENCRYPTED__":true,"data":"U2FsdGVkX18eF6B7sP1jZboo1xDjIzZv6vJCux5uNfErmQqQpfEaKzQj5c8b263/phZ82sS/cbI4U1d3DcC8yQ=="},"secret_data":{"other":"field"}},"id":"evt-1","ts":1793268000000}""";

    // Written with SecondKey, to be read with it as a fallback key.
    public const string SecondKeyEnvelope = """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"DzTJ9+VdWIK6RgX3TvKpujhs6eRzrGC0GOryxMtSGBOS6k+F47Zxoh5ZC1Gz/qnhrtnEUtmTJGc="}""";
    public const string SecondKeyAesEnvelope = """{"__ENCRYPTED__":true,"data":"U2FsdGVkX18PRU6AngzpMmeawazFTyErdJu2GyNTRoNPziD7Buj15YTMkZRtMqe8"}""";

    public static Vector Find(Vector[] vectors, string name) => vectors.Single(vector => vector.Name == name);

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}

internal sealed record Vector(string Name, string Key, string Plaintext, string Envelope)
{
    public override string ToString() => Name;
}
