namespace TerraScale.Dashboard.Common.Validation
{
    public sealed class OnboardingValidationException(string message, string field = "") : Exception(message)
    {
        public string Field { get; } = field;
    }
}
