namespace TerraScale.Dashboard.Features.Organizations.Wizard;

public sealed record OrganizationWizardModel(OrganizationPage Page, string Suffix)
{
    public int Step => Page.WizardStep == "payment" ? 2 : Page.WizardStep == "project" ? 3 : 1;
    public int Progress => Step * 100 / 3;
    public string Id(string name) => name + "-" + Suffix;
}
