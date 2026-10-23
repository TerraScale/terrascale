using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

public sealed class SpendingFormTests
{
    [Test]
    [Arguments("", 0L)]
    [Arguments("0", 0L)]
    [Arguments("12.50", 1250L)]
    [Arguments("12.5", 1250L)]
    [Arguments("92233720368547758.07", long.MaxValue)]
    public async Task ReportOnlyCapParsesExactCents(string value, long expected)
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["spendCap"] = value });
        await Assert.That(SpendingForm.TryParse(form, out var cents)).IsTrue();
        await Assert.That(cents).IsEqualTo(expected);
    }

    [Test]
    [Arguments("-1")]
    [Arguments("1.001")]
    [Arguments("1e2")]
    [Arguments("1,000")]
    [Arguments("92233720368547758.08")]
    public async Task InvalidOrOverflowedCapCannotBeSaved(string value)
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["spendCap"] = value });
        await Assert.That(SpendingForm.TryParse(form, out _)).IsFalse();
    }

    [Test]
    [Arguments("paidExcess")]
    [Arguments("initial_paidExcess")]
    [Arguments("projCap_01JA0000000000000000000001")]
    [Arguments("dbWrite_01JA0000000000000000000002")]
    public async Task InternalFormCannotAcceptLegacyCustomerControls(string key)
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["spendCap"] = "120.00", [key] = "1" });
        await Assert.That(SpendingForm.TryParse(form, out _)).IsFalse();
    }

    [Test]
    public async Task MissingOrRepeatedCapCannotBeSaved()
    {
        await Assert.That(SpendingForm.TryParse(new FormCollection(new Dictionary<string, StringValues>()), out _)).IsFalse();
        var repeated = new FormCollection(new Dictionary<string, StringValues> { ["spendCap"] = new StringValues(["1", "2"]) });
        await Assert.That(SpendingForm.TryParse(repeated, out _)).IsFalse();
    }
}
