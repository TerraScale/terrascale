using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed record TerraBaseCompleted;

public interface ITerraBaseOutcome<TSelf, TValue> where TSelf : ITerraBaseOutcome<TSelf, TValue>
{
    static abstract TSelf Accepted(TValue value);

    static abstract TSelf Refused(TerraBaseRefusal refusal);
}

public readonly union TerraBaseRawOutcome(RawAnswer, TerraBaseRefusal);

public readonly union TerraBaseRegionsOutcome(TerraBaseRegions, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRegionsOutcome, TerraBaseRegions>
{
    public static TerraBaseRegionsOutcome Accepted(TerraBaseRegions value) => new(value);

    public static TerraBaseRegionsOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRegions Require() => this switch
    {
        TerraBaseRegions value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseHealthOutcome(TerraBaseHealth, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseHealthOutcome, TerraBaseHealth>
{
    public static TerraBaseHealthOutcome Accepted(TerraBaseHealth value) => new(value);

    public static TerraBaseHealthOutcome Refused(TerraBaseRefusal refusal) => new(refusal);
}

public readonly union TerraBaseDatabasePageOutcome(TerraBaseDatabasePage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseDatabasePageOutcome, TerraBaseDatabasePage>
{
    public static TerraBaseDatabasePageOutcome Accepted(TerraBaseDatabasePage value) => new(value);

    public static TerraBaseDatabasePageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseDatabasePage Require() => this switch
    {
        TerraBaseDatabasePage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseDatabaseOutcome(TerraBaseManagedDatabase, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseDatabaseOutcome, TerraBaseManagedDatabase>
{
    public static TerraBaseDatabaseOutcome Accepted(TerraBaseManagedDatabase value) => new(value);

    public static TerraBaseDatabaseOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseManagedDatabase Require() => this switch
    {
        TerraBaseManagedDatabase value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseDatabaseRegionsOutcome(TerraBaseDatabaseRegions, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseDatabaseRegionsOutcome, TerraBaseDatabaseRegions>
{
    public static TerraBaseDatabaseRegionsOutcome Accepted(TerraBaseDatabaseRegions value) => new(value);

    public static TerraBaseDatabaseRegionsOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseDatabaseRegions Require() => this switch
    {
        TerraBaseDatabaseRegions value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseActionOutcome(TerraBaseCompleted, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseActionOutcome, TerraBaseCompleted>
{
    public static TerraBaseActionOutcome Accepted(TerraBaseCompleted value) => new(value);

    public static TerraBaseActionOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseCompleted Require() => this switch
    {
        TerraBaseCompleted value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseJsonOutcome(JsonElement, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseJsonOutcome, JsonElement>
{
    public static TerraBaseJsonOutcome Accepted(JsonElement value) => new(value);

    public static TerraBaseJsonOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public JsonElement Require() => this switch
    {
        JsonElement value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRulesOutcome(TerraBaseRules, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRulesOutcome, TerraBaseRules>
{
    public static TerraBaseRulesOutcome Accepted(TerraBaseRules value) => new(value);

    public static TerraBaseRulesOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRules Require() => this switch
    {
        TerraBaseRules value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRulesDeployOutcome(TerraBaseRulesDeploy, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRulesDeployOutcome, TerraBaseRulesDeploy>
{
    public static TerraBaseRulesDeployOutcome Accepted(TerraBaseRulesDeploy value) => new(value);

    public static TerraBaseRulesDeployOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRulesDeploy Require() => this switch
    {
        TerraBaseRulesDeploy value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRulesVersionPageOutcome(TerraBaseRulesVersionPage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRulesVersionPageOutcome, TerraBaseRulesVersionPage>
{
    public static TerraBaseRulesVersionPageOutcome Accepted(TerraBaseRulesVersionPage value) => new(value);

    public static TerraBaseRulesVersionPageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRulesVersionPage Require() => this switch
    {
        TerraBaseRulesVersionPage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRulesTestOutcome(TerraBaseRulesTestResult, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRulesTestOutcome, TerraBaseRulesTestResult>
{
    public static TerraBaseRulesTestOutcome Accepted(TerraBaseRulesTestResult value) => new(value);

    public static TerraBaseRulesTestOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRulesTestResult Require() => this switch
    {
        TerraBaseRulesTestResult value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRestoreWindowOutcome(TerraBaseRestoreWindow, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRestoreWindowOutcome, TerraBaseRestoreWindow>
{
    public static TerraBaseRestoreWindowOutcome Accepted(TerraBaseRestoreWindow value) => new(value);

    public static TerraBaseRestoreWindowOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRestoreWindow Require() => this switch
    {
        TerraBaseRestoreWindow value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBasePointInTimePlanOutcome(TerraBasePointInTimePlan, TerraBaseRefusal) : ITerraBaseOutcome<TerraBasePointInTimePlanOutcome, TerraBasePointInTimePlan>
{
    public static TerraBasePointInTimePlanOutcome Accepted(TerraBasePointInTimePlan value) => new(value);

    public static TerraBasePointInTimePlanOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBasePointInTimePlan Require() => this switch
    {
        TerraBasePointInTimePlan value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseRequestLogPageOutcome(TerraBaseRequestLogPage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseRequestLogPageOutcome, TerraBaseRequestLogPage>
{
    public static TerraBaseRequestLogPageOutcome Accepted(TerraBaseRequestLogPage value) => new(value);

    public static TerraBaseRequestLogPageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseRequestLogPage Require() => this switch
    {
        TerraBaseRequestLogPage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseProjectUsageOutcome(TerraBaseProjectUsage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseProjectUsageOutcome, TerraBaseProjectUsage>
{
    public static TerraBaseProjectUsageOutcome Accepted(TerraBaseProjectUsage value) => new(value);

    public static TerraBaseProjectUsageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseProjectUsage Require() => this switch
    {
        TerraBaseProjectUsage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseDatabaseUsageOutcome(TerraBaseDatabaseUsage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseDatabaseUsageOutcome, TerraBaseDatabaseUsage>
{
    public static TerraBaseDatabaseUsageOutcome Accepted(TerraBaseDatabaseUsage value) => new(value);

    public static TerraBaseDatabaseUsageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseDatabaseUsage Require() => this switch
    {
        TerraBaseDatabaseUsage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionListOutcome(IReadOnlyList<TerraBaseFunctionInfo>, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionListOutcome, IReadOnlyList<TerraBaseFunctionInfo>>
{
    public static TerraBaseFunctionListOutcome Accepted(IReadOnlyList<TerraBaseFunctionInfo> value) => new(value);

    public static TerraBaseFunctionListOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public IReadOnlyList<TerraBaseFunctionInfo> Require() => this switch
    {
        IReadOnlyList<TerraBaseFunctionInfo> value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionOutcome(TerraBaseFunctionInfo, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionOutcome, TerraBaseFunctionInfo>
{
    public static TerraBaseFunctionOutcome Accepted(TerraBaseFunctionInfo value) => new(value);

    public static TerraBaseFunctionOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFunctionInfo Require() => this switch
    {
        TerraBaseFunctionInfo value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionInvocationsOutcome(IReadOnlyList<TerraBaseFunctionInvocation>, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionInvocationsOutcome, IReadOnlyList<TerraBaseFunctionInvocation>>
{
    public static TerraBaseFunctionInvocationsOutcome Accepted(IReadOnlyList<TerraBaseFunctionInvocation> value) => new(value);

    public static TerraBaseFunctionInvocationsOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public IReadOnlyList<TerraBaseFunctionInvocation> Require() => this switch
    {
        IReadOnlyList<TerraBaseFunctionInvocation> value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionStatsOutcome(TerraBaseFunctionStats, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionStatsOutcome, TerraBaseFunctionStats>
{
    public static TerraBaseFunctionStatsOutcome Accepted(TerraBaseFunctionStats value) => new(value);

    public static TerraBaseFunctionStatsOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFunctionStats Require() => this switch
    {
        TerraBaseFunctionStats value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionSecretsOutcome(IReadOnlyList<TerraBaseFunctionSecret>, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionSecretsOutcome, IReadOnlyList<TerraBaseFunctionSecret>>
{
    public static TerraBaseFunctionSecretsOutcome Accepted(IReadOnlyList<TerraBaseFunctionSecret> value) => new(value);

    public static TerraBaseFunctionSecretsOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public IReadOnlyList<TerraBaseFunctionSecret> Require() => this switch
    {
        IReadOnlyList<TerraBaseFunctionSecret> value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionSecretOutcome(TerraBaseFunctionSecret, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionSecretOutcome, TerraBaseFunctionSecret>
{
    public static TerraBaseFunctionSecretOutcome Accepted(TerraBaseFunctionSecret value) => new(value);

    public static TerraBaseFunctionSecretOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFunctionSecret Require() => this switch
    {
        TerraBaseFunctionSecret value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFunctionCallOutcome(TerraBaseFunctionCall, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFunctionCallOutcome, TerraBaseFunctionCall>
{
    public static TerraBaseFunctionCallOutcome Accepted(TerraBaseFunctionCall value) => new(value);

    public static TerraBaseFunctionCallOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFunctionCall Require() => this switch
    {
        TerraBaseFunctionCall value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFileBucketOutcome(TerraBaseFileBucket, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFileBucketOutcome, TerraBaseFileBucket>
{
    public static TerraBaseFileBucketOutcome Accepted(TerraBaseFileBucket value) => new(value);

    public static TerraBaseFileBucketOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFileBucket Require() => this switch
    {
        TerraBaseFileBucket value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFileBucketPageOutcome(TerraBaseFileBucketPage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFileBucketPageOutcome, TerraBaseFileBucketPage>
{
    public static TerraBaseFileBucketPageOutcome Accepted(TerraBaseFileBucketPage value) => new(value);

    public static TerraBaseFileBucketPageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFileBucketPage Require() => this switch
    {
        TerraBaseFileBucketPage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseFileUrlOutcome(TerraBaseFileUrl, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseFileUrlOutcome, TerraBaseFileUrl>
{
    public static TerraBaseFileUrlOutcome Accepted(TerraBaseFileUrl value) => new(value);

    public static TerraBaseFileUrlOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseFileUrl Require() => this switch
    {
        TerraBaseFileUrl value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseStoredFileOutcome(TerraBaseStoredFile, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseStoredFileOutcome, TerraBaseStoredFile>
{
    public static TerraBaseStoredFileOutcome Accepted(TerraBaseStoredFile value) => new(value);

    public static TerraBaseStoredFileOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseStoredFile Require() => this switch
    {
        TerraBaseStoredFile value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}

public readonly union TerraBaseStoredFilePageOutcome(TerraBaseStoredFilePage, TerraBaseRefusal) : ITerraBaseOutcome<TerraBaseStoredFilePageOutcome, TerraBaseStoredFilePage>
{
    public static TerraBaseStoredFilePageOutcome Accepted(TerraBaseStoredFilePage value) => new(value);

    public static TerraBaseStoredFilePageOutcome Refused(TerraBaseRefusal refusal) => new(refusal);

    public TerraBaseStoredFilePage Require() => this switch
    {
        TerraBaseStoredFilePage value => value,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}
