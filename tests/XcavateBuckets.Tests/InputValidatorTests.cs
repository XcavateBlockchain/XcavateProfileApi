using XcavateBuckets.Domain;

namespace XcavateBuckets.Tests;

[TestFixture]
public class InputValidatorTests
{
    private const string Hex32 = "0x0000000000000000000000000000000000000000000000000000000000000000";

    private static InputValidator NewValidator() => new(new BucketOptions());

    [Test]
    public void Text_accepts_a_value_at_the_limit()
    {
        Assert.DoesNotThrow(() => NewValidator().Text(new string('a', 256), 256, "name"));
    }

    [Test]
    public void Text_rejects_a_value_over_the_limit()
    {
        var ex = Assert.Throws<BucketException>(
            () => NewValidator().Text(new string('a', 257), 256, "name"))!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
            Assert.That(ex.Message, Does.Contain("name"));
        });
    }

    [Test]
    public void Required_rejects_an_empty_value()
    {
        var ex = Assert.Throws<BucketException>(
            () => NewValidator().Required(string.Empty, 256, "name"))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void Text_accepts_null()
    {
        Assert.DoesNotThrow(() => NewValidator().Text(null, 256, "schemaUri"));
    }

    [TestCase("0xab", false, TestName = "Hex32_rejects_too_short")]
    [TestCase("not-hex-at-all", false, TestName = "Hex32_rejects_non_hex")]
    [TestCase(Hex32, true, TestName = "Hex32_accepts_32_bytes_with_prefix")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", true,
        TestName = "Hex32_accepts_32_bytes_without_prefix")]
    [TestCase(Hex32 + "00", false, TestName = "Hex32_rejects_33_bytes")]
    public void Hex32_enforces_exactly_32_bytes(string value, bool valid)
    {
        var validator = NewValidator();

        if (valid)
        {
            Assert.DoesNotThrow(() => validator.Hex32Value(value, "encryptionKey"));
        }
        else
        {
            var ex = Assert.Throws<BucketException>(
                () => validator.Hex32Value(value, "encryptionKey"))!;
            Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
        }
    }

    [Test]
    public void PropertiesJson_returns_null_when_there_are_no_properties()
    {
        Assert.That(NewValidator().PropertiesJson(null), Is.Null);
    }

    [Test]
    public void PropertiesJson_returns_null_for_an_empty_collection()
    {
        Assert.That(NewValidator().PropertiesJson([]), Is.Null);
    }

    [Test]
    public void PropertiesJson_serialises_sorted_by_key()
    {
        var json = NewValidator().PropertiesJson(new Dictionary<string, string>
        {
            ["zeta"] = "2",
            ["alpha"] = "1"
        });

        Assert.That(json, Is.EqualTo("""{"alpha":"1","zeta":"2"}"""));
    }

    [Test]
    public void PropertiesJson_rejects_too_many_properties()
    {
        var many = Enumerable.Range(0, 33).ToDictionary(i => $"k{i}", _ => "v");

        var ex = Assert.Throws<BucketException>(() => NewValidator().PropertiesJson(many))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void PropertiesJson_rejects_an_over_long_key()
    {
        var props = new Dictionary<string, string> { [new string('k', 65)] = "v" };

        var ex = Assert.Throws<BucketException>(() => NewValidator().PropertiesJson(props))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void PropertiesJson_rejects_an_over_long_value()
    {
        var props = new Dictionary<string, string> { ["k"] = new string('v', 513) };

        var ex = Assert.Throws<BucketException>(() => NewValidator().PropertiesJson(props))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [Test]
    public void PropertiesJson_rejects_a_duplicate_key()
    {
        List<KeyValuePair<string, string>> props =
        [
            new("k", "1"),
            new("k", "2")
        ];

        var ex = Assert.Throws<BucketException>(() => NewValidator().PropertiesJson(props))!;

        Assert.That(ex.Code, Is.EqualTo(BucketErrorCode.InvalidInput));
    }

    [TestCase(BucketErrorCode.NotAdmin, "NOT_ADMIN")]
    [TestCase(BucketErrorCode.UnknownNamespace, "UNKNOWN_NAMESPACE")]
    [TestCase(BucketErrorCode.BucketIsLocked, "BUCKET_IS_LOCKED")]
    [TestCase(BucketErrorCode.LastManagerRemoval, "LAST_MANAGER_REMOVAL")]
    [TestCase(BucketErrorCode.InvalidInput, "INVALID_INPUT")]
    public void ToErrorCode_screaming_snake_cases_the_enum(BucketErrorCode code, string expected)
    {
        Assert.That(code.ToErrorCode(), Is.EqualTo(expected));
    }

    [Test]
    public void Every_error_code_has_a_distinct_wire_string()
    {
        var codes = Enum.GetValues<BucketErrorCode>().Select(c => c.ToErrorCode()).ToList();

        Assert.That(codes, Is.Unique);
    }
}
