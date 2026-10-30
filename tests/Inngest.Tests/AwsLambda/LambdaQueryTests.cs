using Inngest.AwsLambda;

namespace Inngest.Tests.AwsLambda;

public sealed class LambdaQueryTests
{
    [Theory]
    [InlineData("a=1&b=2", "a", "1")]
    [InlineData("a=1&b=2", "b", "2")]
    [InlineData("a=1&b=2", "c", null)]
    [InlineData("", "a", null)]
    [InlineData("a=1", "A", "1")]
    [InlineData("FNID=x", "fnId", "x")]
    [InlineData("a=b%20c", "a", "b c")]
    [InlineData("a=b+c", "a", "b c")]
    [InlineData("a=b%2Bc", "a", "b+c")]
    [InlineData("a=%C3%A9%F0%9F%98%80", "a", "é😀")]
    [InlineData("a=b=c", "a", "b=c")]
    [InlineData("a=", "a", "")]
    [InlineData("a", "a", "")]
    [InlineData("&&a=1&", "a", "1")]
    [InlineData("=1&a=2", "a", "2")]
    [InlineData("f%6EId=x", "fnId", "x")]
    [InlineData("a%20b=1", "a b", "1")]
    [InlineData("a+b=1", "a b", "1")]
    [InlineData("url=http://www.google.com&x=2019-03-12T16%3A06%3A06.549817%2B00%3A00", "x", "2019-03-12T16:06:06.549817+00:00")]
    [InlineData("a=%zz", "a", "%zz")]
    public void Find_reads_the_decoded_value_of_a_parameter(string query, string name, string? expected)
    {
        LambdaQuery.Find(query, name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a=1&a=2", "1,2")]
    [InlineData("a=1&b=x&a=2&A=3", "1,2,3")]
    [InlineData("a=&a=2", ",2")]
    public void Find_joins_the_values_of_a_repeated_parameter_with_commas(string query, string expected)
    {
        LambdaQuery.Find(query, "a").ShouldBe(expected);
    }

    [Fact]
    public void Build_prefers_the_multi_value_form()
    {
        var query = LambdaQuery.Build(
            new Dictionary<string, IList<string>> { ["a"] = ["1", "2"], ["b"] = ["x"] },
            new Dictionary<string, string> { ["a"] = "2", ["b"] = "x" });

        query.ShouldBe("a=1&a=2&b=x");
    }

    [Fact]
    public void Build_falls_back_to_the_single_value_form()
    {
        LambdaQuery.Build(null, new Dictionary<string, string> { ["a"] = "1", ["b"] = "x" }).ShouldBe("a=1&b=x");
        LambdaQuery.Build(new Dictionary<string, IList<string>>(), new Dictionary<string, string> { ["a"] = "1" }).ShouldBe("a=1");
    }

    [Fact]
    public void Build_is_empty_without_parameters()
    {
        LambdaQuery.Build(null, null).ShouldBeEmpty();
        LambdaQuery.Build(new Dictionary<string, IList<string>>(), new Dictionary<string, string>()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a b+c%d/é&=;#?")]
    [InlineData("😀")]
    [InlineData("")]
    [InlineData("%2B")]
    public void Build_and_Find_round_trip_whatever_the_value_holds(string value)
    {
        var query = LambdaQuery.Build(null, new Dictionary<string, string> { ["x y&z"] = value });

        LambdaQuery.Find(query, "x y&z").ShouldBe(value);
    }
}
